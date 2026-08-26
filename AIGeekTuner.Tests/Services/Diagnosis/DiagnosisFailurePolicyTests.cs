using AIGeekTuner.Services.Diagnosis;

namespace AIGeekTuner.Tests.Services.Diagnosis;

public class DiagnosisFailurePolicyTests
{
    [Theory]
    [InlineData(DiagnosisError.OllamaUnavailable)]
    [InlineData(DiagnosisError.PromptGenerationFailed)]
    [InlineData(DiagnosisError.AiRequestFailed)]
    [InlineData(DiagnosisError.AiResponseInvalid)]
    [InlineData(DiagnosisError.SafetyCheckFailed)]
    public void ShouldPersist_True_ForRealFailures(DiagnosisError error)
    {
        Assert.True(DiagnosisFailurePolicy.ShouldPersistFailure(error));
    }

    [Theory]
    [InlineData(DiagnosisError.InvalidRequest)]
    [InlineData(DiagnosisError.EmptyFaultLog)]
    [InlineData(DiagnosisError.HardwareUnavailable)]
    public void ShouldPersist_False_ForValidationIssues(DiagnosisError error)
    {
        Assert.False(DiagnosisFailurePolicy.ShouldPersistFailure(error));
    }
}
