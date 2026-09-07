// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using TeamSpeak9.Core.Audio;
using TSLib.Audio;

namespace TeamSpeak9.Core.Tests.Audio;

/// <summary>
/// Builds PCM blocks with a known RMS level so the gate and detector tests can talk in dBFS
/// instead of raw sample values.
/// </summary>
internal static class AudioTestSignals
{
    /// <summary>Sample rate every audio test uses, matching the pipeline.</summary>
    public const int SampleRate = 48000;

    private const double FullScale = 32768.0;

    /// <summary>
    /// A block of alternating +/- samples whose RMS lands on <paramref name="levelDb"/> dBFS.
    /// Alternating rather than constant keeps the block free of DC offset.
    /// </summary>
    public static short[] Block(int sampleCount, double levelDb)
    {
        var value = Amplitude(levelDb);

        var block = new short[sampleCount];
        for (var i = 0; i < sampleCount; i++)
            block[i] = i % 2 == 0 ? value : (short)-value;

        return block;
    }

    /// <summary>
    /// The level <see cref="Block"/> actually reaches, which is the requested level rounded to the
    /// nearest whole sample value. At -50 dBFS that rounding is worth ~0.03 dB, enough to fail an
    /// assertion against the nominal figure, so tests compare against this instead.
    /// </summary>
    public static double LevelDb(double levelDb)
    {
        var value = Amplitude(levelDb);
        return value == 0 ? VoiceActivityDetector.SilenceDb : 20.0 * Math.Log10(value / FullScale);
    }

    private static short Amplitude(double levelDb) => levelDb <= -300.0
        ? (short)0
        : (short)Math.Clamp(Math.Round(Math.Pow(10.0, levelDb / 20.0) * FullScale), 0, short.MaxValue);

    /// <summary>A digitally silent block.</summary>
    public static short[] Silence(int sampleCount) => new short[sampleCount];

    /// <summary>The little-endian byte layout the audio pipes actually move around.</summary>
    public static byte[] Pcm(int sampleCount, double levelDb) => ToBytes(Block(sampleCount, levelDb));

    /// <summary>The byte layout of a digitally silent block.</summary>
    public static byte[] SilentPcm(int sampleCount) => new byte[sampleCount * 2];

    public static byte[] ToBytes(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}

/// <summary>
/// An <see cref="IAudioPassiveConsumer"/> that keeps every write, standing in for whatever the
/// gate is wired to.
/// </summary>
internal sealed class RecordingConsumer : IAudioPassiveConsumer
{
    /// <summary>Mutable so a test can check that <c>Active</c> is mirrored rather than hard-coded.</summary>
    public bool Active { get; set; } = true;

    public List<byte[]> Writes { get; } = [];

    public List<Meta?> Metas { get; } = [];

    public void Write(Span<byte> data, Meta? meta)
    {
        Writes.Add(data.ToArray());
        Metas.Add(meta);
    }
}
