// TeamSpeak9 - PC client
// WASAPI playback fed by a pump thread that drains TSLib's client mixdown.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TeamSpeak9.Core.Audio;
using TSLib.Audio;

namespace TeamSpeak9.App.Audio;

/// <summary>
/// The receive chain ends in <c>ClientMixdown</c>, which is a passive producer: somebody has to pull
/// from it. This sink owns that pump thread, applies the output volume in software, and hands the
/// result to WASAPI through a buffered provider.
/// </summary>
/// <remarks>
/// Volume is applied here rather than through <c>WasapiOut.Volume</c> on purpose: the latter writes
/// the per application level in the Windows volume mixer, which would outlive the session.
/// </remarks>
internal sealed class WasapiPlaybackSink : IAudioPlaybackSink
{
    private const int LatencyMs = 60;
    private const int TargetBufferMs = 80;
    private const int BacklogMs = 500;
    private const int IdleSleepMs = 5;
    private const int PumpBytes = 3840;

    private readonly ILogger log;
    private readonly object sync = new();
    private readonly MMDevice device;
    private readonly WasapiOut output;
    private readonly BufferedWaveProvider outgoing;
    private readonly byte[] pump = new byte[PumpBytes];
    private readonly TimeSpan targetBuffer = TimeSpan.FromMilliseconds(TargetBufferMs);
    private Thread? pumpThread;
    private volatile bool running;
    private volatile float volume = 1f;
    private bool disposed;

    internal WasapiPlaybackSink(MMDevice device, AudioDeviceInfo info, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(log);

        this.device = device;
        this.log = log;
        Device = info;

        outgoing = new BufferedWaveProvider(new WaveFormat(
            AudioPipeline.SampleRate,
            AudioPipeline.BitsPerSample,
            AudioPipeline.PlaybackChannels))
        {
            BufferDuration = TimeSpan.FromMilliseconds(BacklogMs),
            DiscardOnBufferOverflow = true,

            // Pad underruns with silence so a quiet channel never stalls the render client.
            ReadFully = true,
        };

        output = new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMs);
        output.PlaybackStopped += OnPlaybackStopped;
        output.Init(outgoing);
    }

    public AudioDeviceInfo Device { get; }

    public IAudioPassiveProducer? InStream { get; set; }

    public float Volume
    {
        get => volume;
        set => volume = Math.Clamp(value, 0f, 1f);
    }

    public void Start()
    {
        lock (sync)
        {
            if (disposed || running)
            {
                return;
            }

            running = true;
            outgoing.ClearBuffer();
            output.Play();

            pumpThread = new Thread(PumpLoop)
            {
                Name = "ts9-audio-playback",
                IsBackground = true,
            };
            pumpThread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (sync)
        {
            if (!running)
            {
                return;
            }

            running = false;
            thread = pumpThread;
            pumpThread = null;
        }

        thread?.Join(TimeSpan.FromSeconds(1));
        WasapiDevices.Quiet(output.Stop, log);
        outgoing.ClearBuffer();
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        Stop();
        output.PlaybackStopped -= OnPlaybackStopped;
        WasapiDevices.Quiet(output.Dispose, log);
        WasapiDevices.Quiet(device.Dispose, log);
    }

    private void PumpLoop()
    {
        while (running)
        {
            var source = InStream;
            if (source is null || outgoing.BufferedDuration > targetBuffer)
            {
                Thread.Sleep(IdleSleepMs);
                continue;
            }

            int read;
            try
            {
                read = source.Read(pump, 0, pump.Length, out _);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "读取混音数据失败，播放泵线程退出。");
                return;
            }

            if (read <= 0)
            {
                Thread.Sleep(IdleSleepMs);
                continue;
            }

            var level = volume;
            if (level < 1f)
            {
                ApplyVolume(pump.AsSpan(0, read), level);
            }

            outgoing.AddSamples(pump, 0, read);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            log.LogError(e.Exception, "播放设备 {Device} 意外停止。", Device.Name);
        }
    }

    private static void ApplyVolume(Span<byte> pcm, float factor)
    {
        var samples = MemoryMarshal.Cast<byte, short>(pcm[..(pcm.Length & ~1)]);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)Math.Clamp((int)(samples[i] * factor), short.MinValue, short.MaxValue);
        }
    }
}
