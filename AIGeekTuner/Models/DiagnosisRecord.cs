using System.Text.Json.Serialization;

namespace AIGeekTuner.Models
{
    /// <summary>
    /// 历史索引条目。新增字段对旧数据向后兼容：
    /// 反序列化缺失时 Succeeded=true、ModelName/DurationMs/FailureReason 为 null
    /// （UI 显示“未知”），不伪造具体值。
    /// </summary>
    public sealed class DiagnosisRecord
    {
        public required Guid DiagnosisId { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required string LogFileName { get; init; }

        /// <summary>成功记录为 AI 摘要；失败记录为用户可读的失败原因摘要。</summary>
        public required string Summary { get; init; }

        public required DiagnosticRiskLevel RiskLevel { get; init; }

        public required double Confidence { get; init; }

        public required SafetyStatus SafetyStatus { get; init; }

        public required string DiagnosisOutcomePath { get; init; }

        // ---- V1 新增元数据（旧文件缺省时保持合理空值） ----

        public bool Succeeded { get; init; } = true;

        public string? ModelName { get; init; }

        /// <summary>V2-M5.1B（Gate L）：产生结果的 Provider 显示名（旧文件缺省 null）。</summary>
        public string? ProviderName { get; init; }

        public long? DurationMs { get; init; }

        public string? FailureReason { get; init; }

        // ---- 列表展示辅助（不参与序列化） ----

        [JsonIgnore]
        public string StatusDisplay => Succeeded ? "成功" : "失败";

        [JsonIgnore]
        public string ModelDisplay =>
            string.IsNullOrWhiteSpace(ModelName) ? "未知" : ModelName!;

        [JsonIgnore]
        public string DurationDisplay
        {
            get
            {
                if (DurationMs is null)
                {
                    return "未知";
                }

                var totalSeconds = DurationMs.Value / 1000d;
                return totalSeconds >= 60
                    ? $"{(int)(totalSeconds / 60)} 分 {totalSeconds % 60:0} 秒"
                    : $"{totalSeconds:0.#} 秒";
            }
        }
    }
}
