// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System;
using TeamSpeak9.Core.Settings;
using TSLib.Audio;

namespace TeamSpeak9.Core.Audio;

/// <summary>
/// Decides whether captured audio is forwarded downstream, implementing the three
/// <see cref="PushToTalkMode"/> behaviours on top of a <see cref="VoiceActivityDetector"/>.
/// </summary>
/// <remarks>
/// The detector is fed on every write even while the gate is closed, so the UI level meter keeps
/// updating when the user is muted or not holding the push-to-talk key.
/// </remarks>
public sealed class GatePipe : IAudioPipe
{
    private readonly VoiceActivityDetector detector;
    private bool transmitting;

    public GatePipe(VoiceActivityDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        this.detector = detector;
    }

    public IAudioPassiveConsumer? OutStream { get; set; }

    public bool Active => OutStream?.Active ?? false;

    /// <summary>How the gate decides to open. Defaults to <see cref="PushToTalkMode.VoiceActivation"/>.</summary>
    public PushToTalkMode Mode { get; set; } = PushToTalkMode.VoiceActivation;

    /// <summary>Set by the hotkey hook while the push-to-talk key is held down.</summary>
    public bool HotkeyHeld { get; set; }

    /// <summary>Hard mute. Takes precedence over <see cref="Mode"/>.</summary>
    public bool Muted { get; set; }

    /// <summary>Most recent input level in dBFS, for the level meter.</summary>
    public double LevelDb => detector.LevelDb;

    /// <summary>Raised when the gate opens or closes, for the local "you are talking" indicator.</summary>
    public event Action<bool>? TransmittingChanged;

    /// <summary>Whether audio is currently being forwarded.</summary>
    public bool IsTransmitting => transmitting;

    public void Write(Span<byte> data, Meta? meta)
    {
        if (OutStream is null || data.IsEmpty)
            return;

        // Always measure, even when closed, so the level meter and hangover stay accurate.
        var voiceDetected = detector.ProcessPcm16(data);

        var open = Mode switch
        {
            PushToTalkMode.Continuous => true,
            PushToTalkMode.PushToTalk => HotkeyHeld,
            _ => voiceDetected,
        };

        if (Muted)
            open = false;

        if (open != transmitting)
        {
            transmitting = open;
            TransmittingChanged?.Invoke(open);
        }

        if (open)
            OutStream.Write(data, meta);
    }

    /// <summary>Drops any hangover state so a reconnect or device switch starts closed.</summary>
    public void Reset()
    {
        detector.Reset();
        if (transmitting)
        {
            transmitting = false;
            TransmittingChanged?.Invoke(false);
        }
    }
}
