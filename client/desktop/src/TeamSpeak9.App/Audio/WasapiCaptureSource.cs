// TeamSpeak9 - PC client
// WASAPI capture adapted to the 48 kHz / mono / PCM16 stream that TSLib's Opus encoder expects.

using System;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TeamSpeak9.Core.Audio;
using TSLib.Audio;

namespace TeamSpeak9.App.Audio;

/// <summary>
/// Pulls microphone data through a shared mode WASAPI client and converts whatever the endpoint
/// mix format happens to be (usually 32 bit float stereo) into the mono PCM16 the send chain wants.
/// </summary>
/// <remarks>
/// <see cref="Read"/> is called from the <c>PreciseTimedPipe</c> tick thread and must never block:
/// the pipe treats a zero length read as "nothing captured this tick" and simply waits.
/// </remarks>
internal sealed class WasapiCaptureSource : IAudioCaptureSource
{
    private const int CaptureBufferMs = 50;
    private const int BacklogMs = 400;

    private readonly ILogger log;
    private readonly object sync = new();
    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly BufferedWaveProvider incoming;
    private readonly IWaveProvider reader;
    private volatile bool running;
    private bool disposed;

    internal WasapiCaptureSource(MMDevice device, AudioDeviceInfo info, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(log);

        this.device = device;
        this.log = log;
        Device = info;

        capture = new WasapiCapture(device, true, CaptureBufferMs);
        var native = capture.WaveFormat is WaveFormatExtensible extensible
            ? extensible.ToStandardWaveFormat()
            : capture.WaveFormat;

        incoming = new BufferedWaveProvider(native)
        {
            BufferDuration = TimeSpan.FromMilliseconds(BacklogMs),
            DiscardOnBufferOverflow = true,

            // Must stay false: padding with silence would defeat the "0 means idle" contract.
            ReadFully = false,
        };

        ISampleProvider samples = incoming.ToSampleProvider();
        if (samples.WaveFormat.Channels > AudioPipeline.CaptureChannels)
        {
            samples = new MonoDownmixSampleProvider(samples);
        }

        if (samples.WaveFormat.SampleRate != AudioPipeline.SampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, AudioPipeline.SampleRate);
        }

        reader = new SampleToWaveProvider16(samples);

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
    }

    public AudioDeviceInfo Device { get; }

    public void Start()
    {
        lock (sync)
        {
            if (disposed || running)
            {
                return;
            }

            running = true;
            capture.StartRecording();
        }
    }

    public void Stop()
    {
        lock (sync)
        {
            if (!running)
            {
                return;
            }

            running = false;
            capture.StopRecording();
        }

        incoming.ClearBuffer();
    }

    public int Read(byte[] buffer, int offset, int length, out Meta? meta)
    {
        // Capture carries no metadata; StaticMetaPipe stamps the outgoing frames further downstream.
        meta = null;
        return running ? reader.Read(buffer, offset, length) : 0;
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
            running = false;
        }

        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;

        // Dispose joins the capture thread, so it has to run outside our lock.
        WasapiDevices.Quiet(capture.StopRecording, log);
        WasapiDevices.Quiet(capture.Dispose, log);
        WasapiDevices.Quiet(device.Dispose, log);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!running || e.BytesRecorded <= 0)
        {
            return;
        }

        // BufferedWaveProvider locks internally, so this stays off our own lock and cannot deadlock
        // against Dispose joining the capture thread.
        incoming.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            log.LogError(e.Exception, "录音设备 {Device} 意外停止。", Device.Name);
        }
    }

    /// <summary>
    /// Averages every channel into one. NAudio only ships a stereo specific downmix, and endpoint
    /// mix formats can legitimately report more than two channels.
    /// </summary>
    private sealed class MonoDownmixSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly int channels;
        private float[] scratch = Array.Empty<float>();

        internal MonoDownmixSampleProvider(ISampleProvider source)
        {
            this.source = source;
            channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var needed = count * channels;
            if (scratch.Length < needed)
            {
                scratch = new float[needed];
            }

            var read = source.Read(scratch, 0, needed);
            var frames = read / channels;
            var scale = 1f / channels;

            for (var frame = 0; frame < frames; frame++)
            {
                var start = frame * channels;
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += scratch[start + channel];
                }

                buffer[offset + frame] = sum * scale;
            }

            return frames;
        }
    }
}
