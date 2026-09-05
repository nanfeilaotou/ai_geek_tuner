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
            ArgumentNullException.ThrowIfNull(parseError);
            return Build("结构校验失败：" + parseError.Message);
        }

        public static string Build(DiagnosticGroundingValidationException groundingError)
        {
            ArgumentNullException.ThrowIfNull(groundingError);
            return Build("grounding 校验失败：" + groundingError.ToRepairMessage());
        }

        private static string Build(string validationMessage)
        {
            return $$"""

你上一条回答未能通过 JSON 结构校验或 grounding 校验：{{validationMessage}}

请严格按以下要求重新输出：
1. 只输出一个合法的 JSON 对象；第一个非空白字符必须是 {{'{'}}，最后一个非空白字符必须是 {{'}'}}。
2. 字段结构与最初要求完全一致：summary、rootCause、confidence（0 到 1 的数字）、riskLevel（只能取 "Low"、"Medium"、"High"）、evidence 数组（kind、description；Fact 还必须有 sourceId 和 sourceQuote）、recommendations 数组（action、reason、riskLevel 必填，precautions 为字符串数组）。
3. 结构错误时，尽量保留已有且受证据支持的语义，只修正 JSON 结构和非法字段值。
4. grounding 错误时，删除 unsupported Fact，或用本次 source 中真实存在的逐字符原文修正 sourceId/sourceQuote；不得为了保留上一条回答而继续保留虚假事实。
5. sourceId 只能引用本次用户消息中实际出现的 source block；sourceQuote 必须逐字符存在于该 source 内容中。无法引用的内容改为 Inference 或省略。
6. 禁止新增本次 sources 中不存在的事实；禁止编造温度、电压、功耗、BIOS、超频状态等输入中不存在的数据。
7. 禁止输出 Markdown、代码围栏、解释文字或 <think> 标签。
""";
        }
    }
}
