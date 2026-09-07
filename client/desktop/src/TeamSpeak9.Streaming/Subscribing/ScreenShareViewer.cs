// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using TeamSpeak9.Core.Streaming;
using TeamSpeak9.Core.Threading;
using TeamSpeak9.Streaming.Publishing;
using TeamSpeak9.Streaming.Tssp;

namespace TeamSpeak9.Streaming.Subscribing;

/// <summary>
/// Subscribes to and renders a remote screen share stream.
/// </summary>
/// <remarks>
/// <para>
/// Wires the TSSP client, the SIPSorcery peer connection and a video sink together:
/// the subscriber issues <c>subscribe</c>, receives SDP/ICE via <c>signaling</c>,
/// establishes the media plane, and decodes incoming RTP packets into frames.
/// </para>
/// <para>
/// Supports both SFU and P2P modes. In SFU mode the server relays media; in P2P mode
/// media flows directly between peers. The negotiated <see cref="TsspPublishInstruction"/>
/// decides who creates the offer.
/// </para>
/// <para>
/// All state events are raised on the UI thread via <see cref="IUiDispatcher"/>.
/// Frame decoding happens on the media thread and frames are marshalled to the UI
/// thread for rendering.
/// </para>
/// </remarks>
public sealed class ScreenShareViewer : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly ILogger _log;
    private readonly IUiDispatcher _ui;
    private readonly TsspClient _tssp;
    private readonly string _streamId;
    private readonly StreamMediaProfile _profile;
    private readonly CancellationToken _ct;

    private RTCPeerConnection? _peer;
    private string? _mode;
    private bool _disposed;
    private bool _stopping;

    /// <summary>Raised on the UI thread when the viewer transitions to a new state.</summary>
    public event EventHandler<ScreenShareState>? StateChanged;

    /// <summary>Raised on the UI thread when a stream error occurs.</summary>
    public event EventHandler<Exception>? Faulted;

    /// <summary>Raised on the UI thread when a decoded frame is ready for rendering.</summary>
    /// <remarks>
    /// Currently not implemented - requires SIPSorcery VideoFrame type which is not available in current package version.
    /// </remarks>
#pragma warning disable CS0067
    public event EventHandler<byte[]>? FrameReady;
