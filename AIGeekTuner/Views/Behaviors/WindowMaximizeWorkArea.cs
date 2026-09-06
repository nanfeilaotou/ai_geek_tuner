using System;
using System.Runtime.InteropServices;

namespace AIGeekTuner.Views.Behaviors;

/// <summary>
/// M5.2C：无边框窗口最大化时，把最大化 bounds 精确钳制到"窗口当前所在
/// 显示器"的工作区（MonitorFromWindow + GetMonitorInfo 的 rcWork）：
/// 不遮任务栏、四边不留白缝；任务栏高度永远来自系统，不硬编码；
/// ptMaxPosition 采用 monitor 相对坐标，多显示器 / 任意 DPI 语义一致。
/// </summary>
public static class WindowMaximizeWorkArea
{
    public const int WmGetMinMaxInfo = 0x0024;

    private const int MonitorDefaultToNearest = 2;

    /// <summary>
    /// 处理 WM_GETMINMAXINFO：写入工作区 bounds 后完全接管该消息，
    /// 避免上层把 ptMaxSize 还原成整屏 bounds（这正是旧版最大化盖住
    /// 任务栏并产生边缘缝隙的根因）。返回 false 表示未处理，调用方继续默认链路。
    /// </summary>
    public static bool HandleGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        if (hwnd == IntPtr.Zero || lParam == IntPtr.Zero)
        {
            return false;
        }

        var hMonitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (hMonitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = MonitorInfo.Create();
        if (!GetMonitorInfoW(hMonitor, ref monitorInfo))
        {
            return false;
        }

        if (Marshal.PtrToStructure(lParam, typeof(MinMaxInfo)) is not MinMaxInfo minMaxInfo)
        {
            return false;
        }

        FillFromMonitor(ref minMaxInfo, monitorInfo.RcWork, monitorInfo.RcMonitor);
        Marshal.StructureToPtr(minMaxInfo, lParam, false);
        return true;
    }

    /// <summary>
    /// ptMaxSize = rcWork 尺寸；ptMaxPosition = rcWork 原点相对 monitor 原点的
    /// 偏移。全部为物理像素，由 Windows 按所在显示器 DPI 解释，因此无需在
    /// 这里做 DIP 换算，也不引入任何固定补偿边距。
    /// </summary>
    internal static void FillFromMonitor(
        ref MinMaxInfo minMaxInfo,
        NativeRect workArea,
        NativeRect monitorBounds)
    {
        minMaxInfo.PtMaxSize = new NativePoint
        {
            X = workArea.Right - workArea.Left,
            Y = workArea.Bottom - workArea.Top
        };
        minMaxInfo.PtMaxPosition = new NativePoint
        {
            X = workArea.Left - monitorBounds.Left,
            Y = workArea.Top - monitorBounds.Top
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo
    {
        public NativePoint PtReserved;
        public NativePoint PtMaxSize;
        public NativePoint PtMaxPosition;
        public NativePoint PtMinTrackSize;
        public NativePoint PtMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public uint CbSize;
        public NativeRect RcMonitor;
        public NativeRect RcWork;
        public uint DwFlags;

        public static MonitorInfo Create() => new()
        {
            CbSize = (uint)Marshal.SizeOf<MonitorInfo>()
        };
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo info);
}
