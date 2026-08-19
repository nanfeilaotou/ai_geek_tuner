using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Services.Settings
{
    public sealed class LocalDataDirectoryService : ILocalDataDirectoryService
    {
        private readonly ApplicationDataPaths _paths;

        public LocalDataDirectoryService(string? directoryPath = null)
        {
            _paths = directoryPath is null
                ? ApplicationDataPaths.Default
                : new ApplicationDataPaths(directoryPath);
            DirectoryPath = _paths.RootDirectory;
        }

        public LocalDataDirectoryService(ApplicationDataPaths paths)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            DirectoryPath = _paths.RootDirectory;
        }

        public string DirectoryPath { get; }

        public void Open()
        {
            try
            {
                _paths.EnsureDirectories();
                Process.Start(new ProcessStartInfo
                {
                    FileName = DirectoryPath,
                    UseShellExecute = true
                });
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or SecurityException
                    or Win32Exception
                    or InvalidOperationException
                    or NotSupportedException)
            {
                throw new LocalDataDirectoryException(
                    "无法打开本地数据目录，请检查目录访问权限。",
                    exception);
            }
        }

    }
}
