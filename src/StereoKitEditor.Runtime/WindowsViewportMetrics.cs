using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StereoKitEditor.Runtime;

/// <summary>
/// Tracks the real Win32 client area used by the embedded StereoKit window.
/// StereoKit's display dimensions describe the surface created at startup and
/// do not follow the later cross-process SetParent/MoveWindow resize reliably.
/// </summary>
internal sealed class WindowsViewportMetrics
{
    private readonly nint _windowHandle;

    public WindowsViewportMetrics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        _windowHandle = process.MainWindowHandle;
        if (_windowHandle == 0)
        {
            _windowHandle = FindCurrentThreadWindow();
        }
    }

    public (int Width, int Height) GetClientSize(int fallbackWidth, int fallbackHeight)
    {
        fallbackWidth = Math.Max(1, fallbackWidth);
        fallbackHeight = Math.Max(1, fallbackHeight);
        if (_windowHandle == 0
            || !NativeMethods.IsWindow(_windowHandle)
            || !NativeMethods.GetClientRect(_windowHandle, out var bounds))
        {
            return (fallbackWidth, fallbackHeight);
        }

        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        return width > 0 && height > 0
            ? (width, height)
            : (fallbackWidth, fallbackHeight);
    }

    private static nint FindCurrentThreadWindow()
    {
        nint result = 0;
        NativeMethods.EnumThreadWindows(
            NativeMethods.GetCurrentThreadId(),
            (window, _) =>
            {
                if (NativeMethods.GetClientRect(window, out var bounds)
                    && bounds.Right > bounds.Left
                    && bounds.Bottom > bounds.Top)
                {
                    result = window;
                    return false;
                }

                return true;
            },
            0);
        return result;
    }

    private static class NativeMethods
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate bool EnumWindowsProcedure(nint window, nint parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumThreadWindows(
            uint threadId,
            EnumWindowsProcedure callback,
            nint parameter);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(nint window, out NativeRect rectangle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
