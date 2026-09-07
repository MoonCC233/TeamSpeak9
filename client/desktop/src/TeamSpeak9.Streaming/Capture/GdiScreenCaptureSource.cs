// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using TeamSpeak9.Core.Streaming;

namespace TeamSpeak9.Streaming.Capture;

/// <summary>
/// Captures one monitor or window through GDI <c>BitBlt</c> and delivers BGRA frames to the encoder.
/// </summary>
/// <remarks>
/// <para>
/// The capture loop runs on a dedicated pump thread. Each frame is copied out of a device-context
/// bitmap into a pooled CPU buffer, then handed to <see cref="FrameArrived"/> as a borrowed
/// <see cref="ScreenFrame"/>. The buffer is recycled as soon as the handler returns, so handlers
/// must copy anything they need to keep.
/// </para>
/// <para>
/// GDI capture does not see hardware-accelerated content (games, video overlays). It is the
/// pragmatic baseline; a Windows.Graphics.Capture backend can replace it later without touching
/// the <see cref="IScreenCaptureSource"/> contract.
/// </para>
/// </remarks>
internal sealed class GdiScreenCaptureSource : IScreenCaptureSource
{
    private readonly ILogger _log;
    private readonly ScreenCaptureTarget _target;
    private readonly object _sync = new();

    private Thread? _pumpThread;
    private CancellationTokenSource? _cts;

    private ScreenCaptureOptions _options;
    private byte[] _buffer = Array.Empty<byte>();
    private int _bufferStride;
    private (int Width, int Height) _currentSize;

    private volatile bool _running;
    private bool _disposed;

    /// <summary>
    /// Initialises a new capture source bound to <paramref name="target"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> or <paramref name="log"/> is <see langword="null"/>.</exception>
    public GdiScreenCaptureSource(ScreenCaptureTarget target, ScreenCaptureOptions options, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(log);

        _target = target;
        _options = options;
        _log = log;
        _currentSize = (target.Width, target.Height);
    }

    /// <inheritdoc />
    public event ScreenFrameHandler? FrameArrived;

    /// <inheritdoc />
    public event EventHandler? Closed;

    /// <inheritdoc />
    public ScreenCaptureTarget Target => _target;

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <inheritdoc />
    public (int Width, int Height) CurrentSize => _currentSize;

    /// <inheritdoc />
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_running)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _pumpThread = new Thread(() => PumpLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "TS9-ScreenCapture",
            };
            _running = true;
            _pumpThread.Start();
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            _cts?.Cancel();
        }
    }

    /// <inheritdoc />
    public void UpdateOptions(ScreenCaptureOptions options)
    {
        lock (_sync)
        {
            _options = options;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;
            _cts?.Cancel();
        }
    }

    private void PumpLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _running)
            {
                if (!CaptureOnce(token))
                {
                    break;
                }

                Thread.Sleep(33); // ~30 fps
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "屏幕采集线程异常退出。");
        }
        finally
        {
            _running = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool CaptureOnce(CancellationToken token)
    {
        nint hdcScreen = GetDC(IntPtr.Zero);
        if (hdcScreen == 0)
        {
            return false;
        }

        bool ok = false;
        try
        {
            nint hdcMem = CreateCompatibleDC(hdcScreen);
            if (hdcMem != 0)
            {
                try
                {
                    int width = _target.Width;
                    int height = _target.Height;
                    int rowBytes = width * ScreenFrame.BytesPerPixel;
                    int total = rowBytes * height;

                    if (_buffer.Length < total)
                    {
                        _buffer = new byte[total];
                    }

                    nint hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
                    if (hBitmap != 0)
                    {
                        try
                        {
                            nint old = SelectObject(hdcMem, hBitmap);
                            try
                            {
                                if (BitBlt(hdcMem, 0, 0, width, height, hdcScreen, 0, 0, SRCCOPY))
                                {
                                    var bmi = new BITMAPINFO
                                    {
                                        bmiHeader = new BITMAPINFOHEADER
                                        {
                                            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                                            biWidth = width,
                                            biHeight = -height,
                                            biPlanes = 1,
                                            biBitCount = 32,
                                            biCompression = BI_RGB,
                                        },
                                    };

                                    if (GetDIBits(hdcMem, hBitmap, 0, (uint)height, _buffer, ref bmi, DIB_RGB_COLORS) != 0)
                                    {
                                        _currentSize = (width, height);
                                        _bufferStride = rowBytes;

                                        var frame = new ScreenFrame(
                                            (nint)Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, 0),
                                            rowBytes,
                                            width,
                                            height,
                                            TimeSpan.FromMilliseconds(Environment.TickCount64));

                                        FrameArrived?.Invoke(frame);
                                        ok = true;
                                    }
                                }
                            }
                            finally
                            {
                                SelectObject(hdcMem, old);
                            }
                        }
                        finally
                        {
                            DeleteObject(hBitmap);
                        }
                    }
                }
                finally
                {
                    DeleteDC(hdcMem);
                }
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }

        return ok;
    }

    private const uint SRCCOPY = 0x00CC0020;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hdcDest, int xDest, int yDest, int width, int height, nint hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint cLines, byte[] lpvBits, ref BITMAPINFO lpbmi, uint usage);
}