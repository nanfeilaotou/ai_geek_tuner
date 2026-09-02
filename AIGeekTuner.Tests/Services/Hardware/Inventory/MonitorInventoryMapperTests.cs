using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Services.Hardware.Inventory;
using Xunit;

namespace AIGeekTuner.Tests.Services.Hardware.Inventory
{
    /// <summary>Gate N：Monitor inventory（1/多屏、malformed EDID、缺序列号、模式匹配）。</summary>
    public sealed class MonitorInventoryMapperTests
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

        private static ushort[] Chars(string text) => text.Select(character => (ushort)character).ToArray();

        private static byte[] MakeEdid(string? name, string? serial)
        {
            var edid = new byte[128];
            edid[0] = 0x00;
            for (var i = 1; i <= 6; i++)
            {
                edid[i] = 0xFF;
            }

            ushort word = (ushort)((('A' - 'A' + 1) << 10) | (('O' - 'A' + 1) << 5) | ('C' - 'A' + 1));
            edid[8] = (byte)((word >> 8) & 0xFF);
            edid[9] = (byte)(word & 0xFF);
            edid[10] = 0xD3;
            edid[11] = 0x24;

            void WriteDescriptor(int offset, byte type, string? text)
            {
                if (text is null)
                {
                    return;
                }

                edid[offset] = 0;
                edid[offset + 1] = 0;
                edid[offset + 2] = 0;
                edid[offset + 3] = type;
                edid[offset + 4] = 0;
                var ascii = System.Text.Encoding.ASCII.GetBytes(text);
                for (var i = 0; i < 13 && i < ascii.Length; i++)
                {
                    edid[offset + 5 + i] = ascii[i];
                }
            }

            WriteDescriptor(54, 0xFC, name);
            WriteDescriptor(72, 0xFF, serial);
            return edid;
        }

        private static IInventoryRow MonitorIdRow(
            string instanceName,
            string? serial = "SDA123",
            string manufacturerCode = "AOC",
            string productCode = "24D3") =>
            Row(
                ("InstanceName", instanceName),
                ("ManufacturerName", Chars(manufacturerCode)),
                ("ProductCodeId", Chars(productCode)),
                ("SerialNumberId", serial is null ? Array.Empty<ushort>() : Chars(serial)),
                ("UserFriendlyName", Chars("AOC U28P2A")),
                ("YearOfManufacture", (object)2023u));

        [Fact]
        public void TwoMonitors_StayIndependent_WithSizes_AndModes()
        {
            var monitorIds = new IInventoryRow[]
            {
                MonitorIdRow("DISPLAY\\AOC24D3\\5&1&UID4353"),
                MonitorIdRow("DISPLAY\\GSM5B09\\5&1&UID4360", manufacturerCode: "GSM", productCode: "5B09"),
            };
            var displayParams = new IInventoryRow[]
            {
                Row(("InstanceName", "DISPLAY\\AOC24D3\\5&1&UID4353"),
                    ("MaxHorizontalImageSize", 62u), ("MaxVerticalImageSize", 34u)),
                Row(("InstanceName", "DISPLAY\\GSM5B09\\5&1&UID4360"),
                    ("MaxHorizontalImageSize", 52u), ("MaxVerticalImageSize", 29u)),
            };
            var modes = new List<MonitorInventoryMapper.DisplayModeDescriptor>
            {
                new("\\\\.\\DISPLAY1", "MONITOR\\AOC24D3\\{guid}", 3840, 2160, 144, true, true),
                new("\\\\.\\DISPLAY2", "MONITOR\\GSM5B09\\{guid}", 2560, 1440, 60, false, true),
            };

            var monitors = MonitorInventoryMapper.Map(monitorIds, displayParams, [], modes);

            Assert.Equal(2, monitors.Count);
            Assert.Equal("AOC U28P2A", monitors[0].FriendlyName);
            Assert.Equal("3840 × 2160", monitors[0].CurrentResolution);
            Assert.NotEqual(monitors[0].EdidIdentity, monitors[1].EdidIdentity);
            // 3840×2160 @144Hz 匹配到主屏。
            Assert.True(monitors[0].IsPrimary);
            Assert.False(monitors[1].IsPrimary);
            Assert.Equal("2560 × 1440", monitors[1].CurrentResolution);
            // 对角线 27.8 英寸（62×34cm）。
            Assert.NotNull(monitors[0].DiagonalInches);
            Assert.Equal(27.8, monitors[0].DiagonalInches!.Value, 1);
        }

        [Fact]
        public void MalformedEdid_FallsBackToWmiIdentity_NoCrash()
        {
            var garbage = new byte[128];
            new Random(3).NextBytes(garbage);
            var rawEdids = new[]
            {
                new MonitorInventoryMapper.RawEdidEntry("DISPLAY\\AOC24D3", garbage),
            };
            var monitorIds = new IInventoryRow[] { MonitorIdRow("DISPLAY\\AOC24D3\\5&1&UID4353") };

            var monitors = MonitorInventoryMapper.Map(
                monitorIds, [], rawEdids, Array.Empty<MonitorInventoryMapper.DisplayModeDescriptor>());

            Assert.Single(monitors);
            // WMI 字段仍然有效（EDID 解析失败只是补充通道失效）。
            Assert.Equal("AOC U28P2A", monitors[0].FriendlyName);
            Assert.Null(monitors[0].CurrentResolution); // 没有模式数据 → 不显示
        }

        [Fact]
        public void MissingMonitorSerial_SerialBecomesNull()
        {
            var rawEdids = new[]
            {
                new MonitorInventoryMapper.RawEdidEntry(
                    "DISPLAY\\AOC24D3", MakeEdid(name: "AOC U28P2A", serial: null)),
            };
            var monitorIds = new IInventoryRow[]
            {
                MonitorIdRow("DISPLAY\\AOC24D3\\5&1&UID4353", serial: null),
            };

            var monitors = MonitorInventoryMapper.Map(
                monitorIds, [], rawEdids, Array.Empty<MonitorInventoryMapper.DisplayModeDescriptor>());

            Assert.Single(monitors);
            Assert.Null(monitors[0].SerialNumber);
            Assert.Equal("AOC U28P2A", monitors[0].FriendlyName);
        }

        [Theory]
        [InlineData("MONITOR\\ACI24D3\\{4d36e96e-e325-11ce-bfc1-08002be10318}\\{guid}", "ACI24D3")]
        [InlineData("\\\\?\\DISPLAY#BOE0B35#5&17d37f5&3&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", "BOE0B35")]
        public void ExtractPnpId_Supports_DeviceTree_And_InterfacePath_Forms(string deviceId, string expected)
        {
            Assert.Equal(expected, MonitorInventoryMapper.ExtractPnpId(deviceId));
        }

        [Fact]
        public void SingleMonitor_MinimalData_StillListed()
        {
            var monitorIds = new IInventoryRow[]
            {
                Row(("InstanceName", "DISPLAY\\XYZ123\\5&0"), ("YearOfManufacture", (object)2020u)),
            };

            var monitors = MonitorInventoryMapper.Map(monitorIds, [], [], []);

            Assert.Single(monitors);
            Assert.Equal(2020u, monitors[0].YearOfManufacture);
            Assert.Null(monitors[0].FriendlyName);
            Assert.Null(monitors[0].DiagonalInches);
        }
    }
}