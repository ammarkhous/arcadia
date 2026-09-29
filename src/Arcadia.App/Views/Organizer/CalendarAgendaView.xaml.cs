using System.Windows.Controls;
using System.Windows.Input;
using Arcadia.App.ViewModels.Organizer;

namespace Arcadia.App.Views.Organizer;

/// <summary>The agenda list: ↑/↓ move between entries (day headings are skipped), Enter opens, Space toggles a task, Delete removes an event.</summary>
public partial class CalendarAgendaView : UserControl
{
    public CalendarAgendaView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (List.SelectedItem == null) List.SelectedItem = List.Items.OfType<CalendarEntry>().FirstOrDefault();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    private CalendarViewModel? Vm => DataContext as CalendarViewModel;

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (List.SelectedItem is not CalendarEntry entry || Vm is not { } vm) return;
        switch (e.Key)
        {
            case Key.Enter: vm.OpenEntryCommand.Execute(entry); break;
            case Key.Space when entry.IsTask: vm.ToggleTaskCommand.Execute(entry); break;
            case Key.Delete when entry.IsEvent: vm.DeleteEntryCommand.Execute(entry); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is CalendarEntry entry) Vm?.OpenEntryCommand.Execute(entry);
    }
}
