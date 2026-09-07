// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using Microsoft.Extensions.Logging;
using TeamSpeak9.Core.Streaming;

namespace TeamSpeak9.Streaming.Capture;

/// <summary>
/// Creates <see cref="GdiScreenCaptureSource"/> instances for the current machine.
/// </summary>
/// <remarks>
/// GDI capture works on every Windows version, so <see cref="IsSupported"/> is always
/// <see langword="true"/>. A Windows.Graphics.Capture backend can be swapped in later without
/// touching the <see cref="IScreenCaptureFactory"/> contract.
/// </remarks>
public sealed class WindowsScreenCaptureFactory : IScreenCaptureFactory
{
    private readonly ILogger _log;

    /// <summary>
    /// Initialises a new factory.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is <see langword="null"/>.</exception>
    public WindowsScreenCaptureFactory(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public IScreenCaptureSource Create(ScreenCaptureTarget target, ScreenCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new GdiScreenCaptureSource(target, options, _log);
    }
}