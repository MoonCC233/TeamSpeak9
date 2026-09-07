// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

namespace TeamSpeak9.Core.Audio;

/// <summary>
/// Level based voice activation with a hangover, matching the
/// "语音激活" transmit mode.
/// </summary>
/// <remarks>
/// <para>
/// Each block of samples is reduced to an RMS level in dBFS and compared against a threshold.
/// Crossing the threshold opens the gate; falling back below it keeps the gate open for
/// <see cref="HangoverMilliseconds"/> so that the natural gaps between words do not chop
/// speech into fragments.
/// </para>
/// <para>
/// The hangover is counted in <b>samples</b> rather than wall clock time. The capture chain hands
/// over fixed size blocks at a fixed rate, so sample counting gives the same answer while being
/// deterministic - which is what makes this class unit testable.
/// </para>
/// <para>
/// Not thread safe: one instance belongs to one capture chain and is only touched from
/// <see cref="TSLib.Audio.PreciseTimedPipe"/>'s ticker thread.
/// </para>
/// </remarks>
public sealed class VoiceActivityDetector
{
    /// <summary>Level reported for a fully silent block, standing in for negative infinity.</summary>
    public const double SilenceDb = -160.0;

    /// <summary>Full scale for signed 16-bit samples.</summary>
    private const double FullScale = 32768.0;

    private readonly int sampleRate;
    private int hangoverSamples;
    private int remainingHangoverSamples;

    /// <param name="sampleRate">Sample rate of the blocks handed to <see cref="Process"/>, in Hz.</param>
    /// <param name="thresholdDb">Activation threshold in dBFS; <c>-40</c> is the settings default.</param>
    /// <param name="hangoverMilliseconds">How long the gate stays open after the level drops.</param>
    public VoiceActivityDetector(int sampleRate, double thresholdDb, int hangoverMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

        this.sampleRate = sampleRate;
        ThresholdDb = thresholdDb;
        HangoverMilliseconds = hangoverMilliseconds;
    }

    /// <summary>Activation threshold in dBFS. Values above <c>0</c> never open the gate.</summary>
    public double ThresholdDb { get; set; }

    private int hangoverMilliseconds;

    /// <summary>
    /// How long the gate stays open after the level falls back below the threshold, in milliseconds.
    /// Negative values are clamped to zero.
    /// </summary>
    public int HangoverMilliseconds
    {
        get => hangoverMilliseconds;
        set
        {
            hangoverMilliseconds = Math.Max(0, value);

            // Converted eagerly because Process runs on the audio thread, where a division per
            // block is worth avoiding.
            hangoverSamples = (int)((long)sampleRate * hangoverMilliseconds / 1000);
            remainingHangoverSamples = Math.Min(remainingHangoverSamples, hangoverSamples);
        }
    }

    /// <summary>Level of the most recent block in dBFS, for a level meter.</summary>
    public double LevelDb { get; private set; } = SilenceDb;

    /// <summary>Whether the gate is currently open, without processing anything new.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>
    /// Feeds one block of mono or interleaved samples and reports whether the gate is open.
    /// </summary>
    /// <returns>True while audio should be transmitted.</returns>
    public bool Process(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
            return IsOpen;

        LevelDb = MeasureDb(samples);

        if (LevelDb >= ThresholdDb)
        {
            remainingHangoverSamples = hangoverSamples;
            IsOpen = true;
            return true;
        }

        if (remainingHangoverSamples > 0)
        {
            remainingHangoverSamples = Math.Max(0, remainingHangoverSamples - samples.Length);
            IsOpen = remainingHangoverSamples > 0;
            return IsOpen;
        }

        IsOpen = false;
        return false;
    }

    /// <summary>
    /// Feeds one block of little-endian 16-bit PCM, the layout the audio pipes move around.
    /// A trailing odd byte is ignored.
    /// </summary>
    public bool ProcessPcm16(ReadOnlySpan<byte> pcm) => Process(AsSamples(pcm));

    /// <summary>Closes the gate and forgets the hangover, e.g. after a device switch.</summary>
    public void Reset()
    {
        remainingHangoverSamples = 0;
        IsOpen = false;
        LevelDb = SilenceDb;
    }

    /// <summary>
    /// RMS level of a block in dBFS, clamped at <see cref="SilenceDb"/> for digital silence.
    /// </summary>
    public static double MeasureDb(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
            return SilenceDb;

        double sumOfSquares = 0;
        foreach (short sample in samples)
        {
            double normalized = sample / FullScale;
            sumOfSquares += normalized * normalized;
        }

        double rms = Math.Sqrt(sumOfSquares / samples.Length);
        if (rms <= 0)
            return SilenceDb;

        return Math.Max(SilenceDb, 20.0 * Math.Log10(rms));
    }

    /// <summary>
    /// Reinterprets a little-endian 16-bit PCM buffer as samples. A trailing odd byte is dropped.
    /// </summary>
    /// <remarks>
    /// Correct only on little-endian hosts, which is every platform this client targets.
    /// </remarks>
    internal static ReadOnlySpan<short> AsSamples(ReadOnlySpan<byte> pcm) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm[..(pcm.Length & ~1)]);
}
