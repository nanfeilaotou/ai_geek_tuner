using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Tests.Models.Telemetry
{
    /// <summary>§20：Catalog 是唯一事实源——数量、唯一性、无 dead key。</summary>
    public class TelemetryMetricCatalogTests
    {
        [Fact]
        public void Catalog_ContainsExactlyEighteenMetrics()
        {
            // V2-M4.5B：+ memory.module.temperature → 17。
            // V2-M4.5C Gate F：+ gpu.memory.clock（显存频率 numeric）→ 18。
            Assert.Equal(18, TelemetryMetricCatalog.All.Count);
        }

        [Fact]
        public void Catalog_HasNoDuplicatesOrPhantomKeys()
        {
            var values = TelemetryMetricCatalog.All
                .Select(key => key.Value)
                .ToArray();
            Assert.Equal(values.Length, values.Distinct().Count());
            Assert.All(values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        }

        [Fact]
        public void Catalog_Families_SumToWhole()
        {
            Assert.Equal(
                TelemetryMetricCatalog.All.Count,
                TelemetryMetricCatalog.Cpu.Count
                + TelemetryMetricCatalog.Gpu.Count
                + TelemetryMetricCatalog.Memory.Count
                + TelemetryMetricCatalog.Storage.Count);
        }

        [Fact]
        public void EveryKnownMetricKey_IsInCatalog()
        {
            // 反射枚举 TelemetryMetricKey 的全部静态字段，防止有人加键却忘登目录。
            var knownKeys = typeof(TelemetryMetricKey)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(field => field.FieldType == typeof(TelemetryMetricKey))
                .Select(field => ((TelemetryMetricKey)field.GetValue(null)!).Value)
                .ToHashSet();
            var cataloged = TelemetryMetricCatalog.All.Select(key => key.Value).ToHashSet();

            Assert.True(knownKeys.SetEquals(cataloged),
                "Catalog 与 TelemetryMetricKey 静态定义不同步：" +
                string.Join(",", knownKeys.Except(cataloged).Union(cataloged.Except(knownKeys))));
        }
    }
}
