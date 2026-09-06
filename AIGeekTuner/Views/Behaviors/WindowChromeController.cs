using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AIGeekTuner.Views.Behaviors
{
    public static class WindowChromeController
    {
        public static void Minimize(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);
            window.WindowState = WindowState.Minimized;
        }

        public static void ToggleMaximize(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        public static void Close(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);
            window.Close();
        }

        public static bool IsMaximized(WindowState state) => state == WindowState.Maximized;

        /// <summary>
        /// M5.2C：WindowStyle=None + ResizeMode=NoResize 的窗口原生样式缺少
        /// WS_MINIMIZEBOX / WS_MAXIMIZEBOX，shell 据此认为窗口"不可最小化"，
        /// 导致窗口已聚焦时点击任务栏按钮不会最小化（标准最小化/还原消息
        /// 链路本身没有被吃掉）。补回这两个标准位即恢复 Windows shell 默认
        /// 语义；不引入托盘、热键或二次点击假逻辑。
        /// </summary>
        public static void ApplyStandardTaskbarBehavior(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var style = GetWindowLong(hwnd, GWLStyle);
            SetWindowLong(hwnd, GWLStyle, style | WsMaximizeBox | WsMinimizeBox);
        }

        private const int GWLStyle = -16;
        private const int WsMaximizeBox = 0x00010000;
        private const int WsMinimizeBox = 0x00020000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);
    }
}