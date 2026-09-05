namespace AIGeekTuner.Services.Diagnosis
{
    public enum DiagnosisError
    {
        InvalidRequest,
        EmptyFaultLog,
        HardwareUnavailable,

        /// <summary>旧值：legacy Ollama 专用。持久化的旧历史 FailureCode 仍可能包含它。</summary>
        OllamaUnavailable,

        /// <summary>V2-M5.1B：provider 中立的“当前无可用 AI 服务提供方”。</summary>
        AiUnavailable,

        PromptGenerationFailed,
        AiRequestFailed,
        AiResponseInvalid,
        SafetyCheckFailed
    }
}
