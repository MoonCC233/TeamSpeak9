// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using TeamSpeak9.Core.Audio;

namespace TeamSpeak9.Core.Tests.Audio;

public class VoiceActivityDetectorTests
{
    private const int SampleRate = AudioTestSignals.SampleRate;

    private static VoiceActivityDetector Detector(double thresholdDb = -40.0, int hangoverMs = 300) =>
        new(SampleRate, thresholdDb, hangoverMs);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsANonPositiveSampleRate(int sampleRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoiceActivityDetector(sampleRate, -40.0, 300));
    }

    [Fact]
    public void StartsClosedAndSilent()
    {
        var detector = Detector();

        Assert.False(detector.IsOpen);
        Assert.Equal(VoiceActivityDetector.SilenceDb, detector.LevelDb);
    }

    [Fact]
    public void DigitalSilenceMeasuresAsTheSilenceFloor()
    {
        Assert.Equal(VoiceActivityDetector.SilenceDb, VoiceActivityDetector.MeasureDb(AudioTestSignals.Silence(480)));
    }

    [Fact]
    public void AnEmptyBlockMeasuresAsTheSilenceFloor()
    {
        Assert.Equal(VoiceActivityDetector.SilenceDb, VoiceActivityDetector.MeasureDb(ReadOnlySpan<short>.Empty));
    }

    [Fact]
    public void FullScaleMeasuresAsZeroDbfs()
    {
        var block = new short[480];
        Array.Fill(block, short.MaxValue);

        // short.MaxValue is one LSB short of full scale, hence the tolerance rather than an exact 0.
        Assert.Equal(0.0, VoiceActivityDetector.MeasureDb(block), 3);
    }

    [Theory]
    [InlineData(-6.0)]
    [InlineData(-20.0)]
    [InlineData(-40.0)]
    [InlineData(-60.0)]
    public void MeasuresTheLevelOfAGeneratedBlock(double levelDb)
    {
        Assert.Equal(
            AudioTestSignals.LevelDb(levelDb),
            VoiceActivityDetector.MeasureDb(AudioTestSignals.Block(960, levelDb)),
            6);
    }

    [Fact]
    public void OpensWhenTheLevelReachesTheThreshold()
    {
        var detector = Detector(thresholdDb: -40.0);

        Assert.True(detector.Process(AudioTestSignals.Block(960, -30.0)));
        Assert.True(detector.IsOpen);
    }

    [Fact]
    public void StaysClosedBelowTheThreshold()
    {
        var detector = Detector(thresholdDb: -40.0);

        Assert.False(detector.Process(AudioTestSignals.Block(960, -50.0)));
        Assert.False(detector.IsOpen);
    }

    [Fact]
    public void AThresholdAboveFullScaleNeverOpens()
    {
        var detector = Detector(thresholdDb: 1.0);

        Assert.False(detector.Process(AudioTestSignals.Block(960, 0.0)));
    }

    [Fact]
    public void TracksTheLevelEvenWhileClosed()
    {
        var detector = Detector(thresholdDb: -40.0);

        detector.Process(AudioTestSignals.Block(960, -50.0));

        Assert.False(detector.IsOpen);
        Assert.Equal(AudioTestSignals.LevelDb(-50.0), detector.LevelDb, 6);
    }

    [Fact]
    public void HoldsTheGateOpenForTheHangoverThenCloses()
    {
        // 300 ms at 48 kHz is 14400 samples, so three 4800-sample blocks drain it exactly.
        var detector = Detector(thresholdDb: -40.0, hangoverMs: 300);
        detector.Process(AudioTestSignals.Block(4800, -20.0));

        Assert.True(detector.Process(AudioTestSignals.Silence(4800)));
        Assert.True(detector.Process(AudioTestSignals.Silence(4800)));
        Assert.False(detector.Process(AudioTestSignals.Silence(4800)));
    }

    [Fact]
    public void ZeroHangoverClosesOnTheNextBlock()
    {
        var detector = Detector(thresholdDb: -40.0, hangoverMs: 0);
        detector.Process(AudioTestSignals.Block(4800, -20.0));

        Assert.False(detector.Process(AudioTestSignals.Silence(4800)));
    }

    [Fact]
    public void ClampsANegativeHangoverToZero()
    {
        var detector = Detector(hangoverMs: -5);

        Assert.Equal(0, detector.HangoverMilliseconds);
    }

    [Fact]
    public void ShorteningTheHangoverTrimsTheRemainingHold()
    {
        var detector = Detector(thresholdDb: -40.0, hangoverMs: 300);
        detector.Process(AudioTestSignals.Block(4800, -20.0));

        detector.HangoverMilliseconds = 0;

        Assert.False(detector.Process(AudioTestSignals.Silence(4800)));
    }

    [Fact]
    public void LoweringTheThresholdMidStreamTakesEffectImmediately()
    {
        var detector = Detector(thresholdDb: -20.0, hangoverMs: 0);
        Assert.False(detector.Process(AudioTestSignals.Block(960, -30.0)));

        detector.ThresholdDb = -40.0;

        Assert.True(detector.Process(AudioTestSignals.Block(960, -30.0)));
    }

    [Fact]
    public void AnEmptyBlockLeavesTheStateAlone()
    {
        var detector = Detector(thresholdDb: -40.0);
        detector.Process(AudioTestSignals.Block(960, -20.0));
        var level = detector.LevelDb;

        Assert.True(detector.Process(ReadOnlySpan<short>.Empty));
        Assert.True(detector.IsOpen);
        Assert.Equal(level, detector.LevelDb);
    }

    [Fact]
    public void ResetClosesTheGateAndForgetsTheLevel()
    {
        var detector = Detector(thresholdDb: -40.0);
        detector.Process(AudioTestSignals.Block(960, -20.0));

        detector.Reset();

        Assert.False(detector.IsOpen);
        Assert.Equal(VoiceActivityDetector.SilenceDb, detector.LevelDb);
    }

    [Fact]
    public void ResetDropsTheHangoverSoTheNextSilentBlockStaysClosed()
    {
        var detector = Detector(thresholdDb: -40.0, hangoverMs: 300);
        detector.Process(AudioTestSignals.Block(4800, -20.0));

        detector.Reset();

        Assert.False(detector.Process(AudioTestSignals.Silence(4800)));
    }

    [Fact]
    public void ProcessPcm16MatchesProcess()
    {
        var detector = Detector(thresholdDb: -40.0);

        Assert.True(detector.ProcessPcm16(AudioTestSignals.Pcm(960, -20.0)));
        Assert.Equal(AudioTestSignals.LevelDb(-20.0), detector.LevelDb, 6);
    }

    [Fact]
    public void ProcessPcm16IgnoresATrailingOddByte()
    {
        var detector = Detector(thresholdDb: -40.0);
        var pcm = AudioTestSignals.Pcm(960, -20.0);
        var odd = new byte[pcm.Length + 1];
        pcm.CopyTo(odd, 0);

        Assert.True(detector.ProcessPcm16(odd));
        Assert.Equal(AudioTestSignals.LevelDb(-20.0), detector.LevelDb, 6);
    }

    [Fact]
    public void ProcessPcm16OfASingleByteIsATreatedAsEmpty()
    {
        var detector = Detector();

        Assert.False(detector.ProcessPcm16(new byte[1]));
        Assert.Equal(VoiceActivityDetector.SilenceDb, detector.LevelDb);
    }

    [Fact]
    public void AsSamplesDropsTheTrailingOddByte()
    {
        Assert.Equal(2, VoiceActivityDetector.AsSamples(new byte[5]).Length);
    }
}
