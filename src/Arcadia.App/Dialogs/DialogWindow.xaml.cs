using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.Dialogs;

/// <summary>Arcadia-styled modal host for any <see cref="DialogViewModel"/>.</summary>
public partial class DialogWindow : Window
{
    public DialogWindow(DialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Width = viewModel.DialogWidth;
        CancelCommand = new RelayCommand(() => Finish(false));
        viewModel.CloseRequested += (_, accepted) => Finish(accepted);
        Loaded += async (_, _) =>
        {
            MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            await viewModel.OnShownAsync();
        };
    }

    public ICommand CancelCommand { get; }

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(false);

    private void Finish(bool accepted)
    {
        if (!IsLoaded) return;
        DialogResult = accepted;
    }
}
