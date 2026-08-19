using Microsoft.Win32;

namespace AIGeekTuner.Services.Dialogs
{
    public sealed class OpenFileDialogService : IFilePickerService
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
    }
}
