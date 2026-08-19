namespace AIGeekTuner.Services.Diagnosis
{
    public enum DiagnosisError
    {
        InvalidRequest,
        EmptyFaultLog,
        HardwareUnavailable,
        OllamaUnavailable,
        PromptGenerationFailed,
        AiRequestFailed,
        AiResponseInvalid,
        SafetyCheckFailed
    }
}
