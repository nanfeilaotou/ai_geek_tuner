using System.Diagnostics;
using System.Windows.Input;

namespace AIGeekTuner.Commands
{
    public sealed class AsyncRelayCommand<T> : ICommand
        where T : notnull
    {
        private readonly Func<T, Task> _execute;
        private readonly Func<T, bool>? _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(
            Func<T, Task> execute,
            Func<T, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) =>
            !_isExecuting &&
            parameter is T value &&
            (_canExecute?.Invoke(value) ?? true);

        public async void Execute(object? parameter)
        {
            if (parameter is T value)
            {
                await ExecuteAsync(value);
            }
        }

        public async Task ExecuteAsync(T parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            _isExecuting = true;
            NotifyCanExecuteChanged();
            try
            {
                await _execute(parameter);
            }
            catch (OperationCanceledException)
            {
                // 取消是命令的正常结束路径，界面状态由各 ViewModel 自行恢复；
                // 这里只阻止它以未处理异常的形式逃逸到调度器。
                Debug.WriteLine("AsyncRelayCommand<T>: operation cancelled.");
            }
            catch (Exception exception)
            {
                // 委托内部已处理的业务异常不会到达这里；
                // 能到达这里的只有真正未预期的异常，交给统一错误通道。
                CommandErrorReporter.Report(exception);
            }
            finally
            {
                _isExecuting = false;
                NotifyCanExecuteChanged();
            }
        }

        public void NotifyCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
