// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TeamSpeak9.App.ViewModels;
using TeamSpeak9.App.Views;
using TeamSpeak9.Core.Connection;
using TeamSpeak9.Core.Model;
using TeamSpeak9.Core.Settings;
using TeamSpeak9.Core.Streaming;
using TeamSpeak9.Core.Threading;
using TeamSpeak9.Streaming;
using TeamSpeak9.Streaming.Capture;
using TeamSpeak9.Streaming.Encoding;
using TeamSpeak9.Streaming.Publishing;
using TeamSpeak9.Streaming.Tssp;
using TSLib.Messages;

namespace TeamSpeak9.App.Streaming;

/// <summary>
/// Orchestrates the screen share lifecycle: resolves the TSSP endpoint, connects the signaling
/// client, builds the media profile, and starts/stops the publisher.
/// </summary>
public sealed class ScreenShareService : IDisposable
{
    private readonly TsConnection _connection;
    private readonly IScreenCaptureFactory _captureFactory;
    private readonly ScreenVideoEncoder _encoder;
    private readonly TsspClient _tssp;
    private readonly StreamSettings _settings;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<ScreenShareService> _log;
        private readonly Func<SharePickerViewModel, SharePickerWindow> _sharePickerFactory;

        private ScreenSharePublisher? _publisher;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        public ScreenShareService(
            TsConnection connection,
            IScreenCaptureFactory captureFactory,
            ScreenVideoEncoder encoder,
            TsspClient tssp,
            StreamSettings settings,
            IUiDispatcher ui,
            ILogger<ScreenShareService> log,
            Func<SharePickerViewModel, SharePickerWindow> sharePickerFactory)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(captureFactory);
            ArgumentNullException.ThrowIfNull(encoder);
            ArgumentNullException.ThrowIfNull(tssp);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(ui);
            ArgumentNullException.ThrowIfNull(log);
            ArgumentNullException.ThrowIfNull(sharePickerFactory);

