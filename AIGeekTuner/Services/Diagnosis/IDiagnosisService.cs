using AIGeekTuner.Configuration;
using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Diagnosis
{
    public interface IDiagnosisService
    {
        /// <summary>
        /// 执行一次诊断。<paramref name="configuration"/> 是调用方在开始时取得的
        /// 配置快照；整个诊断过程（prompt、readiness、模型请求）只使用这一份，
        /// 期间设置被保存为新版本也不影响本次。
        /// </summary>
        Task<DiagnosisOutcome> DiagnoseAsync(
            DiagnosticRequest request,
            DiagnosticConfiguration configuration,
            CancellationToken cancellationToken = default);
    }
}
