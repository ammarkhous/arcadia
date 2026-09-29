using System.Windows.Controls;

namespace Arcadia.App.Dialogs;

public partial class GameEditorView : UserControl
{
    public GameEditorView() => InitializeComponent();

    /// <summary>"Detect running": the process list is read only when the drop-down opens.</summary>
    private void OnDetectRunningOpened(object? sender, EventArgs e)
    {
        if (DataContext is GameEditorViewModel vm) vm.DetectRunningCommand.Execute(null);
    }
}
