// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using TeamSpeak9.Core.Streaming;
using TeamSpeak9.Core.Threading;
using TeamSpeak9.Streaming.Encoding;
using TeamSpeak9.Streaming.Tssp;

namespace TeamSpeak9.Streaming.Publishing;

/// <summary>
/// Publishes a captured screen or window as a TSSP stream.
/// </summary>
/// <remarks>
/// <para>
/// Wires the capture source, the <see cref="ScreenVideoEncoder"/> and the TSSP client together:
/// frames arrive on the capture thread, are encoded on the fly, and the access units are handed
/// to the SIPSorcery peer connection for RTP transport. The TSSP client only carries signalling;
/// the media plane lives entirely in this class.
/// </para>
/// <para>
/// The publisher supports both SFU and P2P modes. In SFU mode the server relays the media to every
/// subscriber in the channel; in P2P mode the server only exchanges SDP/ICE and the media flows
/// directly between the two peers. The negotiated <see cref="TsspPublishInstruction"/> decides who
/// creates the offer: the publisher or the server.
/// </para>
/// <para>
/// All state events are raised on the UI thread via <see cref="IUiDispatcher"/>. Frame delivery and
/// encoding happen on the capture thread and are never marshalled, so the UI stays responsive even
/// at high frame rates.
/// </para>
/// </remarks>
public sealed class ScreenSharePublisher : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly ILogger _log;
    private readonly IUiDispatcher _ui;
    private readonly IScreenCaptureFactory _captureFactory;
    private readonly ScreenVideoEncoder _encoder;
    private readonly TsspClient _tssp;
    private readonly ScreenCaptureTarget _target;
    private readonly StreamMediaProfile _profile;
    private readonly ScreenCaptureOptions _options;

    private IScreenCaptureSource? _capture;
    private RTCPeerConnection? _peer;
    private MediaStreamTrack? _videoTrack;
    private string? _streamId;
    private string? _mode;
    private bool _disposed;
    private bool _stopping;

    /// <summary>Raised on the UI thread when the publisher transitions to a new state.</summary>
    public event EventHandler<ScreenShareState>? StateChanged;

    /// <summary>Raised on the UI thread when a stream error occurs.</summary>
    public event EventHandler<Exception>? Faulted;

    /// <summary>
    /// Initialises a new publisher.
    /// </summary>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ScreenSharePublisher(
        ILogger log,
        IUiDispatcher ui,
        IScreenCaptureFactory captureFactory,
        ScreenVideoEncoder encoder,
        TsspClient tssp,
        ScreenCaptureTarget target,
        StreamMediaProfile profile,
        ScreenCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(log);
                ArgumentNullException.ThrowIfNull(ui);
                ArgumentNullException.ThrowIfNull(captureFactory);
                ArgumentNullException.ThrowIfNull(encoder);
                ArgumentNullException.ThrowIfNull(tssp);
                ArgumentNullException.ThrowIfNull(target);
                ArgumentNullException.ThrowIfNull(profile);

        _log = log;
        _ui = ui;
        _captureFactory = captureFactory;
        _encoder = encoder;
        _tssp = tssp;
        _target = target;
        _profile = profile;
        _options = options;
    }

    /// <summary>The stream id assigned by the server, or <see langword="null"/> before <see cref="StartAsync"/>.</summary>
    public string? StreamId => _streamId;

    /// <summary>The negotiated media mode (<c>sfu</c> or <c>p2p</c>), or <see langword="null"/> before start.</summary>
    public string? Mode => _mode;

    /// <summary>
    /// Starts publishing. Subscribes to <see cref="TsspClient.SignalingReceived"/> before issuing
    /// <c>setup</c>, then creates the capture source and the peer connection.
    /// </summary>
    /// <param name="cancellationToken">Cancels the <c>setup</c> exchange.</param>
    /// <exception cref="InvalidOperationException">The publisher is already running or disposed.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_capture is not null)
            {
                throw new InvalidOperationException("发布器已在运行。");
            }
        }

        // Subscribe before setup: the server may push the first signaling message before setup returns.
        _tssp.SignalingReceived += OnSignalingReceived;
        _tssp.Bye += OnBye;

        try
        {
            _capture = _captureFactory.Create(_target, _options);
            _capture.FrameArrived += OnFrameArrived;
            _capture.Closed += OnCaptureClosed;

            _peer = CreatePeerConnection();
            _videoTrack = new MediaStreamTrack(
                [StreamCodecs.ToVideoFormat(_profile.Codec)],
                MediaStreamStatusEnum.SendOnly);
            _peer.addTrack(_videoTrack);

            var response = await _tssp.SetupAsync(new TsspSetupRequest
            {
                Token = _tssp.Session?.SessionToken ?? string.Empty,
                Mode = _profile.Codec == VideoCodec.H264 ? TsspModes.Sfu : TsspModes.Sfu,
                StreamType = _target.Kind == ScreenCaptureKind.Window ? TsspStreamTypes.Window : TsspStreamTypes.Screen,
                Accessibility = TsspAccessibility.Channel,
                Name = _target.Name,
                Properties = StreamCodecs.ToProperties(_profile),
            }, cancellationToken).ConfigureAwait(false);

            _streamId = response.StreamId;
            _mode = response.Mode;
            _log.LogInformation("屏幕共享已发布：流 {StreamId}，模式 {Mode}", _streamId, _mode);

            // The server may have already sent signaling before setup returned; drain it now.
            HandlePendingSignaling();

            RaiseState(ScreenShareState.Publishing);
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    /// <summary>
    /// Stops publishing and releases all resources. Safe to call multiple times.
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
                    await _tssp.StopAsync(new TsspStopRequest { Token = _tssp.Session?.SessionToken ?? string.Empty, StreamId = _streamId })
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "停止流 {StreamId} 时服务端返回错误", _streamId);
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
        _tssp.Bye -= OnBye;
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
                Role = TsspRoles.Publisher,
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
            _log.LogDebug("屏幕共享 ICE 连接状态：{State}", state);
            if (state == RTCIceConnectionState.failed)
            {
                _ = StopAsync();
            }
        };

        pc.onconnectionstatechange += state =>
        {
            _log.LogDebug("屏幕共享 PeerConnection 状态：{State}", state);
            if (state == RTCPeerConnectionState.closed)
            {
                _ = StopAsync();
            }
        };

        return pc;
    }

    /// <summary>
    /// Handles a signaling message from the server. In SFU mode the server relays the peer's
    /// answer; in P2P mode it relays the subscriber's offer or answer.
    /// </summary>
    private void OnSignalingReceived(object? sender, TsspSignalingMessage message)
    {
        if (message.StreamId != _streamId)
        {
            return;
        }

        if (message.Role != TsspRoles.Publisher)
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
            _log.LogError(ex, "处理屏幕共享信令失败");
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

        var answer = peer.createAnswer(new RTCAnswerOptions
        {
            X_WaitForIceGatheringToComplete = true,
        });

        peer.setLocalDescription(answer);

        if (_streamId is not null)
        {
            _ = SendSignalingAsync(new TsspSignalingMessage
            {
                StreamId = _streamId,
                Role = TsspRoles.Publisher,
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
    }

    private void HandleRemoteCandidate(TsspSignalingMessage message)
    {
        var peer = _peer;
        if (peer is null || string.IsNullOrWhiteSpace(message.SignalingData))
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
            sdpMLineIndex = candidate.SdpMLineIndex is int mline ? (ushort)mline : (ushort)0,
            usernameFragment = candidate.UsernameFragment,
        });
    }

    /// <summary>
    /// Drains any signaling the server pushed before <c>setup</c> returned. The TSSP client raises
    /// <see cref="TsspClient.SignalingReceived"/> synchronously on its receive loop, so by the time
    /// <see cref="TsspClient.SetupAsync"/> returns any early signaling has already been dispatched.
    /// </summary>
    private void HandlePendingSignaling()
    {
        // Nothing to drain: the TSSP client dispatches signaling synchronously.
    }

    /// <summary>Encodes a captured frame and hands the access unit to the peer connection.</summary>
    private void OnFrameArrived(in ScreenFrame frame)
    {
        var peer = _peer;
        if (peer is null)
        {
            return;
        }

        try
        {
            byte[]? encoded = _encoder.Encode(frame, _profile);
            if (encoded is null || encoded.Length == 0)
            {
                return;
            }

            uint durationMs = (uint)Math.Max(1, Math.Round(1000.0 / _profile.FrameRate));
            peer.SendVideo(durationMs, encoded);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "编码或发送屏幕共享帧失败");
            RaiseFault(ex);
        }
    }

    /// <summary>The capture source closed unexpectedly; treat it as an implicit stop.</summary>
    private void OnCaptureClosed(object? sender, EventArgs e)
    {
        _log.LogInformation("屏幕共享采集源已关闭，停止发布");
        _ = StopAsync();
    }

    /// <summary>The TSSP server said goodbye; stop publishing.</summary>
    private void OnBye(object? sender, TsspByeEvent e)
    {
        _log.LogInformation("TSSP 服务端告别，停止屏幕共享");
        _ = StopAsync();
    }

    private async Task SendSignalingAsync(TsspSignalingMessage message)
    {
        try
        {
            await _tssp.SendSignalingAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "发送屏幕共享信令失败");
        }
    }

    private void Cleanup()
    {
        var capture = _capture;
        var peer = _peer;

        _capture = null;
        _peer = null;
        _videoTrack = null;

        if (capture is not null)
        {
            capture.FrameArrived -= OnFrameArrived;
            capture.Closed -= OnCaptureClosed;
            capture.Dispose();
        }

        if (peer is not null)
        {
            try
            {
                peer.Close("publisher stopped");
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "关闭屏幕共享 PeerConnection 时抛出异常");
            }
            peer.Dispose();
        }
    }

    private void RaiseState(ScreenShareState state)
    {
        _ui.Post(() => StateChanged?.Invoke(this, state));
    }

    private void RaiseFault(Exception ex)
    {
        _ui.Post(() => Faulted?.Invoke(this, ex));
    }
}

/// <summary>The lifecycle state of a <see cref="ScreenSharePublisher"/>.</summary>
public enum ScreenShareState
{
    /// <summary>Not yet started.</summary>
    Idle,

    /// <summary>Connecting to the stream.</summary>
    Connecting,

    /// <summary>Publishing is active.</summary>
    Publishing,

    /// <summary>Playing/viewing a stream.</summary>
    Playing,

    /// <summary>Publishing has stopped.</summary>
    Stopped,
}