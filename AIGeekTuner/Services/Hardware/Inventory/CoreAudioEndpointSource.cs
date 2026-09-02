using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// Gate I：Core Audio MMDevice 枚举（playback/capture endpoint + default 标记）。
    /// 只读枚举；不做音量/切换/测试。失败返回空集（Gate M）。
    /// </summary>
    public interface IAudioEndpointSource
    {
        IReadOnlyList<AudioInventoryMapper.EndpointDescriptor> GetEndpoints();
    }

    public sealed class CoreAudioEndpointSource : IAudioEndpointSource
    {
        private enum EDataFlow
        {
            eRender = 0,
            eCapture = 1,
        }

        private const int DeviceStateActive = 0x1;
        private const int ClsCtxInProcServer = 0x1;
        private static readonly Guid DeviceFriendlyNameKey =
            new("a45c254e-df1c-4efd-8020-67d146a850e0"); // pid 14

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private sealed class MMDeviceEnumeratorCom { }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig]
            int GetCount(out int count);
            [PreserveSig]
            int Item(int index, out IMMDevice device);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iId, int clsCtx, IntPtr activationParams, out IntPtr instance);
            [PreserveSig]
            int OpenPropertyStore(int access, out IPropertyStore properties);
            [PreserveSig]
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig]
            int GetState(out int state);
        }

        [ComImport]
        [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig]
            int GetCount(out int count);
            [PreserveSig]
            int GetAt(int index, out PropertyKey key);
            [PreserveSig]
            int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig]
            int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig]
            int Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid FormatId;
            public int PropertyId;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort VariantType;
            [FieldOffset(8)] public IntPtr PointerValue;
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant variant);

        public IReadOnlyList<AudioInventoryMapper.EndpointDescriptor> GetEndpoints()
        {
            var results = new List<AudioInventoryMapper.EndpointDescriptor>();
            try
            {
                var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorCom();
                var defaultIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectDefaultIds(enumerator, defaultIds);

                AppendFlow(enumerator, EDataFlow.eRender, results, defaultIds);
                AppendFlow(enumerator, EDataFlow.eCapture, results, defaultIds);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ExceptionLogWriter.Write(exception, "Inventory/CoreAudio");
            }

            return results;
        }

        private static void CollectDefaultIds(IMMDeviceEnumerator enumerator, HashSet<string> ids)
        {
            if (enumerator.GetDefaultAudioEndpoint((int)EDataFlow.eRender, 0, out var render) == 0
                && render is not null)
            {
                AddDefaultId(render, ids);
            }

            if (enumerator.GetDefaultAudioEndpoint((int)EDataFlow.eCapture, 0, out var capture) == 0
                && capture is not null)
            {
                AddDefaultId(capture, ids);
            }
        }

        private static void AddDefaultId(IMMDevice device, HashSet<string> ids)
        {
            try
            {
                if (device.GetId(out var id) == 0 && id is not null)
                {
                    ids.Add(id);
                }
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "Inventory/CoreAudio/default-id");
            }
        }

        private static void AppendFlow(
            IMMDeviceEnumerator enumerator,
            EDataFlow flow,
            List<AudioInventoryMapper.EndpointDescriptor> results,
            HashSet<string> defaultIds)
        {
            if (enumerator.EnumAudioEndpoints((int)flow, DeviceStateActive, out var collection) != 0
                || collection is null)
            {
                return;
            }

            try
            {
                if (collection.GetCount(out var count) != 0)
                {
                    return;
                }

                for (var index = 0; index < count; index++)
                {
                    if (collection.Item(index, out var device) != 0 || device is null)
                    {
                        continue;
                    }

                    try
                    {
                        if (device.GetId(out var id) != 0 || id is null)
                        {
                            continue;
                        }

                        device.GetState(out var state);
                        var friendlyName = ReadFriendlyName(device);
                        results.Add(new AudioInventoryMapper.EndpointDescriptor(
                            Id: id,
                            FriendlyName: friendlyName,
                            Direction: flow == EDataFlow.eRender
                                ? AudioEndpointDirection.Playback
                                : AudioEndpointDirection.Capture,
                            State: MapState(state),
                            IsDefault: defaultIds.Contains(id)));
                    }
                    catch (Exception exception)
                    {
                        // 单个 endpoint 异常不拖垮音频 inventory（Gate M）。
                        ExceptionLogWriter.Write(exception, "Inventory/CoreAudio/endpoint");
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(collection);
            }
        }

        private static string? ReadFriendlyName(IMMDevice device)
        {
            if (device.OpenPropertyStore(0 /* STGM_READ */, out var properties) != 0
                || properties is null)
            {
                return null;
            }

            try
            {
                var friendly = ReadProperty(properties, 14);
                return friendly ?? ReadProperty(properties, 2);
            }
            finally
            {
                Marshal.ReleaseComObject(properties);
            }
        }

        private static string? ReadProperty(IPropertyStore properties, int propertyId)
        {
            var key = new PropertyKey
            {
                FormatId = DeviceFriendlyNameKey,
                PropertyId = propertyId,
            };
            if (properties.GetValue(ref key, out var variant) != 0)
            {
                return null;
            }

            try
            {
                // VT_LPWSTR = 31
                return variant.VariantType == 31 && variant.PointerValue != IntPtr.Zero
                    ? HardwarePlaceholderFilter.Sanitize(Marshal.PtrToStringUni(variant.PointerValue))
                    : null;
            }
            finally
            {
                PropVariantClear(ref variant);
            }
        }

        private static string? MapState(int state) => state switch
        {
            0x1 => "Active",
            0x2 => "Disabled",
            0x4 => "NotPresent",
            0x8 => "Unplugged",
            _ => null,
        };
    }
}