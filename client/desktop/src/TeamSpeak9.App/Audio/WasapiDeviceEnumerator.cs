// TeamSpeak9 - PC client
// Device listing for the top bar menus and the audio settings page.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using TeamSpeak9.Core.Audio;

namespace TeamSpeak9.App.Audio;

/// <summary>
/// Lists active capture and render endpoints. The first entry is always the synthetic
/// "system default" pseudo device so the UI can offer "follow Windows" like the official client.
/// </summary>
internal sealed class WasapiDeviceEnumerator : IAudioDeviceEnumerator
{
    private readonly ILogger<WasapiDeviceEnumerator> log;

    public WasapiDeviceEnumerator(ILogger<WasapiDeviceEnumerator> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        this.log = log;
    }

    public IReadOnlyList<AudioDeviceInfo> ListDevices(AudioDeviceKind kind)
    {
        var devices = new List<AudioDeviceInfo> { AudioDeviceInfo.SystemDefault(kind) };
        var flow = WasapiDevices.FlowFor(kind);

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = WasapiDevices.TryGetDefaultId(enumerator, flow);

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(new AudioDeviceInfo(
                        device.ID,
                        device.FriendlyName,
                        kind,
                        string.Equals(device.ID, defaultId, StringComparison.Ordinal)));
                }
            }
        }
        catch (COMException ex)
        {
            log.LogWarning(ex, "枚举音频设备失败，只返回系统默认设备。");
        }

        return devices;
    }
}
