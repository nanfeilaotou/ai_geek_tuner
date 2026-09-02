using AIGeekTuner.Models.Hardware.Inventory;
using AIGeekTuner.Services.Hardware.Inventory;
using Xunit;

namespace AIGeekTuner.Tests.Services.Hardware.Inventory
{
    /// <summary>Gate N：Audio endpoint 与 Network adapter mapper。</summary>
    public sealed class AudioNetworkMapperTests
    {
        // ---- Gate I：Audio ----

        [Fact]
        public void Audio_Playback_Capture_Default_AreMapped()
        {
            var endpoints = new List<AudioInventoryMapper.EndpointDescriptor>
            {
                new("endpoint-1", "扬声器 (Realtek Audio)", AudioEndpointDirection.Playback, "Active", true),
                new("endpoint-2", "耳机 (NVIDIA HD Audio)", AudioEndpointDirection.Playback, "Active", false),
                new("endpoint-3", "麦克风 (Webcam)", AudioEndpointDirection.Capture, "Active", true),
            };

            var devices = AudioInventoryMapper.Map(endpoints);

            Assert.Equal(3, devices.Count);
            Assert.Equal(AudioEndpointDirection.Playback, devices[0].Direction);
            Assert.True(devices[0].IsDefault);
            Assert.False(devices[1].IsDefault);
            Assert.Equal(AudioEndpointDirection.Capture, devices[2].Direction);
            Assert.All(devices, device => Assert.Equal(InventorySource.CoreAudio, device.Source));
        }

        // ---- Gate J：Network ----

        private static NetworkInventoryMapper.AdapterDescriptor Adapter(
            string name,
            string description,
            string type = "Ethernet",
            string status = "Up",
            bool? dhcp = null,
            string[]? ipv4 = null,
            string[]? ipv6 = null) => new(
            Name: name,
            Description: description,
            InterfaceType: type,
            OperationalStatus: status,
            LinkSpeedBps: 1_000_000_000,
            MacAddress: "AA-BB-CC-DD-EE-FF",
            IPv4Addresses: ipv4 ?? ["192.168.1.10"],
            IPv6Addresses: ipv6 ?? ["fe80::1"],
            DhcpEnabled: dhcp,
            Gateways: ["192.168.1.1"],
            DnsServers: ["192.168.1.1", "fd00::1"]);

        [Fact]
        public void Network_PhysicalAdapters_Kept_WithAddresses()
        {
            var adapters = NetworkInventoryMapper.Map(
            [
                Adapter("以太网", "Realtek Gaming 2.5GbE Family Controller"),
                Adapter("WLAN", "Intel(R) Wi-Fi 6 AX210", type: "Wireless80211"),
            ]);

            Assert.Equal(2, adapters.Count);
            Assert.All(adapters, adapter => Assert.False(adapter.IsVirtual));
            Assert.Contains("192.168.1.10", adapters[0].IPv4Addresses);
            Assert.Contains("fe80::1", adapters[0].IPv6Addresses);
            Assert.Equal("192.168.1.1", Assert.Single(adapters[0].Gateways));
        }

        [Fact]
        public void Network_VirtualAdapters_Kept_ButMarkedVirtual()
        {
            var adapters = NetworkInventoryMapper.Map(
            [
                Adapter("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter"),
                Adapter("WireGuard Tunnel", "WireGuard Tunnel"),
            ]);

            Assert.Equal(2, adapters.Count); // 不误删真实用户需求（VPN/虚拟交换机）
            Assert.All(adapters, adapter => Assert.True(adapter.IsVirtual));
        }

        [Fact]
        public void Network_PseudoAdapters_AreRemoved()
        {
            var adapters = NetworkInventoryMapper.Map(
            [
                Adapter("WAN Miniport (IP)", "WAN Miniport (IP)"),
                Adapter("Teredo", "Microsoft Teredo Tunneling Adapter"),
                Adapter("Loopback", "Microsoft Loopback", type: "Loopback"),
                Adapter("以太网", "Realtek Gaming 2.5GbE Family Controller"),
            ]);

            Assert.Single(adapters); // 只剩物理网卡
            Assert.Equal("以太网", adapters[0].Name);
        }

        [Fact]
        public void Network_PlaceholderMac_BecomesNull()
        {
            var adapter = Adapter("以太网", "Realtek") with { MacAddress = "None" };

            var results = NetworkInventoryMapper.Map([adapter]);

            Assert.Single(results);
            Assert.Null(results[0].MacAddress);
        }
    }
}