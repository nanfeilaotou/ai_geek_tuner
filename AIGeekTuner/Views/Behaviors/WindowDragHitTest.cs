using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace AIGeekTuner.Views.Behaviors
{
    /// <summary>
    /// Deterministic hit-test policy for explicit window drag regions.
    /// Interactive descendants always win over the drag gesture.
    /// </summary>
    public static class WindowDragHitTest
    {
        public static bool IsInteractive(DependencyObject? source)
        {
            for (var current = source; current is not null; current = GetParent(current))
            {
                if (IsInteractiveElement(current))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns true only when the hit is inside <paramref name="dragRoot"/> and
        /// no interactive element owns the hit. Layout containers and ScrollViewer
        /// are deliberately transparent to dragging.
        /// </summary>
        public static bool IsDraggableFrom(
            DependencyObject? originalSource,
            DependencyObject dragRoot)
        {
            ArgumentNullException.ThrowIfNull(dragRoot);

            for (var current = originalSource; current is not null; current = GetParent(current))
            {
                if (IsInteractiveElement(current))
                {
                    return false;
                }

                if (ReferenceEquals(current, dragRoot))
                {
                    return true;
                }
            }

            // Once the window is hosted by an HWND, Window.GetWindow is the
            // reliable ownership boundary even when the visual tree ends at a
            // Page/Frame content root.
            if (dragRoot is Window rootWindow
                && originalSource is not null
                && ReferenceEquals(Window.GetWindow(originalSource), rootWindow))
            {
                return true;
            }

            return false;
        }

        private static bool IsInteractiveElement(DependencyObject current) =>
            current is ICommandSource
            || current is ButtonBase
            || current is ToggleButton
            || current is TextBoxBase
            || current is PasswordBox
            || current is Selector
            || current is ListBoxItem
            || current is ListViewItem
            || current is TreeViewItem
            || current is DataGrid
            || current is DataGridRow
            || current is DataGridCell
            || current is DataGridColumnHeader
            || current is ScrollBar
            || current is Thumb
            || current is Slider
            || current is Hyperlink
            || current is Menu
            || current is MenuItem
            || current is CheckBox
            || current is RadioButton
            || current is Expander
            || current is TabItem
            || IsExplicitlyFocusable(current);

        private static bool IsExplicitlyFocusable(DependencyObject current) =>
            current is UIElement element
            && element.Focusable
            && current is not ScrollViewer
            && current is not Frame
            && current is not Page
            && current is not ContentPresenter
            && DependencyPropertyHelper.GetValueSource(
                element,
                UIElement.FocusableProperty).BaseValueSource != BaseValueSource.Default;

        private static DependencyObject? GetParent(DependencyObject current)
        {
            if (current is FrameworkContentElement contentElement && contentElement.Parent is not null)
            {
                return contentElement.Parent;
            }

            if (current is Visual visual)
            {
                var visualParent = VisualTreeHelper.GetParent(visual);
                if (visualParent is not null)
                {
                    return visualParent;
                }
            }

            // LogicalTreeHelper covers detached test trees and content presenters
            // whose visual parent is materialized only after layout.
            return LogicalTreeHelper.GetParent(current);
        }
    }
}
