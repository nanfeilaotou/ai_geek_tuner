using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Services.Hardware.Inventory;
using Xunit;

namespace AIGeekTuner.Tests.Services.Hardware.Inventory
{
    /// <summary>Gate N：system / memory / GPU / storage mapper（全部假 WMI 行）。</summary>
    public sealed class StaticInventoryMapperTests
    {
        private static DictionaryInventoryRow Row(params (string Key, object? Value)[] values)
        {
            var dictionary = new Dictionary<string, object?>();
            foreach (var (key, value) in values)
            {
                dictionary[key] = value;
            }

            return new DictionaryInventoryRow(dictionary);
        }

        // ---- Gate D：CPU / 板 / BIOS / OS ----

        [Fact]
        public void Cpu_Maps_Topology_Clocks_AndPlaceholderFiltering()
        {
            var rows = new IInventoryRow[]
            {
                Row(
                    ("Name", "Intel(R) Core(TM) Ultra 7"),
                    ("Manufacturer", "GenuineIntel"),
                    ("Architecture", 9u),
                    ("NumberOfCores", 12u),
                    ("NumberOfLogicalProcessors", 16u),
                    ("MaxClockSpeed", 5400u),
                    ("CurrentClockSpeed", 3200u),
                    ("VirtualizationFirmwareEnabled", true)),
            };
            var cache = new IInventoryRow[]
            {
                Row(("Level", 3u), ("InstalledSize", 20480u)),   // L2
                Row(("Level", 4u), ("InstalledSize", 36864u)),   // L3
                Row(("Level", 3u), ("InstalledSize", 1024u)),    // 同级取最大
            };

            var cpu = SystemInventoryMappers.MapCpu(rows, cache);

            Assert.NotNull(cpu);
            Assert.Equal("Intel(R) Core(TM) Ultra 7", cpu!.Name);
            Assert.Equal("GenuineIntel", cpu.Manufacturer);
            Assert.Equal("x64", cpu.Architecture);
            Assert.Equal(12u, cpu.PhysicalCores);
            Assert.Equal(16u, cpu.LogicalCores);
            // V2-M4.5C Gate A 语义审计：MaxClockSpeed 是额定基准（base），
            // CurrentClockSpeed 是当前运行频率（不得当 base）；Turbo max 无可靠来源 → null。
            Assert.Null(cpu.MaxClockSpeedMHz);
            Assert.Equal(5400u, cpu.BaseClockSpeedMHz);
            Assert.True(cpu.VirtualizationFirmwareEnabled);
        }

        [Fact]
        public void Cpu_BaseClock_IsRatedBase_NotCurrentSpeed()
        {
            // 真机审计（i9-13980HX）：MaxClockSpeed=2200=额定基准，
            // CurrentClockSpeed 随负载波动——绝不能把 Current 当 base。
            var rows = new IInventoryRow[]
            {
                Row(("Name", "X"), ("MaxClockSpeed", 2200u), ("CurrentClockSpeed", 4600u)),
            };

            var cpu = SystemInventoryMappers.MapCpu(rows, Array.Empty<IInventoryRow>());

            Assert.Equal(2200u, cpu!.BaseClockSpeedMHz);
            Assert.Null(cpu.MaxClockSpeedMHz);
        }

        [Fact]
        public void Cpu_PlaceholderManufacturer_BecomesNull()
        {
            var rows = new IInventoryRow[]
            {
                Row(("Name", "X"), ("Manufacturer", "To Be Filled By O.E.M.")),
            };

            var cpu = SystemInventoryMappers.MapCpu(rows, Array.Empty<IInventoryRow>());

            Assert.NotNull(cpu);
            Assert.Null(cpu!.Manufacturer); // 不污染数据层（Gate D）
        }

        [Fact]
        public void Motherboard_SerialSanitized()
        {
            var rows = new IInventoryRow[]
            {
                Row(
                    ("Manufacturer", "ASUSTeK COMPUTER INC."),
                    ("Product", "ROG STRIX Z790-E"),
                    ("Version", "Rev 1.xx"),
                    ("SerialNumber", "Default string")),
            };

            var board = SystemInventoryMappers.MapMotherboard(rows);

            Assert.NotNull(board);
            Assert.Equal("ROG STRIX Z790-E", board!.Product);
            Assert.Equal("Rev 1.xx", board.Version);
            Assert.Null(board.SerialNumber); // placeholder → null
        }

