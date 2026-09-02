using System;
using System.Collections.Generic;
using System.Linq;
using AIGeekTuner.Models.Hardware.Inventory;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// Gate J：网络适配器 inventory。physical/pseudo 过滤走"标记而非删除"：
    /// 明显的 pseudo adapter（WAN Miniport、Teredo、ISATAP、loopback 等）剔除；
    /// VPN/TAP/Hyper-V 等真实存在的虚拟适配器保留并 IsVirtual = true。
    /// IP/MAC 只进本机 inventory 展示，绝不做 AI prompt / report 的一部分。
    /// </summary>
    public static class NetworkInventoryMapper
    {
        public sealed record AdapterDescriptor(
            string Name,
            string Description,
            string InterfaceType,
            string OperationalStatus,
            ulong? LinkSpeedBps,
            string? MacAddress,
            IReadOnlyList<string> IPv4Addresses,
            IReadOnlyList<string> IPv6Addresses,
            bool? DhcpEnabled,
            IReadOnlyList<string> Gateways,
            IReadOnlyList<string> DnsServers);

        // 明显 pseudo：出现在 Description（小写）中即剔除。VPN/TAP/Hyper-V/WSL 不在内。
        private static readonly string[] PseudoMarkers =
        [
            "wan miniport",
            "teredo",
            "isatap",
            "microsoft kernel debug network adapter",
            "bluetooth device (personal area network)",
            "virtualbox host-only",
            "km-test",
            "microsoft wi-fi direct virtual adapter",
            "microsoft hosted network virtual adapter",
        ];

        public static IReadOnlyList<NetworkAdapterInventoryInfo> Map(
            IReadOnlyList<AdapterDescriptor> adapters)
        {
            var results = new List<NetworkAdapterInventoryInfo>(adapters.Count);
            foreach (var adapter in adapters)
            {
                var description = adapter.Description ?? string.Empty;
                var isVirtual = IsVirtual(description, adapter.InterfaceType);
                if (IsPseudo(description, adapter.InterfaceType))
                {
                    continue;
                }

                results.Add(new NetworkAdapterInventoryInfo(
                    Name: adapter.Name,
                    Description: adapter.Description,
                    InterfaceType: adapter.InterfaceType,
                    OperationalStatus: adapter.OperationalStatus,
                    LinkSpeedBps: adapter.LinkSpeedBps,
                    MacAddress: HardwarePlaceholderFilter.SanitizeSerialNumber(adapter.MacAddress),
                    IPv4Addresses: adapter.IPv4Addresses.ToArray(),
                    IPv6Addresses: adapter.IPv6Addresses.ToArray(),
                    DhcpEnabled: adapter.DhcpEnabled,
                    Gateways: adapter.Gateways.ToArray(),
                    DnsServers: adapter.DnsServers.ToArray(),
                    IsVirtual: isVirtual,
                    Source: InventorySource.WindowsNetwork));
            }

            return results
                .OrderBy(adapter => adapter.IsVirtual)
                .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static bool IsPseudo(string? description, string? interfaceType)
        {
            var lower = (description ?? string.Empty).ToLowerInvariant();
            if (PseudoMarkers.Any(marker => lower.Contains(marker)))
            {
                return true;
            }

            return interfaceType is "Loopback" or "Tunnel";
        }

        internal static bool IsVirtual(string? description, string? interfaceType)
        {
            var lower = (description ?? string.Empty).ToLowerInvariant();
            if (interfaceType is "Loopback" or "Tunnel")
            {
                return true;
            }

            return lower.Contains("virtual") || lower.Contains("hyper-v")
                || lower.Contains("vmware") || lower.Contains("tap")
                || lower.Contains("tun") || lower.Contains("wireguard")
                || lower.Contains("openvpn") || lower.Contains("wsl");
        }
    }
}
