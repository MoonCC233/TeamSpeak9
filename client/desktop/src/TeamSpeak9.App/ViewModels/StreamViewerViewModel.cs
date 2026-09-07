// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Linq;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TeamSpeak9.App.Streaming;
using TeamSpeak9.Core.Connection;
using TeamSpeak9.Core.Settings;
using TeamSpeak9.Core.Streaming;
using TeamSpeak9.Core.Threading;
using TeamSpeak9.Streaming;
using TeamSpeak9.Streaming.Publishing;
using TeamSpeak9.Streaming.Subscribing;
using TeamSpeak9.Streaming.Tssp;

namespace TeamSpeak9.App.ViewModels;

/// <summary>
/// View model for a stream in the stream list.
/// </summary>
public sealed partial class StreamViewModel : ObservableObject
{
    [ObservableProperty]
    private string _streamId = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _mode = string.Empty;

    [ObservableProperty]
    private string _streamType = string.Empty;

    [ObservableProperty]
    private string _accessibility = string.Empty;

    [ObservableProperty]
    private TsspPeerRef _publisher = new();

    [ObservableProperty]
    private int _viewerCount;

    [ObservableProperty]
    private long _createdAt;

    [ObservableProperty]
    private IReadOnlyDictionary<string, string>? _properties;

    [ObservableProperty]
    private bool _isSubscribed;

    [ObservableProperty]
    private bool _isSubscribing;

    [ObservableProperty]
    private string? _errorMessage;

    public StreamViewModel(TsspStream stream)
    {
        UpdateFromStream(stream);
    }

    public void UpdateFromStream(TsspStream stream)
    {
        StreamId = stream.StreamId;
        Name = stream.Name ?? string.Empty;
        Mode = stream.Mode;
        StreamType = stream.StreamType;
        Accessibility = stream.Accessibility;
        Publisher = stream.Publisher;
        ViewerCount = stream.ViewerCount;
        CreatedAt = stream.CreatedAt;
        Properties = stream.Properties;
    }

    public string PublisherName => Publisher.Nickname ?? Publisher.Uid ?? $"CLID {Publisher.Clid}";

    public string CreatedAtText => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt).ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public string ResolutionText
    {
        get
        {
            if (Properties is null)
            {
                return "未知";
            }

            var width = Properties.TryGetValue("width", out var w) && int.TryParse(w, out var wi) ? wi : 0;
            var height = Properties.TryGetValue("height", out var h) && int.TryParse(h, out var hi) ? hi : 0;
            var fps = Properties.TryGetValue("framerate", out var f) && int.TryParse(f, out var fi) ? fi : 0;
            var bitrate = Properties.TryGetValue("bitrate_kbps", out var b) && int.TryParse(b, out var bi) ? bi : 0;

            if (width > 0 && height > 0)
            {
                return $"{width}×{height} @ {fps}fps, {bitrate}kbps";
            }

            return "未知";
        }
    }
}

/// <summary>
/// View model for the stream viewer panel: lists available streams and manages subscriptions.
/// </summary>
public sealed partial class StreamViewerViewModel : ObservableObject, IDisposable
{
    private readonly TsConnection _connection;
    private readonly ScreenShareService _screenShareService;
    private readonly TsspClient _tssp;
    private readonly StreamSettings _settings;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<StreamViewerViewModel> _log;

    private readonly ObservableCollection<StreamViewModel> _streams = new();
    private readonly Dictionary<string, ScreenShareViewer> _viewers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _refreshCts = new();
    private bool _disposed;

    public StreamViewerViewModel(
        TsConnection connection,
        ScreenShareService screenShareService,
        TsspClient tssp,
        StreamSettings settings,
        IUiDispatcher ui,
        ILogger<StreamViewerViewModel> log)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(screenShareService);
        ArgumentNullException.ThrowIfNull(tssp);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(log);

        _connection = connection;
        _screenShareService = screenShareService;
        _tssp = tssp;
        _settings = settings;
        _ui = ui;
        _log = log;

        Streams = new ReadOnlyObservableCollection<StreamViewModel>(_streams);

        _tssp.StreamAdded += OnStreamAdded;
        _tssp.StreamUpdated += OnStreamUpdated;
        _tssp.StreamRemoved += OnStreamRemoved;

