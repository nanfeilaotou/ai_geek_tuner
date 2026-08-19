using System.Windows;

namespace AIGeekTuner.Services.Dialogs
{
    public sealed class MessageBoxConfirmationDialogService
        : IConfirmationDialogService
    {
        public bool Confirm(string title, string message) =>
            MessageBox.Show(
                message,
                title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
