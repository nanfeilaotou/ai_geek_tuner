using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Views.Behaviors;

/// <summary>
/// Routes the one native hit-test message needed by the borderless window and
/// observes the native move loop. The Windows subclass helpers keep WPF's
/// existing procedure chain intact; this class never replaces the window
/// procedure head.
/// </summary>
public sealed class WindowChromeHitTestRouter : IDisposable
{
    public const int WmNcHitTest = 0x0084;
    public const int WmNcLButtonDown = 0x00A1;
    public const int WmNcLButtonDblClk = 0x00A3;
    public const int WmNcDestroy = 0x0082;
    public const int WmEnterSizeMove = 0x0231;
    public const int WmMoving = 0x0216;
    public const int WmExitSizeMove = 0x0232;
    public const int HtClient = 1;
    public const int HtCaption = 2;

    private static long _nextSubclassId;

    private readonly Window _window;
    private readonly SubclassProc _subclassProc;
    private readonly SidebarMoveHoverTracker? _sidebarMoveHoverTracker;
    private readonly UIntPtr _subclassId;
    private IntPtr _hwnd;
    private bool _attached;
    private bool _disposed;
    private bool _moveExitQueued;

    public WindowChromeHitTestRouter(Window window, FrameworkElement? sidebarRoot = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _subclassProc = NativeSubclassProcedure;
        _sidebarMoveHoverTracker = sidebarRoot is null
            ? null
            : new SidebarMoveHoverTracker(sidebarRoot);
        var id = unchecked((ulong)Interlocked.Increment(ref _nextSubclassId));
        _subclassId = new UIntPtr(id == 0 ? 1UL : id);
        Attach();
    }

    public bool IsAttached => _attached && !_disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_attached && _hwnd != IntPtr.Zero)
        {
            try
            {
                RemoveWindowSubclass(_hwnd, _subclassProc, _subclassId);
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "WindowChrome subclass remove");
            }
        }

        _attached = false;
        _hwnd = IntPtr.Zero;
        GC.KeepAlive(_subclassProc);
    }

    /// <summary>
    /// Client-content hit tests must stay client-side. Background dragging is a
    /// mouse-down gesture, not a permanent native caption classification.
    /// </summary>
    public static IntPtr Classify(DependencyObject? originalSource, DependencyObject dragRoot)
    {
        ArgumentNullException.ThrowIfNull(dragRoot);
        return new IntPtr(HtClient);
    }

    /// <summary>
    /// Starts one native window move after WPF has classified the mouse-down as
    /// a draggable background gesture. This deliberately uses the native move
    /// message instead of the WPF modal drag helper.
    /// </summary>
    public static bool BeginNativeWindowMove(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        ReleaseCapture();
        SendMessage(hwnd, WmNcLButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
        return true;
    }

    private void Attach()
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero || _disposed || _attached)
        {
            return;
        }

        try
        {
            if (SetWindowSubclass(hwnd, _subclassProc, _subclassId, UIntPtr.Zero))
            {
                _hwnd = hwnd;
                _attached = true;
            }
        }
        catch (Exception exception)
        {
            ExceptionLogWriter.Write(exception, "WindowChrome subclass install");
        }
    }

    private IntPtr NativeSubclassProcedure(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        try
        {
            if (message == WmNcDestroy)
            {
                // Remove first so the callback cannot be invoked again while
                // the HWND is being torn down, then let the existing WPF chain
                // run.
                RemoveWindowSubclass(hwnd, _subclassProc, _subclassId);

                _attached = false;
                _disposed = true;
                _hwnd = IntPtr.Zero;
                _sidebarMoveHoverTracker?.Clear();
                WindowMoveState.SetIsMoving(_window, false);
                return DefSubclassProc(hwnd, message, wParam, lParam);
            }

            if (subclassId != _subclassId)
            {
                return DefSubclassProc(hwnd, message, wParam, lParam);
            }

            if (message == WmEnterSizeMove)
            {
                SetMovingState(true);
                UpdateSidebarMoveHoverFromCursor(begin: true);
            }
            else if (message == WmMoving)
            {
                UpdateSidebarMoveHoverFromCursor(begin: false);
            }
            else if (message == WmExitSizeMove)
            {
                UpdateSidebarMoveHoverFromCursor(begin: false);
                ScheduleMoveEndSynchronization();
            }
            else if (message == WmNcHitTest)
            {
                // Let WPF WindowChrome own the real caption/titlebar hit-test.
                // In particular, main page content must remain HTCLIENT so the
                // standard WPF mouse-wheel route is preserved.
            }
            else if (message == WindowMaximizeWorkArea.WmGetMinMaxInfo)
            {
                // M5.2C：把最大化 bounds 钳制到所在显示器工作区（不遮任务栏、
                // 不留白缝）。完全接管该消息，防止 ptMaxSize 被还原成整屏
                // bounds；NoResize 固定尺寸窗口不依赖 track-size 约束。
                if (WindowMaximizeWorkArea.HandleGetMinMaxInfo(hwnd, lParam))
                {
                    return IntPtr.Zero;
                }
            }
            else if (message == WmNcLButtonDblClk
                && wParam.ToInt32() == HtCaption)
            {
                WindowChromeController.ToggleMaximize(_window);
                return IntPtr.Zero;
            }
        }
        catch (Exception exception)
        {
            // Never allow managed code to cross the native callback boundary.
            ExceptionLogWriter.Write(exception, "WindowChrome subclass callback");
            return DefSubclassProc(hwnd, message, wParam, lParam);
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ScheduleMoveEndSynchronization()
    {
        if (_moveExitQueued || _disposed)
        {
            return;
        }

        _moveExitQueued = true;
        _window.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                try
                {
                    if (Mouse.Captured is DependencyObject captured
                        && (ReferenceEquals(captured, _window)
                            || ReferenceEquals(Window.GetWindow(captured), _window)))
                    {
                        Mouse.Capture(null);
                    }

                    Mouse.Synchronize();
                }
                catch (Exception exception)
                {
                    ExceptionLogWriter.Write(exception, "WindowChrome move-end input sync");
                }
                finally
                {
                    WindowMoveState.SetIsMoving(_window, false);
                    _sidebarMoveHoverTracker?.Clear();
                    _moveExitQueued = false;
                }
            }));
    }

    private void UpdateSidebarMoveHoverFromCursor(bool begin)
    {
        if (_sidebarMoveHoverTracker is null || !TryGetCursorPosition(out var screenPoint))
        {
            return;
        }

        if (begin)
        {
            _sidebarMoveHoverTracker.BeginTracking(screenPoint);
        }
        else
        {
            _sidebarMoveHoverTracker.UpdateFromScreenPoint(screenPoint);
        }
    }

    private void SetMovingState(bool value)
    {
        if (_window.Dispatcher.CheckAccess())
        {
            WindowMoveState.SetIsMoving(_window, value);
            return;
        }

        _window.Dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            new Action(() => WindowMoveState.SetIsMoving(_window, value)));
    }

    private delegate IntPtr SubclassProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData);

    private static bool TryGetCursorPosition(out Point screenPoint)
    {
        if (GetCursorPos(out var point))
        {
            screenPoint = new Point(point.X, point.Y);
            return true;
        }

        screenPoint = default;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr hwnd,
        SubclassProc subclassProc,
        UIntPtr subclassId,
        UIntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hwnd,
        SubclassProc subclassProc,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);
}