        [Fact]
        public void Bios_Maps_Version_AndReleaseDate()
        {
            var rows = new IInventoryRow[]
            {
                Row(
                    ("Manufacturer", "American Megatrends International, LLC."),
                    ("SMBIOSBIOSVersion", "1302"),
                    ("ReleaseDate", "20250101000000.000000+000"),
                    ("SMBIOSMajorVersion", 3u),
                    ("SMBIOSMinorVersion", 0u)),
            };

            var bios = SystemInventoryMappers.MapBios(rows);

            Assert.NotNull(bios);
            Assert.Equal("1302", bios!.SmbiosBiosVersion);
            Assert.Equal("3.0", bios.SmbiosVersion);
            Assert.NotNull(bios.ReleaseDateUtc);
            Assert.Equal(2025, bios.ReleaseDateUtc!.Value.Year);
        }

        [Fact]
        public void Os_Maps_ComputerName_AndBootTime()
        {
            var rows = new IInventoryRow[]
            {
                Row(
                    ("Caption", "Microsoft Windows 11 专业版"),
                    ("Version", "10.0.26100"),
                    ("OSArchitecture", "64-bit"),
                    ("LastBootUpTime", "20260201080000.000000+480")),
            };

            var os = SystemInventoryMappers.MapOs(rows, "DESKTOP-TEST");

            Assert.NotNull(os);
            Assert.Equal("Microsoft Windows 11 专业版", os!.Name);
            Assert.Equal("DESKTOP-TEST", os.ComputerName);
            Assert.NotNull(os.LastBootUtc);
        }

        // ---- Gate E：每条 DIMM 独立 ----

        [Fact]
        public void Memory_TwoDimms_KeepIndependentIdentity()
        {
            var rows = new IInventoryRow[]
            {
                Row(
                    ("DeviceLocator", "ChannelA-DIMM0"),
                    ("BankLabel", "BANK 0"),
                    ("Capacity", (object)17179869184ul),
                    ("Manufacturer", "SK Hynix"),
                    ("PartNumber", "HMAA1GX6MCR6N-XN"),
                    ("SerialNumber", "12345678"),
                    ("Speed", 6400u),
                    ("ConfiguredClockSpeed", 5600u),
                    ("FormFactor", 8u),
                    ("DataWidth", 64u),
                    ("TotalWidth", 64u)),
                Row(
                    ("DeviceLocator", "ChannelB-DIMM0"),
                    ("BankLabel", "BANK 2"),
                    ("Capacity", (object)17179869184ul),
                    ("Manufacturer", "SK Hynix"),
                    ("PartNumber", "HMAA1GX6MCR6N-XN"),
                    ("SerialNumber", "87654321"),
                    ("Speed", 6400u),
                    ("ConfiguredClockSpeed", 5600u),
                    ("FormFactor", 8u),
                    ("DataWidth", 64u),
                    ("TotalWidth", 64u)),
            };

            var modules = MemoryInventoryMapper.Map(rows);

            Assert.Equal(2, modules.Count);
            Assert.Equal("ChannelA-DIMM0", modules[0].DeviceLocator);
            Assert.Equal("ChannelB-DIMM0", modules[1].DeviceLocator);
            Assert.NotEqual(modules[0].SerialNumber, modules[1].SerialNumber); // 独立 identity
            Assert.Equal(17179869184ul, modules[0].CapacityBytes);
            Assert.Equal(6400u, modules[0].SpeedMHz);
            Assert.Equal(5600u, modules[0].ConfiguredClockSpeedMHz);
            Assert.Equal("DIMM", modules[0].FormFactor);
        }

        [Fact]
        public void Memory_FormFactor_UnknownValue_BecomesNull()
        {
            var rows = new IInventoryRow[] { Row(("FormFactor", 99u), ("DeviceLocator", "X")) };

            var modules = MemoryInventoryMapper.Map(rows);

            Assert.Single(modules);
            Assert.Null(modules[0].FormFactor); // 不确定就不显示
        }

