using System;
using System.Collections.Generic;
using System.Management;
using System.Linq;
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

    /// <summary>
    /// Optional projection seam for the static inventory path.  Keeping this
    /// separate from <see cref="IWmiInventorySource"/> leaves existing test and
    /// legacy consumers on the original SELECT * contract.
    /// </summary>
    public interface IProjectedWmiInventorySource
    {
        IReadOnlyList<IInventoryRow> QueryProjected(
            string wmiClass,
            string? scope,
            IReadOnlyCollection<string> requiredProperties);
    }

    public sealed class WmiInventorySource : IWmiInventorySource, IProjectedWmiInventorySource
    {
        private const string DefaultScope = @"root\CIMV2";

        public IReadOnlyList<IInventoryRow> Query(string wmiClass, string? scope = null)
            => QueryCore(wmiClass, scope, properties: null);

        public IReadOnlyList<IInventoryRow> Query(
            string wmiClass,
            string? scope,
            IReadOnlyCollection<string> requiredProperties) =>
            QueryCore(wmiClass, scope, requiredProperties);

        public IReadOnlyList<IInventoryRow> QueryProjected(
            string wmiClass,
            string? scope,
            IReadOnlyCollection<string> requiredProperties) =>
            QueryCore(wmiClass, scope, requiredProperties);

        /// <summary>Builds a safe WQL projection for the known inventory classes.</summary>
        public static string BuildSelectQuery(
            string wmiClass,
            IReadOnlyCollection<string>? requiredProperties = null)
        {
            if (!IsIdentifier(wmiClass))
            {
                throw new ArgumentException("WMI class must be an identifier.", nameof(wmiClass));
            }

            if (requiredProperties is null || requiredProperties.Count == 0)
            {
                return $"SELECT * FROM {wmiClass}";
            }

            var properties = requiredProperties
                .Where(IsIdentifier)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (properties.Length != requiredProperties.Count)
            {
                throw new ArgumentException("WMI properties must be identifiers.", nameof(requiredProperties));
            }

            return $"SELECT {string.Join(",", properties)} FROM {wmiClass}";
        }

        private IReadOnlyList<IInventoryRow> QueryCore(
            string wmiClass,
            string? scope,
            IReadOnlyCollection<string>? properties)
        {
            try
            {
                var rows = new List<IInventoryRow>();
                using var searcher = new ManagementObjectSearcher(
                    scope ?? DefaultScope,
                    BuildSelectQuery(wmiClass, properties));
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

        private static bool IsIdentifier(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)
                || !(char.IsLetter(value[0]) || value[0] == '_'))
            {
                return false;
            }

            for (var index = 1; index < value.Length; index++)
            {
                var character = value[index];
                if (!(char.IsLetterOrDigit(character) || character == '_'))
                {
                    return false;
                }
            }

            return true;
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
