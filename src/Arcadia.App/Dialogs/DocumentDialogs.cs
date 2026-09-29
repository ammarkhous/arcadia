using Arcadia.Core.Documents;
using Arcadia.Core.Localization;
using Arcadia.Core.Projects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.Dialogs;

public enum AddFolderMode { Reference, Index, Watch }

/// <summary>How to add a folder: one reference, its documents indexed once, or indexed and watched.</summary>
public sealed partial class AddFolderViewModel : DialogViewModel
{
    public AddFolderViewModel(ILocalizer loc, string folder)
    {
        Title = loc["Doc_AddFolderTitle"];
        Folder = folder;
        FolderName = PathRules.DisplayName(folder);
    }

    public override string Icon => "Documents";
    public override double DialogWidth => 540;
    public string Folder { get; }
    public string FolderName { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsReference), nameof(IsIndex), nameof(IsWatch))] private AddFolderMode _mode = AddFolderMode.Index;
    [ObservableProperty] private bool _includeSubfolders = true;

    public bool IsReference { get => Mode == AddFolderMode.Reference; set { if (value) Mode = AddFolderMode.Reference; } }
    public bool IsIndex { get => Mode == AddFolderMode.Index; set { if (value) Mode = AddFolderMode.Index; } }
    public bool IsWatch { get => Mode == AddFolderMode.Watch; set { if (value) Mode = AddFolderMode.Watch; } }

    [RelayCommand] private void Confirm() => Close(true);
    [RelayCommand] private void Cancel() => Close(false);
}

/// <summary>Choose the project to associate documents with.</summary>
public sealed partial class ProjectPickerViewModel : DialogViewModel
{
    private readonly IReadOnlyList<ProjectSummary> _all;

    public ProjectPickerViewModel(ILocalizer loc, IReadOnlyList<ProjectSummary> projects)
    {
        Title = loc["Doc_AssociateTitle"];
        _all = projects.OrderBy(p => p.Status == ProjectStatus.Archived).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _projects = _all;
        Empty = _all.Count == 0;
    }

    public override string Icon => "Projects";
    public override double DialogWidth => 460;
    public bool Empty { get; }

    [ObservableProperty] private IReadOnlyList<ProjectSummary> _projects;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))] private ProjectSummary? _selected;
    [ObservableProperty] private string _filter = string.Empty;

    partial void OnFilterChanged(string value) =>
        Projects = string.IsNullOrWhiteSpace(value) ? _all : _all.Where(p => p.Name.Contains(value.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();

    private bool CanConfirm() => Selected != null;

    [RelayCommand(CanExecute = nameof(CanConfirm))] private void Confirm() => Close(true);
    [RelayCommand] private void Cancel() => Close(false);
}
