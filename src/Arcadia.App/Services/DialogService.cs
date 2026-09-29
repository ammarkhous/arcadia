using System.Windows;
using Arcadia.App.Dialogs;
using Arcadia.Core.Localization;
using Arcadia.Core.Media;
using Microsoft.Win32;

namespace Arcadia.App.Services;

/// <summary>Modal dialogs and file pickers, kept behind an interface so view models stay testable.</summary>
public interface IDialogService
{
    bool Show(DialogViewModel viewModel);
    bool Confirm(string message, string confirmText);
    IReadOnlyList<string> PickImages(bool multiple);
    string? PickProgram();
    string? PickFolder(string? initial = null);
    /// <summary>Returns the chosen path and the 1-based index of the chosen filter, or null.</summary>
    (string Path, int FilterIndex)? PickSaveFile(string suggestedName, string filter);
    string? PickFile(string filter);
    /// <summary>Any files (native dialog); optionally starting in a folder.</summary>
    IReadOnlyList<string> PickFiles(string filter, bool multiple, string? initialFolder = null);
}

public sealed class DialogService(ILocalizer loc) : IDialogService
{
    public bool Show(DialogViewModel viewModel)
    {
        var window = new DialogWindow(viewModel);
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        if (owner != null && !ReferenceEquals(owner, window) && owner.IsVisible) window.Owner = owner;
        return window.ShowDialog() == true;
    }

    public bool Confirm(string message, string confirmText) =>
        Show(new ConfirmDialogViewModel(loc["Confirm_Title"], message, confirmText, loc["Editor_Cancel"]));

    public IReadOnlyList<string> PickImages(bool multiple)
    {
        var patterns = string.Join(";", IMediaStore.SupportedExtensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Multiselect = multiple,
            Filter = $"Images ({patterns})|{patterns}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public string? PickFolder(string? initial = null)
    {
        var dialog = new OpenFolderDialog();
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public (string Path, int FilterIndex)? PickSaveFile(string suggestedName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            FileName = suggestedName,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog() == true ? (dialog.FileName, dialog.FilterIndex) : null;
    }

    public IReadOnlyList<string> PickFiles(string filter, bool multiple, string? initialFolder = null)
    {
        var dialog = new OpenFileDialog { Filter = filter, Multiselect = multiple, DereferenceLinks = true };
        if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder)) dialog.InitialDirectory = initialFolder;
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public string? PickFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, DereferenceLinks = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickProgram()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Programs and shortcuts (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url",
            DereferenceLinks = false,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
