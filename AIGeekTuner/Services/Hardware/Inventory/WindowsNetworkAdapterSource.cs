using System.Collections.Generic;
using System.Linq;
using AIGeekTuner.Services.Diagnostics;
using System.Net.NetworkInformation;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// Gate J：System.Net.NetworkInformation → 网络适配器描述符（纯 managed，无 native 依赖）。
    /// 过滤/标记逻辑在 NetworkInventoryMapper。
    /// </summary>
    public interface INetworkAdapterSource
    {
        IReadOnlyList<NetworkInventoryMapper.AdapterDescriptor> GetAdapters();
    }

    public sealed class WindowsNetworkAdapterSource : INetworkAdapterSource
    {
        public IReadOnlyList<NetworkInventoryMapper.AdapterDescriptor> GetAdapters()
        {
            var results = new List<NetworkInventoryMapper.AdapterDescriptor>();
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        var properties = adapter.GetIPProperties();
                        var unicast = properties.UnicastAddresses.ToArray();
                        var ipv4 = unicast
                            .Where(address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            .Select(address => address.Address.ToString())
                            .ToArray();
                        var ipv6 = unicast
                            .Where(address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                            .Select(address => address.Address.ToString())
                            .ToArray();

                        results.Add(new NetworkInventoryMapper.AdapterDescriptor(
                            Name: adapter.Name,
                            Description: adapter.Description,
                            InterfaceType: adapter.NetworkInterfaceType.ToString(),
                            OperationalStatus: adapter.OperationalStatus.ToString(),
                            LinkSpeedBps: (ulong)adapter.Speed,
                            MacAddress: FormatMac(adapter.GetPhysicalAddress()),
                            IPv4Addresses: ipv4,
                            IPv6Addresses: ipv6,
                            DhcpEnabled: properties.GetIPv4Properties()?.IsDhcpEnabled,
                            Gateways: properties.GatewayAddresses
                                .Select(gateway => gateway.Address?.ToString())
                                .Where(text => !string.IsNullOrEmpty(text) && text != "0.0.0.0")
                                .Select(text => text!)
                                .ToArray(),
                            DnsServers: properties.DnsAddresses
                                .Select(address => address.ToString())
                                .ToArray()));
                    }
                    catch (System.Exception exception)
                    {
                        // 单个适配器异常不拖垮网络 inventory（Gate M）。
                        ExceptionLogWriter.Write(exception, "Inventory/Network/adapter");
                    }
                }
            }
            catch (System.Exception exception) when (exception is not System.OperationCanceledException)
            {
                ExceptionLogWriter.Write(exception, "Inventory/Network");
            }

            return results;
        }

        private static string? FormatMac(PhysicalAddress address)
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length == 0)
            {
                return null;
            }

            return string.Join("-", bytes.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
        }
    }
}