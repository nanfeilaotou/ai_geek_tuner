namespace AIGeekTuner.Services.Diagnosis
{
    /// <summary>
    /// 失败历史落库分类：只有真正启动诊断之后的故障才留痕；
    /// 输入校验类问题与用户主动取消不属于系统失败。
    /// </summary>
    public static class DiagnosisFailurePolicy
    {
        public static bool ShouldPersistFailure(DiagnosisError error)
        {
            return error is not (
                DiagnosisError.InvalidRequest or
                DiagnosisError.EmptyFaultLog or
                DiagnosisError.HardwareUnavailable);
        }
    }
}
