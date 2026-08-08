using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace StereoKitEditor.App.Controls;

public sealed class NativeViewportHost : NativeControlHost
{
    private nint _containerHandle;
    private nint _childHandle;
    private nint _pendingHandle;
    private nint _originalStyle;
    private nint _originalExtendedStyle;
    private int _childWidth;
    private int _childHeight;
    private readonly NativeMethods.SubclassProcedure _subclassProcedure;
    private bool _containerSubclassed;

    public NativeViewportHost()
    {
        _subclassProcedure = HandleContainerMessage;
        SizeChanged += (_, _) =>
        {
            ResizeChild();
            Dispatcher.UIThread.Post(ResizeChild, DispatcherPriority.Background);
        };
        LayoutUpdated += (_, _) => ResizeChild();
        GotFocus += (_, _) => FocusWindow();
    }

    public nint AttachedWindowHandle => _childHandle;

    public void FocusWindow()
    {
        if (!OperatingSystem.IsWindows()
            || _childHandle == 0
            || !NativeMethods.IsWindow(_childHandle))
        {
            return;
        }

        var childThread = NativeMethods.GetWindowThreadProcessId(_childHandle, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = childThread != currentThread
            && NativeMethods.AttachThreadInput(currentThread, childThread, attach: true);
        try
        {
            NativeMethods.SetFocus(_childHandle);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThread, childThread, attach: false);
            }
        }
    }

    public void AttachWindow(nint windowHandle)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AttachWindow(windowHandle));
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (_childHandle == windowHandle)
        {
            ResizeChild();
            return;
        }

        DetachWindow();
        _pendingHandle = windowHandle;
        TryAttachPendingWindow();
    }

    public void DetachWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            _childHandle = 0;
            _pendingHandle = 0;
            return;
        }

        _pendingHandle = 0;
        if (_childHandle == 0 || !NativeMethods.IsWindow(_childHandle))
        {
            _childHandle = 0;
            return;
        }

        NativeMethods.ShowWindow(_childHandle, NativeMethods.SwHide);
        NativeMethods.SetParent(_childHandle, 0);
        NativeMethods.SetWindowLongPtr(_childHandle, NativeMethods.GwlStyle, _originalStyle);
        NativeMethods.SetWindowLongPtr(_childHandle, NativeMethods.GwlExStyle, _originalExtendedStyle);
        NativeMethods.SetWindowPos(
            _childHandle,
            0,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove
                | NativeMethods.SwpNoSize
                | NativeMethods.SwpNoZOrder
                | NativeMethods.SwpNoActivate
                | NativeMethods.SwpFrameChanged);
        _childHandle = 0;
        _childWidth = 0;
        _childHeight = 0;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var control = base.CreateNativeControlCore(parent);
        _containerHandle = control.Handle;
        _containerSubclassed = NativeMethods.SetWindowSubclass(
            _containerHandle,
            _subclassProcedure,
            NativeMethods.ViewportSubclassId,
            0);
        TryAttachPendingWindow();
        return control;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        DetachWindow();
        if (_containerSubclassed && _containerHandle != 0)
        {
            NativeMethods.RemoveWindowSubclass(
                _containerHandle,
                _subclassProcedure,
                NativeMethods.ViewportSubclassId);
            _containerSubclassed = false;
        }

        _containerHandle = 0;
        base.DestroyNativeControlCore(control);
    }

    private nint HandleContainerMessage(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == NativeMethods.WmParentNotify)
        {
            var notification = (uint)((nuint)wParam & 0xFFFF);
            if (notification is NativeMethods.WmLeftButtonDown
                or NativeMethods.WmRightButtonDown
                or NativeMethods.WmMiddleButtonDown
                or NativeMethods.WmXButtonDown)
            {
                FocusWindow();
            }
        }
        else if (message is NativeMethods.WmSize
                 or NativeMethods.WmDpiChanged
                 or NativeMethods.WmDisplayChange)
        {
            Dispatcher.UIThread.Post(ResizeChild, DispatcherPriority.Render);
        }
        else if (message == NativeMethods.WmSetFocus)
        {
            FocusWindow();
        }

        return NativeMethods.DefSubclassProc(window, message, wParam, lParam);
    }

    private void TryAttachPendingWindow()
    {
        if (_containerHandle == 0
            || _pendingHandle == 0
            || !NativeMethods.IsWindow(_pendingHandle))
        {
            return;
        }

        _childHandle = _pendingHandle;
        _pendingHandle = 0;
        _originalStyle = NativeMethods.GetWindowLongPtr(_childHandle, NativeMethods.GwlStyle);
        _originalExtendedStyle = NativeMethods.GetWindowLongPtr(_childHandle, NativeMethods.GwlExStyle);

        var childStyle = (_originalStyle
            & ~NativeMethods.WsPopup
            & ~NativeMethods.WsCaption
            & ~NativeMethods.WsThickFrame
            & ~NativeMethods.WsSysMenu
            & ~NativeMethods.WsMinimizeBox
            & ~NativeMethods.WsMaximizeBox)
            | NativeMethods.WsChild
            | NativeMethods.WsVisible;
        var childExtendedStyle = (_originalExtendedStyle & ~NativeMethods.WsExAppWindow)
            | NativeMethods.WsExToolWindow;

        NativeMethods.ShowWindow(_childHandle, NativeMethods.SwHide);
        NativeMethods.SetWindowLongPtr(_childHandle, NativeMethods.GwlStyle, childStyle);
        NativeMethods.SetWindowLongPtr(_childHandle, NativeMethods.GwlExStyle, childExtendedStyle);
        NativeMethods.SetParent(_childHandle, _containerHandle);
        NativeMethods.SetWindowPos(
            _childHandle,
            0,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove
                | NativeMethods.SwpNoSize
                | NativeMethods.SwpNoZOrder
                | NativeMethods.SwpNoActivate
                | NativeMethods.SwpFrameChanged);
        ResizeChild();
        NativeMethods.ShowWindow(_childHandle, NativeMethods.SwShow);
        Dispatcher.UIThread.Post(FocusWindow, DispatcherPriority.Background);
    }

    private void ResizeChild()
    {
        if (!OperatingSystem.IsWindows()
            || _containerHandle == 0
            || _childHandle == 0
            || !NativeMethods.IsWindow(_childHandle)
            || !NativeMethods.GetClientRect(_containerHandle, out var bounds))
        {
            return;
        }

        var width = Math.Max(1, bounds.Right - bounds.Left);
        var height = Math.Max(1, bounds.Bottom - bounds.Top);
        if (_childWidth == width && _childHeight == height)
        {
            return;
        }

        NativeMethods.MoveWindow(_childHandle, 0, 0, width, height, repaint: true);
        _childWidth = width;
        _childHeight = height;
    }

    private static class NativeMethods
    {
        public const nuint ViewportSubclassId = 1;
        public const int GwlStyle = -16;
        public const int GwlExStyle = -20;
        public const int SwHide = 0;
        public const int SwShow = 5;

        public static readonly nint WsPopup = unchecked((nint)0x80000000);
        public static readonly nint WsChild = 0x40000000;
        public static readonly nint WsVisible = 0x10000000;
        public static readonly nint WsCaption = 0x00C00000;
        public static readonly nint WsThickFrame = 0x00040000;
        public static readonly nint WsSysMenu = 0x00080000;
        public static readonly nint WsMinimizeBox = 0x00020000;
        public static readonly nint WsMaximizeBox = 0x00010000;
        public static readonly nint WsExAppWindow = 0x00040000;
        public static readonly nint WsExToolWindow = 0x00000080;

        public const uint SwpNoSize = 0x0001;
        public const uint SwpNoMove = 0x0002;
        public const uint SwpNoZOrder = 0x0004;
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpFrameChanged = 0x0020;
        public const uint WmParentNotify = 0x0210;
        public const uint WmSize = 0x0005;
        public const uint WmSetFocus = 0x0007;
        public const uint WmDisplayChange = 0x007E;
        public const uint WmDpiChanged = 0x02E0;
        public const uint WmLeftButtonDown = 0x0201;
        public const uint WmRightButtonDown = 0x0204;
        public const uint WmMiddleButtonDown = 0x0207;
        public const uint WmXButtonDown = 0x020B;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate nint SubclassProcedure(
            nint window,
            uint message,
            nint wParam,
            nint lParam,
            nuint subclassId,
            nuint referenceData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowSubclass(
            nint window,
            SubclassProcedure procedure,
            nuint subclassId,
            nuint referenceData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveWindowSubclass(
            nint window,
            SubclassProcedure procedure,
            nuint subclassId);

        [DllImport("comctl32.dll")]
        public static extern nint DefSubclassProc(
            nint window,
            uint message,
            nint wParam,
            nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetParent(nint child, nint newParent);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetFocus(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(uint fromThread, uint toThread, bool attach);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(nint window, int command);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveWindow(
            nint window,
            int x,
            int y,
            int width,
            int height,
            [MarshalAs(UnmanagedType.Bool)] bool repaint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(nint window, out NativeRect rectangle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            nint window,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        public static nint GetWindowLongPtr(nint window, int index) =>
            Environment.Is64BitProcess
                ? GetWindowLongPtr64(window, index)
                : new nint(GetWindowLong32(window, index));

        public static nint SetWindowLongPtr(nint window, int index, nint value) =>
            Environment.Is64BitProcess
                ? SetWindowLongPtr64(window, index, value)
                : new nint(SetWindowLong32(window, index, value.ToInt32()));

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(nint window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern nint GetWindowLongPtr64(nint window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(nint window, int index, int value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern nint SetWindowLongPtr64(nint window, int index, nint value);
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
