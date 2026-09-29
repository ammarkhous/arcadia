using System.Windows.Controls;
using System.Windows.Input;

namespace Arcadia.App.Views.Organizer;

public partial class TaskDetailsView : UserControl
{
    /// <summary>Enter on the task list moves the keyboard into the details title.</summary>
    public static Action? FocusTitleRequested;

    public TaskDetailsView()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusTitleRequested = () => Dispatcher.BeginInvoke(() => { TitleBox.Focus(); Keyboard.Focus(TitleBox); TitleBox.SelectAll(); });
    }
}
