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
