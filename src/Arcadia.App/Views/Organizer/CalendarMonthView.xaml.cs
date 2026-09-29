using System.Windows.Controls;
using System.Windows.Input;
using Arcadia.App.ViewModels.Organizer;

namespace Arcadia.App.Views.Organizer;

public partial class CalendarMonthView : UserControl
{
    public CalendarMonthView() => InitializeComponent();

    private CalendarViewModel? Vm => DataContext as CalendarViewModel;

    /// <summary>Double-click a day: a new event on it.</summary>
    private void OnDayDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { DataContext: MonthDay day } && Vm is { } vm) vm.NewEventOnDayCommand.Execute(day);
        e.Handled = true;
    }

    private void OnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.FrameworkElement { DataContext: CalendarEntry entry } && Vm is { } vm) vm.OpenEntryCommand.Execute(entry);
    }

    /// <summary>Enter opens the selected entry, Delete removes an event.</summary>
    private void OnEntryKey(object sender, KeyEventArgs e)
    {
        if (Vm is not { SelectedEntry: { } entry } vm) return;
        if (e.Key == Key.Enter) { vm.OpenEntryCommand.Execute(entry); e.Handled = true; }
        else if (e.Key == Key.Delete && entry.IsEvent) { vm.DeleteEntryCommand.Execute(entry); e.Handled = true; }
    }
}
