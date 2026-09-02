using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// Gate H：GDI 显示模式枚举（当前分辨率/刷新率/主屏/监视器 PNP id）。
    /// 只读查询当前设置；不改分辨率/布局。失败返回空集（Gate M）。
    /// </summary>
    public interface IDisplayModeSource
    {
        IReadOnlyList<MonitorInventoryMapper.DisplayModeDescriptor> GetModes();
    }

    /// <summary>
    /// DEVMODEW ABI 镜像结构 —— 字段与顺序逐项对照 Windows SDK 头文件
    /// um\wingdi.h 中 <c>typedef struct _devicemodeW</c>（x64 自然对齐）：
    /// dmDeviceName[32]W=0..64 · dmSpecVersion/dmDriverVersion/dmSize/dmDriverExtra
    /// 4×WORD=64..72 · dmFields=72 · DUMMYUNIONNAME（printer 侧 8×short 与
    /// display 侧 POINTL+2×DWORD 同为 16 字节）=76..92 · dmColor..dmCollate
    /// 5×short=92..102 · dmFormName[32]W=102..166 · dmLogPixels=166 ·
    /// dmBitsPerPel=168 · dmPelsWidth=172 · dmPelsHeight=176 ·
    /// DUMMYUNIONNAME2(dmDisplayFlags|dmNup)=180 · dmDisplayFrequency=184 ·
    /// dmICMMethod..dmPanningHeight 8×DWORD=188..220 · sizeof(DEVMODEW)=220。
    /// 另见 https://learn.microsoft.com/windows/win32/api/wingdi/ns-wingdi-devmodew 。
    /// 所有字段偏移一律经 <see cref="DevModeWLayout"/> 由 Marshal.OffsetOf 从本结构
    /// 推导，禁止在业务代码手写魔数；GdiDevModeWLayoutTests 将推导结果与上述
    /// documented 值做回归锁定。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public unsafe struct DevModeW
    {
        public fixed char dmDeviceName[32];
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;

        // DUMMYUNIONNAME 取 display 侧展开（POINTL dmPosition + DWORD
        // dmDisplayOrientation + DWORD dmDisplayFixedOutput），与 printer 侧同为
        // 16 字节；此处仅用于推导偏移，两种展开对后续字段偏移等价。
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        public fixed char dmFormName[32];
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;

        // DUMMYUNIONNAME2：union { DWORD dmDisplayFlags; DWORD dmNup; }
        public uint dmDisplayFlags;

        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    /// <summary>
    /// DEVMODEW 字段偏移与 DM_* 字段标志的唯一权威来源。
    /// 偏移在类型初始化时经 Marshal.OffsetOf 从 <see cref="DevModeW"/>（documented
    /// ABI 镜像）推导，而非手写常量；DM_ 标志取自 wingdi.h。
    /// </summary>
    public static class DevModeWLayout
    {
        public const uint DMPelsWidth = 0x00080000;
        public const uint DMPelsHeight = 0x00100000;
        public const uint DMDisplayFrequency = 0x00400000;

        /// <summary>documented sizeof(DEVMODEW)（x64）。</summary>
        public static readonly int StructSize = Marshal.SizeOf<DevModeW>();
        /// <summary>dmSize（ushort）字段偏移。</summary>
        public static readonly int OffsetSize = (int)Marshal.OffsetOf<DevModeW>(nameof(DevModeW.dmSize));
        /// <summary>dmFields（uint）字段偏移。</summary>
        public static readonly int OffsetFields = (int)Marshal.OffsetOf<DevModeW>(nameof(DevModeW.dmFields));
        /// <summary>dmPelsWidth（uint）字段偏移。</summary>
        public static readonly int OffsetPelsWidth = (int)Marshal.OffsetOf<DevModeW>(nameof(DevModeW.dmPelsWidth));
        /// <summary>dmPelsHeight（uint）字段偏移。</summary>
        public static readonly int OffsetPelsHeight = (int)Marshal.OffsetOf<DevModeW>(nameof(DevModeW.dmPelsHeight));
        /// <summary>dmDisplayFrequency（uint）字段偏移。</summary>
        public static readonly int OffsetDisplayFrequency = (int)Marshal.OffsetOf<DevModeW>(nameof(DevModeW.dmDisplayFrequency));
    }

    public sealed class GdiDisplayModeSource : IDisplayModeSource
    {
        private const int EnumCurrentSettings = -1;
        private const uint DisplayDeviceAttachedToDesktop = 0x00000001;
        private const uint DisplayDevicePrimaryDevice = 0x00000004;
        private const uint EddGetDeviceInterfaceName = 0x00000001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private unsafe struct DISPLAY_DEVICE
        {
            public int cb;
            public fixed char DeviceName[32];
            public fixed char DeviceString[128];
            public uint StateFlags;
            public fixed char DeviceID[128];
            public fixed char DeviceKey[128];
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(
            string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsEx(
            string lpszDeviceName, int iModeNum, IntPtr lpDevMode, uint dwFlags);

        public unsafe IReadOnlyList<MonitorInventoryMapper.DisplayModeDescriptor> GetModes()
        {
            var results = new List<MonitorInventoryMapper.DisplayModeDescriptor>();
            try
            {
                for (uint adapterIndex = 0; ; adapterIndex++)
                {
                    var adapter = NewDisplayDevice();
                    if (!EnumDisplayDevices(null, adapterIndex, ref adapter, 0))
                    {
                        break;
                    }

                    var adapterName = ReadFixedString(adapter.DeviceName, 32);
                    var isActive = (adapter.StateFlags & DisplayDeviceAttachedToDesktop) != 0;
                    var isPrimary = (adapter.StateFlags & DisplayDevicePrimaryDevice) != 0;
                    if (!isActive || string.IsNullOrEmpty(adapterName))
                    {
                        continue;
                    }

                    // 适配器下的监视器设备（DeviceID = "MONITOR\ACI24D3\{...}"）。
                    var monitor = NewDisplayDevice();
                    string? monitorDeviceId = null;
                    if (EnumDisplayDevices(adapterName, 0, ref monitor, EddGetDeviceInterfaceName))
                    {
                        monitorDeviceId = ReadFixedString(monitor.DeviceID, 128);
                    }

                    var devMode = Marshal.AllocHGlobal(DevModeBufferBytes);
                    try
                    {
                        ZeroMemory(devMode, DevModeBufferBytes);
                        Marshal.WriteInt16(devMode, DevModeWLayout.OffsetSize, (short)DevModeWLayout.StructSize);
                        var ok = EnumDisplaySettingsEx(adapterName, EnumCurrentSettings, devMode, 0);
                        if (!ok)
                        {
                            Services.Diagnostics.ExceptionLogWriter.Write(
                                new Exception("EnumDisplaySettingsEx failed err=" + Marshal.GetLastWin32Error()
                                    + " adapter=" + adapterName + " dmSize=" + Marshal.ReadInt16(devMode, DevModeWLayout.OffsetSize)),
                                "Inventory/GdiDisplay/debug");
                        }

                        var descriptor = ok
                            ? ParseCurrentDevMode(devMode, adapterName, monitorDeviceId, isPrimary)
                            : null;
                        if (descriptor is not null)
                        {
                            results.Add(descriptor);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(devMode);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ExceptionLogWriter.Write(exception, "Inventory/GdiDisplay");
            }

            return results;
        }

        // EnumDisplaySettingsEx 以 dmSize 为准填充结构体本体（dmDriverExtra=0 时
        // 不写额外区）；缓冲区预留裕量，避免任何驱动实现越界写入。
        private static readonly int DevModeBufferBytes = DevModeWLayout.StructSize + 64;

        /// <summary>
        /// 按 documented DEVMODEW 布局（<see cref="DevModeWLayout"/>）解析
        /// EnumDisplaySettingsEx 返回的当前显示模式。
        /// dmFields 未声明 DM_PELSWIDTH/DM_PELSHEIGHT 时返回 null；
        /// 未声明 DM_DISPLAYFREQUENCY 时刷新率为 0。测试用合成缓冲区即可覆盖，
        /// 不依赖真实显示器。
        /// </summary>
        public static MonitorInventoryMapper.DisplayModeDescriptor? ParseCurrentDevMode(
            IntPtr devMode, string deviceName, string? monitorDeviceId, bool isPrimary)
        {
            var fields = (uint)Marshal.ReadInt32(devMode, DevModeWLayout.OffsetFields);
            if ((fields & (DevModeWLayout.DMPelsWidth | DevModeWLayout.DMPelsHeight))
                != (DevModeWLayout.DMPelsWidth | DevModeWLayout.DMPelsHeight))
            {
                return null;
            }

            var width = (int)Marshal.ReadInt32(devMode, DevModeWLayout.OffsetPelsWidth);
            var height = (int)Marshal.ReadInt32(devMode, DevModeWLayout.OffsetPelsHeight);
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var frequency = (fields & DevModeWLayout.DMDisplayFrequency) != 0
                ? (uint)Marshal.ReadInt32(devMode, DevModeWLayout.OffsetDisplayFrequency)
                : 0u;

            return new MonitorInventoryMapper.DisplayModeDescriptor(
                DeviceName: deviceName,
                MonitorDeviceId: monitorDeviceId,
                Width: width,
                Height: height,
                RefreshRateHz: frequency,
                IsPrimary: isPrimary,
                IsActive: true);
        }

        private static unsafe void ZeroMemory(IntPtr pointer, int size)
        {
            var p = (byte*)pointer;
            for (var i = 0; i < size; i++)
            {
                p[i] = 0;
            }
        }

        private static DISPLAY_DEVICE NewDisplayDevice()
        {
            var device = new DISPLAY_DEVICE
            {
                cb = Marshal.SizeOf<DISPLAY_DEVICE>(),
            };
            return device;
        }

        private static unsafe string ReadFixedString(char* buffer, int length)
        {
            var builder = new System.Text.StringBuilder(length);
            for (var i = 0; i < length; i++)
            {
                if (buffer[i] == '\0')
                {
                    break;
                }

                builder.Append(buffer[i]);
            }

            return builder.ToString();
        }
    }
}