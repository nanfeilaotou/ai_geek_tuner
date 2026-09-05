using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AIGeekTuner.Views.Behaviors
{
    /// <summary>
    /// Best-effort Windows 11 native rounded-corner preference.
    /// </summary>
    public static class WindowCornerController
    {
        private const int DwmWindowCornerPreference = 33;
        private const int DwmWindowCornerRound = 2;

        public static bool TryApplyRoundedCorners(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);
            return TryApplyRoundedCorners(new WindowInteropHelper(window).Handle);
        }

        public static bool TryApplyRoundedCorners(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var preference = DwmWindowCornerRound;
                return DwmSetWindowAttribute(
                    hwnd,
                    DwmWindowCornerPreference,
                    ref preference,
                    sizeof(int)) == 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        [DllImport("dwmapi.dll", ExactSpelling = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int attribute,
            ref int value,
            int valueSize);
    }
}
