using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AIGeekTuner.Views.Behaviors
{
    /// <summary>
    /// Routes native hit testing for the borderless window. A display-only WPF
    /// element is exposed as HTCAPTION so Windows owns the move loop; interactive
    /// elements remain HTCLIENT and continue through normal WPF input routing.
    ///
    /// WPF installs its WindowFilterMessage hook at HwndSource construction and
    /// can short-circuit WM_NCHITTEST before AddHook callbacks. A tiny HWND
    /// subclass is therefore used for this one message; every other message is
    /// forwarded to the original WPF window procedure unchanged.
    /// </summary>
    public sealed class WindowChromeHitTestRouter : IDisposable
    {
        public const int WmNcHitTest = 0x0084;
        public const int WmNcLButtonDblClk = 0x00A3;
        public const int HtClient = 1;
        public const int HtCaption = 2;

        private const int GwlWndProc = -4;

        private readonly Window _window;
        private readonly NativeWndProc _nativeWndProc;
        private IntPtr _hwnd;
        private IntPtr _originalWndProc;
        private bool _disposed;

        public WindowChromeHitTestRouter(Window window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _nativeWndProc = NativeWindowProcedure;
            Attach();
        }

        public bool IsAttached => _originalWndProc != IntPtr.Zero;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_hwnd != IntPtr.Zero && _originalWndProc != IntPtr.Zero)
            {
                SetWindowLongPtr(_hwnd, GwlWndProc, _originalWndProc);
            }

            _hwnd = IntPtr.Zero;
            _originalWndProc = IntPtr.Zero;
            GC.KeepAlive(_nativeWndProc);
        }

        /// <summary>Maps the already classified WPF hit to the native result.</summary>
        public static IntPtr Classify(DependencyObject? originalSource, DependencyObject dragRoot) =>
            WindowDragHitTest.IsDraggableFrom(originalSource, dragRoot)
                ? new IntPtr(HtCaption)
                : new IntPtr(HtClient);

        /// <summary>Converts a native screen coordinate to WPF device-independent units.</summary>
        public static Point ScreenPixelsToDip(Window window, Point screenPixels)
        {
            ArgumentNullException.ThrowIfNull(window);
            return window.PointFromScreen(screenPixels);
        }

        private void Attach()
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            _hwnd = hwnd;
            var replacement = Marshal.GetFunctionPointerForDelegate(_nativeWndProc);
            _originalWndProc = SetWindowLongPtr(_hwnd, GwlWndProc, replacement);
            if (_originalWndProc == IntPtr.Zero)
            {
                _hwnd = IntPtr.Zero;
            }
        }

        private IntPtr NativeWindowProcedure(
            IntPtr hwnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam)
        {
            try
            {
                if (message == WmNcHitTest
                    && TryClassifyNativePoint(lParam, out var result))
                {
                    return result;
                }

                // With ResizeMode=NoResize Windows may not expose a maximize box,
                // so the native non-client double-click is completed explicitly.
                // It is still gated by the exact same WPF interaction policy.
                if (message == WmNcLButtonDblClk
                    && TryClassifyNativePoint(lParam, out var doubleClickResult)
                    && doubleClickResult == new IntPtr(HtCaption))
                {
                    WindowChromeController.ToggleMaximize(_window);
                    return IntPtr.Zero;
                }
            }
            catch (Exception)
            {
                // A native callback must never let a WPF/layout exception cross
                // the unmanaged boundary; the original procedure remains the
                // safe fallback for this message.
            }

            return CallWindowProc(_originalWndProc, hwnd, message, wParam, lParam);
        }

        private bool TryClassifyNativePoint(IntPtr lParam, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (_disposed)
            {
                return false;
            }

            var value = lParam.ToInt64();
            var screenPixels = new Point(
                unchecked((short)(value & 0xFFFF)),
                unchecked((short)((value >> 16) & 0xFFFF)));

            Point dip;
            try
            {
                dip = ScreenPixelsToDip(_window, screenPixels);
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            var width = _window.ActualWidth > 0 ? _window.ActualWidth : _window.Width;
            var height = _window.ActualHeight > 0 ? _window.ActualHeight : _window.Height;
            if (width <= 0 || height <= 0 || !new Rect(0, 0, width, height).Contains(dip))
            {
                return false;
            }

            DependencyObject? hit = null;
            try
            {
                hit = _window.InputHitTest(dip) as DependencyObject;
            }
            catch (InvalidOperationException)
            {
                // Layout may still be materializing during the first native hit test.
            }

            // A hit-test-invisible background has no DependencyObject to walk;
            // inside this window it still follows the global background default.
            result = hit is null
                ? new IntPtr(HtCaption)
                : Classify(hit, _window);
            return true;
        }

        private delegate IntPtr NativeWndProc(
            IntPtr hwnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(
            IntPtr hwnd,
            int index,
            IntPtr newLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallWindowProc(
            IntPtr previousWndProc,
            IntPtr hwnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam);
    }
}
