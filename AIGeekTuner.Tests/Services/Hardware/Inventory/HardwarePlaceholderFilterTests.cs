using AIGeekTuner.Services.Hardware.Inventory;
using Xunit;

namespace AIGeekTuner.Tests.Services.Hardware.Inventory
{
    /// <summary>Gate N：placeholder sanitizer（集中识别 OEM 占位文本）。</summary>
    public sealed class HardwarePlaceholderFilterTests
    {
        [Theory]
        [InlineData("To Be Filled By O.E.M.")]
        [InlineData("Default string")]
        [InlineData("System Serial Number")]
        [InlineData("None")]
        [InlineData("N/A")]
        [InlineData("Unknown")]
        [InlineData("0123456789")]
        [InlineData("  ")]
        [InlineData("Type2 - Board Vendor Name1")]
        public void Sanitize_Placeholders_BecomeNull(string placeholder)
        {
            Assert.Null(HardwarePlaceholderFilter.Sanitize(placeholder));
        }

        [Theory]
        [InlineData("ASUSTeK COMPUTER INC.")]
        [InlineData("SK Hynix")]
        [InlineData("American Megatrends International, LLC.")]
        public void Sanitize_RealValues_AreKept(string value)
        {
            Assert.Equal(value.Trim(), HardwarePlaceholderFilter.Sanitize(value));
        }

        [Fact]
        public void SanitizeSerialNumber_AllSameCharacters_BecomeNull()
        {
            Assert.Null(HardwarePlaceholderFilter.SanitizeSerialNumber("XXXXXXXX"));
            Assert.Null(HardwarePlaceholderFilter.SanitizeSerialNumber("00000000"));
            Assert.Equal("S4X7NB00Z123", HardwarePlaceholderFilter.SanitizeSerialNumber("S4X7NB00Z123"));
        }
    }
}
