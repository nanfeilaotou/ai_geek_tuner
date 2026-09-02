using System.Collections.Generic;
using AIGeekTuner.Models.Hardware.Inventory;

namespace AIGeekTuner.Services.Hardware.Inventory
{
    /// <summary>
    /// Gate I：音频 endpoint inventory（Core Audio MMDevice）。
    /// 只读枚举 playback/capture + default 标记；不做音量/切默认/播放测试。
    /// Win32_SoundDevice 的硬件信息本轮不混入 endpoint 列表。
    /// </summary>
    public static class AudioInventoryMapper
    {
        public sealed record EndpointDescriptor(
            string Id,
            string? FriendlyName,
            AudioEndpointDirection Direction,
            string? State,
            bool IsDefault);

        public static IReadOnlyList<AudioDeviceInfo> Map(IReadOnlyList<EndpointDescriptor> endpoints)
        {
            var devices = new List<AudioDeviceInfo>(endpoints.Count);
            foreach (var endpoint in endpoints)
            {
                devices.Add(new AudioDeviceInfo(
                    FriendlyName: endpoint.FriendlyName,
                    DeviceId: endpoint.Id,
                    Direction: endpoint.Direction,
                    State: endpoint.State,
                    IsDefault: endpoint.IsDefault,
                    Source: InventorySource.CoreAudio));
            }

            return devices;
        }
    }
}
