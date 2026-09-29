using System.Windows;
using System.Windows.Controls;
using Arcadia.App.ViewModels.ItTools;

namespace Arcadia.App.Views.ItTools.Tools;

/// <summary>Accepts a file dropped anywhere on the tool.</summary>
public partial class HashView : UserControl
{
    public HashView()
    {
        InitializeComponent();
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && DataContext is HashToolViewModel vm && vm.IsIdle)
                vm.SetFile(files[0]);
        };
    }
}
