namespace AIGeekTuner.Configuration
{
    public sealed class SafetyGuardOptions
    {
        public double LowConfidenceThreshold { get; init; } = 0.65;

        public decimal RejectVoltageAboveVolts { get; init; } = 1.70m;

        public decimal RejectVoltageAtOrBelowVolts { get; init; } = 0m;

        public IReadOnlyList<string> MonitoredVoltageParameters { get; init; } =
        [
            "Vcore",
            "CPU Voltage",
            "VDDQ",
            "VDD",
            "SOC Voltage"
        ];

        public IReadOnlyList<string> DangerousOperationPhrases { get; init; } =
        [
            "禁用保护机制",
            "关闭保护机制",
            "绕过安全限制",
            "绕过保护限制",
            "直接修改关键系统保护",
            "禁用过热保护",
            "关闭过热保护",
            "禁用过流保护",
            "关闭过流保护",
            "disable protection",
            "disable safety protection",
            "bypass safety limit",
            "bypass protection limit",
            "disable thermal protection",
            "disable overcurrent protection"
        ];

        public IReadOnlyList<string> AbsoluteCertaintyPhrases { get; init; } =
        [
            "一定损坏",
            "一定坏了",
            "绝对损坏",
            "绝对是",
            "肯定损坏",
            "肯定是",
            "毫无疑问",
            "完全确定",
            "必然是",
            "100%",
            "definitely damaged",
            "certainly damaged",
            "without a doubt",
            "absolutely certain"
        ];
    }
}
