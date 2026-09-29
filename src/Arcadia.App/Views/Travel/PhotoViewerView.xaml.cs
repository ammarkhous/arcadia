using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arcadia.App.ViewModels.Travel;

namespace Arcadia.App.Views.Travel;

public partial class PhotoViewerView : UserControl
{
    public PhotoViewerView() => InitializeComponent();

    private PhotoViewerViewModel? Vm => DataContext as PhotoViewerViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e) => Focus();

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) { if (e.Key == Key.Escape) Focus(); return; }
        switch (e.Key)
        {
            case Key.Right: Vm?.NextCommand.Execute(null); break;
            case Key.Left: Vm?.PreviousCommand.Execute(null); break;
            case Key.Escape: Vm?.CloseCommand.Execute(null); break;
            case Key.OemPlus or Key.Add: Vm?.ZoomInCommand.Execute(null); break;
            case Key.OemMinus or Key.Subtract: Vm?.ZoomOutCommand.Execute(null); break;
            case Key.D0 or Key.NumPad0: Vm?.FitCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm == null) return;
        if (e.Delta > 0) Vm.ZoomInCommand.Execute(null); else Vm.ZoomOutCommand.Execute(null);
        e.Handled = true;
    }

    private void OnCaptionLostFocus(object sender, RoutedEventArgs e) => Vm?.SaveCaptionCommand.Execute(null);
}
