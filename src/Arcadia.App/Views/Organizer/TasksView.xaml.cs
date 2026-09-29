using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arcadia.App.ViewModels.Organizer;

namespace Arcadia.App.Views.Organizer;

/// <summary>Keys on the list: Space toggles done, Delete deletes, Enter edits (details title). Ctrl+N focuses quick add.</summary>
public partial class TasksView : UserControl
{
    private TasksViewModel? _vm;

    public TasksView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.NewTaskRequested -= OnNewTask;
            _vm = DataContext as TasksViewModel;
            if (_vm != null) _vm.NewTaskRequested += OnNewTask;
        };
    }

    private void OnNewTask(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { QuickBox.Focus(); Keyboard.Focus(QuickBox); }, System.Windows.Threading.DispatcherPriority.Loaded);

    private void OnListKey(object sender, KeyEventArgs e)
    {
        if (_vm?.Selected is not TaskListItem item) return;
        switch (e.Key)
        {
            case Key.Space: _vm.ToggleDoneCommand.Execute(item); break;
            case Key.Delete: _vm.DeleteCommand.Execute(item); break;
            case Key.Enter: TaskDetailsView.FocusTitleRequested?.Invoke(); break;
            default: return;
        }
        e.Handled = true;
    }
}
