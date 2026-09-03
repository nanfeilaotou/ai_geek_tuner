using System;
using System.Collections.Generic;
using System.Linq;
using AIGeekTuner.Models.Hardware.Inventory;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// V2-M4.5C.1 Gate I：电池静态/运行信息装配。
    ///
    /// 来源调查（真机实测，只读）：
    /// - Win32_Battery：EstimatedChargeRemaining / BatteryStatus / DesignVoltage / Name 可靠；
    ///   FullChargeCapacity/DesignCapacity 在多数消费机型为 null，不使用。
    /// - root\WMI BatteryStaticData：DesignedCapacity（设计容量）；不少机器不可用（本机 0 条）。
    /// - root\WMI BatteryFullChargedCapacity：FullChargedCapacity（满充容量）。
    /// - root\WMI BatteryStatus：RemainingCapacity / Voltage / ChargeRate / DischargeRate /
    ///   PowerOnline / Charging / Discharging。
    ///
    /// WearPercent 只有 DesignCapacity &gt; 0 且 FullChargeCapacity &gt; 0 时计算：
    /// Wear = 1 - Full/Design；Full &gt; Design（计量偏差）时 Wear=0%，
    /// raw 数值保持真实，不做修正。来源缺失/为 0 的字段一律 null，绝不填假默认。
    /// </summary>
    public static class BatteryInventoryMapper
    {
        public static BatteryInfo? Map(
            IReadOnlyList<IInventoryRow> staticData,
            IReadOnlyList<IInventoryRow> fullCharged,
            IReadOnlyList<IInventoryRow> status,
            IReadOnlyList<IInventoryRow> win32Battery)
        {
            var win32 = win32Battery.FirstOrDefault();
            var staticRow = staticData.FirstOrDefault();
            var fullRow = fullCharged.FirstOrDefault();
            var statusRow = status.FirstOrDefault();

            if (win32 is null && staticRow is null && fullRow is null && statusRow is null)
            {
                return null; // 台式机/无电池：不出 section，绝不造假。
            }

            var designCapacity = MaxZero(staticRow?.UInt32("DesignedCapacity"));
            var fullChargeCapacity = MaxZero(fullRow?.UInt32("FullChargedCapacity"));
            var remainingCapacity = MaxZero(statusRow?.UInt32("RemainingCapacity"));
            var voltage = MaxZero(statusRow?.UInt32("Voltage"))
                ?? MaxZero(win32?.UInt32("DesignVoltage"));
            var chargeRate = MaxZero(statusRow?.UInt32("ChargeRate"));
            var dischargeRate = MaxZero(statusRow?.UInt32("DischargeRate"));

            var powerOnline = statusRow?.Boolean("PowerOnline");
            var charging = statusRow?.Boolean("Charging") == true;
            var discharging = statusRow?.Boolean("Discharging") == true;

            uint? chargePercent = win32?.UInt32("EstimatedChargeRemaining") is { } percent
                ? ClampPercent(percent)
                : null;
            if (chargePercent is null
                && remainingCapacity is > 0
                && fullChargeCapacity is > 0)
            {
                chargePercent = ClampPercent((uint)Math.Round(
                    remainingCapacity.Value * 100d / fullChargeCapacity.Value));
            }

            // 健康/损耗：只有设计与满充都可用才计算（本机 BatteryStaticData 缺失 → 不显示）。
            double? healthPercent = null;
            double? wearPercent = null;
            if (designCapacity is > 0 && fullChargeCapacity is > 0)
            {
                var health = fullChargeCapacity.Value * 100d / designCapacity.Value;
                healthPercent = Math.Clamp(health, 0d, 100d);
                wearPercent = fullChargeCapacity.Value >= designCapacity.Value
                    ? 0d
                    : Math.Clamp(100d - health, 0d, 100d);
            }

            return new BatteryInfo(
                Name: win32?.String("Name"),
                PowerOnline: powerOnline ?? !discharging,
                Charging: charging,
                Discharging: discharging,
                ChargePercent: chargePercent,
                DesignCapacityMWh: designCapacity,
                FullChargeCapacityMWh: fullChargeCapacity,
                RemainingCapacityMWh: remainingCapacity,
                VoltageMillivolts: voltage,
                ChargeRateMilliwatts: chargeRate,
                DischargeRateMilliwatts: dischargeRate,
                HealthPercent: healthPercent,
                WearPercent: wearPercent,
                Source: InventorySource.Wmi);
        }

        private static uint? MaxZero(uint? value) => value is > 0 ? value : null;

        private static uint ClampPercent(uint percent) => Math.Clamp(percent, 0u, 100u);
    }
}
