using AIGeekTuner.Services.Hardware.Inventory;
using Xunit;

namespace AIGeekTuner.Tests.Services.Hardware.Inventory
{
    /// <summary>Gate N：EDID 解析（正常 / malformed / 缺序列号）。</summary>
    public sealed class EdidParserTests
    {
        private static byte[] MakeEdid(
            string mfg = "ACI",
            ushort product = 0x24D3,
            string? name = "AOC U28P2A",
            string? serial = "SDA12345")
        {
            var edid = new byte[128];
            edid[0] = 0x00;
            for (var i = 1; i <= 6; i++)
            {
                edid[i] = 0xFF;
            }

            // 厂商码：3 个 5-bit 字母 → 12 bit。
            ushort word = (ushort)(((mfg[0] - 'A' + 1) << 10) | ((mfg[1] - 'A' + 1) << 5) | (mfg[2] - 'A' + 1));
            edid[8] = (byte)((word >> 8) & 0xFF);
            edid[9] = (byte)(word & 0xFF);
            edid[10] = (byte)(product & 0xFF);
            edid[11] = (byte)((product >> 8) & 0xFF);

            WriteDescriptor(edid, 54, 0xFC, name);
            WriteDescriptor(edid, 72, 0xFF, serial);
            return edid;
        }

        private static void WriteDescriptor(byte[] edid, int offset, byte type, string? text)
        {
            if (text is null)
            {
                return; // 描述符区保持 0 → 解析不到。
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

        [Fact]
        public void ValidEdid_Parses_Manufacturer_Product_Name_Serial()
        {
            var identity = EdidParser.Parse(MakeEdid());

            Assert.NotNull(identity);
            Assert.Equal("ACI", identity!.ManufacturerCode);
            Assert.Equal("24D3", identity.ProductCode);
            Assert.Equal("AOC U28P2A", identity.MonitorName);
            Assert.Equal("SDA12345", identity.SerialNumber);
        }

        [Fact]
        public void MalformedEdid_ReturnsNull_NotThrow()
        {
            Assert.Null(EdidParser.Parse(null));
            Assert.Null(EdidParser.Parse(Array.Empty<byte>()));
            Assert.Null(EdidParser.Parse(new byte[] { 1, 2, 3 }));
            var garbage = new byte[128];
            new Random(7).NextBytes(garbage);
            Assert.Null(EdidParser.Parse(garbage));
            var brokenHeader = MakeEdid();
            brokenHeader[2] = 0x00; // 头部破坏
            Assert.Null(EdidParser.Parse(brokenHeader));
        }

        [Fact]
        public void MissingMonitorSerial_NameStillParses()
        {
            var identity = EdidParser.Parse(MakeEdid(serial: null));

            Assert.NotNull(identity);
            Assert.Equal("AOC U28P2A", identity!.MonitorName);
            Assert.Null(identity.SerialNumber);
        }
    }
}
