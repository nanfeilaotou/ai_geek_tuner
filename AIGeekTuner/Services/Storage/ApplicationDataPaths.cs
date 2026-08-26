using System.Diagnostics;
using System.IO;

namespace AIGeekTuner.Services.Storage
{
    public sealed class ApplicationDataPaths
    {
        public ApplicationDataPaths(
            string? rootDirectory = null,
            string? legacyRootDirectory = null)
        {
            // QA 钩子：设置 AIGEEKTUNER_DATA_DIR 可把全部数据重定向到隔离目录；
            // 未设置时行为与过去完全一致。发布用户不需要也不应该设置它。
            var overrideRoot = ResolveOverrideRoot();

            RootDirectory = Path.GetFullPath(
                rootDirectory
                ?? overrideRoot
                ?? Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "AI-GeekTuner"));
            HistoryDirectory = Path.Combine(RootDirectory, "History");
            ReportsDirectory = Path.Combine(RootDirectory, "Reports");
            SettingsDirectory = Path.Combine(RootDirectory, "Settings");
            SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");
            LogsDirectory = Path.Combine(RootDirectory, "Logs");

            var effectiveLegacyRoot = legacyRootDirectory
                ?? (overrideRoot is null
                    ? null
                    : Path.Combine(RootDirectory, "legacy"));
            LegacyRootDirectory = Path.GetFullPath(
                effectiveLegacyRoot ?? Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ApplicationData),
                    "AI-GeekTuner"));

            LegacyHistoryDirectory = Path.Combine(
                LegacyRootDirectory,
                "Reports");
            LegacySettingsFilePath = Path.Combine(
                LegacyRootDirectory,
                "settings.json");
        }

        private static string? ResolveOverrideRoot()
        {
            var raw = Environment.GetEnvironmentVariable("AIGEEKTUNER_DATA_DIR");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(raw);
            }
            catch (Exception exception)
            {
                // 非法路径按未设置处理：QA 钩子不允许把启动过程变成崩溃点。
                Trace.WriteLine(
                    $"AIGEEKTUNER_DATA_DIR invalid, ignored: {exception.Message}");
                return null;
            }
        }

        public string RootDirectory { get; }

        public string HistoryDirectory { get; }

        public string ReportsDirectory { get; }

        public string SettingsDirectory { get; }

        public string SettingsFilePath { get; }

        public string LogsDirectory { get; }

        public string LegacyRootDirectory { get; }

        public string LegacyHistoryDirectory { get; }

        public string LegacySettingsFilePath { get; }

        public void EnsureDirectories()
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(HistoryDirectory);
            Directory.CreateDirectory(ReportsDirectory);
            Directory.CreateDirectory(SettingsDirectory);
            Directory.CreateDirectory(LogsDirectory);
        }

        public static ApplicationDataPaths Default { get; } = new();
    }
}
