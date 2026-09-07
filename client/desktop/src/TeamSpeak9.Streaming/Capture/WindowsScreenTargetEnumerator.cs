// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using TeamSpeak9.Core.Streaming;

namespace TeamSpeak9.Streaming.Capture;

/// <summary>
/// Enumerates the monitors and windows a screen share can target, using the Win32 display and
/// window APIs.
/// </summary>
/// <remarks>
/// <para>
/// Monitors are listed primary-first with their friendly device name and pixel size. Windows are
/// filtered to those worth offering: visible, non-minimised, titled, and not this process's own
/// windows. The window list is a snapshot — it can change between the picker opening and
/// <see cref="IScreenCaptureSource.Start"/> — which is why the source binds to the handle, not the
/// index.
/// </para>
/// </remarks>
public sealed class WindowsScreenTargetEnumerator : IScreenTargetEnumerator
{
    private readonly ILogger _log;

    /// <summary>
    /// Initialises a new enumerator.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is <see langword="null"/>.</exception>
    public WindowsScreenTargetEnumerator(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <inheritdoc />
    public IReadOnlyList<ScreenCaptureTarget> ListDisplays()
    {
        var displays = new List<ScreenCaptureTarget>();
        int index = 0;

        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (hMonitor, _, _, _) =>
            {
                var info = new MonitorInfo
                {
                    Size = Marshal.SizeOf<MonitorInfo>(),
                };

                if (GetMonitorInfo(hMonitor, ref info))
                {
                    string name = info.DeviceName.TrimEnd('\0');
                    int width = info.Monitor.Right - info.Monitor.Left;
                    int height = info.Monitor.Bottom - info.Monitor.Top;

                    displays.Add(new ScreenCaptureTarget(
                        ScreenCaptureKind.Display,
                        hMonitor,
                        name,
                        width,
                        height,
                        index));
                }
                else
                {
                    _log.LogWarning("屏幕共享：无法读取显示器 {Handle} 的信息。", hMonitor);
                }

                index++;
                return true;
            },
            IntPtr.Zero);

        return displays;
    }

    /// <inheritdoc />
    public IReadOnlyList<ScreenCaptureTarget> ListWindows()
    {
        var windows = new List<ScreenCaptureTarget>();
        int index = 0;
        nint ownProcess = Environment.ProcessId;

        EnumWindows(
            (hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd) || IsIconic(hWnd))
                {
                    return true;
                }

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == ownProcess)
                {
                    return true;
                }

                int length = GetWindowTextLength(hWnd);
                if (length <= 0)
                {
                    return true;
                }

                var title = new char[length + 1];
                GetWindowText(hWnd, title, title.Length);
                string name = new string(title).TrimEnd('\0');

                if (string.IsNullOrWhiteSpace(name))
                {
                    return true;
                }

                GetWindowRect(hWnd, out var rect);
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                if (width <= 0 || height <= 0)
                {
                    return true;
                }

                windows.Add(new ScreenCaptureTarget(
                    ScreenCaptureKind.Window,
                    hWnd,
                    name,
                    width,
                    height,
                    index));

                index++;
                return true;
            },
            IntPtr.Zero);

        return windows;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    private delegate bool WindowEnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowEnumProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);
}