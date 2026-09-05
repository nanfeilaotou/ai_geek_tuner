using System.Windows;

namespace AIGeekTuner.Views.Behaviors;

/// <summary>
/// Explicit navigation-item hover state used while Windows owns the native
/// move loop. It is deliberately separate from WPF's IsMouseOver, which can
/// remain rooted at the drag-start element during that loop.
/// </summary>
public static class SidebarHoverState
{
    public static readonly DependencyProperty IsMoveHoveredProperty =
        DependencyProperty.RegisterAttached(
            "IsMoveHovered",
            typeof(bool),
            typeof(SidebarHoverState),
            new FrameworkPropertyMetadata(false));

    public static bool GetIsMoveHovered(DependencyObject element) =>
        (bool)element.GetValue(IsMoveHoveredProperty);

    public static void SetIsMoveHovered(DependencyObject element, bool value) =>
        element.SetValue(IsMoveHoveredProperty, value);

    /// <summary>
    /// Shared truth table for the two visual states. During the native move
    /// loop the explicit screen-coordinate target completely replaces the
    /// potentially stale WPF IsMouseOver value.
    /// </summary>
    public static bool IsHoverActive(bool isMoving, bool isMouseOver, bool isMoveHovered) =>
        isMoving ? isMoveHovered : isMouseOver;
}
