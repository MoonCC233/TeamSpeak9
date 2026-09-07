// TeamSpeak9 - PC client
// The seam that keeps the Windows only WASAPI stack out of TeamSpeak9.Core.

using System;
using Microsoft.Extensions.Logging;
using TeamSpeak9.Core.Audio;

namespace TeamSpeak9.App.Audio;

internal sealed class WasapiDeviceFactory : IAudioDeviceFactory
{
    private readonly ILogger<WasapiDeviceFactory> log;

    public WasapiDeviceFactory(ILogger<WasapiDeviceFactory> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        this.log = log;
    }

    public IAudioCaptureSource CreateCapture(string deviceId)
    {
        var (device, info) = WasapiDevices.Open(deviceId, AudioDeviceKind.Input, log);
        try
        {
            return new WasapiCaptureSource(device, info, log);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    public IAudioPlaybackSink CreatePlayback(string deviceId)
    {
        var (device, info) = WasapiDevices.Open(deviceId, AudioDeviceKind.Output, log);
        try
        {
            return new WasapiPlaybackSink(device, info, log);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }
}
