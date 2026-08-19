namespace AIGeekTuner.Services.Settings
{
    public interface ILocalDataDirectoryService
    {
        string DirectoryPath { get; }

        void Open();
    }
}
