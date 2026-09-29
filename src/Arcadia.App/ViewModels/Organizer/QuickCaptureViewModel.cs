using Arcadia.App.Services;
using Arcadia.Core.Organizer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.ViewModels.Organizer;

/// <summary>
/// Ctrl+Shift+N from anywhere: one line becomes a task, a note or an event without leaving the page. Dates are read
/// by <see cref="QuickDateParser"/> and previewed before saving. When the current page is about a project, trip or game,
/// the capture can be attached to it (on by default, one click to detach).
/// </summary>
public sealed partial class QuickCaptureViewModel(IQuickCaptureService capture, OrganizerContext ctx, Finance.FinanceActions finance, Arcadia.Core.Finance.IQuickExpenseService expenses) : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private CaptureKind _kind = CaptureKind.Task;
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private string? _hint;
    [ObservableProperty] private bool _hintIsWarning;
    [ObservableProperty] private ContextChip? _context;
    [ObservableProperty] private bool _attach = true;
    [ObservableProperty] private bool _isSaving;

    // settable, so the kind chips work with clicks, keyboard and UI Automation alike
    public bool IsTask { get => Kind == CaptureKind.Task; set { if (value) Kind = CaptureKind.Task; } }
    public bool IsNote { get => Kind == CaptureKind.Note; set { if (value) Kind = CaptureKind.Note; } }
    public bool IsEvent { get => Kind == CaptureKind.Event; set { if (value) Kind = CaptureKind.Event; } }
    public bool IsExpense { get => Kind == CaptureKind.Expense; set { if (value) Kind = CaptureKind.Expense; } }

    /// <summary>The view focuses the text box when this fires.</summary>
    public event EventHandler? FocusRequested;

    public void Open(CaptureKind? kind = null, ContextChip? context = null, string? text = null)
    {
        if (kind is { } k) Kind = k;
        Context = context;
        Attach = context != null;
        Text = text ?? string.Empty;
        UpdateHint();
        IsOpen = true;
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand] private void Close() => IsOpen = false;

    [RelayCommand]
    private void SetKind(string? kind)
    {
        if (Enum.TryParse<CaptureKind>(kind, true, out var k)) Kind = k;
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnKindChanged(CaptureKind value)
    {
        OnPropertyChanged(nameof(IsTask));
        OnPropertyChanged(nameof(IsNote));
        OnPropertyChanged(nameof(IsEvent));
        OnPropertyChanged(nameof(IsExpense));
        UpdateHint();
    }

    partial void OnTextChanged(string value) => UpdateHint();

    private void UpdateHint()
    {
        HintIsWarning = false;
        if (Kind == CaptureKind.Expense)
        {
            if (string.IsNullOrWhiteSpace(Text)) { Hint = ctx.Loc["Capture_ExpenseHint"]; return; }
            var q = Arcadia.Core.Finance.QuickExpenseParser.Parse(Text, ctx.Now, finance.Ctx.Money.DecimalStyle);
            if (!q.IsClear) { Hint = ctx.Loc["Capture_ExpenseUnclear"]; HintIsWarning = true; return; }
            var money = finance.Ctx.Format(q.Amount!.Value, q.Currency ?? finance.Ctx.Prefs.DefaultCurrency);
            Hint = "→ " + Services.FinancePrivacy.Apply(money, Services.FinancePrivacy.Instance.IsHidden) + "  ·  " + q.Description
                   + (q.Date is { } qd ? "  ·  " + OrgText.Day(qd, ctx.Today, ctx.Loc) : string.Empty);
            return;
        }
        if (Kind == CaptureKind.Note || string.IsNullOrWhiteSpace(Text))
        {
            Hint = Kind switch
            {
                CaptureKind.Note => ctx.Loc["Capture_NoteHint"],
                CaptureKind.Event => ctx.Loc["Capture_EventHint"],
                _ => ctx.Loc["Capture_TaskHint"],
            };
            return;
        }
        var p = QuickDateParser.Parse(Text, ctx.Now);
        if (p.Ambiguous) { Hint = ctx.Loc["Task_DateUnclear"]; HintIsWarning = true; return; }
        if (p.Date is { } d) Hint = "→ " + OrgText.Due(d, p.Time, ctx.Now, ctx.Loc);
        else Hint = Kind == CaptureKind.Event ? "→ " + ctx.Loc["Capture_EventToday"] : ctx.Loc["Capture_NoDate"];
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Text) || IsSaving) return;
        IsSaving = true;
        try
        {
            if (Kind == CaptureKind.Expense)
            {
                await SaveExpenseAsync();
                return;
            }
            CaptureResult? result = null;
            var context = Attach ? Context?.Ref : null;
            if (!await ctx.TryAsync(async () => result = await capture.CaptureAsync(Kind, Text, ctx.Now, context), "Capture_ErrSave")) return;
            var r = result!;
            ctx.Changed(r.Kind switch { CaptureKind.Note => OrganizerChange.Notes, CaptureKind.Event => OrganizerChange.Events, _ => OrganizerChange.Tasks });
            var when = r.Date is { } d ? " · " + OrgText.Due(d, r.Time, ctx.Now, ctx.Loc) : string.Empty;
            ctx.Notifications.Show(ctx.Loc.Format("Capture_Saved_" + r.Kind, r.Title) + when, NotificationKind.Success);
            if (r.DateWasAmbiguous) ctx.Notifications.Show(ctx.Loc["Task_DateUnclearSaved"], NotificationKind.Info);
            Text = string.Empty;
            IsOpen = false;
        }
        finally { IsSaving = false; }
    }

    /// <summary>
    /// "€35 groceries": saved at once when the pattern, the account and the category are clear; otherwise the transaction
    /// editor opens with what was understood (nothing is guessed).
    /// </summary>
    private async Task SaveExpenseAsync()
    {
        var context = Attach ? Context?.Ref : null;
        Arcadia.Core.Finance.QuickExpenseResult? result = null;
        if (!await finance.Ctx.TryAsync(async () => result = await expenses.CaptureAsync(Text, ctx.Now, finance.Ctx.Money.DecimalStyle, finance.Ctx.Prefs.DefaultAccountId, context), "Fin_ErrSave"))
            return;
        if (result!.Saved is { } saved)
        {
            var account = (await finance.Ctx.Store.GetAccountAsync(saved.AccountId))?.Currency ?? finance.Ctx.Prefs.DefaultCurrency;
            var money = Services.FinancePrivacy.Apply(finance.Ctx.Format(saved.Amount, account), Services.FinancePrivacy.Instance.IsHidden);
            ctx.Notifications.Show(ctx.Loc.Format("Capture_Saved_Expense", saved.Description, money), Services.NotificationKind.Success);
            finance.Ctx.Changed();
            Text = string.Empty;
            IsOpen = false;
            return;
        }
        IsOpen = false;
        var draft = result.Draft;
        Text = string.Empty;
        await finance.NewTransactionAsync(Arcadia.Core.Finance.TransactionType.Expense, draft);
    }
}
