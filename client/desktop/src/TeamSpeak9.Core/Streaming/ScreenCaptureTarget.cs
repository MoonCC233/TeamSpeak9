// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Globalization;

namespace TeamSpeak9.Core.Streaming;

/// <summary>
/// What a screen share captures.
/// </summary>
public enum ScreenCaptureKind
{
    /// <summary>An entire monitor.</summary>
    Display,

    /// <summary>A single top-level window.</summary>
    Window,
}

/// <summary>
/// One capturable surface, as offered to the user in the share picker.
/// </summary>
/// <param name="Kind">Monitor or window.</param>
/// <param name="Handle">
/// Platform handle: an <c>HMONITOR</c> for <see cref="ScreenCaptureKind.Display"/>, an <c>HWND</c>
/// for <see cref="ScreenCaptureKind.Window"/>. Zero is never valid.
/// </param>
/// <param name="Name">Friendly name to show: monitor description or window title.</param>
/// <param name="Width">Surface width in pixels at enumeration time.</param>
/// <param name="Height">Surface height in pixels at enumeration time.</param>
/// <param name="Index">
/// Zero-based ordinal within its kind. Stable within one enumeration only, which is why
/// <see cref="Handle"/> — not this — is what a capture source binds to.
/// </param>
/// <remarks>
/// <para>
/// <see cref="Width"/> and <see cref="Height"/> are a snapshot: windows get resized while shared,
/// and the frame source reports the authoritative size on every frame. They exist so the picker
/// can show "1920 × 1080" and so the initial encoder profile has something to clamp.
/// </para>
/// <para>
/// <see cref="ToSourceId"/> produces the value that goes into <c>setup.properties.source</c>
/// (docs/protocol/tssp-v1.md §5.2). It deliberately carries no window title: titles leak document
/// names to every viewer and are not stable across restarts.
/// </para>
/// </remarks>
public sealed record ScreenCaptureTarget(
    ScreenCaptureKind Kind,
    nint Handle,
    string Name,
    int Width,
    int Height,
    int Index)
{
    /// <summary>Wire identifier for <c>setup.properties.source</c>, e.g. <c>display:0</c>.</summary>
    /// <remarks>
    /// Windows are keyed by handle rather than index because the window list changes between the
    /// picker opening and <c>setup</c> being sent, whereas monitor order does not.
    /// </remarks>
    public string ToSourceId() => Kind switch
    {
        ScreenCaptureKind.Display => string.Create(CultureInfo.InvariantCulture, $"display:{Index}"),
        ScreenCaptureKind.Window => string.Create(CultureInfo.InvariantCulture, $"window:{(long)Handle}"),
        _ => throw new InvalidOperationException($"未知的采集类型：{Kind}。"),
    };
}
