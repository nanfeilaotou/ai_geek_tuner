using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace AIGeekTuner.Views.Behaviors
{
    /// <summary>
    /// Re-runs WPF mouse hit-testing after DragMove's native modal loop exits.
    /// </summary>
    public static class PostDragInputSynchronizer
    {
        public static void Schedule(Dispatcher dispatcher)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            Schedule(
                (priority, callback) => dispatcher.BeginInvoke(priority, callback),
                Mouse.Synchronize);
        }

        public static void Schedule(
            Action<DispatcherPriority, Action> schedule,
            Action synchronize)
        {
            ArgumentNullException.ThrowIfNull(schedule);
            ArgumentNullException.ThrowIfNull(synchronize);
            schedule(DispatcherPriority.Input, synchronize);
        }
    }
}
