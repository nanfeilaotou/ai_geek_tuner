using System.Windows;

namespace AIGeekTuner.Views.Behaviors;

/// <summary>
/// Inheritable state exposed only while Windows owns the native move loop.
/// Sidebar navigation styles use it to suppress transient hover visuals while
/// preserving the selected-page visual.
/// </summary>
public static class WindowMoveState
{
    public static readonly DependencyProperty IsMovingProperty =
        DependencyProperty.RegisterAttached(
            "IsMoving",
            typeof(bool),
            typeof(WindowMoveState),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsMoving(DependencyObject element) =>
        (bool)element.GetValue(IsMovingProperty);

    public static void SetIsMoving(DependencyObject element, bool value) =>
        element.SetValue(IsMovingProperty, value);
}
