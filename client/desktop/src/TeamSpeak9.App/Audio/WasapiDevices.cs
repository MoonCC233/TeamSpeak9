// TeamSpeak9 - PC client
// Shared helpers that turn persisted device ids into live WASAPI endpoints.

using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using TeamSpeak9.Core.Audio;

namespace TeamSpeak9.App.Audio;

internal static class WasapiDevices
{
    internal static DataFlow FlowFor(AudioDeviceKind kind)
        => kind == AudioDeviceKind.Input ? DataFlow.Capture : DataFlow.Render;

    internal static string? TryGetDefaultId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            return device.ID;
        }
        catch (COMException)
        {
            // No endpoint of this kind exists at all, which is a valid machine state.
            return null;
        }
    }

    /// <summary>
    /// Opens the endpoint behind a persisted id. An empty id means "follow Windows", which is
    /// resolved once when the session starts; later default device changes are not tracked.
    /// </summary>
    internal static (MMDevice Device, AudioDeviceInfo Info) Open(string? deviceId, AudioDeviceKind kind, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var flow = FlowFor(kind);
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = TryGetDefaultId(enumerator, flow);
        MMDevice? device = null;

        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                device = enumerator.GetDevice(deviceId);
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                log.LogWarning(ex, "音频设备 {DeviceId} 不可用，回退到系统默认设备。", deviceId);
            }
        }

        if (device is null)
        {
            if (defaultId is null)
            {
                throw new InvalidOperationException(kind == AudioDeviceKind.Input
                    ? "系统没有可用的录音设备。"
                    : "系统没有可用的播放设备。");
            }

            device = enumerator.GetDevice(defaultId);
        }

        var info = new AudioDeviceInfo(
            device.ID,
            device.FriendlyName,
            kind,
            string.Equals(device.ID, defaultId, StringComparison.Ordinal));
        return (device, info);
    }

    /// <summary>Runs a teardown step that must never take the caller down with it.</summary>
    internal static void Quiet(Action action, ILogger log)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "关闭音频设备时忽略了一个异常。");
        }
    }
}
