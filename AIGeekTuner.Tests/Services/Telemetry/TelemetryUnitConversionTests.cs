using AIGeekTuner.Services.Telemetry;

namespace AIGeekTuner.Tests.Services.Telemetry
{
    public class TelemetryUnitConversionTests
    {
        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 1048576)]
        [InlineData(512.5, 537395200)]
        public void MegabytesToBytes_UsesMiB(double megabytes, double expectedBytes)
        {
            Assert.Equal(expectedBytes, TelemetryUnitConversion.MegabytesToBytes(megabytes), precision: 0);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(2, 2147483648)]
        public void GibibytesToBytes_UsesGiB(double gibibytes, double expectedBytes)
        {
            Assert.Equal(expectedBytes, TelemetryUnitConversion.GibibytesToBytes(gibibytes), precision: 1);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(3.8, 3800)]
        public void GigahertzToMegahertz_MultipliesBy1000(double gigahertz, double expected)
        {
            Assert.Equal(expected, TelemetryUnitConversion.GigahertzToMegahertz(gigahertz), precision: 6);
        }

        [Theory]
        [InlineData("71.4", 71.4)]
        [InlineData(" 84 ", 84)]
        [InlineData("-12.25", -12.25)]
        public void TryParseInvariant_AcceptsWellFormedNumbers(string text, double expected)
        {
            Assert.True(TelemetryUnitConversion.TryParseInvariant(text, out var value));
            Assert.Equal(expected, value, precision: 6);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("abc")]
        [InlineData("12,5")] // 非不变文化分隔符必须拒绝，避免区域歧义
        public void TryParseInvariant_RejectsMalformedInput(string? text)
        {
            Assert.False(TelemetryUnitConversion.TryParseInvariant(text, out _));
        }
    }
}
