using Arcadia.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.Dialogs;

/// <summary>Start or rename a diagnostic session: a title (required) and an optional ticket/customer reference.</summary>
public sealed partial class DiagnosticSessionEditorViewModel : DialogViewModel
{
    private readonly ILocalizer _loc;

    public DiagnosticSessionEditorViewModel(ILocalizer loc, string? title, string? reference)
    {
        _loc = loc;
        IsNew = title == null;
        Title = loc[IsNew ? "Diag_NewTitle" : "Diag_EditTitle"];
        _sessionTitle = title ?? loc.Format("Diag_DefaultTitle", DateTime.Now.ToString("g", loc.Culture));
        _reference = reference ?? string.Empty;
    }

    public override string Icon => "Diagnostic";
    public override double DialogWidth => 500;
    public bool IsNew { get; }
    public string ConfirmText => _loc[IsNew ? "Diag_Start" : "Editor_Save"];

    [ObservableProperty] private string _sessionTitle;
    [ObservableProperty] private string _reference;
    [ObservableProperty] private string? _titleError;

    [RelayCommand]
    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(SessionTitle)) { TitleError = _loc["Diag_ErrTitle"]; return; }
        if (SessionTitle.Trim().Length > 200) { TitleError = _loc["Diag_ErrTitleLength"]; return; }
        Reference = Reference.Trim();
        Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}
