namespace AIGeekTuner.Commands
{
    /// <summary>
    /// 异步命令中未被委托处理的异常统一出口。
    /// 命令层不做任何 UI 决策，展示方式由 App 在启动时订阅决定。
    /// </summary>
    public static class CommandErrorReporter
    {
        public static event EventHandler<Exception>? ErrorOccurred;

        public static void Report(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            ErrorOccurred?.Invoke(null, exception);
        }
    }
}
