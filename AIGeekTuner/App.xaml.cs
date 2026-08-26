using System.Windows;
using System.Windows.Threading;
using AIGeekTuner.Commands;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner
{
    public partial class App : Application
    {
        // MessageBox 会泵送 Dispatcher 消息，处理期间可能再次派发异常；
        // 该标记防止错误提示无限递归叠加。
        private bool _isShowingUnhandledErrorDialog;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            CommandErrorReporter.ErrorOccurred += OnCommandErrorOccurred;
        }

        // UI 线程意外异常：先留痕，再提示用户并标记已处理，避免单个控件级错误直接闪退；
        // 只有进程状态已不可信的异常（OutOfMemoryException）才交还默认终止流程。
        private void OnDispatcherUnhandledException(
            object sender,
            DispatcherUnhandledExceptionEventArgs e)
        {
            ExceptionLogWriter.Write(e.Exception, "WPF Dispatcher");

            if (e.Exception is OutOfMemoryException)
            {
                return;
            }

            if (_isShowingUnhandledErrorDialog)
            {
                // 已有提示在展示：只留痕不再叠加弹窗，但同样阻止闪退。
                e.Handled = true;
                return;
            }

            _isShowingUnhandledErrorDialog = true;
            try
            {
                MessageBox.Show(
                    $"发生未预期的错误：{e.Exception.Message}\n\n详细信息已写入本地数据目录的 Logs 文件夹。",
                    "AI-GeekTuner",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                e.Handled = true;
            }
            finally
            {
                _isShowingUnhandledErrorDialog = false;
            }
        }

        // 后台线程 / 终结器线程的致命异常：进程必然终止，无法恢复；
        // 这里只做最后的诊断留痕，不做任何“假装可以继续”的处理。
        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var source = $"AppDomain (IsTerminating={e.IsTerminating})";
            if (e.ExceptionObject is Exception exception)
            {
                ExceptionLogWriter.Write(exception, source);
            }
            else
            {
                ExceptionLogWriter.Write(
                    new InvalidOperationException($"Non-exception failure: {e.ExceptionObject}"),
                    source);
            }
        }

        // .NET 默认策略本就不会因未观察任务异常终止进程；
        // 显式 SetObserved 表达同一语义，同时把被丢弃的异常留痕便于排查。
        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            ExceptionLogWriter.Write(e.Exception, "Unobserved Task");
            e.SetObserved();
        }

        private void OnCommandErrorOccurred(object? sender, Exception exception)
        {
            ExceptionLogWriter.Write(exception, "UI Command");

            if (_isShowingUnhandledErrorDialog)
            {
                return;
            }

            _isShowingUnhandledErrorDialog = true;
            try
            {
                MessageBox.Show(
                    $"操作未能完成：{exception.Message}\n\n详细信息已写入本地数据目录的 Logs 文件夹。",
                    "AI-GeekTuner",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                _isShowingUnhandledErrorDialog = false;
            }
        }
    }
}
