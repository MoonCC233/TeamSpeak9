// TeamSpeak9 - PC client
// Ties the platform neutral AudioPipeline to the connection, the settings store and the UI.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TeamSpeak9.Core.Audio;
using TeamSpeak9.Core.Connection;
using TeamSpeak9.Core.Settings;
using TeamSpeak9.Core.Threading;
using TSLib.Full;

namespace TeamSpeak9.App.Audio;

/// <summary>
/// Owns the voice lifecycle: attaches <see cref="AudioPipeline"/> when a session becomes usable,
/// detaches it when the session ends, and exposes device selection to the view models.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place that bridges the two threading worlds involved. <see cref="TsConnection"/>
/// raises its ready and closing notifications on the scheduler thread, and the pipeline raises
/// <see cref="AudioPipeline.TransmittingChanged"/> on the audio tick thread; both are re-posted to
/// the UI thread before anything bound can observe them.
/// </para>
/// <para>
/// Resolved eagerly at startup rather than on first use: the constructor is what subscribes to the
/// connection, so a lazily resolved instance would miss the notification for a session that was
/// already established.
/// </para>
/// </remarks>
public sealed class AudioService : IDisposable
{
    private readonly TsConnection connection;
    private readonly AudioPipeline pipeline;
    private readonly IAudioDeviceEnumerator devices;
    private readonly AppSettings settings;
    private readonly SettingsStore settingsStore;
    private readonly IUiDispatcher ui;
    private readonly ILogger<AudioService> log;
    private readonly object sync = new();

    private IReadOnlyList<AudioDeviceInfo>? inputDevices;
    private IReadOnlyList<AudioDeviceInfo>? outputDevices;
    private bool disposed;

    public AudioService(
        TsConnection connection,
        AudioPipeline pipeline,
        IAudioDeviceEnumerator devices,
        AppSettings settings,
        SettingsStore settingsStore,
        IUiDispatcher ui,
        ILogger<AudioService> log)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(log);

        this.connection = connection;
        this.pipeline = pipeline;
        this.devices = devices;
        this.settings = settings;
        this.settingsStore = settingsStore;
        this.ui = ui;
        this.log = log;

        connection.ClientReady += OnClientReady;
        connection.ClientClosing += OnClientClosing;
        pipeline.TransmittingChanged += OnPipelineTransmittingChanged;
    }

    /// <summary>
    /// Raised on the UI thread when the send gate opens or closes, so the top bar can light up.
    /// </summary>
    public event Action<bool>? TransmittingChanged;

    /// <summary>Raised on the UI thread after the device lists were re-enumerated.</summary>
    public event Action? DevicesChanged;

    /// <summary>Capture endpoints, starting with the synthetic "system default" entry.</summary>
    public IReadOnlyList<AudioDeviceInfo> InputDevices => Cached(AudioDeviceKind.Input);

    /// <summary>Render endpoints, starting with the synthetic "system default" entry.</summary>
    public IReadOnlyList<AudioDeviceInfo> OutputDevices => Cached(AudioDeviceKind.Output);

    /// <summary>Id of the selected capture device; empty means follow the Windows default.</summary>
    public string SelectedInputDeviceId => settings.Audio.InputDeviceId;

    /// <summary>Id of the selected render device; empty means follow the Windows default.</summary>
    public string SelectedOutputDeviceId => settings.Audio.OutputDeviceId;

    /// <summary>Whether a voice session is currently wired up.</summary>
    public bool IsAttached => pipeline.IsAttached;

    /// <summary>Whether the send gate is currently passing audio.</summary>
    public bool IsTransmitting => pipeline.IsTransmitting;

    /// <summary>Current input level in dBFS, for a level meter.</summary>
    public double LevelDb => pipeline.LevelDb;

    /// <summary>Push-to-talk key state, forwarded to the gate.</summary>
    public bool HotkeyHeld
    {
        get => pipeline.HotkeyHeld;
        set => pipeline.HotkeyHeld = value;
    }

    /// <summary>
    /// Drops the cached device lists so the next read re-enumerates. Raises
    /// <see cref="DevicesChanged"/> once the new lists are available.
    /// </summary>
    public void RefreshDevices()
    {
        lock (sync)
        {
            inputDevices = null;
            outputDevices = null;
        }

        RaiseDevicesChanged();
    }

    /// <summary>
    /// Selects a capture device and rebuilds the send chain. A no-op when the id is unchanged.
    /// </summary>
    /// <param name="deviceId">
    /// An id from <see cref="InputDevices"/>, or <see cref="AudioDeviceInfo.SystemDefaultId"/> to
    /// follow the Windows default.
    /// </param>
    public void SelectInputDevice(string deviceId)
        => Select(deviceId, isInput: true);

    /// <summary>Selects a render device and rebuilds the receive chain.</summary>
    public void SelectOutputDevice(string deviceId)
        => Select(deviceId, isInput: false);

    /// <summary>
    /// Pushes the current <see cref="AudioSettings"/> into the live chains. Callers that changed
    /// settings themselves (mute toggles, volume sliders) use this instead of the device setters.
    /// </summary>
    public void ApplySettings() => pipeline.ApplySettings();

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;

            disposed = true;
        }

        connection.ClientReady -= OnClientReady;
        connection.ClientClosing -= OnClientClosing;
        pipeline.TransmittingChanged -= OnPipelineTransmittingChanged;

        // Also disposed by the container, but the pipeline is idempotent and this makes the
        // ordering explicit: no more notifications can arrive once we are gone.
        pipeline.Dispose();
    }

    private void Select(string deviceId, bool isInput)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var audio = settings.Audio;
        string current = isInput ? audio.InputDeviceId : audio.OutputDeviceId;
        if (string.Equals(current, deviceId, StringComparison.Ordinal))
            return;

        if (isInput)
            audio.InputDeviceId = deviceId;
        else
            audio.OutputDeviceId = deviceId;

        // Rebuild first so the change is audible immediately; persisting is best effort.
        pipeline.ApplySettings();
        _ = PersistAsync();
    }

    private async Task PersistAsync()
    {
        try
        {
            await settingsStore.SaveAsync(settings).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "保存音频设备设置失败。");
        }
    }

    private IReadOnlyList<AudioDeviceInfo> Cached(AudioDeviceKind kind)
    {
        lock (sync)
        {
            var cached = kind == AudioDeviceKind.Input ? inputDevices : outputDevices;
            if (cached is not null)
                return cached;
        }

        // Enumeration is a COM round trip, so it runs outside the lock. A concurrent second call
        // may duplicate the work, which is cheaper than holding the lock across it.
        var listed = devices.ListDevices(kind);

        lock (sync)
        {
            if (kind == AudioDeviceKind.Input)
                return inputDevices ??= listed;

            return outputDevices ??= listed;
        }
    }

    /// <remarks>Runs on the scheduler thread.</remarks>
    private void OnClientReady(TsFullClient client) => pipeline.Attach(client);

    /// <remarks>Runs on the scheduler thread.</remarks>
    private void OnClientClosing(TsFullClient client) => pipeline.Detach();

    /// <remarks>Runs on the audio tick thread.</remarks>
    private void OnPipelineTransmittingChanged(bool value)
    {
        if (TransmittingChanged is null)
            return;

        ui.Post(() => TransmittingChanged?.Invoke(value));
    }

    private void RaiseDevicesChanged()
    {
        if (DevicesChanged is null)
            return;

        if (ui.IsOnUiThread)
            DevicesChanged.Invoke();
        else
            ui.Post(() => DevicesChanged?.Invoke());
    }
}
