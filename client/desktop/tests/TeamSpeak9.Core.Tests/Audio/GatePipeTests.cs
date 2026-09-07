// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using TeamSpeak9.Core.Audio;
using TeamSpeak9.Core.Settings;

namespace TeamSpeak9.Core.Tests.Audio;

public class GatePipeTests
{
    private const int SampleRate = AudioTestSignals.SampleRate;

    private static GatePipe Gate(out RecordingConsumer sink, double thresholdDb = -40.0, int hangoverMs = 0)
    {
        sink = new RecordingConsumer();
        return new GatePipe(new VoiceActivityDetector(SampleRate, thresholdDb, hangoverMs)) { OutStream = sink };
    }

    private static void Write(GatePipe gate, byte[] pcm) => gate.Write(pcm.AsSpan(), null);

    [Fact]
    public void RejectsAMissingDetector()
    {
        Assert.Throws<ArgumentNullException>(() => new GatePipe(null!));
    }

    [Fact]
    public void DefaultsToVoiceActivationAndClosed()
    {
        var gate = Gate(out _);

        Assert.Equal(PushToTalkMode.VoiceActivation, gate.Mode);
        Assert.False(gate.IsTransmitting);
        Assert.False(gate.HotkeyHeld);
        Assert.False(gate.Muted);
    }

    [Fact]
    public void MirrorsTheDownstreamActiveFlag()
    {
        var gate = Gate(out var sink);
        Assert.True(gate.Active);

        sink.Active = false;
        Assert.False(gate.Active);
    }

    [Fact]
    public void IsInactiveWithoutADownstream()
    {
        Assert.False(new GatePipe(new VoiceActivityDetector(SampleRate, -40.0, 0)).Active);
    }

    [Fact]
    public void WithoutADownstreamItNeitherThrowsNorMeasures()
    {
        var gate = new GatePipe(new VoiceActivityDetector(SampleRate, -40.0, 0)) { Mode = PushToTalkMode.Continuous };

        Write(gate, AudioTestSignals.Pcm(960, -6.0));

        Assert.False(gate.IsTransmitting);
        Assert.Equal(VoiceActivityDetector.SilenceDb, gate.LevelDb);
    }

    [Fact]
    public void AnEmptyBlockIsDropped()
    {
        var gate = Gate(out var sink);
        gate.Mode = PushToTalkMode.Continuous;

        Write(gate, []);

        Assert.Empty(sink.Writes);
        Assert.False(gate.IsTransmitting);
    }

    [Fact]
    public void ContinuousForwardsRegardlessOfLevel()
    {
        var gate = Gate(out var sink);
        gate.Mode = PushToTalkMode.Continuous;

        Write(gate, AudioTestSignals.SilentPcm(960));

        Assert.Single(sink.Writes);
        Assert.True(gate.IsTransmitting);
    }

    [Fact]
    public void PushToTalkForwardsOnlyWhileTheHotkeyIsHeld()
    {
        var gate = Gate(out var sink);
        gate.Mode = PushToTalkMode.PushToTalk;
        var loud = AudioTestSignals.Pcm(960, -6.0);

        Write(gate, loud);
        Assert.Empty(sink.Writes);

        gate.HotkeyHeld = true;
        Write(gate, loud);
        Assert.Single(sink.Writes);

        gate.HotkeyHeld = false;
        Write(gate, loud);
        Assert.Single(sink.Writes);
    }

    [Fact]
    public void PushToTalkIgnoresSilenceWhileHeldDown()
    {
        var gate = Gate(out var sink);
        gate.Mode = PushToTalkMode.PushToTalk;
        gate.HotkeyHeld = true;

        Write(gate, AudioTestSignals.SilentPcm(960));

        Assert.Single(sink.Writes);
    }

    [Fact]
    public void VoiceActivationFollowsTheLevel()
    {
        var gate = Gate(out var sink);

        Write(gate, AudioTestSignals.Pcm(960, -50.0));
        Assert.Empty(sink.Writes);

        Write(gate, AudioTestSignals.Pcm(960, -20.0));
        Assert.Single(sink.Writes);

        Write(gate, AudioTestSignals.Pcm(960, -50.0));
        Assert.Single(sink.Writes);
    }

