using System.Windows;
using System.Windows.Controls;
using Arcadia.App.ViewModels.Automation;

namespace Arcadia.App.Views.Automation;

public partial class AutomationView : UserControl
{
    public AutomationView() => InitializeComponent();

    /// <summary>"Detect running process": the list is read when it is opened, not continuously.</summary>
    private void OnDetectOpened(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FieldViewModel field }) field.DetectCommand.Execute(null);
    }
}
