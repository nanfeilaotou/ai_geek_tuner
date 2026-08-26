using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Services.Diagnostics
{
    /// <summary>
    /// 全局兜底专用的极简异常留痕，不是通用日志框架：
    /// 只记录异常元数据（时间、来源、类型、消息、堆栈），不记录日志正文、Prompt、模型响应或任何用户内容。
    /// </summary>
    public static class ExceptionLogWriter
    {
        private const int MaxMessageLength = 2000;

        // 多个线程可能同时进入兜底路径（后台线程崩溃 + UI 提示），串行化追加避免交错写入。
        private static readonly object WriteGate = new();

        public static void Write(Exception exception, string source)
        {
            if (exception is null)
            {
                return;
            }

            try
            {
                var logDirectory = ApplicationDataPaths.Default.LogsDirectory;
                Directory.CreateDirectory(logDirectory);
                var filePath = Path.Combine(
                    logDirectory,
                    $"exceptions-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

                var root = exception.GetBaseException();
                var builder = new StringBuilder();
                builder.Append('[')
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                    .Append("] source=")
                    .Append(source)
                    .AppendLine();
                builder.Append("type=").AppendLine(root.GetType().FullName);
                builder.Append("message=")
                    .AppendLine(Limit(root.Message));
                builder.AppendLine("stack=");
                builder.AppendLine(exception.StackTrace);

                lock (WriteGate)
                {
                    File.AppendAllText(filePath, builder.ToString());
                }
            }
            catch (Exception writeFailure)
            {
                // 留痕失败只能降级为调试输出；这里再抛出会掩盖正在处理的原始异常。
                Trace.WriteLine($"ExceptionLogWriter write failed: {writeFailure.Message}");
            }
        }

        private static string Limit(string value)
        {
            var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return normalized.Length <= MaxMessageLength
                ? normalized
                : normalized[..MaxMessageLength] + "…";
        }
    }
}
