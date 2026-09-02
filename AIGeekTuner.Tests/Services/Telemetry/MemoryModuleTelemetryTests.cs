using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry;
using AIGeekTuner.Services.Telemetry.Aida64;
using AIGeekTuner.Services.Telemetry.HwInfo;
using AIGeekTuner.Services.Telemetry.LibreHardwareMonitor;
using Xunit;

namespace AIGeekTuner.Tests.Services.Telemetry
{
    /// <summary>
    /// V2-M4.5B Gate C–H/I：per-DIMM 内存温度 telemetry domain。
    /// 全部使用合成数据，不依赖真实机器（Gate I）。
    /// </summary>
    public class MemoryModuleTelemetryTests
    {
        private static readonly DateTimeOffset CapturedAtUtc =
            new(2024, 9, 1, 8, 0, 0, TimeSpan.Zero);

        // ------------------------------------------------------ HWiNFO (Gate D)
        [Fact]
        public void HwInfo_TwoDimmSpdHubTemperatures_ExactMapping()
        {
            var sensors = new[]
            {
                new HwInfoSensorEntry(0, "RAM Module #0"),
                new HwInfoSensorEntry(1, "RAM Module #1"),
                new HwInfoSensorEntry(2, "RAM"), // 泛 Memory 传感器：不是模块 parent
            };
            var readings = new[]
            {
                new HwInfoReadingEntry(0, 1, "SPD Hub Temperature", "°C", 36.5, ReadingType: 1),
                new HwInfoReadingEntry(1, 1, "SPD Hub Temperature", "°C", 38.0, ReadingType: 1),
                new HwInfoReadingEntry(2, 1, "Memory Usage", "%", 41, ReadingType: 7),
            };

            var canonical = HwInfoCanonicalMapper.Map(sensors, readings, CapturedAtUtc);

            var moduleTemps = canonical
                .Where(reading => reading.MetricKey == TelemetryMetricKey.MemoryModuleTemperature)
                .ToArray();
            Assert.Equal(2, moduleTemps.Length);
            Assert.Equal(36.5, moduleTemps[0].Value);
            Assert.Equal(TelemetryDeviceKind.MemoryModule, moduleTemps[0].Device.Kind);
            Assert.Equal("memory-module:0", moduleTemps[0].Device.DeviceKey);
            Assert.Equal("memory-module:1", moduleTemps[1].Device.DeviceKey);
            // Gate D：原始 label 必须保留（SPD Hub Temperature，不是"DRAM 结温"）。
            Assert.All(moduleTemps, reading => Assert.Equal("SPD Hub Temperature", reading.SourceLabel));
            Assert.All(moduleTemps, reading => Assert.Equal(TelemetryUnit.Celsius, reading.Unit));
        }

        [Fact]
        public void HwInfo_GenericParentOrWrongLabel_NotMapped()
        {
            var sensors = new[]
            {
                new HwInfoSensorEntry(0, "RAM"),            // 泛 parent
                new HwInfoSensorEntry(1, "RAM Module #0"),  // 模块 parent
                new HwInfoSensorEntry(2, "Motherboard"),
            };
            var readings = new[]
            {
                // 泛 parent 上的 SPD Hub 读数：不满足"明确模块 parent"条件 → 不映射。
                new HwInfoReadingEntry(0, 1, "SPD Hub Temperature", "°C", 37, ReadingType: 1),
                // 模块 parent 但 label 非精确匹配 → 不映射（禁止 Contains 泛化）。
                new HwInfoReadingEntry(1, 1, "DIMM Temperature", "°C", 37, ReadingType: 1),
                // 非模块 parent 的其它温度照旧不产生模块指标。
                new HwInfoReadingEntry(2, 2, "Temperature", "°C", 45, ReadingType: 1),
            };

            var canonical = HwInfoCanonicalMapper.Map(sensors, readings, CapturedAtUtc);

            Assert.DoesNotContain(canonical, reading =>
                reading.MetricKey == TelemetryMetricKey.MemoryModuleTemperature);
        }

