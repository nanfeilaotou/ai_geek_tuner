using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry;

namespace AIGeekTuner.Tests.Services.Telemetry
{
    /// <summary>§27 设备 Reconciliation：重点断言“不会发生错误 merge”。</summary>
    public class TelemetryDeviceReconcilerTests
    {
        private static SourceDeviceInfo Gpu(
            TelemetrySourceKind source,
            string nativeId,
            string name,
            int ordinal,
            params string[] strongIds) =>
            new(source, TelemetryDeviceKind.Gpu, nativeId, name, ordinal, strongIds);

        private static SourceDeviceInfo Disk(
            TelemetrySourceKind source,
            string nativeId,
            string name,
            int ordinal) =>
            new(source, TelemetryDeviceKind.Storage, nativeId, name, ordinal, []);

        private static Dictionary<(TelemetrySourceKind, string), string> KeyMap(
            IReadOnlyList<CanonicalDeviceGroup> groups)
        {
            var map = new Dictionary<(TelemetrySourceKind, string), string>();
            foreach (var group in groups)
            {
                foreach (var member in group.Members)
                {
                    map[(member.Source, member.NativeDeviceId)] = group.CanonicalKey;
                }
            }

            return map;
        }

        [Fact]
        public void CaseA_SingleGpu_ReportedByAllProviders_MergesDespiteNameStyle()
        {
            var devices = new[]
            {
                Gpu(TelemetrySourceKind.HwInfo, "gpu:5", "GPU [#0]: NVIDIA GeForce RTX 4080", 0),
                Gpu(TelemetrySourceKind.Aida64, "gpu:0", "", 0),
                Gpu(TelemetrySourceKind.LibreHardwareMonitor, "gpu:0", "NVIDIA GeForce RTX 4080 Laptop GPU", 0),
            };

            var groups = TelemetryDeviceReconciler.Reconcile(devices);

            var gpuGroups = groups.Where(group => group.Kind == TelemetryDeviceKind.Gpu).ToArray();
            var single = Assert.Single(gpuGroups);
            Assert.Equal(3, single.Members.Count);
        }

        [Fact]
        public void CaseB_IgpuAndDgpu_ReversedOrdinals_NeverMergeByOrdinal()
        {
            // AIDA: GPU1=Intel GPU2=NVIDIA；LHM: 第一个=NVIDIA 第二个=Intel。
            var devices = new[]
            {
                new SourceDeviceInfo(TelemetrySourceKind.Aida64, TelemetryDeviceKind.Gpu, "gpu:0", "Intel Iris Xe", 0, []),
                new SourceDeviceInfo(TelemetrySourceKind.Aida64, TelemetryDeviceKind.Gpu, "gpu:1", "NVIDIA GeForce RTX 4080", 1, []),
                new SourceDeviceInfo(TelemetrySourceKind.LibreHardwareMonitor, TelemetryDeviceKind.Gpu, "gpu:0", "NVIDIA GeForce RTX 4080", 0, []),
                new SourceDeviceInfo(TelemetrySourceKind.LibreHardwareMonitor, TelemetryDeviceKind.Gpu, "gpu:1", "Intel Iris Xe Graphics", 1, []),
            };

            var map = KeyMap(TelemetryDeviceReconciler.Reconcile(devices));

            // Intel 与 NVIDIA 各自合并成一组；两组的 canonical key 必须不同。
            var intelKey = map[(TelemetrySourceKind.Aida64, "gpu:0")];
            var nvidiaKey = map[(TelemetrySourceKind.Aida64, "gpu:1")];
            Assert.NotEqual(intelKey, nvidiaKey);
            Assert.Equal(nvidiaKey, map[(TelemetrySourceKind.LibreHardwareMonitor, "gpu:0")]);
            Assert.Equal(intelKey, map[(TelemetrySourceKind.LibreHardwareMonitor, "gpu:1")]);
        }

        [Fact]
        public void CaseC_DualNvidiaDistinctModels_MergeByExactModel()
        {
            var devices = new[]
            {
                Gpu(TelemetrySourceKind.HwInfo, "gpu:10", "GPU [#0]: NVIDIA GeForce RTX 3080", 0),
                Gpu(TelemetrySourceKind.HwInfo, "gpu:11", "GPU [#1]: NVIDIA GeForce RTX 4090", 1),
                Gpu(TelemetrySourceKind.LibreHardwareMonitor, "gpu:0", "NVIDIA GeForce RTX 4090", 0),
                Gpu(TelemetrySourceKind.LibreHardwareMonitor, "gpu:1", "NVIDIA GeForce RTX 3080", 1),
            };

            var map = KeyMap(TelemetryDeviceReconciler.Reconcile(devices));

            var key3080 = map[(TelemetrySourceKind.HwInfo, "gpu:10")];
            var key4090 = map[(TelemetrySourceKind.HwInfo, "gpu:11")];
            Assert.NotEqual(key3080, key4090);
            // LHM 的顺序与 HWiNFO 相反——按名称而不是按 ordinal 合并：
            Assert.Equal(key3080, map[(TelemetrySourceKind.LibreHardwareMonitor, "gpu:1")]);
            Assert.Equal(key4090, map[(TelemetrySourceKind.LibreHardwareMonitor, "gpu:0")]);
        }