        // ---- Gate F：GPU / VRAM ----

        [Fact]
        public void Gpu_TwoAdapters_KeepIndependent_AndVramAbove4GB()
        {
            var dxgi = new List<GpuInventoryMapper.AdapterDescriptor>
            {
                new("NVIDIA GeForce RTX 4080", 0x10DE, 0x2704, 12884901888ul, 8_589_934_592ul),
                new("Intel(R) Arc(TM) Graphics", 0x8086, 0x7D55, 1073741824ul, 4_294_967_296ul),
            };
            var wmi = new IInventoryRow[]
            {
                Row(
                    ("Name", "NVIDIA GeForce RTX 4080"),
                    ("AdapterCompatibility", "NVIDIA"),
                    ("PNPDeviceID", "PCI\\VEN_10DE&DEV_2704&SUBSYS_40801458"),
                    ("DriverVersion", "32.0.15.6636"),
                    ("DriverDate", "20250101000000.000000+000")),
                Row(
                    ("Name", "Intel(R) Arc(TM) Graphics"),
                    ("AdapterCompatibility", "Intel"),
                    ("PNPDeviceID", "PCI\\VEN_8086&DEV_7D55"),
                    ("DriverVersion", "32.0.101.6129")),
            };

            var gpus = GpuInventoryMapper.Map(dxgi, wmi);

            Assert.Equal(2, gpus.Count);
            Assert.Equal(12884901888ul, gpus[0].DedicatedVideoMemoryBytes); // 12GB 不截断
            Assert.Equal("NVIDIA", gpus[0].Vendor);
            Assert.Equal(InventorySource.DXGI, gpus[0].Source);
            Assert.Equal("32.0.15.6636", gpus[0].DriverVersion);
            Assert.NotNull(gpus[0].DriverDateUtc);
            Assert.Equal("Intel", gpus[1].Vendor);
            Assert.Equal(1073741824ul, gpus[1].DedicatedVideoMemoryBytes);
        }

        [Fact]
        public void Gpu_WmiOnlyAdapter_KeptWithoutTruncatedVram()
        {
            var wmi = new IInventoryRow[]
            {
                Row(("Name", "Microsoft Basic Render Driver"), ("AdapterRAM", (object)268435456ul)),
            };

            var gpus = GpuInventoryMapper.Map(Array.Empty<GpuInventoryMapper.AdapterDescriptor>(), wmi);

            Assert.Single(gpus);
            Assert.Null(gpus[0].DedicatedVideoMemoryBytes); // 绝不用不可信 AdapterRAM
            Assert.Equal(InventorySource.Wmi, gpus[0].Source);
        }

        // ---- Gate G：Storage / partitions ----

