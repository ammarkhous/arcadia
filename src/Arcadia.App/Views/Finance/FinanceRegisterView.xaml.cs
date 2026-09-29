using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arcadia.App.ViewModels.Finance;

namespace Arcadia.App.Views.Finance;

public partial class FinanceRegisterView : UserControl
{
    public FinanceRegisterView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is FinanceRegisterViewModel old) old.SelectRequested -= OnSelectRequested;
            if (e.NewValue is FinanceRegisterViewModel vm) vm.SelectRequested += OnSelectRequested;
        };
    }

    private void OnSelectRequested(object? sender, EventArgs e)
    {
        if (List.SelectedItem != null) List.ScrollIntoView(List.SelectedItem);
    }

    /// <summary>Enter edits, Delete removes, Ctrl+D duplicates the selected transaction.</summary>
    private void OnListKey(object sender, KeyEventArgs e)
    {
        if (DataContext is not FinanceRegisterViewModel vm || List.SelectedItem is not TransactionItem item) return;
        switch (e.Key)
        {
            case Key.Enter: vm.EditCommand.Execute(item); e.Handled = true; break;
            case Key.Delete: vm.DeleteCommand.Execute(item); e.Handled = true; break;
            case Key.D when Keyboard.Modifiers == ModifierKeys.Control: vm.DuplicateCommand.Execute(item); e.Handled = true; break;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FinanceRegisterViewModel vm && (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>()?.DataContext is TransactionItem item)
            vm.EditCommand.Execute(item);
    }
}

internal static class TreeExtensions
{
    public static T? FindAncestor<T>(this DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