        [Fact]
        public void CaseD_DualSameModel_NoStrongIdentity_StaysUnresolved()
        {
            var name = "NVIDIA GeForce RTX 4090";
            var devices = new[]
            {
                Gpu(TelemetrySourceKind.HwInfo, "gpu:a", "GPU [#0]: " + name, 0),
                Gpu(TelemetrySourceKind.HwInfo, "gpu:b", "GPU [#1]: " + name, 1),
                Gpu(TelemetrySourceKind.LibreHardwareMonitor, "gpu:0", name, 0),
                Gpu(TelemetrySourceKind.LibreHardwareMonitor, "gpu:1", name, 1),
            };

            var groups = TelemetryDeviceReconciler.Reconcile(devices);

            // 名称在来源内不唯一 → 不允许任何 merge：4 个设备 = 4 个独立组。
            Assert.Equal(4, groups.Count(group => group.Kind == TelemetryDeviceKind.Gpu));
        }

        [Fact]
        public void PartialVisibility_SeenDevicesDiffer_SingletonMustNotMerge()
        {
            // AIDA 只看到 iGPU，HWiNFO 只看到 dGPU（部分可见）：
            // 虽然各自都只报了 1 个 GPU，但名称互相矛盾 → 禁止单例合并。
            var devices = new[]
            {
                Gpu(TelemetrySourceKind.HwInfo, "gpu:0", "GPU [#0]: NVIDIA GeForce RTX 4080", 0),
                Gpu(TelemetrySourceKind.Aida64, "gpu:0", "Intel Iris Xe", 0),
            };

            var groups = TelemetryDeviceReconciler.Reconcile(devices);

            Assert.Equal(2, groups.Count(group => group.Kind == TelemetryDeviceKind.Gpu));
        }

        [Fact]
        public void StrongIds_MatchAcrossProviders_EvenWhenNamesDiffer()
        {
            var devices = new[]
            {
                Gpu(TelemetrySourceKind.HwInfo, "gpu:x", "Weird Name A", 0, "pci:10de-2704"),
                Gpu(TelemetrySourceKind.Aida64, "gpu:0", "Another Label", 0, "pci:10de-2704"),
            };

            var groups = TelemetryDeviceReconciler.Reconcile(devices);

            var single = Assert.Single(groups, group => group.Kind == TelemetryDeviceKind.Gpu);
            Assert.Equal(2, single.Members.Count);
        }

        [Fact]
        public void SingleDisk_AidaIndexAndLhmDrive_SingletonMerge()
        {
            var devices = new[]
            {
                Disk(TelemetrySourceKind.Aida64, "storage:hdd:1", "HDD #1", 0),
                Disk(TelemetrySourceKind.LibreHardwareMonitor, "storage:Samsung SSD 990 PRO", "Samsung SSD 990 PRO", 0),
            };

            var groups = TelemetryDeviceReconciler.Reconcile(devices);

            var single = Assert.Single(groups, group => group.Kind == TelemetryDeviceKind.Storage);
            Assert.Equal(2, single.Members.Count);
        }

        [Fact]
        public void DualDisk_AidaIndexesVsLhmNames_DoNotMerge()
        {
            var devices = new[]
            {
                Disk(TelemetrySourceKind.Aida64, "storage:hdd:1", "HDD #1", 0),
                Disk(TelemetrySourceKind.Aida64, "storage:hdd:2", "HDD #2", 1),
                Disk(TelemetrySourceKind.LibreHardwareMonitor, "storage:ssd-a", "SSD A", 0),
                Disk(TelemetrySourceKind.LibreHardwareMonitor, "storage:ssd-b", "SSD B", 1),
            };

            var groups = TelemetryDeviceReconciler.Reconcile(devices);

            Assert.Equal(4, groups.Count(group => group.Kind == TelemetryDeviceKind.Storage));
        }

        [Fact]
        public void UnresolvedDevices_KeepSourceLocalNamespace()
        {
            var devices = new[] { Disk(TelemetrySourceKind.Aida64, "storage:hdd:2", "HDD #2", 1) };

            var groups = TelemetryDeviceReconciler.Reconcile(devices);

            var single = Assert.Single(groups, group => group.Kind == TelemetryDeviceKind.Storage);
            Assert.Contains("src:", single.CanonicalKey);
            Assert.Contains("Aida64", single.CanonicalKey);
        }
    }
}
