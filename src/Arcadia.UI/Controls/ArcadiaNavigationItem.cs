using System.Windows;
using System.Windows.Controls;

namespace Arcadia.UI.Controls;

/// <summary>
/// Sidebar console key. A RadioButton so that selection is exclusive and keyboard navigation works natively.
/// </summary>
public class ArcadiaNavigationItem : RadioButton
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(ArcadiaNavigationItem));
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(nameof(Badge), typeof(int), typeof(ArcadiaNavigationItem), new PropertyMetadata(0));
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(ArcadiaNavigationItem), new FrameworkPropertyMetadata(false));

    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public int Badge { get => (int)GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }
    /// <summary>Icon-only presentation used when the window is narrow.</summary>
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    static ArcadiaNavigationItem()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ArcadiaNavigationItem), new FrameworkPropertyMetadata(typeof(ArcadiaNavigationItem)));
    }

    /// <summary>
    /// Screen readers and UI Automation select an item without clicking it; run the command then too.
    /// (Navigation commands are idempotent, so a mouse click executing it twice is harmless.)
    /// </summary>
    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        if (IsLoaded && Command?.CanExecute(CommandParameter) == true) Command.Execute(CommandParameter);
    }
}