        [Fact]
        public void HwInfo_ModuleSensors_ClassifiedAsMemoryModuleDevices()
        {
            var sensors = new[]
            {
                new HwInfoSensorEntry(0, "RAM Module #0"),
                new HwInfoSensorEntry(1, "DIMM1"),
                new HwInfoSensorEntry(2, "RAM"),
            };

            Assert.Equal(
                TelemetryDeviceKind.MemoryModule,
                HwInfoCanonicalMapper.DescribeSourceDevice(sensors, 0).Kind);
            Assert.Equal(
                TelemetryDeviceKind.MemoryModule,
                HwInfoCanonicalMapper.DescribeSourceDevice(sensors, 1).Kind);
            Assert.Equal(
                TelemetryDeviceKind.Memory,
                HwInfoCanonicalMapper.DescribeSourceDevice(sensors, 2).Kind);
        }

        [Fact]
        public void HwInfo_Ddr5DimmSensorName_MappedWithLocatorStrongId()
        {
            // 实测 HWiNFO DDR5 命名：模块 parent + 括号内 SMBIOS DeviceLocator。
            var sensorName = "DDR5 DIMM [#0] (BANK 0/Controller0-ChannelA-DIMM0)";
            var sensors = new[] { new HwInfoSensorEntry(7, sensorName) };
            var readings = new[]
            {
                new HwInfoReadingEntry(7, 16777216, "SPD Hub Temperature", "\u00b0C", 52.0, ReadingType: 1),
            };

            var canonical = HwInfoCanonicalMapper.Map(sensors, readings, CapturedAtUtc);

            var moduleTemp = Assert.Single(canonical.Where(reading =>
                reading.MetricKey == TelemetryMetricKey.MemoryModuleTemperature));
            Assert.Equal("memory-module:0", moduleTemp.Device.DeviceKey);
            Assert.Equal(52.0, moduleTemp.Value);

            var info = HwInfoCanonicalMapper.DescribeSourceDevice(sensors, 7);
            Assert.Equal(TelemetryDeviceKind.MemoryModule, info.Kind);
            Assert.Equal("locator:Controller0-ChannelA-DIMM0", info.StrongIds[0]);
            Assert.True(MemoryModuleSensorNames.TryGetModuleDeviceLocator(
                sensorName, out var locator));
            Assert.Equal("Controller0-ChannelA-DIMM0", locator);
        }

        [Fact]
        public void ModuleSensorNames_InvalidParents_AreRejected()
        {
            Assert.False(MemoryModuleSensorNames.TryGetModuleIndex("Memory Timings", out _));
            Assert.False(MemoryModuleSensorNames.TryGetModuleIndex("DDR5 DIMM Temperature", out _));
            Assert.False(MemoryModuleSensorNames.TryGetModuleIndex("S.M.A.R.T.: Some Disk", out _));
            Assert.False(MemoryModuleSensorNames.TryGetModuleIndex(null, out _));
            Assert.False(MemoryModuleSensorNames.TryGetModuleDeviceLocator("RAM Module #0", out _));
        }

        // ------------------------------------------------------ AIDA64 (Gate E)
        [Fact]
        public void Aida_Tdimm1AndTdimm2_MappedToDistinctModules()
        {
            var raw = new[]
            {
                MakeAidaRaw("TDIMM1", 41.5),
                MakeAidaRaw("TDIMM2", 42.5),
            };

            var canonical = Aida64CanonicalMapper.Map(raw);

            var moduleTemps = canonical
                .Where(reading => reading.MetricKey == TelemetryMetricKey.MemoryModuleTemperature)
                .ToArray();
            Assert.Equal(2, moduleTemps.Length);
            Assert.Equal("memory-module:1", moduleTemps[0].Device.DeviceKey);
            Assert.Equal(41.5, moduleTemps[0].Value);
            Assert.Equal("memory-module:2", moduleTemps[1].Device.DeviceKey);
        }

        [Fact]
        public void Aida_GenericTdimm_NotAssignedToAnyModule()
        {
            // Gate E：无法证明对应具体模块 → 只保留 raw，绝不挂到某一根 DIMM。
            var raw = new[]
            {
                MakeAidaRaw("TDIMM", 41.5),
                MakeAidaRaw("TDIMMTS1", 42.0),
                MakeAidaRaw("TDIMMTS64", 43.0),
            };

            var canonical = Aida64CanonicalMapper.Map(raw);

            Assert.DoesNotContain(canonical, reading =>
                reading.MetricKey == TelemetryMetricKey.MemoryModuleTemperature);
            Assert.False(Aida64CanonicalMapper.TryClassifyDeviceAndUnit(
                "TDIMM", out _, out _));
        }

