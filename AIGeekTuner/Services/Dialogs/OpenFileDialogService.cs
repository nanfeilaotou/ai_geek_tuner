using Microsoft.Win32;

namespace AIGeekTuner.Services.Dialogs
{
    public sealed class OpenFileDialogService : IFilePickerService, IFileDialogService
    {
        public string? PickFaultLogFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择故障日志",
                Filter = "故障日志 (*.txt;*.log)|*.txt;*.log|文本文件 (*.txt)|*.txt|日志文件 (*.log)|*.log",
                CheckFileExists = true,
                Multiselect = false
            };

            return dialog.ShowDialog() == true
                ? dialog.FileName
                : null;
        }

        public string? PickSaveFile(string title, string filter, string defaultFileName)
        {
            var dialog = new SaveFileDialog
            {
                Title = title,
                Filter = filter,
                FileName = defaultFileName,
                AddExtension = true,
                OverwritePrompt = true,
                CheckPathExists = true
            };

            return dialog.ShowDialog() == true
                ? dialog.FileName
                : null;
        }

        public string? PickOpenFile(string title, string filter)
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true,
                Multiselect = false
            };

            return dialog.ShowDialog() == true
                ? dialog.FileName
                : null;
        }
    }
}
