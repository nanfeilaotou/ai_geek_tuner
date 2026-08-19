namespace AIGeekTuner.Services.Dialogs
{
    public interface IConfirmationDialogService
    {
        bool Confirm(string title, string message);
    }
}