        [Fact]
        public void Aida_ImplausibleTemperature_Rejected()
        {
            var raw = new[] { MakeAidaRaw("TDIMM1", 999) };

            var canonical = Aida64CanonicalMapper.Map(raw);

            Assert.DoesNotContain(canonical, reading =>
                reading.MetricKey == TelemetryMetricKey.MemoryModuleTemperature);
        }

        private static RawTelemetryReading MakeAidaRaw(string id, double value)
        {
            var device = Aida64CanonicalMapper.TryClassifyDeviceAndUnit(id, out var identity, out var unit)
                ? identity
                : TelemetryDeviceIdentity.SystemBoard("system");
            return new RawTelemetryReading(
                TelemetrySourceKind.Aida64,
                id,
                id,
                value,
                unit,
                device,
                new SourceDeviceInfo(
                    TelemetrySourceKind.Aida64,
                    device.Kind,
                    device.DeviceKey,
                    device.DisplayName,
                    0,
                    []),
                CapturedAtUtc);
        }

        // ------------------------------------------------------ LHM (Gate F)
        [Fact]
        public void Lhm_ModuleParentWithTemperature_Mapped()
        {
            var moduleDevice = TelemetryDeviceIdentity.MemoryModule("memory-module:1", "RAM Module #1");
            var raw = new[]
            {
                MakeLhmRaw(moduleDevice, "Temperature", 41),
                MakeLhmRaw(TelemetryDeviceIdentity.Memory("Memory"), "Temperature", 42),
            };

            var canonical = LibreHardwareMonitorCanonicalMapper.Map(raw);

            var moduleTemps = canonical
                .Where(reading => reading.MetricKey == TelemetryMetricKey.MemoryModuleTemperature)
                .ToArray();
            _ = moduleTemps; // 断言见下（保持结构清晰）
            var reading = Assert.Single(moduleTemps);
            Assert.Equal("memory-module:1", reading.Device.DeviceKey);
            Assert.Equal(41, reading.Value);
        }

        [Fact]
        public void Lhm_AggregateMemoryNode_NotMapped()
        {
            var raw = new[]
            {
                MakeLhmRaw(TelemetryDeviceIdentity.Memory("Memory"), "Temperature", 42),
            };

            var canonical = LibreHardwareMonitorCanonicalMapper.Map(raw);

            Assert.DoesNotContain(canonical, reading =>
                reading.MetricKey == TelemetryMetricKey.MemoryModuleTemperature);
        }

        private static RawTelemetryReading MakeLhmRaw(
            TelemetryDeviceIdentity device, string label, double value) =>
            new(
                TelemetrySourceKind.LibreHardwareMonitor,
                "Temperature:" + label,
                label,
                value,
                TelemetryUnit.Celsius,
                device,
                new SourceDeviceInfo(
                    TelemetrySourceKind.LibreHardwareMonitor,
                    device.Kind,
                    device.DeviceKey,
                    device.DisplayName,
                    0,
                    []),
                CapturedAtUtc);

        // ------------------------------------------- Hub 优先级 / 保守合并 (Gate H)
        private sealed class StubProvider(
            TelemetrySourceKind kind,
            TelemetryProviderResult result) : ITelemetryProvider
        {
            public TelemetrySourceKind SourceKind => kind;

            public Task<TelemetryProviderResult> ReadSnapshotAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult(result);
        }

