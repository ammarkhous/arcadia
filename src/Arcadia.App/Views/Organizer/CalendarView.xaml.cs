using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Arcadia.App.ViewModels.Organizer;

namespace Arcadia.App.Views.Organizer;

public partial class CalendarView : UserControl
{
    public CalendarView()
    {
        InitializeComponent();
        DayView.IsSingleDay = true;
        PreviewKeyDown += OnKey;
    }

    /// <summary>
    /// Planner keys when no text field has the focus: T/M/W/D/A switch views, N creates an event,
    /// ←/→ move by the view's step (PageUp/PageDown and Alt+Home are key bindings).
    /// </summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (DataContext is not CalendarViewModel vm || Keyboard.Modifiers != ModifierKeys.None) return;
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or PasswordBox) return;
        switch (e.Key)
        {
            case Key.T: vm.View = ViewModels.Organizer.CalendarView.Today; break;
            case Key.M: vm.View = ViewModels.Organizer.CalendarView.Month; break;
            case Key.W: vm.View = ViewModels.Organizer.CalendarView.Week; break;
            case Key.D: vm.View = ViewModels.Organizer.CalendarView.Day; break;
            case Key.A: vm.View = ViewModels.Organizer.CalendarView.Agenda; break;
            case Key.N: vm.NewEventCommand.Execute(null); break;
            case Key.Left when vm.View is not ViewModels.Organizer.CalendarView.Agenda: vm.PreviousCommand.Execute(null); break;
            case Key.Right when vm.View is not ViewModels.Organizer.CalendarView.Agenda: vm.NextCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }
}
