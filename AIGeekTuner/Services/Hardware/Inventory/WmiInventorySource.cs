using System;
using System.Collections.Generic;
using System.Management;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>WMI 只读查询 seam（Gate N）：生产用 System.Management，测试用假行。</summary>
    public interface IWmiInventorySource
    {
        /// <summary>查询一个 WMI 类（默认 root\CIMV2），返回容错行；查询失败返回空集。</summary>
        IReadOnlyList<IInventoryRow> Query(string wmiClass, string? scope = null);

        /// <summary>root\wmi WmiMonitorDescriptorMethods.WmiGetMonitorRawEEdidV1Block（best-effort）。</summary>
        byte[]? GetMonitorEdid(string instanceName);
    }

    public sealed class WmiInventorySource : IWmiInventorySource
    {
        private const string DefaultScope = @"root\CIMV2";

        public IReadOnlyList<IInventoryRow> Query(string wmiClass, string? scope = null)
        {
            try
            {
                var rows = new List<IInventoryRow>();
                using var searcher = new ManagementObjectSearcher(scope ?? DefaultScope, $"SELECT * FROM {wmiClass}");
                using var results = searcher.Get();
                foreach (ManagementBaseObject item in results)
                {
                    rows.Add(new WmiInventoryRow(item));
                }

                return rows;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ExceptionLogWriter.Write(exception, $"Inventory/Wmi/{wmiClass}");
                return Array.Empty<IInventoryRow>();
            }
        }

        public byte[]? GetMonitorEdid(string instanceName)
        {
            try
            {
                // WQL 字符串字面量：反斜杠与单引号都需要转义。
                var escaped = instanceName
                    .Replace("\\", "\\\\\\", StringComparison.Ordinal)
                    .Replace("'", "\\'", StringComparison.Ordinal);
                using var searcher = new ManagementObjectSearcher(
                    @"root\wmi",
                    $"SELECT * FROM WmiMonitorDescriptorMethods WHERE InstanceName = '{escaped}'");
                using var results = searcher.Get();
                foreach (ManagementBaseObject item in results)
                {
                    using (item)
                    {
                        if (item is not ManagementObject monitorMethod)
                        {
                            continue;
                        }

                        using var output = monitorMethod.InvokeMethod(
                            "WmiGetMonitorRawEEdidV1Block",
                            new object?[] { (uint)0 }) as ManagementBaseObject;
                        if (output?["BlockContent"] is byte[] block && block.Length > 0)
                        {
                            return block;
                        }
                    }
                }

                return null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ExceptionLogWriter.Write(exception, "Inventory/Wmi/Edid");
                return null;
            }
        }
    }
}