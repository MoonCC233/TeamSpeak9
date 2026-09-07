// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Runtime.InteropServices;

namespace TeamSpeak9.Core.Streaming;

/// <summary>
/// One captured frame, always 32-bit BGRA with a premultiplied-irrelevant (opaque) alpha channel.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Data"/> is only valid for the duration of the <see cref="ScreenFrameHandler"/>
/// call.</b> The capture source owns the memory and recycles it as soon as the handler returns.
/// Anything that needs the pixels later must copy them.
/// </para>
/// <para>
/// BGRA is what <c>Windows.Graphics.Capture</c> produces natively and what FFmpeg's swscale
/// accepts directly, so no colour conversion happens on this path.
/// </para>
/// <para>
/// <see cref="Stride"/> is frequently larger than <c>Width * 4</c> because GPU staging textures are
/// row-aligned. Never assume the buffer is tightly packed.
/// </para>
/// </remarks>
/// <param name="Data">Pointer to the first pixel of the first row.</param>
/// <param name="Stride">Bytes between the starts of two consecutive rows.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Timestamp">
/// When the compositor produced the frame, on the capture source's monotonic clock. Used to derive
/// RTP timestamps, so it must never go backwards.
/// </param>
public readonly record struct ScreenFrame(nint Data, int Stride, int Width, int Height, TimeSpan Timestamp)
{
    /// <summary>Bytes per pixel of the BGRA layout this pipeline uses end to end.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>True when the frame carries a usable buffer of non-zero area.</summary>
    public bool IsValid => Data != 0 && Width > 0 && Height > 0 && Stride >= Width * BytesPerPixel;

    /// <summary>Copies the frame into a freshly allocated tightly packed BGRA buffer.</summary>
    /// <remarks>
    /// The escape hatch for consumers that cannot work with the borrowed pointer. Costs one full
    /// frame copy, so the encoder path avoids it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The frame does not carry a usable buffer.</exception>
    public byte[] ToArray()
    {
        if (!IsValid)
            throw new InvalidOperationException("帧数据无效，无法复制。");

        int rowBytes = Width * BytesPerPixel;
        var buffer = new byte[rowBytes * Height];

        for (int y = 0; y < Height; y++)
        {
            Marshal.Copy(Data + (y * Stride), buffer, y * rowBytes, rowBytes);
        }

        return buffer;
    }
}

/// <summary>
/// Receives frames from an <see cref="IScreenCaptureSource"/>.
/// </summary>
/// <remarks>
/// <para>
/// Invoked on the capture source's own thread, never on the UI thread, and <b>must not block</b>:
/// the source drops frames it cannot deliver rather than queueing them, so a slow handler lowers
/// the effective frame rate instead of growing memory.
/// </para>
/// <para>
/// A plain delegate rather than <c>EventHandler&lt;ScreenFrame&gt;</c> because frames arrive up to
/// 60 times a second and <see cref="ScreenFrame"/> is a struct; the generic form would box each one.
/// </para>
/// </remarks>
/// <param name="frame">The frame. Its buffer is invalid once this call returns.</param>
public delegate void ScreenFrameHandler(in ScreenFrame frame);

/// <summary>
/// Knobs the user controls in the screen share settings, mapped onto the platform capture session.
/// </summary>
/// <param name="ShowBorder">
/// Draw the system highlight around the captured surface. Maps to
/// <see cref="Settings.StreamSettings.ShowCaptureBorder"/>. Windows 10 always draws it regardless;
/// only Windows 11 honours turning it off.
/// </param>
/// <param name="CaptureCursor">
/// Include the mouse pointer. Maps to <see cref="Settings.StreamSettings.CaptureCursor"/>.
/// </param>
public readonly record struct ScreenCaptureOptions(bool ShowBorder, bool CaptureCursor)
{
    /// <summary>Matches the shipped <see cref="Settings.StreamSettings"/> defaults.</summary>
    public static ScreenCaptureOptions Default => new(ShowBorder: true, CaptureCursor: true);

    /// <summary>Projects the persisted settings onto the capture session.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <see langword="null"/>.</exception>
    public static ScreenCaptureOptions From(Settings.StreamSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ScreenCaptureOptions(settings.ShowCaptureBorder, settings.CaptureCursor);
    }
}

