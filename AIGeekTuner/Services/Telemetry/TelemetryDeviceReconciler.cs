using AIGeekTuner.Models.Telemetry;

namespace AIGeekTuner.Services.Telemetry
{
    /// <summary>
    /// (Source, 源本地设备键) → canonical 设备身份 的只读查找表。
    /// 未命中 = 未合并设备：Hub 会保留其源侧身份，不参与跨源 fallback。
    /// </summary>
    public sealed class DeviceReconciliationLookup
    {
        private readonly Dictionary<(TelemetrySourceKind Source, string NativeKey), TelemetryDeviceIdentity> _map;

        public DeviceReconciliationLookup(
            Dictionary<(TelemetrySourceKind Source, string NativeKey), TelemetryDeviceIdentity> map)
        {
            _map = map;
        }

        public static DeviceReconciliationLookup Empty { get; } =
            new(new Dictionary<(TelemetrySourceKind, string), TelemetryDeviceIdentity>());

        public bool TryResolve(
            TelemetrySourceKind source,
            string nativeDeviceKey,
            out TelemetryDeviceIdentity identity)
        {
            if (_map.TryGetValue((source, nativeDeviceKey), out var resolved))
            {
                identity = resolved;
                return true;
            }

            identity = null!;
            return false;
        }
    }

    /// <summary>
    /// 一组被判定为同一物理设备的源侧设备（可能只有一个成员——未合并的源本地设备也是一组）。
    /// </summary>
    public sealed record CanonicalDeviceGroup(
        TelemetryDeviceKind Kind,
        string CanonicalKey,
        string DisplayName,
        IReadOnlyList<SourceDeviceInfo> Members);

