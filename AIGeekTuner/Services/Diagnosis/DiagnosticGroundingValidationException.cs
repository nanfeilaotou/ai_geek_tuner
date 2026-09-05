namespace AIGeekTuner.Services.Diagnosis
{
    public sealed class DiagnosticGroundingValidationException : Exception
    {
        public DiagnosticGroundingValidationException(IReadOnlyList<string> errors)
            : base("诊断结果包含未被本次输入支持的事实证据。")
        {
            Errors = errors is null
                ? throw new ArgumentNullException(nameof(errors))
                : errors.ToArray();
        }

        public IReadOnlyList<string> Errors { get; }

        /// <summary>只把简短、结构化的校验信息提供给 repair，不泄漏堆栈。</summary>
        public string ToRepairMessage() =>
            string.Join("；", Errors);
    }
}
