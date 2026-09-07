using System.Windows;

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
    }
}