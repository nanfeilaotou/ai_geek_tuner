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
                if (current is ICommandSource
                    || current is ButtonBase
                    || current is TextBoxBase
                    || current is PasswordBox
                    || current is ComboBox
                    || current is ListBox
                    || current is ListBoxItem
                    || current is DataGrid
                    || current is DataGridRow
                    || current is DataGridCell
                    || current is ScrollBar
                    || current is Slider
                    || current is Hyperlink
                    || current is Menu
                    || current is MenuItem
                    || current is CheckBox
                    || current is RadioButton
                    || current is Expander
                    || current is (UIElement { Focusable: true }))
                {
                    return true;
                }
            }

            return false;
        }

        private static DependencyObject? GetParent(DependencyObject current) =>
            current switch
            {
                FrameworkContentElement contentElement => contentElement.Parent,
                Visual visual => VisualTreeHelper.GetParent(visual),
                _ => null
            };
    }
}