        _ = RefreshLoopAsync(_refreshCts.Token);
    }

    public ReadOnlyObservableCollection<StreamViewModel> Streams { get; }

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private string? _refreshError;

    [ObservableProperty]
    private StreamViewModel? _selectedStream;

    [ObservableProperty]
    private ScreenShareViewer? _activeViewer;

    [ObservableProperty]
    private byte[]? _currentFrame;

    [ObservableProperty]
    private bool _isFullscreen;

    partial void OnSelectedStreamChanged(StreamViewModel? value)
    {
        if (value is not null && !value.IsSubscribed)
        {
            _ = SubscribeAsync(value);
        }
    }

    partial void OnActiveViewerChanged(ScreenShareViewer? value)
    {
        if (value is not null)
        {
            value.FrameReady += OnFrameReady;
            value.StateChanged += OnViewerStateChanged;
            value.Faulted += OnViewerFaulted;
        }
    }

    private void OnFrameReady(object? sender, byte[]? frame)
    {
        CurrentFrame = frame;
    }

    private void OnViewerStateChanged(object? sender, ScreenShareState state)
    {
        _log.LogDebug("查看器状态变更：{State}", state);
    }

    private void OnViewerFaulted(object? sender, Exception ex)
    {
        _log.LogError(ex, "查看器发生错误");
        _ = _ui.InvokeAsync(() =>
        {
            if (SelectedStream is not null)
            {
                SelectedStream.ErrorMessage = ex.Message;
                SelectedStream.IsSubscribed = false;
                SelectedStream.IsSubscribing = false;
            }
            ActiveViewer = null;
        });
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!_connection.IsConnected || !_tssp.IsAuthenticated)
        {
            return;
        }

        IsRefreshing = true;
        RefreshError = null;

        try
        {
            var response = await _tssp.ListAsync(new TsspListRequest
            {
                Token = _tssp.Session?.SessionToken ?? string.Empty,
            }).ConfigureAwait(false);

            await _ui.InvokeAsync(() =>
            {
                var existing = _streams.ToDictionary(s => s.StreamId, StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var stream in response.Streams)
                {
                    seen.Add(stream.StreamId);
                    if (existing.TryGetValue(stream.StreamId, out var vm))
                    {
                        vm.UpdateFromStream(stream);
                    }
                    else
                    {
                        _streams.Add(new StreamViewModel(stream));
                    }
                }

                // Remove streams that no longer exist
                for (int i = _streams.Count - 1; i >= 0; i--)
                {
                    if (!seen.Contains(_streams[i].StreamId))
                    {
                        _streams.RemoveAt(i);
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "刷新流列表失败");
            RefreshError = ex.Message;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task SubscribeAsync(StreamViewModel stream)
    {
        if (stream.IsSubscribed || stream.IsSubscribing)
        {
            return;
        }

        stream.IsSubscribing = true;
        stream.ErrorMessage = null;

        try
        {
            var profile = new StreamMediaProfile
            {
                Codec = stream.Properties?.TryGetValue("codec", out var codecStr) == true
                    && Enum.TryParse<VideoCodec>(codecStr, true, out var codec) ? codec : VideoCodec.Vp8,
                Width = stream.Properties?.TryGetValue("width", out var w) == true && int.TryParse(w, out var wi) ? wi : 1920,
                Height = stream.Properties?.TryGetValue("height", out var h) == true && int.TryParse(h, out var hi) ? hi : 1080,
                FrameRate = stream.Properties?.TryGetValue("framerate", out var f) == true && int.TryParse(f, out var fi) ? fi : 30,
                BitrateKbps = stream.Properties?.TryGetValue("bitrate_kbps", out var b) == true && int.TryParse(b, out var bi) ? bi : 5000,
                HasAudio = stream.Properties?.TryGetValue("has_audio", out var a) == true && bool.TryParse(a, out var ai) && ai,
            };

            var viewer = new ScreenShareViewer(
                _log,
                _ui,
                _tssp,
                stream.StreamId,
                profile,
                _refreshCts.Token);

            await viewer.StartAsync(_settings.ModePreference == StreamModePreference.PreferP2P ? TsspModes.P2P : null).ConfigureAwait(false);

            _viewers[stream.StreamId] = viewer;

            await _ui.InvokeAsync(() =>
            {
                stream.IsSubscribed = true;
                stream.IsSubscribing = false;
                ActiveViewer = viewer;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "订阅流 {StreamId} 失败", stream.StreamId);
            await _ui.InvokeAsync(() =>
            {
                stream.IsSubscribing = false;
                stream.ErrorMessage = ex.Message;
            }).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task UnsubscribeAsync(StreamViewModel? stream)
    {
        if (stream is null || !stream.IsSubscribed)
        {
            return;
        }

        if (_viewers.TryGetValue(stream.StreamId, out var viewer))
        {
            await viewer.StopAsync().ConfigureAwait(false);
            await viewer.DisposeAsync().ConfigureAwait(false);
            _viewers.Remove(stream.StreamId);
        }

        await _ui.InvokeAsync(() =>
        {
            stream.IsSubscribed = false;
            if (ActiveViewer == viewer)
            {
                ActiveViewer = null;
                CurrentFrame = null;
            }
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        IsFullscreen = !IsFullscreen;
    }

    private void OnStreamAdded(object? sender, TsspStreamEvent e)
    {
        _ = _ui.InvokeAsync(() =>
        {
            var existing = _streams.FirstOrDefault(s => string.Equals(s.StreamId, e.Stream.StreamId, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.UpdateFromStream(e.Stream);
            }
            else
            {
                _streams.Add(new StreamViewModel(e.Stream));
            }
        });
    }

    private void OnStreamUpdated(object? sender, TsspStreamEvent e)
    {
        _ = _ui.InvokeAsync(() =>
        {
            var existing = _streams.FirstOrDefault(s => string.Equals(s.StreamId, e.Stream.StreamId, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.UpdateFromStream(e.Stream);
            }
        });
    }

    private void OnStreamRemoved(object? sender, TsspStreamRemovedEvent e)
    {
        _ = _ui.InvokeAsync(() =>
        {
            var existing = _streams.FirstOrDefault(s => string.Equals(s.StreamId, e.StreamId, StringComparison.Ordinal));
            if (existing is not null)
            {
                _streams.Remove(existing);
            }

            if (_viewers.TryGetValue(e.StreamId, out var viewer))
            {
                _ = viewer.StopAsync();
                _ = viewer.DisposeAsync();
                _viewers.Remove(e.StreamId);
            }

            if (SelectedStream?.StreamId == e.StreamId)
            {
                SelectedStream = null;
                ActiveViewer = null;
                CurrentFrame = null;
            }
        });
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested && _connection.IsConnected && _tssp.IsAuthenticated)
                {
                    await RefreshAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "流列表自动刷新出错");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _refreshCts.Cancel();
        _refreshCts.Dispose();

        _tssp.StreamAdded -= OnStreamAdded;
        _tssp.StreamUpdated -= OnStreamUpdated;
        _tssp.StreamRemoved -= OnStreamRemoved;

        foreach (var viewer in _viewers.Values)
        {
            _ = viewer.StopAsync();
            _ = viewer.DisposeAsync();
        }
        _viewers.Clear();
    }
}