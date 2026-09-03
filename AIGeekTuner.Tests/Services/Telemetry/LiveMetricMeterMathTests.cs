using AIGeekTuner.Controls;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry.Presentation;
using Xunit;

namespace AIGeekTuner.Tests.Services.Telemetry
{
    /// <summary>V2-M4.5C Gate E/K：meter 几何与控制契约。</summary>
    public class LiveMetricMeterMathTests
    {
        [Theory]
        [InlineData(50, 0.5)]
        [InlineData(0, 0.0)]
        [InlineData(100, 1.0)]
        [InlineData(120, 1.0)]   // usage clamp：超出 scale clamp 到末端
        [InlineData(-5, 0.0)]    // 负值 clamp 到起点
        public void UsageScale_Clamps(double current, double expectedFill)
        {
            var g = LiveMetricMeterMath.Compute(0, 100, current, null, null);
            Assert.Equal(expectedFill, g.NormalizedFillEnd, 6);
        }

        [Theory]
        [InlineData(90, 90.0 / 110.0)]
        [InlineData(115, 1.0)]   // temp clamp（CPU/GPU 0-110 标尺，超限 clamp）
        [InlineData(0, 0.0)]
        public void TemperatureScale_Clamps(double current, double expectedFill)
        {
            var g = LiveMetricMeterMath.Compute(0, 110, current, null, null);
            Assert.Equal(expectedFill, g.NormalizedFillEnd, 6);
        }

        [Fact]
        public void LowEqualsHigh_BothMarkersOverlap_NoJitter()
        {
            var g = LiveMetricMeterMath.Compute(0, 100, 55, 55, 55);

            Assert.Equal(g.NormalizedLow, g.NormalizedHigh);
            Assert.Equal(g.NormalizedCurrent, g.NormalizedLow);
            Assert.True(LiveMetricMeterMath.MarkersOrdered(g));
        }

        [Fact]
        public void Markers_AlwaysOrdered_LowNotAfterHigh()
        {
            var g = LiveMetricMeterMath.Compute(0, 100, 50, 40, 70);
            Assert.True(LiveMetricMeterMath.MarkersOrdered(g));
            Assert.True(g.NormalizedLow!.Value <= g.NormalizedHigh!.Value);
        }

        [Fact]
        public void MissingLowHigh_MarkersAbsent()
        {
            var g = LiveMetricMeterMath.Compute(0, 100, 50, null, null);
            Assert.False(g.NormalizedLow.HasValue);
            Assert.False(g.NormalizedHigh.HasValue);
        }

        [Fact]
        public void MeterControl_ExposesNoHealthColorState()
        {
            // Gate E：禁止红黄绿健康等级——控件不得暴露健康/等级/颜色状态属性。
            var properties = typeof(LiveMetricMeter).GetProperties();
            Assert.All(properties, property =>
            {
                Assert.DoesNotContain("Health", property.Name);
                Assert.DoesNotContain("Level", property.Name);
                Assert.DoesNotContain("StatusColor", property.Name);
            });
        }

        [Fact]
        public void ValueFormatting_Units()
        {
            Assert.Equal("53.3 °C", HardwareLiveViewBuilder.FormatValue(53.3, TelemetryUnit.Celsius));
            Assert.Equal("45 %", HardwareLiveViewBuilder.FormatValue(45.04, TelemetryUnit.Percent));
            Assert.Equal("2100 MHz", HardwareLiveViewBuilder.FormatValue(2100, TelemetryUnit.Megahertz));
            Assert.Equal("65 W", HardwareLiveViewBuilder.FormatValue(65, TelemetryUnit.Watt));
            Assert.Equal("16.5 GB", HardwareLiveViewBuilder.FormatValue(16.5 * 1073741824, TelemetryUnit.Byte));
        }
    }
}