        [Fact]
        public void Storage_TwoPhysicalDisks_WithPartitions()
        {
            var disks = new IInventoryRow[]
            {
                Row(
                    ("DeviceId", (object)0ul),
                    ("FriendlyName", "Samsung SSD 990 PRO 2TB"),
                    ("Model", "Samsung SSD 990 PRO 2TB"),
                    ("SerialNumber", "S6Z1NJ0R123456"),
                    ("FirmwareVersion", "1B2QGXA7"),
                    ("Size", (object)2000398934016ul),
                    ("BusType", 11u),
                    ("MediaType", 4u),
                    ("HealthStatus", "Healthy")),
                Row(
                    ("DeviceId", (object)1ul),
                    ("FriendlyName", "WD Blue SN580 1TB"),
                    ("Model", "WD Blue SN580 1TB"),
                    ("SerialNumber", "24123A456789"),
                    ("FirmwareVersion", "411100WD"),
                    ("Size", (object)1000204886016ul),
                    ("BusType", 11u),
                    ("MediaType", 4u),
                    ("HealthStatus", "Healthy")),
            };
            var partitions = new IInventoryRow[]
            {
                Row(("DiskNumber", 0u), ("DriveLetter", "C:"), ("Size", (object)999822661120ul)),
                Row(("DiskNumber", 1u), ("DriveLetter", "D:"), ("Size", (object)1000204886016ul)),
            };
            var volumes = new IInventoryRow[]
            {
                Row(("DriveLetter", "C:"), ("FileSystem", "NTFS"), ("FileSystemLabel", "System"),
                    ("Size", (object)999822661120ul), ("SizeRemaining", (object)536870912000ul)),
                Row(("DriveLetter", "D:"), ("FileSystem", "NTFS"), ("FileSystemLabel", "Data"),
                    ("Size", (object)1000204886016ul), ("SizeRemaining", (object)900000000000ul)),
            };

            var results = StorageInventoryMapper.Map(disks, partitions, volumes);

            Assert.Equal(2, results.Count);                     // 两块 NVMe 独立
            Assert.Equal(0u, results[0].DiskNumber);
            Assert.Equal(1u, results[1].DiskNumber);
            Assert.Equal("SATA", results[0].BusType);
            Assert.Equal("SSD", results[0].MediaType);
            Assert.Equal("Healthy", results[0].HealthStatus);
            var partition = Assert.Single(results[0].Partitions);
            Assert.Equal("C", partition.DriveLetter);
            Assert.Equal("NTFS", partition.FileSystem);
            Assert.Equal("System", partition.Label);
            Assert.Equal(536870912000ul, partition.FreeSpaceBytes);
        }

        [Fact]
        public void Storage_NoLetterPartitions_Char16NullJunk_NeverBecomeDriveLetters()
        {
            // V2-M4.5C.1 Gate F 真因回归：System.Management 把无盘符 char16 封送为
            // '\0'（char，非 null 非空白）。真机（i9-13980HX）实测：MSFT_Partition
            // 无盘符时 DriveLetter 封送值即 '\0'——旧实现把它当合法盘符，导致
            // EFI/MSR/Recovery/OEM 分区全部通过 IsUserVisibleVolume 进 UI
            //（用户看到 卷 0.3 GB / 卷 0 GB / 卷 1.1 GB / 卷 26 GB）。
            var disks = new IInventoryRow[]
            {
                Row(
                    ("DeviceId", (object)0ul),
                    ("FriendlyName", "Samsung MZVL21T0HCLR-00B00"),
                    ("Model", "Samsung MZVL21T0HCLR-00B00"),
                    ("Size", (object)1_000_000_000_000ul),
                    ("BusType", 17u)),
            };
            var partitions = new IInventoryRow[]
            {
                // 真实形态：260MB EFI / 16MB MSR / C: / 1.1GB Recovery / 26GB OEM——
                // 无盘符分区 DriveLetter 为 char '\0'，有盘符为 char 'C'。
                Row(("DiskNumber", 0u), ("DriveLetter", '\0'), ("Size", (object)272_629_760ul)),
                Row(("DiskNumber", 0u), ("DriveLetter", '\0'), ("Size", (object)16_777_216ul)),
                Row(("DiskNumber", 0u), ("DriveLetter", 'C'), ("Size", (object)994_575_384_576ul)),
                Row(("DiskNumber", 0u), ("DriveLetter", '\0'), ("Size", (object)1_153_433_600ul)),
                Row(("DiskNumber", 0u), ("DriveLetter", '\0'), ("Size", (object)27_917_287_424ul)),
            };
            var volumes = new IInventoryRow[]
            {
                Row(("DriveLetter", "C:"), ("FileSystem", "NTFS"), ("FileSystemLabel", "OS"),
                    ("Size", (object)994_575_384_576ul)),
            };

            var results = StorageInventoryMapper.Map(disks, partitions, volumes);
            var disk = Assert.Single(results);

            // '\0' 绝不成为盘符；只有真实字母 'C' 保留。
            Assert.Equal(4, disk.Partitions.Count(p => p.DriveLetter is null));
            var lettered = disk.Partitions.Where(p => p.DriveLetter is not null).ToArray();
            var c = Assert.Single(lettered);
            Assert.Equal("C", c.DriveLetter);
            Assert.Equal("NTFS", c.FileSystem);
        }
    }
}