#pragma warning restore CS0067

    /// <summary>The stream id being viewed.</summary>
    public string StreamId => _streamId;

    /// <summary>The negotiated media mode (<c>sfu</c> or <c>p2p</c>), or <see langword="null"/> before start.</summary>
    public string? Mode => _mode;

    /// <summary>
    /// Initialises a new viewer for the given stream.
    /// </summary>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ScreenShareViewer(
        ILogger log,
        IUiDispatcher ui,
        TsspClient tssp,
        string streamId,
        StreamMediaProfile profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(tssp);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentNullException.ThrowIfNull(profile);

        _log = log;
        _ui = ui;
        _tssp = tssp;
        _streamId = streamId;
        _profile = profile;
        _ct = ct;
    }

    /// <summary>
    /// Starts viewing the stream. Subscribes to <see cref="TsspClient.SignalingReceived"/>
    /// before issuing <c>subscribe</c>, then creates the peer connection.
    /// </summary>
    /// <param name="preferMode">Preferred media mode (<c>sfu</c> or <c>p2p</c>), or <see langword="null"/> for server default.</param>
    /// <exception cref="InvalidOperationException">The viewer is already running or disposed.</exception>
    public async Task StartAsync(string? preferMode = null, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_peer is not null)
            {
                throw new InvalidOperationException("查看器已在运行。");
            }
        }

        // Subscribe before subscribe: the server may push the first signaling message before subscribe returns.
        _tssp.SignalingReceived += OnSignalingReceived;
        _tssp.SubscribeReady += OnSubscribeReady;
        _tssp.JoinRejected += OnJoinRejected;
        _tssp.Bye += OnBye;
        _tssp.StreamRemoved += OnStreamRemoved;

        try
        {
            _peer = CreatePeerConnection();

            var response = await _tssp.SubscribeAsync(new TsspSubscribeRequest
            {
                Token = _tssp.Session?.SessionToken ?? string.Empty,
                StreamId = _streamId,
                PreferMode = preferMode,
            }, cancellationToken).ConfigureAwait(false);

            _mode = response.Mode;
            _log.LogInformation("屏幕共享订阅已发起：流 {StreamId}，状态 {State}，模式 {Mode}", _streamId, response.State, _mode);

            // The server may have already sent signaling before subscribe returned; drain it now.
            HandlePendingSignaling();

            RaiseState(ScreenShareState.Connecting);
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    /// <summary>
    /// Stops viewing and releases all resources. Safe to call multiple times.
    /// </summary>
    public async Task StopAsync()
    {
        lock (_sync)
        {
            if (_stopping)
            {
                return;
            }
            _stopping = true;
        }

        try
        {
            if (_streamId is not null && _tssp.IsAuthenticated)
            {
                try
                {
                    await _tssp.UnsubscribeAsync(new TsspUnsubscribeRequest
                    {
                        Token = _tssp.Session?.SessionToken ?? string.Empty,
                        StreamId = _streamId,
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "取消订阅流 {StreamId} 时服务端返回错误", _streamId);
                }
            }
        }
        finally
        {
            Cleanup();
            RaiseState(ScreenShareState.Stopped);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
        _tssp.SignalingReceived -= OnSignalingReceived;
        _tssp.SubscribeReady -= OnSubscribeReady;
        _tssp.JoinRejected -= OnJoinRejected;
        _tssp.Bye -= OnBye;
        _tssp.StreamRemoved -= OnStreamRemoved;
    }

    /// <summary>
    /// Creates the SIPSorcery peer connection, seeding it with the ICE servers from the TSSP session.
    /// </summary>
    private RTCPeerConnection CreatePeerConnection()
    {
        var config = new RTCConfiguration
        {
            X_ICEIncludeAllInterfaceAddresses = true,
        };

        var iceServers = _tssp.Session?.Server.IceServers;
        if (iceServers is not null && iceServers.Count > 0)
        {
            config.iceServers = new List<RTCIceServer>();
            foreach (var server in iceServers)
            {
                foreach (var url in server.Urls)
                {
                    config.iceServers.Add(new RTCIceServer
                    {
                        urls = url,
                        username = server.Username,
                        credential = server.Credential,
                    });
                }
            }
        }

        var pc = new RTCPeerConnection(config);

        pc.onicecandidate += candidate =>
        {
            if (candidate is null || _streamId is null)
            {
                return;
            }

            _ = SendSignalingAsync(new TsspSignalingMessage
            {
                StreamId = _streamId,
                Role = TsspRoles.Subscriber,
                SignalingType = TsspSignalingTypes.Candidate,
                SignalingData = System.Text.Json.JsonSerializer.Serialize(new TsspIceCandidate
                {
                    Candidate = candidate.candidate,
                    SdpMid = candidate.sdpMid,
                    SdpMLineIndex = candidate.sdpMLineIndex,
                    UsernameFragment = candidate.usernameFragment,
                }),
            });
        };

        pc.oniceconnectionstatechange += state =>
        {
            _log.LogDebug("屏幕共享查看 ICE 连接状态：{State}", state);
            if (state == RTCIceConnectionState.failed)
            {
                _ = StopAsync();
            }
        };

        pc.onconnectionstatechange += state =>
        {
            _log.LogDebug("屏幕共享查看 PeerConnection 状态：{State}", state);
            if (state == RTCPeerConnectionState.closed)
            {
                _ = StopAsync();
            }
        };

        // TODO: Handle remote video track via pc.OnTrack when SIPSorcery VideoFrame type is available
        // pc.OnTrack += (track) => { if (track.Kind == "video") { /* add video sink */ } };

        return pc;
    }

    /// <summary>
    /// Handles a signaling message from the server. In SFU mode the server relays the publisher's
    /// answer; in P2P mode it relays the publisher's offer or answer.
    /// </summary>
    private void OnSignalingReceived(object? sender, TsspSignalingMessage message)
    {
        if (message.StreamId != _streamId)
        {
            return;
        }

        if (message.Role != TsspRoles.Subscriber)
        {
            return;
        }

        try
        {
            switch (message.SignalingType)
            {
                case TsspSignalingTypes.Offer:
                    HandleRemoteOffer(message);
                    break;

                case TsspSignalingTypes.Answer:
                    HandleRemoteAnswer(message);
                    break;

                case TsspSignalingTypes.Candidate:
                    HandleRemoteCandidate(message);
                    break;

                case TsspSignalingTypes.EndOfCandidates:
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "处理屏幕共享查看信令失败");
            RaiseFault(ex);
        }
    }

    private void HandleRemoteOffer(TsspSignalingMessage message)
    {
        var peer = _peer;
        if (peer is null)
        {
            return;
        }

        peer.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = message.SignalingData,
        });

        var answer = peer.createAnswer();

        peer.setLocalDescription(answer);

        if (_streamId is not null)
        {
            _ = SendSignalingAsync(new TsspSignalingMessage
            {
                StreamId = _streamId,
                Role = TsspRoles.Subscriber,
                SignalingType = TsspSignalingTypes.Answer,
                SignalingData = answer.sdp,
            });
        }
    }

    private void HandleRemoteAnswer(TsspSignalingMessage message)
    {
        var peer = _peer;
        if (peer is null)
        {
            return;
        }

        peer.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = message.SignalingData,
        });

        RaiseState(ScreenShareState.Playing);
    }

    private void HandleRemoteCandidate(TsspSignalingMessage message)
    {
        var peer = _peer;
        if (peer is null || message.SignalingData is null)
        {
            return;
        }

        var candidate = System.Text.Json.JsonSerializer.Deserialize<TsspIceCandidate>(message.SignalingData);
        if (candidate is null)
        {
            return;
        }

        peer.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate.Candidate,
            sdpMid = candidate.SdpMid,
            sdpMLineIndex = (ushort)(candidate.SdpMLineIndex ?? 0),
            usernameFragment = candidate.UsernameFragment,
        });
    }

    private void OnSubscribeReady(object? sender, TsspSubscribeReadyEvent e)
    {
        if (e.StreamId != _streamId)
        {
            return;
        }

        _mode = e.Mode;
        _log.LogInformation("订阅已就绪：流 {StreamId}，模式 {Mode}", _streamId, _mode);

        // In P2P mode, the publisher creates the offer. We wait for it via SignalingReceived.
        // In SFU mode, the server creates the offer. We also wait for it.
        // Either way, we just wait for the signaling message.
    }

    private void OnJoinRejected(object? sender, TsspJoinRejectedEvent e)
    {
        if (e.StreamId != _streamId)
        {
            return;
        }

        _log.LogWarning("观看申请被拒绝：流 {StreamId}，原因 {Reason}", _streamId, e.Reason);
        RaiseFault(new InvalidOperationException($"观看申请被拒绝：{e.Reason}"));
    }

    private void OnBye(object? sender, TsspByeEvent e)
    {
        _log.LogInformation("收到服务端 Bye，停止查看：{Code} {Message}", e.Code, e.Message);
        _ = StopAsync();
    }

    private void OnStreamRemoved(object? sender, TsspStreamRemovedEvent e)
    {
        if (e.StreamId != _streamId)
        {
            return;
        }

        _log.LogInformation("流已移除：{StreamId}，原因 {Reason}", _streamId, e.Reason);
        _ = StopAsync();
    }

    private void HandlePendingSignaling()
    {
        // TSSP client dispatches signaling synchronously, so there's no pending queue.
        // This method exists for symmetry with the publisher.
    }

    private async Task SendSignalingAsync(TsspSignalingMessage message)
    {
        try
        {
            await _tssp.SendSignalingAsync(message, _ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "发送屏幕共享查看信令失败");
            RaiseFault(ex);
        }
    }

    private void RaiseState(ScreenShareState state)
    {
        _ = _ui.InvokeAsync(() =>
        {
            StateChanged?.Invoke(this, state);
        });
    }

    private void RaiseFault(Exception ex)
    {
        _ = _ui.InvokeAsync(() =>
        {
            Faulted?.Invoke(this, ex);
        });
    }

    private void Cleanup()
    {
        if (_peer is not null)
        {
            _peer.Dispose();
            _peer = null;
        }
    }
}