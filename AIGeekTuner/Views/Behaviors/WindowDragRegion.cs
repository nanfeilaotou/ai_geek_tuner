using System.Windows;
using System.Windows.Input;

namespace AIGeekTuner.Views.Behaviors
{
    /// <summary>
    /// Enables window dragging only on explicitly marked regions.
    /// </summary>
    public static class WindowDragRegion
    {
        public static readonly DependencyProperty IsDragRegionProperty =
            DependencyProperty.RegisterAttached(
                "IsDragRegion",
                typeof(bool),
                typeof(WindowDragRegion),
                new PropertyMetadata(false, OnIsDragRegionChanged));

        public static void SetIsDragRegion(DependencyObject element, bool value) =>
            element.SetValue(IsDragRegionProperty, value);

        public static bool GetIsDragRegion(DependencyObject element) =>
            (bool)element.GetValue(IsDragRegionProperty);

        private static void OnIsDragRegionChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            if (dependencyObject is not UIElement element)
            {
                return;
            }

            if ((bool)args.NewValue)
            {
                element.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            }
            else
            {
                element.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            }
        }

        private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
        {
            var dragRegion = sender as DependencyObject;
            if (args.Handled
                || args.ChangedButton != MouseButton.Left
                || dragRegion is null
                || !WindowDragHitTest.IsDraggableFrom(
                    args.OriginalSource as DependencyObject,
                    dragRegion))
            {
                return;
            }

            var source = args.OriginalSource as DependencyObject ?? sender as DependencyObject;
            var window = source is null ? null : Window.GetWindow(source);
            if (window is null)
            {
                return;
            }

            if (args.ClickCount == 2)
            {
                WindowChromeController.ToggleMaximize(window);
                args.Handled = true;
                return;
            }

            try
            {
                window.DragMove();
                args.Handled = true;
            }
            catch (InvalidOperationException)
            {
                // A synthetic/unit-test mouse event may not have an active HWND.
                // Native WPF input remains deterministic for a real window.
            }
            finally
            {
                Mouse.Capture(null);
                PostDragInputSynchronizer.Schedule(window.Dispatcher);
            }
        }
    }
}