            _connection = connection;
            _captureFactory = captureFactory;
            _encoder = encoder;
            _tssp = tssp;
            _settings = settings;
            _ui = ui;
            _log = log;
            _sharePickerFactory = sharePickerFactory;
        }

        /// <summary>Whether a screen share is currently active.</summary>
        public bool IsActive => _publisher is not null;

        /// <summary>The stream id assigned by the server, or <see langword="null"/> when not active.</summary>
        public string? StreamId => _publisher?.StreamId;

        /// <summary>The negotiated media mode (<c>sfu</c> or <c>p2p</c>), or <see langword="null"/> when not active.</summary>
        public string? Mode => _publisher?.Mode;

        /// <summary>Whether screen capture is supported on this system.</summary>
        public bool IsSupported => _captureFactory.IsSupported;

        /// <summary>
        /// Opens the share picker and starts screen sharing with the user's selection.
        /// </summary>
        /// <param name="cancellationToken">Cancels the setup exchange.</param>
        /// <exception cref="InvalidOperationException">A share is already active.</exception>
        /// <exception cref="OperationCanceledException">The operation was cancelled (user closed picker).</exception>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_publisher is not null)
            {
                throw new InvalidOperationException("屏幕共享已在进行中。");
            }

            if (!_connection.IsConnected)
            {
                throw new InvalidOperationException("未连接到服务器。");
            }

            if (!_captureFactory.IsSupported)
            {
                throw new NotSupportedException("当前系统不支持屏幕共享功能。");
            }

            // Show the share picker on the UI thread
                        var (target, options) = await _ui.InvokeAsync<(ScreenCaptureTarget?, ScreenCaptureOptions)>(() =>
            {
                var vm = new SharePickerViewModel(
                    _captureFactory as IScreenTargetEnumerator ?? throw new InvalidOperationException("Capture factory does not implement IScreenTargetEnumerator"),
                    _settings);
                var dialog = _sharePickerFactory(vm);
                if (dialog.ShowDialog() == true)
                {
                    return (vm.SelectedTarget!, new ScreenCaptureOptions(vm.ShowCaptureBorder, vm.CaptureCursor));
                }
                return (null, default(ScreenCaptureOptions));
                        }).ConfigureAwait(false);

            if (target is null)
            {
                throw new OperationCanceledException("用户取消了屏幕共享选择。");
            }

            await StartAsync(target, options, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Starts sharing the given target.
    /// </summary>
    /// <param name="target">The surface to share (monitor or window).</param>
    /// <param name="options">Capture options (border, cursor).</param>
    /// <param name="cancellationToken">Cancels the setup exchange.</param>
    /// <exception cref="InvalidOperationException">A share is already active.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public async Task StartAsync(ScreenCaptureTarget target, ScreenCaptureOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_publisher is not null)
        {
            throw new InvalidOperationException("屏幕共享已在进行中。");
        }

        if (!_connection.IsConnected)
        {
            throw new InvalidOperationException("未连接到服务器。");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            // 1. Resolve the TSSP endpoint from the server's advertised property or manual setting.
            var advertisedEndpoint = await GetAdvertisedEndpointAsync(_cts.Token).ConfigureAwait(false);
            var resolution = TsspEndpointResolver.Resolve(advertisedEndpoint, _settings, allowInsecureScheme: false);

            if (!resolution.Success)
            {
                throw new InvalidOperationException($"无法解析屏幕共享服务地址：{resolution.Problem} - {resolution.Message}");
            }

            _log.LogInformation("屏幕共享端点已解析：{Endpoint} (来源：{Source})", resolution.Endpoint, resolution.Source);

            // 2. Build the hello request from the TSLib client state.
            var helloRequest = await BuildHelloRequestAsync(resolution.Endpoint, _cts.Token).ConfigureAwait(false);

            // 3. Connect the TSSP client.
                        await _tssp.ConnectAsync(resolution.Endpoint!, helloRequest, cancellationToken: _cts.Token).ConfigureAwait(false);

            // 4. Build the media profile clamped to user settings.
            var profile = BuildMediaProfile(target);

            // 5. Create and start the publisher.
            _publisher = new ScreenSharePublisher(
                _log,
                _ui,
                _captureFactory,
                _encoder,
                _tssp,
                target,
                profile,
                options);

            _publisher.StateChanged += OnPublisherStateChanged;
            _publisher.Faulted += OnPublisherFaulted;

            await _publisher.StartAsync(_cts.Token).ConfigureAwait(false);

            _log.LogInformation("屏幕共享已启动：流 {StreamId}，模式 {Mode}", _publisher.StreamId, _publisher.Mode);
        }
        catch
        {
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Stops the active screen share.
    /// </summary>
    public async Task StopAsync()
    {
        if (_publisher is null)
        {
            return;
        }

        await CleanupAsync().ConfigureAwait(false);
    }

    private async Task<string?> GetAdvertisedEndpointAsync(CancellationToken cancellationToken)
        {
            try
            {
                var result = await _connection.ExecuteAsync(
                    async client =>
                    {
                        var vars = await client.GetServerVariables().ConfigureAwait(false);
                        return vars;
                    },
                    R<TSLib.Messages.ServerUpdated, TSLib.Messages.CommandError>.Err(TSLib.Messages.CommandError.ConnectionClosed)).ConfigureAwait(false);

                if (result.Ok && result.Value is not null)
                {
                    // The custom property virtualserver_sfu_endpoint is echoed back in the variables.
                    // TSLib's ServerUpdated doesn't expose custom properties directly, so we need to
                    // check if there's a way to read it. For now, return null and let the resolver
                    // fall back to the manual endpoint.
                    // TODO: Extend TSLib or use a raw query to read custom virtualserver_* properties.
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "读取服务器公告的 SFU 端点失败，将使用手工配置");
            }

            return null;
        }

    private async Task<Func<CancellationToken, ValueTask<TsspHelloRequest>>> BuildHelloRequestAsync(Uri? endpoint, CancellationToken cancellationToken)
        {
            // Query the TSLib client for our uid, clid, cid.
            var selfInfo = await _connection.ExecuteAsync<(string Uid, int Clid, long Cid)>(
                async client =>
                {
                    var myClientId = client.ClientId;
                    if (myClientId == TSLib.ClientId.Null)
                    {
                        return (Uid: string.Empty, Clid: 0, Cid: 0L);
                    }

                    // Get our own info via whoami command (includes UID and channel)
                    var whoamiResult = await client.SendNotifyCommand(
                        new TSLib.Commands.TsCommand("whoami"),
                        TSLib.Messages.NotificationType.WhoAmIRequest).MapToSingle<TSLib.Messages.WhoAmI>().ConfigureAwait(false);

                    if (!whoamiResult.Ok || whoamiResult.Value is null)
                    {
                        return (Uid: string.Empty, Clid: (int)myClientId.Value, Cid: 0L);
                    }

                    var whoami = whoamiResult.Value;
                    var uid = whoami.Uid != TSLib.Uid.Null ? whoami.Uid.Value : string.Empty;
                    var cid = (long)whoami.ChannelId.Value;

                    return (uid, (int)myClientId.Value, cid);
                },
                (Uid: string.Empty, Clid: 0, Cid: 0L)).ConfigureAwait(false);

            var serverAddress = _connection.CurrentRequest?.Address ?? string.Empty;
            var nonce = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

            return _ => ValueTask.FromResult(new TsspHelloRequest
            {
                ServerAddress = serverAddress,
                Uid = selfInfo.Uid,
                Clid = selfInfo.Clid,
                Cid = selfInfo.Cid,
                Nonce = nonce,
                Client = new TsspClientInfo
                {
                    Name = "TeamSpeak9",
                    Version = typeof(ScreenShareService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    Platform = "Windows",
                },
                Capabilities = new TsspClientCapabilities
                {
                    VideoCodecs = ScreenVideoEncoder.AvailableCodecs.Select(StreamCodecs.ToWire).ToList(),
                    AudioCodecs = ["opus"],
                    MaxRecvStreams = 1,
                }
            });
        }

    private StreamMediaProfile BuildMediaProfile(ScreenCaptureTarget target)
    {
        var availableCodecs = ScreenVideoEncoder.AvailableCodecs;
            var preferredCodec = availableCodecs.FirstOrDefault();

        var profile = new StreamMediaProfile
        {
            Codec = preferredCodec,
            Width = target.Width,
            Height = target.Height,
            FrameRate = _settings.MaxFrameRate,
            BitrateKbps = _settings.MaxBitrateKbps,
            HasAudio = _settings.CaptureAudio,
        };

        return profile.ClampTo(_settings.MaxWidth, _settings.MaxHeight, _settings.MaxFrameRate, _settings.MaxBitrateKbps);
    }

    private void OnPublisherStateChanged(object? sender, ScreenShareState state)
    {
        _log.LogDebug("屏幕共享状态变更：{State}", state);
    }

    private void OnPublisherFaulted(object? sender, Exception ex)
    {
        _log.LogError(ex, "屏幕共享发布器发生错误");
            _ = _ui.InvokeAsync(async () => await CleanupAsync().ConfigureAwait(false));
    }

    private async Task CleanupAsync()
    {
        if (_publisher is not null)
        {
            _publisher.StateChanged -= OnPublisherStateChanged;
            _publisher.Faulted -= OnPublisherFaulted;

            try
            {
                await _publisher.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "停止屏幕共享发布器时出错");
            }

            await _publisher.DisposeAsync().ConfigureAwait(false);
            _publisher = null;
        }

        if (_tssp.State != TsspConnectionState.Disconnected)
        {
            try
            {
                await _tssp.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "断开 TSSP 连接时出错");
            }
        }

        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Fire-and-forget cleanup; Dispose is not async.
        _ = Task.Run(async () =>
        {
            try
            {
                await CleanupAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ScreenShareService 释放时清理出错");
            }
        });
    }
}