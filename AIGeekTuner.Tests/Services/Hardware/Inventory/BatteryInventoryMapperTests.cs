using System;
using System.Linq;
using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Services.Hardware.Inventory;
using Xunit;

namespace AIGeekTuner.Tests.Services.Hardware.Inventory
{
    /// <summary>
    /// V2-M4.5C.1 Gate I：电池 mapper 回归（真实 WMI 口径，全部合成数据）。
    /// 覆盖：无电池 / 正常 / 充电 / 放电 / 设计+满充容量 / Wear 计算 /
    /// Full&gt;Design / 容量缺失 / 部分来源失败 / 绝不造假默认值。
    /// </summary>
    public sealed class BatteryInventoryMapperTests
    {
        private static DictionaryInventoryRow Row(params (string Key, object? Value)[] values) =>
            new(new System.Collections.Generic.Dictionary<string, object?>(
                values.ToDictionary(v => v.Key, v => v.Value)));

        [Fact]
        public void NoBatterySources_ReturnsNull()
        {
            var battery = BatteryInventoryMapper.Map(
                Array.Empty<IInventoryRow>(),
                Array.Empty<IInventoryRow>(),
                Array.Empty<IInventoryRow>(),
                Array.Empty<IInventoryRow>());

            Assert.Null(battery); // 台式机：不出电池 section，绝不造假。
        }

        [Fact]
        public void NormalBattery_PowerOnline_FullAndRemainingKnown()
        {
            // 本机真机形态：BatteryStaticData 缺失，其余三源可用。
            var battery = BatteryInventoryMapper.Map(
                Array.Empty<IInventoryRow>(),
                [Row(("FullChargedCapacity", 63800u))],
                [Row(("RemainingCapacity", 31900u), ("Voltage", 16571u),
                    ("ChargeRate", 0u), ("DischargeRate", 0u),
                    ("PowerOnline", true), ("Charging", false), ("Discharging", false))],
                [Row(("Name", "R220358"), ("EstimatedChargeRemaining", 50u),
                    ("BatteryStatus", 2u), ("DesignVoltage", 16574u))]);

            Assert.NotNull(battery);
            Assert.Equal("R220358", battery!.Name);
            Assert.True(battery.PowerOnline);
            Assert.False(battery.Charging);
            Assert.False(battery.Discharging);
            Assert.Equal(50u, battery.ChargePercent);
            Assert.Null(battery.DesignCapacityMWh);   // 来源缺失 → null，不造假
            Assert.Equal(63800u, battery.FullChargeCapacityMWh);
            Assert.Equal(31900u, battery.RemainingCapacityMWh);
            Assert.Equal(16571u, battery.VoltageMillivolts);
            Assert.Null(battery.HealthPercent);        // 设计容量缺失 → 不算健康度
            Assert.Null(battery.WearPercent);
        }

        [Fact]
        public void ChargingBattery_ChargeRateShown_DischargeZero()
        {
            var battery = BatteryInventoryMapper.Map(
                Array.Empty<IInventoryRow>(),
                [Row(("FullChargedCapacity", 63800u))],
                [Row(("RemainingCapacity", 30000u), ("Voltage", 16000u),
                    ("ChargeRate", 25000u), ("DischargeRate", 0u),
                    ("PowerOnline", true), ("Charging", true), ("Discharging", false))],
                [Row(("EstimatedChargeRemaining", 47u))]);

            Assert.NotNull(battery);
            Assert.True(battery!.Charging);
            Assert.False(battery.Discharging);
            Assert.Equal(25000u, battery.ChargeRateMilliwatts);
            Assert.Null(battery.DischargeRateMilliwatts);
        }

        [Fact]
        public void DischargingBattery_Offline_UsesDischargeRate()
        {
            var battery = BatteryInventoryMapper.Map(
                Array.Empty<IInventoryRow>(),
                [Row(("FullChargedCapacity", 63800u))],
                [Row(("RemainingCapacity", 20000u), ("Voltage", 15400u),
                    ("ChargeRate", 0u), ("DischargeRate", 18000u),
                    ("PowerOnline", false), ("Charging", false), ("Discharging", true))],
                [Row(("EstimatedChargeRemaining", 31u))]);

            Assert.NotNull(battery);
            Assert.True(battery!.Discharging);
            Assert.False(battery.PowerOnline);
            Assert.Equal(18000u, battery.DischargeRateMilliwatts);
            Assert.Null(battery.ChargeRateMilliwatts);
        }