    /// <summary>
    /// 设备 Reconciliation（§14/§15/§16）：只回答“这些是不是同一块物理硬件”，
    /// 绝不做 metric mapping。证据从强到弱：
    /// 1) StrongIds 精确匹配（serial / PCI 身份等，当前各来源尚未提供，规则已就绪）；
    /// 2) 单例规则：所有报告了该 Kind 设备的 Provider 都恰好报了 1 个 → 无歧义合并；
    /// 3) 归一化名称唯一双射：名称归一后完全相等，且每个 Provider 在该 Kind 下至多贡献一个成员、
    ///    且该归一名在其来源内唯一（同型号双卡因此不会被合并）；
    /// 其余一律保持源本地命名空间（src:{source}:{nativeId}），宁可不合并也不错并。
    /// Ordinal 永远不参与合并判定。
    /// </summary>
    public static class TelemetryDeviceReconciler
    {
        public static IReadOnlyList<CanonicalDeviceGroup> Reconcile(
            IEnumerable<SourceDeviceInfo> devices)
        {
            ArgumentNullException.ThrowIfNull(devices);

            // 同一 (Source,NativeDeviceId) 可能因变体传感器折叠产生重复声明：按首个为准。
            var distinctDevices = devices
                .GroupBy(device => (device.Source, device.Kind, device.NativeDeviceId))
                .Select(group => group.First())
                .ToArray();

            var result = new List<CanonicalDeviceGroup>();
            foreach (var kindGroup in distinctDevices.GroupBy(device => device.Kind))
            {
                var pending = kindGroup.ToList();
                var mergedFlags = new HashSet<SourceDeviceInfo>();

                // ---- 1) 强身份：任意 StrongId 相等即合并（跨 Provider union-find 的简化：
                //         当前没有来源提供强 ID；一旦提供，规则立即生效）----
                foreach (var group in pending.GroupBy(device =>
                    device.StrongIds.Count > 0 ? device.StrongIds[0] : null,
                    StringComparer.OrdinalIgnoreCase))
                {
                    if (group.Key is null || group.Key.Length == 0)
                    {
                        continue;
                    }

                    var members = group.Distinct().ToArray();
                    if (members.Select(member => member.Source).Distinct().Count() >= 2)
                    {
                        result.Add(CreateGroup(kindGroup.Key, $"strong:{Sanitize(group.Key)}", members));
                        foreach (var member in members)
                        {
                            mergedFlags.Add(member);
                        }
                    }
                }

                pending = pending.Where(device => !mergedFlags.Contains(device)).ToList();

                // ---- 2) 单例规则：所有报告该 Kind 的 Provider 都恰好报 1 个，
                //         且已提供的非空归一名称互不矛盾（防止“AIDA 只见 iGPU、
                //         HWiNFO 只见 dGPU”的部分可见被误判成单卡）----
                var bySource = pending
                    .GroupBy(device => device.Source)
                    .ToArray();
                var nonEmptyNames = pending
                    .Select(device => NormalizeName(device.NativeDeviceName))
                    .Where(name => name.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var namesConsistent = nonEmptyNames.Count <= 1;
                // 名称矛盾守卫只对 GPU 生效：iGPU/dGPU 部分可见是真实风险；
                // 存储/内存的盘名标签差异不代表身份矛盾。
                var guardApplies = kindGroup.Key == TelemetryDeviceKind.Gpu;
                if (bySource.Length >= 2
                    && bySource.All(sourceGroup => sourceGroup.Count() == 1)
                    && (namesConsistent || !guardApplies))
                {
                    var members = bySource.Select(group => group.First()).ToArray();
                    result.Add(CreateGroup(
                        kindGroup.Key,
                        "singleton",
                        members));
                    continue; // 该 Kind 已整体合并
                }

                // ---- 3) 归一化名称匹配：先精确相等分组；再做“互为唯一包含”配对 ----
                var formedGroups = new List<CanonicalDeviceGroup>();
                var exactGroups = pending
                    .GroupBy(device => NormalizeName(device.NativeDeviceName),
                        StringComparer.OrdinalIgnoreCase)
                    .Where(nameGroup => nameGroup.Key.Length > 0)
                    .Where(nameGroup => nameGroup.Select(member => member.Source)
                        .Distinct()
                        .Count() >= 2)
                    .Where(nameGroup => nameGroup.GroupBy(member => member.Source).All(
                        sourceGroup => sourceGroup.Count() == 1)) // 名称在各自来源内必须唯一
                    .ToArray();
                foreach (var nameGroup in exactGroups)
                {
                    var members = nameGroup.ToArray();
                    var group = CreateGroup(
                        kindGroup.Key,
                        $"name:{Sanitize(nameGroup.Key)}",
                        members);
                    formedGroups.Add(group);
                    result.Add(group);
                    foreach (var member in members)
                    {
                        mergedFlags.Add(member);
                    }
                }

                pending = pending.Where(device => !mergedFlags.Contains(device)).ToList();

                // 互为唯一包含：A 包含 B（或反之，短名 ≥ 6 字符），
                // 且 A 在 B 的来源里是唯一包含候选、B 在 A 的来源里也是唯一包含候选。
                // 这让 “Intel Iris Xe” 与 “Intel Iris Xe Graphics” 能合并，
                // 而同型号双卡（互相都是候选）不会合并。
                var bySourcePending = pending.GroupBy(device => device.Source).ToArray();
                var containmentPairs = new List<(SourceDeviceInfo A, SourceDeviceInfo B)>();
                foreach (var first in pending)
                {
                    foreach (var second in pending)
                    {
                        if (first.Source == second.Source || ReferenceEquals(first, second))
                        {
                            continue;
                        }

                        // 全局互为唯一包含候选：双方的包含候选集都只有对方。
                        if (AreContainmentRelated(first, second, pending)
                            && CountContainmentCandidates(first, pending) == 1
                            && CountContainmentCandidates(second, pending) == 1)
                        {
                            containmentPairs.Add((first, second));
                        }
                    }
                }

                var unionFind = pending.ToDictionary(device => device);
                foreach (var (first, second) in containmentPairs)
                {
                    var rootFirst = Find(unionFind, first);
                    var rootSecond = Find(unionFind, second);
                    if (ReferenceEquals(rootFirst, rootSecond))
                    {
                        continue;
                    }

                    var mergedMembers = MembersOf(unionFind, rootFirst)
                        .Concat(MembersOf(unionFind, rootSecond))
                        .ToArray();
                    var duplicateProvider = mergedMembers
                        .GroupBy(member => member.Source)
                        .Any(sourceGroup => sourceGroup.Count() > 1);
                    if (!duplicateProvider)
                    {
                        unionFind[rootFirst] = rootSecond;
                    }
                }

                var containmentClusters = unionFind.Values.Distinct().ToList();
                foreach (var clusterRoot in containmentClusters)
                {
                    var members = MembersOf(unionFind, clusterRoot).ToArray();
                    if (members.Length < 2
                        || members.Select(member => member.Source).Distinct().Count() < 2)
                    {
                        continue;
                    }

                    var group = CreateGroup(
                        kindGroup.Key,
                        $"name:{Sanitize(NormalizeName(members[0].NativeDeviceName))}",
                        members);
                    formedGroups.Add(group);
                    result.Add(group);
                    foreach (var member in members)
                    {
                        mergedFlags.Add(member);
                    }
                }

                pending = pending.Where(device => !mergedFlags.Contains(device)).ToList();

                // ---- 其余：保持源本地命名空间；但空名设备（来源不提供型号名）
                // 在“本轮恰好只形成一个多成员组”时允许挂靠——无矛盾证据。----
                var emptyNamed = pending
                    .Where(device => NormalizeName(device.NativeDeviceName).Length == 0)
                    .ToList();
                // 仅当“恰好一个空名设备 + 恰好一个合并组”时才挂靠；
                // 多个空名（如 AIDA 同时给出 GPU#1/GPU#2）无法定位，保持源本地。
                if (emptyNamed.Count == 1
                    && formedGroups.Count == 1
                    && formedGroups[0].Members.All(member => member.Source != emptyNamed[0].Source))
                {
                    var target = formedGroups[0];
                    var members = target.Members.ToList();
                    members.AddRange(emptyNamed);
                    result.Remove(target);
                    result.Add(new CanonicalDeviceGroup(
                        target.Kind,
                        target.CanonicalKey,
                        members
                            .Select(member => member.NativeDeviceName.Trim())
                            .Where(name => name.Length > 0)
                            .OrderByDescending(name => name.Length)
                            .FirstOrDefault() ?? target.DisplayName,
                        members));
                    foreach (var emptyDevice in emptyNamed)
                    {
                        mergedFlags.Add(emptyDevice);
                    }
                }

                pending = pending.Where(device => !mergedFlags.Contains(device)).ToList();

                // ---- 其余：保持源本地命名空间 ----
                foreach (var device in pending)
                {
                    result.Add(new CanonicalDeviceGroup(
                        device.Kind,
                        $"src:{device.Source}:{device.NativeDeviceId}",
                        string.IsNullOrWhiteSpace(device.NativeDeviceName)
                            ? device.Kind.ToString()
                            : device.NativeDeviceName,
                        [device]));
                }
            }

            return result;
        }

        /// <summary>由合并结果构建 Hub 使用的查找表。</summary>
        public static DeviceReconciliationLookup ToLookup(
            IReadOnlyList<CanonicalDeviceGroup> groups)
        {
            var map = new Dictionary<(TelemetrySourceKind, string), TelemetryDeviceIdentity>();
            foreach (var group in groups)
            {
                foreach (var member in group.Members)
                {
                    map[(member.Source, member.NativeDeviceId)] =
                        new TelemetryDeviceIdentity(group.Kind, group.CanonicalKey, group.DisplayName);
                }
            }

            return new DeviceReconciliationLookup(map);
        }

        private static CanonicalDeviceGroup CreateGroup(
            TelemetryDeviceKind kind,
            string canonicalKeySuffix,
            IReadOnlyList<SourceDeviceInfo> members)
        {
            var displayName = members
                .Select(member => member.NativeDeviceName.Trim())
                .Where(name => name.Length > 0)
                .OrderByDescending(name => name.Length)
                .FirstOrDefault() ?? kind.ToString();
            return new CanonicalDeviceGroup(
                kind,
                $"{kind.ToString().ToLowerInvariant()}:{canonicalKeySuffix}",
                displayName,
                members);
        }

        /// <summary>短名 ≥6 字符且被对方归一名包含（双向任一）。</summary>
        private static bool AreContainmentRelated(
            SourceDeviceInfo first,
            SourceDeviceInfo second,
            IReadOnlyList<SourceDeviceInfo> pool)
        {
            var a = NormalizeName(first.NativeDeviceName);
            var b = NormalizeName(second.NativeDeviceName);
            if (a.Length < 6 || b.Length < 6)
            {
                return false;
            }

            return a.Contains(b, StringComparison.Ordinal)
                || b.Contains(a, StringComparison.Ordinal);
        }

        /// <summary>pool 中与 self 存在包含关系的其他来源设备数量。</summary>
        private static int CountContainmentCandidates(
            SourceDeviceInfo self,
            IReadOnlyList<SourceDeviceInfo> pool)
        {
            var count = 0;
            foreach (var candidate in pool)
            {
                if (ReferenceEquals(candidate, self)
                    || candidate.Source == self.Source
                    || candidate.Kind != self.Kind)
                {
                    continue;
                }

                if (AreContainmentRelated(self, candidate, pool))
                {
                    count++;
                }
            }

            return count;
        }

        private static SourceDeviceInfo Find(
            Dictionary<SourceDeviceInfo, SourceDeviceInfo> parent,
            SourceDeviceInfo device)
        {
            while (!ReferenceEquals(parent[device], device))
            {
                device = parent[device];
            }

            return device;
        }

        private static IEnumerable<SourceDeviceInfo> MembersOf(
            Dictionary<SourceDeviceInfo, SourceDeviceInfo> parent,
            SourceDeviceInfo root)
        {
            return parent.Keys.Where(device => ReferenceEquals(Find(parent, device), root));
        }

        private static string Sanitize(string value)
        {
            var builder = new System.Text.StringBuilder(value.Length);
            foreach (var ch in value.ToLowerInvariant())
            {
                builder.Append(char.IsLetterOrDigit(ch) ? ch : '-');
            }

            return builder.ToString();
        }

        /// <summary>
        /// 弱名称归一：小写、去多余空白、剥掉 HWiNFO 常见的 “GPU [#0]:” 类前缀与标点。
        /// 只用于第 3 层证据，不参与任何 metric 判定。
        /// </summary>
        public static string NormalizeName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var text = name.Trim();
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                "\\b(?<prefix>[a-z]+\\s*\\[#\\d+\\]\\s*:?\\s*)",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, "[\\s\\-_:]+", " ").Trim();
            text = text.ToLowerInvariant();
            // 占位名（来源不给型号时只给序号/类别）视为匿名，不参与名称证据。
            if (text == "cpu" || text == "memory" || text == "storage" || text == "system"
                || System.Text.RegularExpressions.Regex.IsMatch(
                    text,
                    "^gpu #\\d+$|^hdd #\\d+$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return string.Empty;
            }

            return text;
        }
    }
}