/// <summary>
/// Lists the monitors and windows the user can pick from.
/// </summary>
/// <remarks>
/// Split from <see cref="IScreenCaptureFactory"/> for the same reason
/// <see cref="Audio.IAudioDeviceEnumerator"/> is split from <see cref="Audio.IAudioDeviceFactory"/>:
/// target lists are cheap to fake, live GPU capture sessions are not.
/// </remarks>
public interface IScreenTargetEnumerator
{
    /// <summary>Monitors, primary first.</summary>
    IReadOnlyList<ScreenCaptureTarget> ListDisplays();

    /// <summary>
    /// Top-level windows worth offering: visible, non-minimised, titled, and not our own window.
    /// </summary>
    IReadOnlyList<ScreenCaptureTarget> ListWindows();
}

/// <summary>
/// Produces BGRA frames for one monitor or window.
/// </summary>
/// <remarks>
/// <para>
/// The lifecycle mirrors <see cref="Audio.IAudioCaptureSource"/>: a source is bound to one target
/// for its whole life, and switching targets means disposing this instance and building another.
/// </para>
/// <para>
/// Frames only flow between <see cref="Start"/> and <see cref="Stop"/>, and only to handlers
/// subscribed to <see cref="FrameArrived"/>. Sources are free to skip work entirely while nobody
/// is subscribed.
/// </para>
/// </remarks>
public interface IScreenCaptureSource : IDisposable
{
    /// <summary>Frames, on the capture thread. See <see cref="ScreenFrameHandler"/> for the contract.</summary>
    event ScreenFrameHandler? FrameArrived;

    /// <summary>Raised when the captured window closes or the monitor is detached.</summary>
    /// <remarks>
    /// Fires on the capture thread. The publisher treats it as an implicit stop and tears the
    /// stream down; frames never resume afterwards.
    /// </remarks>
    event EventHandler? Closed;

    /// <summary>What this source is bound to, for logging and UI feedback.</summary>
    ScreenCaptureTarget Target { get; }

    /// <summary>Whether frames are currently flowing.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Size of the most recently delivered frame, or the target's size at construction time when
    /// no frame has arrived yet.
    /// </summary>
    /// <remarks>
    /// Tracked separately from <see cref="ScreenCaptureTarget.Width"/> because shared windows get
    /// resized, which forces the encoder to reinitialise.
    /// </remarks>
    (int Width, int Height) CurrentSize { get; }

    /// <summary>Begins capturing. Safe to call twice; the second call is a no-op.</summary>
    /// <exception cref="InvalidOperationException">The target is gone or the platform refused the session.</exception>
    void Start();

    /// <summary>Stops capturing and releases frame buffers. Safe to call when not started.</summary>
    void Stop();

    /// <summary>
    /// Applies changed user options to a live session.
    /// </summary>
    /// <remarks>
    /// Separate from the constructor so toggling the cursor or border mid-share does not restart
    /// the session, which would otherwise force a keyframe and a visible stutter.
    /// </remarks>
    void UpdateOptions(ScreenCaptureOptions options);
}

/// <summary>
/// Builds the capture sessions the encoder pulls frames from.
/// </summary>
public interface IScreenCaptureFactory
{
    /// <summary>
    /// Whether this machine supports screen capture at all.
    /// </summary>
    /// <remarks>
    /// <c>Windows.Graphics.Capture</c> needs Windows 10 1903+ and a supported GPU driver. The share
    /// button stays disabled with an explanatory tooltip when this is <see langword="false"/>,
    /// which is preferable to letting <see cref="Create"/> throw in the user's face.
    /// </remarks>
    bool IsSupported { get; }

    /// <summary>
    /// Opens a capture session for <paramref name="target"/>.
    /// </summary>
    /// <remarks>Throws rather than returning null; callers surface the message as "无法开始共享".</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><see cref="IsSupported"/> is <see langword="false"/>.</exception>
    /// <exception cref="InvalidOperationException">The target no longer exists.</exception>
    IScreenCaptureSource Create(ScreenCaptureTarget target, ScreenCaptureOptions options);
}
