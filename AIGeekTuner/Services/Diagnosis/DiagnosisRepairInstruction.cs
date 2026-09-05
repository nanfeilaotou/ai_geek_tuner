namespace AIGeekTuner.Services.Diagnosis
{
    /// <summary>
    /// 诊断结果解析失败后的唯一一次修复重试指令（V1 起语义不变）。
    /// OllamaService（legacy）与 provider-aware 运行时路径共用同一份文案，避免漂移。
    /// </summary>
    internal static class DiagnosisRepairInstruction
    {
        public static string Build(DiagnosticResultParsingException parseError)
        {
            return $$"""

你上一条回答未能通过 JSON 结构校验：{{parseError.Message}}

请严格按以下要求重新输出：
1. 只输出一个合法的 JSON 对象；第一个非空白字符必须是 {{'{'}}，最后一个非空白字符必须是 {{'}'}}。
2. 字段结构与最初要求完全一致：summary、rootCause、confidence（0 到 1 的数字）、riskLevel（只能取 "Low"、"Medium"、"High"）、evidence 数组（kind 只能取 "Fact" 或 "Inference"，description 必填）、recommendations 数组（action、reason、riskLevel 必填，precautions 为字符串数组）。
3. 尽可能保留上一条回答中已有的诊断语义与结论，只修正 JSON 结构和非法字段值。
4. 禁止新增上一条回答中没有依据的事实；禁止编造温度、电压、功耗、BIOS、超频状态等输入中不存在的数据。
5. 禁止输出 Markdown、代码围栏、解释文字或 <think> 标签。
""";
        }
    }
}
