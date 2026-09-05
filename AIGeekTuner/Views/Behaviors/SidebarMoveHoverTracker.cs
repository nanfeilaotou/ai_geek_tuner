using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AIGeekTuner.Views.Behaviors;

/// <summary>
/// Tracks the navigation item under the real screen cursor while the native
/// window move loop is active. Bounds are obtained from the live WPF elements,
/// so no DPI- or layout-specific coordinates are embedded here.
/// </summary>
public sealed class SidebarMoveHoverTracker
{
    private readonly FrameworkElement _sidebarRoot;
    private DependencyObject? _current;

    public SidebarMoveHoverTracker(FrameworkElement sidebarRoot)
    {
        _sidebarRoot = sidebarRoot ?? throw new ArgumentNullException(nameof(sidebarRoot));
    }

    public DependencyObject? Current => _current;

    public void BeginTracking(Point screenPoint) => UpdateFromScreenPoint(screenPoint);

    public void UpdateFromScreenPoint(Point screenPoint)
    {
        var bounds = EnumerateNavigationItems()
            .Select(item =>
            {
                var origin = item.PointToScreen(new Point(0, 0));
                var size = item.RenderSize;
                return (Item: (DependencyObject)item, Bounds: new Rect(origin, size));
            })
            .Where(entry => entry.Bounds.Width > 0 && entry.Bounds.Height > 0)
            .ToArray();

        var next = SelectTarget(screenPoint, bounds);
        if (ReferenceEquals(_current, next))
        {
            return;
        }

        if (_current is not null)
        {
            SidebarHoverState.SetIsMoveHovered(_current, false);
        }

        if (next is not null)
        {
            SidebarHoverState.SetIsMoveHovered(next, true);
        }

        _current = next;
    }

    public void Clear()
    {
        if (_current is not null)
        {
            SidebarHoverState.SetIsMoveHovered(_current, false);
            _current = null;
        }
    }

    /// <summary>
    /// Pure coordinate selection used by the native path and behavior tests.
    /// At most one item can be selected; input order is the visual-tree order.
    /// </summary>
    public static DependencyObject? SelectTarget(
        Point screenPoint,
        IReadOnlyList<(DependencyObject Item, Rect Bounds)> items)
    {
        foreach (var item in items)
        {
            if (item.Bounds.Contains(screenPoint))
            {
                return item.Item;
            }
        }

        return null;
    }

    private IEnumerable<ButtonBase> EnumerateNavigationItems()
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(_sidebarRoot);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is ButtonBase button && button.Tag is not null)
            {
                yield return button;
            }

            for (var index = VisualTreeHelper.GetChildrenCount(current) - 1; index >= 0; index--)
            {
                stack.Push(VisualTreeHelper.GetChild(current, index));
            }
        }
    }
}
