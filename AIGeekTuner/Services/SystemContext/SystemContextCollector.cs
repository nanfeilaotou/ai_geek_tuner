using System.Globalization;
using System.Management;
using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Context
{
    public sealed class SystemContextCollector : ISystemContextCollector
    {
        private const string WmiScope = @"root\CIMV2";

        public async Task<SystemContext> CollectAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var operatingSystemTask = QuerySafelyAsync(
                QueryOperatingSystem,
                cancellationToken);
            var processorTask = QuerySafelyAsync(
                QueryProcessors,
                cancellationToken);
            var memoryTask = QuerySafelyAsync(
                QueryTotalMemory,
                cancellationToken);

            await Task.WhenAll(
                operatingSystemTask,
                processorTask,
                memoryTask);

            var operatingSystem = await operatingSystemTask;
            var processor = await processorTask;

            return new SystemContext
            {
                OperatingSystemVersion = operatingSystem?.Version,
                CpuName = processor?.Name,
                CpuCoreCount = processor?.CoreCount,
                TotalMemoryBytes = await memoryTask,
                SystemBootTime = operatingSystem?.BootTime
            };
        }

        private static OperatingSystemSnapshot QueryOperatingSystem()
        {
            using var searcher = CreateSearcher(
                "SELECT Caption, Version, LastBootUpTime FROM Win32_OperatingSystem");
            using var results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    var caption = ReadString(item, "Caption");
                    var version = ReadString(item, "Version");
                    var displayVersion = caption switch
                    {
                        not null when version is not null => $"{caption} {version}",
                        not null => caption,
                        _ => version
                    };

                    return new OperatingSystemSnapshot(
                        displayVersion,
                        ReadDmtfDateTime(item, "LastBootUpTime"));
                }
            }

            return new OperatingSystemSnapshot(null, null);
        }

        private static ProcessorSnapshot QueryProcessors()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ulong coreCount = 0;
            var hasCoreCount = false;

            using var searcher = CreateSearcher(
                "SELECT Name, NumberOfCores FROM Win32_Processor");
            using var results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    var name = ReadString(item, "Name");
                    if (name is not null)
                    {
                        names.Add(name);
                    }

                    var itemCoreCount = ReadUInt32(item, "NumberOfCores");
                    if (itemCoreCount is > 0)
                    {
                        coreCount = checked(coreCount + itemCoreCount.Value);
                        hasCoreCount = true;
                    }
                }
            }

            return new ProcessorSnapshot(
                names.Count == 0
                    ? null
                    : string.Join(" / ", names.Order(StringComparer.OrdinalIgnoreCase)),
                hasCoreCount && coreCount <= uint.MaxValue
                    ? (uint)coreCount
                    : null);
        }

        private static ulong? QueryTotalMemory()
        {
            using var searcher = CreateSearcher(
                "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            using var results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    var totalMemory = ReadUInt64(item, "TotalPhysicalMemory");
                    return totalMemory is > 0 ? totalMemory : null;
                }
            }

            return null;
        }

        private static ManagementObjectSearcher CreateSearcher(string query) =>
            new(WmiScope, query);

        private static string? ReadString(
            ManagementBaseObject item,
            string propertyName)
        {
            var value = item[propertyName];
            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static uint? ReadUInt32(
            ManagementBaseObject item,
            string propertyName)
        {
            try
            {
                var value = item[propertyName];
                return value is null
                    ? null
                    : Convert.ToUInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (
                exception is FormatException or OverflowException)
            {
                return null;
            }
        }

        private static ulong? ReadUInt64(
            ManagementBaseObject item,
            string propertyName)
        {
            try
            {
                var value = item[propertyName];
                return value is null
                    ? null
                    : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (
                exception is FormatException or OverflowException)
            {
                return null;
            }
        }

        private static DateTimeOffset? ReadDmtfDateTime(
            ManagementBaseObject item,
            string propertyName)
        {
            var value = ReadString(item, propertyName);
            if (value is null)
            {
                return null;
            }

            try
            {
                var localDateTime = ManagementDateTimeConverter.ToDateTime(value);
                if (localDateTime.Kind == DateTimeKind.Unspecified)
                {
                    var localOffset = TimeZoneInfo.Local.GetUtcOffset(localDateTime);
                    return new DateTimeOffset(localDateTime, localOffset)
                        .ToUniversalTime();
                }

                return new DateTimeOffset(localDateTime).ToUniversalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static async Task<T?> QuerySafelyAsync<T>(
            Func<T> query,
            CancellationToken cancellationToken)
        {
            try
            {
                return await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = query();
                    cancellationToken.ThrowIfCancellationRequested();
                    return result;
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // SystemContext 是辅助信息；单项 WMI 失败时保留空字段。
                return default;
            }
        }

        private sealed record OperatingSystemSnapshot(
            string? Version,
            DateTimeOffset? BootTime);

        private sealed record ProcessorSnapshot(
            string? Name,
            uint? CoreCount);
    }
}
