using System.Windows;
using System.Windows.Controls;

namespace Arcadia.UI.Controls;

/// <summary>
/// An option chip (radio button) that runs its Command whenever it becomes checked — by mouse, keyboard or
/// UI Automation (screen readers select options without "clicking" them). Commands must be idempotent.
/// </summary>
public class ArcadiaChoice : RadioButton
{
    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        if (IsLoaded && Command?.CanExecute(CommandParameter) == true) Command.Execute(CommandParameter);
    }

    /// <summary>A click on an already-checked option does nothing (instead of re-running the command).</summary>
    protected override void OnClick()
    {
        if (IsChecked != true) base.OnClick();
    }
}