        [Fact]
        public void DesignAndFullCapacity_ComputeHealthAndWear()
        {
            // 设计 63800，满充 57420 → 健康 90%，损耗 10%。
            var battery = BatteryInventoryMapper.Map(
                [Row(("DesignedCapacity", 63800u))],
                [Row(("FullChargedCapacity", 57420u))],
                [Row(("RemainingCapacity", 31900u), ("PowerOnline", true))],
                [Row(("EstimatedChargeRemaining", 50u))]);

            Assert.NotNull(battery);
            Assert.Equal(63800u, battery!.DesignCapacityMWh);
            Assert.Equal(57420u, battery.FullChargeCapacityMWh);
            Assert.Equal(90.0, battery.HealthPercent!.Value, 1);
            Assert.Equal(10.0, battery.WearPercent!.Value, 1);
        }

        [Fact]
        public void FullGreaterThanDesign_WearClampedToZero_RawValuesKept()
        {
            // 计量偏差：满充 > 设计 → Wear=0%，raw 数值保持真实不做修正。
            var battery = BatteryInventoryMapper.Map(
                [Row(("DesignedCapacity", 60000u))],
                [Row(("FullChargedCapacity", 63800u))],
                [Row(("RemainingCapacity", 63801u), ("PowerOnline", true))],
                [Row(("EstimatedChargeRemaining", 100u))]);

            Assert.NotNull(battery);
            Assert.Equal(0.0, battery!.WearPercent!.Value, 1);
            Assert.Equal(100.0, battery.HealthPercent!.Value, 1);
            Assert.Equal(60000u, battery.DesignCapacityMWh);
            Assert.Equal(63800u, battery.FullChargeCapacityMWh);
        }

        [Fact]
        public void ZeroCapacitySources_TreatedAsMissing()
        {
            // 某些机器返回 0 —— 与缺失同等处理，绝不进入健康度计算。
            var battery = BatteryInventoryMapper.Map(
                [Row(("DesignedCapacity", 0u))],
                [Row(("FullChargedCapacity", 0u))],
                [Row(("RemainingCapacity", 0u), ("Voltage", 0u),
                    ("PowerOnline", true))],
                [Row(("BatteryStatus", 2u))]);

            Assert.NotNull(battery);
            Assert.Null(battery!.DesignCapacityMWh);
            Assert.Null(battery!.FullChargeCapacityMWh);
            Assert.Null(battery!.RemainingCapacityMWh);
            Assert.Null(battery!.VoltageMillivolts);
            Assert.Null(battery!.ChargePercent);   // Win32 也没给 → null，绝不填假
            Assert.Null(battery!.HealthPercent);
            Assert.Null(battery!.WearPercent);
        }

        [Fact]
        public void MissingWin32Battery_ChargePercentFallsBackToCapacityRatio()
        {
            // Win32_Battery 失败（部分来源失败场景）：剩余/满充推算电量。
            var battery = BatteryInventoryMapper.Map(
                Array.Empty<IInventoryRow>(),
                [Row(("FullChargedCapacity", 63800u))],
                [Row(("RemainingCapacity", 15950u), ("Voltage", 16000u),
                    ("PowerOnline", false), ("Charging", false), ("Discharging", true))],
                Array.Empty<IInventoryRow>());

            Assert.NotNull(battery);
            Assert.Equal(25u, battery!.ChargePercent);
            Assert.Null(battery.Name);
        }

        [Fact]
        public void ChargePercent_ClampedToHundred()
        {
            var battery = BatteryInventoryMapper.Map(
                Array.Empty<IInventoryRow>(),
                [Row(("FullChargedCapacity", 63800u))],
                [Row(("RemainingCapacity", 63801u), ("PowerOnline", true))],
                [Row(("EstimatedChargeRemaining", 100u))]);

            Assert.NotNull(battery);
            Assert.Equal(100u, battery!.ChargePercent);
        }
    }
}
