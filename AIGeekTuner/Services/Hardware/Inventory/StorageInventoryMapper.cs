using System;
using System.Collections.Generic;
using System.Linq;
using AIGeekTuner.Models.Hardware.Inventory;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// Gate G：MSFT_PhysicalDisk（root\Microsoft\Windows\Storage）→ 每块物理盘独立；
    /// MSFT_Partition/MSFT_Volume 构成简洁 child DTO（DriveLetter/FS/Label/Size/Free）。
    /// 不做 SMART、不做 benchmark。BusType/MediaType/HealthStatus 只映射可靠值。
    /// </summary>
    public static class StorageInventoryMapper
    {
        public static IReadOnlyList<StorageDiskInventoryInfo> Map(
            IReadOnlyList<IInventoryRow> physicalDisks,
            IReadOnlyList<IInventoryRow> partitions,
            IReadOnlyList<IInventoryRow> volumes)
        {
            var volumeByLetter = new Dictionary<string, IInventoryRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in volumes)
            {
                var letter = NormalizeDriveLetter(row.String("DriveLetter"));
                if (letter is not null && !volumeByLetter.ContainsKey(letter))
                {
                    volumeByLetter[letter] = row;
                }
            }

            var partitionsByDisk = new Dictionary<uint, List<(string? Letter, IInventoryRow Row)>>();
            foreach (var row in partitions)
            {
                var diskNumber = row.UInt32("DiskNumber");
                if (!diskNumber.HasValue)
                {
                    continue;
                }

                if (!partitionsByDisk.TryGetValue(diskNumber.Value, out var list))
                {
                    list = new List<(string? Letter, IInventoryRow Row)>();
                    partitionsByDisk[diskNumber.Value] = list;
                }

                list.Add((NormalizeDriveLetter(row.String("DriveLetter")), row));
            }

            var results = new List<StorageDiskInventoryInfo>(physicalDisks.Count);
            foreach (var row in physicalDisks)
            {
                var diskNumber = row.UInt32("DeviceId"); // MSFT_PhysicalDisk.DeviceId = 物理盘号
                var partitionsOnDisk = diskNumber.HasValue
                    && partitionsByDisk.TryGetValue(diskNumber.Value, out var found)
                    ? (IReadOnlyList<(string? Letter, IInventoryRow Row)>)found
                    : Array.Empty<(string? Letter, IInventoryRow Row)>();

                var partitionInfos = partitionsOnDisk
                    .Select(entry =>
                    {
                        var letter = entry.Letter
                            ?? NormalizeDriveLetter(entry.Row.String("DriveLetter"));
                        var volume = letter is not null && volumeByLetter.TryGetValue(letter, out var foundVolume)
                            ? foundVolume
                            : null;
                        return new StoragePartitionInfo(
                            DriveLetter: letter,
                            FileSystem: volume?.String("FileSystem"),
                            Label: volume?.String("FileSystemLabel"),
                            SizeBytes: volume?.UInt64("Size") ?? entry.Row.UInt64("Size"),
                            FreeSpaceBytes: volume?.UInt64("SizeRemaining"));
                    })
                    .OrderBy(partition => partition.DriveLetter, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                results.Add(new StorageDiskInventoryInfo(
                    Model: row.String("Model"),
                    FriendlyName: row.String("FriendlyName"),
                    SerialNumber: HardwarePlaceholderFilter.SanitizeSerialNumber(row.String("SerialNumber")),
                    FirmwareVersion: row.String("FirmwareVersion"),
                    SizeBytes: row.UInt64("Size"),
                    BusType: MapBusType(row.UInt32("BusType")),
                    MediaType: MapMediaType(row.UInt32("MediaType")),
                    HealthStatus: MapHealthStatus(row.String("HealthStatus")),
                    DiskNumber: diskNumber,
                    Partitions: partitionInfos,
                    Source: InventorySource.Wmi));
            }

            return results
                .OrderBy(disk => disk.DiskNumber ?? uint.MaxValue)
                .ToArray();
        }

        private static string? NormalizeDriveLetter(string? letter) =>
            string.IsNullOrWhiteSpace(letter) ? null : letter.Trim().TrimEnd(':').ToUpperInvariant();

        // MSFT_PhysicalDisk.BusType：1=SCSI 2=ATAPI 3=ATA 4=IEEE1394 5=SSA 6=FibreChannel
        // 7=USB 8=RAID 9=iSCSI 10=SAS 11=SATA 12=SD 13=MMC 14=Virtual 15=FileBackedVirtual
        internal static string? MapBusType(uint? busType) => busType switch
        {
            1 => "SCSI",
            2 => "ATAPI",
            3 => "ATA",
            6 => "Fibre Channel",
            7 => "USB",
            8 => "RAID",
            9 => "iSCSI",
            10 => "SAS",
            11 => "SATA",
            12 => "SD",
            13 => "MMC",
            17 => "NVMe",
            14 => "Virtual",
            15 => "File-Backed Virtual",
            _ => null, // 4/5 等罕见值不猜。
        };

        // MSFT_PhysicalDisk.MediaType：0=Unspecified 3=HDD 4=SSD 5=SCM
        internal static string? MapMediaType(uint? mediaType) => mediaType switch
        {
            3 => "HDD",
            4 => "SSD",
            5 => "SCM",
            _ => null,
        };

        // MSFT_PhysicalDisk.HealthStatus：0=Healthy 1=Warning 2=Unhealthy（原文透传更稳妥）
        internal static string? MapHealthStatus(string? healthStatus) => healthStatus switch
        {
            "Healthy" or "Warning" or "Unhealthy" => healthStatus,
            _ => null,
        };
    }
}