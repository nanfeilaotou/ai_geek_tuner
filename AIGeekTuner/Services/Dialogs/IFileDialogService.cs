namespace AIGeekTuner.Services.Dialogs;

public interface IFileDialogService
{
    string? PickSaveFile(string title, string filter, string defaultFileName);

    string? PickOpenFile(string title, string filter);
}