        private static ITelemetryProvider ModuleProvider(
            TelemetrySourceKind kind,
            params (string ModuleKey, string ModuleName, string StrongId, double Value)[] modules)
        {
            var identityByModule = modules
                .Select(module => (
                    Module: module,
                    Identity: TelemetryDeviceIdentity.MemoryModule(module.ModuleKey, module.ModuleName)))
                .ToArray();
            var rawReadings = identityByModule
                .Select(entry => new RawTelemetryReading(
                    kind,
                    entry.Module.ModuleKey,
                    "SPD Hub Temperature",
                    entry.Module.Value,
                    TelemetryUnit.Celsius,
                    entry.Identity,
                    new SourceDeviceInfo(
                        kind,
                        TelemetryDeviceKind.MemoryModule,
                        entry.Identity.DeviceKey,
                        entry.Module.ModuleName,
                        0,
                        [entry.Module.StrongId]),
                    CapturedAtUtc))
                .ToArray();
            var canonicalReadings = identityByModule
                .Select(entry => new TelemetryReading(
                    TelemetryMetricKey.MemoryModuleTemperature,
                    entry.Module.Value,
                    TelemetryUnit.Celsius,
                    entry.Identity,
                    kind,
                    entry.Module.ModuleKey,
                    "SPD Hub Temperature",
                    CapturedAtUtc))
                .ToArray();
            var devices = identityByModule
                .Select(entry => new SourceDeviceInfo(
                    kind,
                    TelemetryDeviceKind.MemoryModule,
                    entry.Identity.DeviceKey,
                    entry.Module.ModuleName,
                    0,
                    [entry.Module.StrongId]))
                .ToArray();
            return new StubProvider(kind, new TelemetryProviderResult(
                TelemetrySourceStatus.Ready,
                "ready",
                rawReadings,
                canonicalReadings,
                CapturedAtUtc,
                devices));
        }

        [Fact]
        public async Task Hub_UnresolvedModules_NotMergedAcrossSources()
        {
            // 两来源各报 2 根模块、无 StrongIds、名称不同 → 保守：不合并，
            // 4 条读数各自留在源本地命名空间，绝不跨源乱合（Gate E/H）。
            var hub = new TelemetryHub(
            [
                ModuleProvider(TelemetrySourceKind.HwInfo,
                    ("memory-module:0", "RAM Module #0", "", 36.5),
                    ("memory-module:1", "RAM Module #1", "", 38.0)),
                ModuleProvider(TelemetrySourceKind.Aida64,
                    ("memory-module:1", "DIMM #1", "", 41.0),
                    ("memory-module:2", "DIMM #2", "", 42.0)),
            ]);

            var snapshot = await hub.ReadAsync();

            var moduleTemps = snapshot.CanonicalReadings
                .Where(reading => reading.MetricKey == TelemetryMetricKey.MemoryModuleTemperature)
                .ToArray();
            Assert.Equal(4, moduleTemps.Length);
            Assert.All(moduleTemps, reading =>
                Assert.StartsWith("src:", reading.Device.DeviceKey));
        }

        [Fact]
        public async Task Hub_SameResolvedModule_FallbackByPriority()
        {
            // 同一根已 resolved 模块（双方提供相同 StrongId）→ 允许 fallback，
            // HWiNFO 按固定优先级胜出（Gate H）。
            var hub = new TelemetryHub(
            [
                ModuleProvider(TelemetrySourceKind.HwInfo,
                    ("memory-module:0", "RAM Module #0", "spd-serial-0x33aa", 36.5)),
                ModuleProvider(TelemetrySourceKind.Aida64,
                    ("memory-module:1", "DIMM #1", "spd-serial-0x33aa", 41.0)),
            ]);

            var snapshot = await hub.ReadAsync();

            var moduleTemps = snapshot.CanonicalReadings
                .Where(reading => reading.MetricKey == TelemetryMetricKey.MemoryModuleTemperature)
                .ToArray();
            var reading = Assert.Single(moduleTemps);
            Assert.Equal(36.5, reading.Value);
            Assert.Equal(TelemetrySourceKind.HwInfo, reading.Source);
        }

        [Fact]
        public async Task Hub_NoModuleSensor_NoValue()
        {
            var hub = new TelemetryHub(
            [
                ModuleProvider(TelemetrySourceKind.HwInfo), // 无模块读数
            ]);

            var snapshot = await hub.ReadAsync();

            Assert.DoesNotContain(snapshot.CanonicalReadings, reading =>
                reading.MetricKey == TelemetryMetricKey.MemoryModuleTemperature);
        }
    }
}
