using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// Gate F：GPU 适配器枚举。主路径 = DXGI IDXGIAdapter::GetDesc（DedicatedVideoMemory
    /// 精确到字节，避开 Win32_VideoController.AdapterRAM 的 32 位 >4GB 截断）；
    /// COM 路径不可用时退回注册表 HardwareInformation.qwMemorySize（驱动报告的
    /// 同源事实）。只读枚举，不修改设备配置；失败返回空集（Gate M）。
    /// </summary>
    public interface IGpuAdapterSource
    {
        IReadOnlyList<GpuInventoryMapper.AdapterDescriptor> GetAdapters();
    }

    public sealed class DxgiAdapterSource : IGpuAdapterSource
    {
        private const uint DisplayClassDeviceMemorySizeMissing = 0;

        [ComImport]
        [Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory
        {
            // IDXGIObject
            [PreserveSig]
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig]
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig]
            int GetPrivateInterface(ref Guid name, out IntPtr ppInterface);
            [PreserveSig]
            int GetParent(ref Guid riid, out IntPtr ppParent);
            // IDXGIFactory
            [PreserveSig]
            int EnumAdapters(uint adapter, out IDXGIAdapter ppAdapter);
            [PreserveSig]
            int MakeWindowAssociation(IntPtr windowHandle, uint flags);
            [PreserveSig]
            int GetWindowAssociation(out IntPtr windowHandle);
            [PreserveSig]
            int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
            [PreserveSig]
            int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        }

        [ComImport]
        [Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter
        {
            // IDXGIObject
            [PreserveSig]
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig]
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig]
            int GetPrivateInterface(ref Guid name, out IntPtr ppInterface);
            [PreserveSig]
            int GetParent(ref Guid riid, out IntPtr ppParent);
            // IDXGIAdapter
            [PreserveSig]
            int EnumOutputs(uint output, out IntPtr ppOutput);
            [PreserveSig]
            int GetDesc(IntPtr pDesc);
            [PreserveSig]
            int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory(ref Guid riid, out IntPtr ppFactory);

        private static readonly Guid FactoryIid = new("7b7166ec-21c7-44ae-b21a-c9ae321ae369");

        // DXGI_ADAPTER_DESC 手动偏移（说明见各读取处）。
        private const int AdapterDescSize = 312;
        private const int DescOffsetVendorId = 256;
        private const int DescOffsetDeviceId = 260;
        private const int DescOffsetDedicatedVideoMemory = 272;
        private const int DescOffsetSharedSystemMemory = 288;

        public IReadOnlyList<GpuInventoryMapper.AdapterDescriptor> GetAdapters()
        {
            var results = CollectViaDxgi();
            if (results.Count == 0)
            {
                results = CollectViaRegistry();
            }

            return results;
        }

        private List<GpuInventoryMapper.AdapterDescriptor> CollectViaDxgi()
        {
            var results = new List<GpuInventoryMapper.AdapterDescriptor>();
            var factoryIid = FactoryIid;
            var hr = CreateDXGIFactory(ref factoryIid, out var factoryPointer);
            if (hr != 0 || factoryPointer == IntPtr.Zero)
            {
                return results;
            }

            try
            {
                var factory = (IDXGIFactory)Marshal.GetObjectForIUnknown(factoryPointer);
                for (uint index = 0; ; index++)
                {
                    if (factory.EnumAdapters(index, out var adapter) != 0 || adapter is null)
                    {
                        break;
                    }

                    try
                    {
                        var descPointer = Marshal.AllocHGlobal(AdapterDescSize);
                        try
                        {
                            ZeroMemory(descPointer, AdapterDescSize);
                            if (adapter.GetDesc(descPointer) == 0)
                            {
                                var description = Marshal.PtrToStringUni(descPointer);
                                if (!string.IsNullOrEmpty(description))
                                {
                                    results.Add(new GpuInventoryMapper.AdapterDescriptor(
                                        Description: description,
                                        VendorId: (ushort)Marshal.ReadInt32(descPointer, DescOffsetVendorId),
                                        DeviceId: (ushort)Marshal.ReadInt32(descPointer, DescOffsetDeviceId),
                                        DedicatedVideoMemoryBytes:
                                            (ulong)Marshal.ReadInt64(descPointer, DescOffsetDedicatedVideoMemory),
                                        SharedSystemMemoryBytes:
                                            (ulong)Marshal.ReadInt64(descPointer, DescOffsetSharedSystemMemory)));
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(descPointer);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ExceptionLogWriter.Write(exception, "Inventory/DXGI");
            }
            finally
            {
                Marshal.Release(factoryPointer);
            }

            return results;
        }

        /// <summary>
        /// 注册表兜底：显示类驱动的 HardwareInformation.qwMemorySize 与 DXGI 读到的是
        /// 同一份驱动报告的显存事实（QWORD，无 32 位截断）。
        /// </summary>
        private static List<GpuInventoryMapper.AdapterDescriptor> CollectViaRegistry()
        {
            var results = new List<GpuInventoryMapper.AdapterDescriptor>();
            try
            {
                using var classKey = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (classKey is null)
                {
                    return results;
                }

                foreach (var subKeyName in classKey.GetSubKeyNames())
                {
                    if (subKeyName.Length != 4 || !subKeyName.StartsWith("000", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        using var subKey = classKey.OpenSubKey(subKeyName);
                        if (subKey is null)
                        {
                            continue;
                        }

                        var description = subKey.GetValue("DriverDesc") as string;
                        var memorySize = ReadMemorySize(subKey);
                        if (string.IsNullOrEmpty(description))
                        {
                            continue;
                        }

                        results.Add(new GpuInventoryMapper.AdapterDescriptor(
                            Description: description,
                            VendorId: 0,
                            DeviceId: 0,
                            DedicatedVideoMemoryBytes: memorySize,
                            SharedSystemMemoryBytes: 0));
                    }
                    catch (Exception exception)
                    {
                        ExceptionLogWriter.Write(exception, "Inventory/DXGI/registry-key");
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ExceptionLogWriter.Write(exception, "Inventory/DXGI/registry");
            }

            return results;
        }

        private static ulong ReadMemorySize(RegistryKey subKey)
        {
            var value = subKey.GetValue("HardwareInformation.qwMemorySize");
            return value switch
            {
                long qword => qword > 0 ? (ulong)qword : 0,
                int dword => dword > 0 ? (ulong)dword : 0,
                byte[] bytes when bytes.Length == 8 => BitConverter.ToUInt64(bytes, 0),
                _ => 0,
            };
        }

        private static unsafe void ZeroMemory(IntPtr pointer, int size)
        {
            var p = (byte*)pointer;
            for (var i = 0; i < size; i++)
            {
                p[i] = 0;
            }
        }
    }
}
