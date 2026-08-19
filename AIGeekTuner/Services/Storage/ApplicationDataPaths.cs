using System.IO;

namespace AIGeekTuner.Services.Storage
{
    public sealed class ApplicationDataPaths
    {
        public ApplicationDataPaths(
            string? rootDirectory = null,
            string? legacyRootDirectory = null)
        {
            RootDirectory = Path.GetFullPath(
                rootDirectory ?? Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "AI-GeekTuner"));
            HistoryDirectory = Path.Combine(RootDirectory, "History");
            ReportsDirectory = Path.Combine(RootDirectory, "Reports");
            SettingsDirectory = Path.Combine(RootDirectory, "Settings");
            SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

            LegacyRootDirectory = Path.GetFullPath(
                legacyRootDirectory ?? Path.Combine(
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

        public string RootDirectory { get; }

        public string HistoryDirectory { get; }

        public string ReportsDirectory { get; }

        public string SettingsDirectory { get; }

        public string SettingsFilePath { get; }

        public string LegacyRootDirectory { get; }

        public string LegacyHistoryDirectory { get; }

        public string LegacySettingsFilePath { get; }

        public void EnsureDirectories()
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(HistoryDirectory);
            Directory.CreateDirectory(ReportsDirectory);
            Directory.CreateDirectory(SettingsDirectory);
        }

        public static ApplicationDataPaths Default { get; } = new();
    }
}