    [Theory]
    [InlineData(PushToTalkMode.Continuous)]
    [InlineData(PushToTalkMode.PushToTalk)]
    [InlineData(PushToTalkMode.VoiceActivation)]
    public void MuteSuppressesEveryMode(PushToTalkMode mode)
    {
        var gate = Gate(out var sink);
        gate.Mode = mode;
        gate.HotkeyHeld = true;
        gate.Muted = true;

        Write(gate, AudioTestSignals.Pcm(960, -6.0));

        Assert.Empty(sink.Writes);
        Assert.False(gate.IsTransmitting);
    }

    [Fact]
    public void MutingMidSpeechClosesTheGate()
    {
        var gate = Gate(out var sink);
        gate.Mode = PushToTalkMode.Continuous;
        var loud = AudioTestSignals.Pcm(960, -6.0);
        Write(gate, loud);

        gate.Muted = true;
        Write(gate, loud);

        Assert.Single(sink.Writes);
        Assert.False(gate.IsTransmitting);
    }

    [Fact]
    public void TheLevelMeterKeepsUpdatingWhileMuted()
    {
        var gate = Gate(out _);
        gate.Muted = true;

        Write(gate, AudioTestSignals.Pcm(960, -20.0));

        Assert.Equal(AudioTestSignals.LevelDb(-20.0), gate.LevelDb, 6);
    }

    [Fact]
    public void TheLevelMeterKeepsUpdatingWhileTheGateIsClosed()
    {
        var gate = Gate(out _);

        Write(gate, AudioTestSignals.Pcm(960, -50.0));

        Assert.Equal(AudioTestSignals.LevelDb(-50.0), gate.LevelDb, 6);
    }

    [Fact]
    public void ForwardsTheOriginalBytesAndMeta()
    {
        var gate = Gate(out var sink);
        gate.Mode = PushToTalkMode.Continuous;
        var pcm = AudioTestSignals.Pcm(64, -12.0);
        var meta = new TSLib.Audio.Meta { Codec = TSLib.Codec.OpusVoice };

        gate.Write(pcm.AsSpan(), meta);

        Assert.Equal(pcm, sink.Writes[0]);
        Assert.Same(meta, sink.Metas[0]);
    }

    [Fact]
    public void RaisesTransmittingChangedOnlyOnTransitions()
    {
        var gate = Gate(out _);
        gate.Mode = PushToTalkMode.Continuous;
        var events = new List<bool>();
        gate.TransmittingChanged += events.Add;
        var pcm = AudioTestSignals.Pcm(960, -6.0);

        Write(gate, pcm);
        Write(gate, pcm);
        Write(gate, pcm);

        Assert.Equal([true], events);
    }

    [Fact]
    public void ReportsBothEdgesOfASpeechBurst()
    {
        var gate = Gate(out _);
        var events = new List<bool>();
        gate.TransmittingChanged += events.Add;

        Write(gate, AudioTestSignals.Pcm(960, -20.0));
        Write(gate, AudioTestSignals.Pcm(960, -50.0));

        Assert.Equal([true, false], events);
    }

    [Fact]
    public void ResetWhileTransmittingReportsTheClose()
    {
        var gate = Gate(out _);
        gate.Mode = PushToTalkMode.Continuous;
        Write(gate, AudioTestSignals.Pcm(960, -6.0));
        var events = new List<bool>();
        gate.TransmittingChanged += events.Add;

        gate.Reset();

        Assert.Equal([false], events);
        Assert.False(gate.IsTransmitting);
    }

    [Fact]
    public void ResetWhileClosedReportsNothing()
    {
        var gate = Gate(out _);
        var events = new List<bool>();
        gate.TransmittingChanged += events.Add;

        gate.Reset();

        Assert.Empty(events);
    }

    [Fact]
    public void ResetClearsTheLevelMeter()
    {
        var gate = Gate(out _);
        Write(gate, AudioTestSignals.Pcm(960, -20.0));

        gate.Reset();

        Assert.Equal(VoiceActivityDetector.SilenceDb, gate.LevelDb);
    }

    [Fact]
    public void TheHangoverKeepsTheGateOpenAcrossBlocks()
    {
        var gate = Gate(out var sink, hangoverMs: 300);
        Write(gate, AudioTestSignals.Pcm(4800, -20.0));

        Write(gate, AudioTestSignals.SilentPcm(4800));
        Write(gate, AudioTestSignals.SilentPcm(4800));

        Assert.Equal(3, sink.Writes.Count);
        Assert.True(gate.IsTransmitting);

        Write(gate, AudioTestSignals.SilentPcm(4800));

        Assert.Equal(3, sink.Writes.Count);
        Assert.False(gate.IsTransmitting);
    }
}
