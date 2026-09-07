// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using TSLib.Audio;

namespace TeamSpeak9.Core.Audio;

/// <summary>
/// Enumerates the machine's capture and render endpoints.
/// </summary>
/// <remarks>
/// Implemented in TeamSpeak9.App on top of WASAPI; this abstraction keeps
/// <see cref="AudioPipeline"/> and its tests free of a Windows-only dependency.
/// </remarks>
public interface IAudioDeviceEnumerator
{
    /// <summary>Capture endpoints, system default first.</summary>
    IReadOnlyList<AudioDeviceInfo> ListDevices(AudioDeviceKind kind);
}

/// <summary>
/// Microphone side of the pipeline: 48 kHz, mono, 16-bit PCM pulled by
/// <see cref="TSLib.Audio.PreciseTimedPipe"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IAudioPassiveProducer.Read"/> is called from the pipe's ticker thread every
/// few milliseconds and <b>must not block</b>: returning <c>0</c> when no samples are buffered
/// is the documented way to say "nothing yet", and <c>PreciseTimedPipe.ReadTick</c> bails out
/// of its inner loop on a zero read.
/// </para>
/// <para>
/// The pipeline never reopens a source. Switching devices disposes the old instance and builds
/// a new one, so <see cref="Start"/> is expected to be called at most once per instance.
/// </para>
/// </remarks>
public interface IAudioCaptureSource : IAudioPassiveProducer
{
    /// <summary>Endpoint this source is bound to, for logging and UI feedback.</summary>
    AudioDeviceInfo Device { get; }

    /// <summary>Opens the endpoint and begins buffering. Safe to call twice; the second call is a no-op.</summary>
    void Start();

    /// <summary>Stops buffering and drops anything already captured. Safe to call when not started.</summary>
    void Stop();
}

/// <summary>
/// Speaker side of the pipeline: 48 kHz, stereo, 16-bit PCM pushed in by
/// <see cref="TSLib.Audio.PreciseTimedPipe"/> (which matches <see cref="TSLib.Audio.DecoderPipe"/>'s output).
/// </summary>
/// <remarks>
/// <see cref="IAudioActiveConsumer"/> does not derive from <see cref="IDisposable"/>, but every
/// real render endpoint holds unmanaged handles, so this interface adds it.
/// </remarks>
public interface IAudioPlaybackSink : IAudioActiveConsumer, IDisposable
{
    /// <summary>Endpoint this sink is bound to, for logging and UI feedback.</summary>
    AudioDeviceInfo Device { get; }

    /// <summary>Linear gain applied before the samples reach the device, <c>0.0</c>–<c>1.0</c>.</summary>
    float Volume { get; set; }

    /// <summary>Opens the endpoint and begins rendering. Safe to call twice; the second call is a no-op.</summary>
    void Start();

    /// <summary>Stops rendering and discards buffered samples. Safe to call when not started.</summary>
    void Stop();
}

/// <summary>
/// Builds the capture and render endpoints the pipeline pulls from and pushes to.
/// </summary>
/// <remarks>
/// Split from <see cref="IAudioDeviceEnumerator"/> so tests can fake the two independently:
/// device lists are cheap to fake, live endpoints are not.
/// </remarks>
public interface IAudioDeviceFactory
{
    /// <summary>
    /// Opens a capture endpoint. <paramref name="deviceId"/> empty selects the system default.
    /// Throws when the endpoint cannot be opened; callers treat that as "no microphone".
    /// </summary>
    IAudioCaptureSource CreateCapture(string deviceId);

    /// <summary>
    /// Opens a render endpoint. <paramref name="deviceId"/> empty selects the system default.
    /// Throws when the endpoint cannot be opened; callers treat that as "no speakers".
    /// </summary>
    IAudioPlaybackSink CreatePlayback(string deviceId);
}
