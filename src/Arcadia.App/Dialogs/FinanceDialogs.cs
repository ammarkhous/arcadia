using System.Collections.ObjectModel;
using System.Globalization;
using Arcadia.App.Services;
using Arcadia.App.ViewModels.Finance;
using Arcadia.App.ViewModels.Organizer;
using Arcadia.Core.Finance;
using Arcadia.Core.Organizer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.Dialogs;

/// <summary>A document attached (or about to be attached) to a transaction.</summary>
public sealed partial class AttachedDocument(int documentId, string name, DocumentRole role, IReadOnlyList<Option<DocumentRole>> roles) : ObservableObject
{
    public int DocumentId { get; } = documentId;
    public string Name { get; } = name;
    public IReadOnlyList<Option<DocumentRole>> Roles { get; } = roles;
    [ObservableProperty] private Option<DocumentRole> _role = roles.First(r => r.Value == role);
    public override string ToString() => Name;
}

/// <summary>
/// New or edited transaction. Fast path: type, amount, account, category — Enter saves. Everything else (time, payee,
/// tags, notes, project, trip, receipts) is optional. Validation follows the accounting model and is explained inline.
/// </summary>
public sealed partial class TransactionEditorViewModel : DialogViewModel
{
    private readonly FinanceContext _ctx;
    private readonly IReadOnlyList<AccountBalance> _accounts;
    private readonly IReadOnlyList<CategoryInfo> _categories;
    private readonly FinanceTransaction? _existing;
    private readonly IReadOnlyList<Option<DocumentRole>> _roles;

    public TransactionEditorViewModel(FinanceContext ctx, IReadOnlyList<AccountBalance> accounts, IReadOnlyList<CategoryInfo> categories, FinanceTransaction? existing,
        TransactionDraft? prefill = null, IReadOnlyDictionary<EntityRef, ContextChip>? chips = null, IReadOnlyList<(int Id, DocumentRole Role, string Name)>? documents = null)
    {
        _ctx = ctx;
        _accounts = accounts;
        _categories = categories;
        _existing = existing;
        var loc = ctx.Loc;
        IsNew = existing == null;
        Title = loc[IsNew ? "Txn_NewTitle" : "Txn_EditTitle"];
        _roles = Enum.GetValues<DocumentRole>().Select(r => new Option<DocumentRole>(r, loc["DocRole_" + r])).ToList();
        AccountOptions = accounts.Where(a => !a.Account.IsArchived || a.Account.Id == existing?.AccountId || a.Account.Id == existing?.ToAccountId)
            .Select(a => new FinanceOption(a.Account.Id, $"{a.Account.Name}  ·  {a.Account.Currency}", a.Account.Icon, a.Account.Accent, Currency: a.Account.Currency)).ToList();

        var d = existing != null
            ? new TransactionDraft(existing.Type, existing.Date, existing.Amount, existing.AccountId, existing.CategoryId, existing.Description, existing.Payee, existing.Time,
                existing.ToAccountId, existing.ToAmount, existing.Tags, existing.Notes, existing.ProjectId, existing.TripId)
            : prefill ?? new TransactionDraft(TransactionType.Expense, ctx.Today, 0m, ctx.Prefs.DefaultAccountId ?? 0);
        _type = d.Type;
        _amountText = d.Amount > 0 ? ctx.Money.FormatInput(d.Amount) : string.Empty;
        _dateInput = DateText.Format(d.Date, loc.Culture);
        _timeText = d.Time is { } t ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : string.Empty;
        _account = AccountOptions.FirstOrDefault(o => o.Id == d.AccountId) ?? AccountOptions.FirstOrDefault(o => o.Id == ctx.Prefs.DefaultAccountId) ?? AccountOptions.FirstOrDefault();
        _toAccount = AccountOptions.FirstOrDefault(o => o.Id == d.ToAccountId);
        _toAmountText = d.ToAmount is { } ta ? ctx.Money.FormatInput(ta) : string.Empty;
        _description = d.Description;
        _payee = d.Payee ?? string.Empty;
        _tags = d.Tags;
        _notes = d.Notes ?? string.Empty;
        _needsReview = existing?.NeedsReview ?? false;
        _project = d.ProjectId is { } p && chips != null ? chips.GetValueOrDefault(new EntityRef(EntityKind.Project, p)) : null;
        _trip = d.TripId is { } tr && chips != null ? chips.GetValueOrDefault(new EntityRef(EntityKind.Trip, tr)) : null;
        _showMore = existing != null && (existing.Time != null || existing.Tags.Length > 0 || !string.IsNullOrEmpty(existing.Notes));
        foreach (var doc in documents ?? []) Documents.Add(new AttachedDocument(doc.Id, doc.Name, doc.Role, _roles));
        RebuildCategories(d.CategoryId);
        UpdatePreview();
    }

    public override string Icon => "Receipt";
    public override double DialogWidth => 620;
    public bool IsNew { get; }
    public int? SavedId { get; private set; }
    public bool Deleted { get; private set; }
    public IReadOnlyList<FinanceOption> AccountOptions { get; }
    public ObservableCollection<FinanceOption> CategoryOptions { get; } = [];
    public ObservableCollection<AttachedDocument> Documents { get; } = [];

    [ObservableProperty] private TransactionType _type;
    [ObservableProperty] private string _amountText;
    [ObservableProperty] private string? _amountPreview;
    [ObservableProperty] private bool _amountInvalid;
    [ObservableProperty] private string _dateInput;
    [ObservableProperty] private string _timeText;
    [ObservableProperty] private FinanceOption? _account;
    [ObservableProperty] private FinanceOption? _toAccount;
    [ObservableProperty] private string _toAmountText;
    [ObservableProperty] private FinanceOption? _category;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _payee;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private string _notes;
    [ObservableProperty] private bool _needsReview;
    [ObservableProperty] private ContextChip? _project;
    [ObservableProperty] private ContextChip? _trip;
    [ObservableProperty] private bool _showMore;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _savedNote;

    public bool IsExpense { get => Type == TransactionType.Expense; set { if (value) Type = TransactionType.Expense; } }
    public bool IsIncome { get => Type == TransactionType.Income; set { if (value) Type = TransactionType.Income; } }
    public bool IsTransfer { get => Type == TransactionType.Transfer; set { if (value) Type = TransactionType.Transfer; } }
    public bool ShowCategory => Type != TransactionType.Transfer;
    public bool ShowToAmount => IsTransfer && Account?.Currency is { } a && ToAccount?.Currency is { } b && a != b;
    public string CurrencySymbol => Account?.Currency is { } c ? Currency.SymbolOf(c) : string.Empty;
    public string ToCurrencySymbol => ToAccount?.Currency is { } c ? Currency.SymbolOf(c) : string.Empty;
    public string PayeeLabel => _ctx.Loc[Type == TransactionType.Income ? "Txn_Source" : "Txn_Payee"];
    public string ConfirmText => _ctx.Loc[IsNew ? "Txn_Add" : "Editor_Save"];

    partial void OnTypeChanged(TransactionType value)
    {
        OnPropertyChanged(nameof(IsExpense));
        OnPropertyChanged(nameof(IsIncome));
        OnPropertyChanged(nameof(IsTransfer));
        OnPropertyChanged(nameof(ShowCategory));
        OnPropertyChanged(nameof(ShowToAmount));
        OnPropertyChanged(nameof(PayeeLabel));
        RebuildCategories(Category?.Id);
        UpdatePreview();
    }

    partial void OnAmountTextChanged(string value) => UpdatePreview();

    partial void OnAccountChanged(FinanceOption? value)
    {
        OnPropertyChanged(nameof(CurrencySymbol));
        OnPropertyChanged(nameof(ShowToAmount));
        UpdatePreview();
    }

    partial void OnToAccountChanged(FinanceOption? value)
    {
        OnPropertyChanged(nameof(ShowToAmount));
        OnPropertyChanged(nameof(ToCurrencySymbol));
    }

    /// <summary>Categories of the matching kind, subcategories indented under their parent.</summary>
    private void RebuildCategories(int? keep)
    {
        var kind = Type == TransactionType.Income ? CategoryKind.Income : CategoryKind.Expense;
        CategoryOptions.Clear();
        CategoryOptions.Add(new FinanceOption(null, _ctx.Loc["Fin_Uncategorized"], "Tag", "Steel"));
        foreach (var c in _categories.Where(c => c.Kind == kind && (!c.IsArchived || c.Id == keep)))
            CategoryOptions.Add(new FinanceOption(c.Id, c.ParentId != null ? "    " + c.Name : c.Name, c.Icon, c.Accent, c.ParentId != null));
        Category = CategoryOptions.FirstOrDefault(o => o.Id == keep) ?? CategoryOptions[0];
    }

    private void UpdatePreview()
    {
        var outcome = _ctx.ParseAmount(AmountText, out var v);
        AmountInvalid = outcome is not (AmountParser.Outcome.Ok or AmountParser.Outcome.Empty) || (outcome == AmountParser.Outcome.Ok && v <= 0);
        AmountPreview = outcome == AmountParser.Outcome.Ok && v > 0 && Account?.Currency is { } cur
            ? "= " + _ctx.Format(Type == TransactionType.Expense ? -v : v, cur)
            : outcome is AmountParser.Outcome.Empty ? null : _ctx.AmountError(outcome == AmountParser.Outcome.Ok ? AmountParser.Outcome.Invalid : outcome);
    }

    [RelayCommand] private void SetToday() => DateInput = DateText.Format(_ctx.Today, _ctx.Loc.Culture);
    [RelayCommand] private void SetYesterday() => DateInput = DateText.Format(_ctx.Today.AddDays(-1), _ctx.Loc.Culture);

    [RelayCommand]
    private void PickProject()
    {
        var picker = new EntityPickerViewModel(_ctx.Organizer, [EntityKind.Project]);
        if (_ctx.Dialogs.Show(picker) && picker.Selected is { } info) Project = new ContextChip(info, OrgText.KindLabel(info.Ref.Kind, _ctx.Loc), _ctx.Navigation);
    }

    [RelayCommand]
    private void PickTrip()
    {
        var picker = new EntityPickerViewModel(_ctx.Organizer, [EntityKind.Trip]);
        if (_ctx.Dialogs.Show(picker) && picker.Selected is { } info) Trip = new ContextChip(info, OrgText.KindLabel(info.Ref.Kind, _ctx.Loc), _ctx.Navigation);
    }

    [RelayCommand] private void ClearProject() => Project = null;
    [RelayCommand] private void ClearTrip() => Trip = null;

    /// <summary>Links a file that is already in Arcadia's Documents library (it is referenced, never copied).</summary>
    [RelayCommand]
    private void AttachDocument()
    {
        var picker = new EntityPickerViewModel(_ctx.Organizer, [EntityKind.Document]) { Title = _ctx.Loc["Txn_AttachTitle"] };
        if (!_ctx.Dialogs.Show(picker) || picker.Selected is not { } info) return;
        if (Documents.Any(d => d.DocumentId == info.Ref.Id)) return;
        Documents.Add(new AttachedDocument(info.Ref.Id, info.Name, DocumentRole.Receipt, _roles));
        ShowMore = true;
    }

    [RelayCommand] private void RemoveDocument(AttachedDocument? doc) { if (doc != null) Documents.Remove(doc); }

    [RelayCommand] private void OpenDocument(AttachedDocument? doc) { if (doc != null) { Close(false); _ctx.Navigation.Open(new EntityRef(EntityKind.Document, doc.DocumentId)); } }

    private TransactionDraft? BuildDraft()
    {
        var loc = _ctx.Loc;
        Error = null;
        if (Account?.Id is not { } accountId) { Error = loc["Fin_ErrNoAccount"]; return null; }
        var outcome = _ctx.ParseAmount(AmountText, out var amount);
        if (outcome != AmountParser.Outcome.Ok || amount <= 0) { Error = _ctx.AmountError(outcome == AmountParser.Outcome.Ok ? AmountParser.Outcome.Invalid : outcome); return null; }
        if (!DateText.TryParse(DateInput, loc.Culture, out var date) || date == null) { Error = loc["Proj_ErrDate"]; return null; }
        if (!OrgText.TryTime(TimeText, loc.Culture, out var time)) { Error = loc["Org_ErrTime"]; return null; }
        int? toAccount = null;
        decimal? toAmount = null;
        if (IsTransfer)
        {
            if (ToAccount?.Id is not { } to || to == accountId) { Error = loc["Fin_ErrTransferAccounts"]; return null; }
            toAccount = to;
            if (ShowToAmount)
            {
                var o2 = _ctx.ParseAmount(ToAmountText, out var ta);
                if (o2 != AmountParser.Outcome.Ok || ta <= 0) { Error = loc["Fin_ErrToAmount"]; return null; }
                toAmount = ta;
            }
        }
        return new TransactionDraft(Type, date.Value, amount, accountId, IsTransfer ? null : Category?.Id, Description.Trim(), string.IsNullOrWhiteSpace(Payee) ? null : Payee.Trim(),
            time, toAccount, toAmount, Tags, string.IsNullOrWhiteSpace(Notes) ? null : Notes, Project?.Ref.Id, Trip?.Ref.Id);
    }

    private async Task<bool> SaveAsync()
    {
        if (BuildDraft() is not { } draft) return false;
        try
        {
            int id;
            if (_existing == null) id = (await _ctx.Store.CreateTransactionAsync(draft)).Id;
            else
            {
                id = _existing.Id;
                await _ctx.Store.UpdateTransactionAsync(id, draft);
                if (_existing.NeedsReview != NeedsReview) await _ctx.Store.SetNeedsReviewAsync(id, NeedsReview);
                foreach (var gone in _existing.Documents.Where(d => Documents.All(x => x.DocumentId != d.DocumentId)))
                    await _ctx.Store.DetachDocumentAsync(id, gone.DocumentId);
            }
            foreach (var doc in Documents)
                if (_existing?.Documents.FirstOrDefault(d => d.DocumentId == doc.DocumentId) is not { } old || old.Role != doc.Role.Value)
                    await _ctx.Store.AttachDocumentAsync(id, doc.DocumentId, doc.Role.Value);
            SavedId = id;
            _ctx.Changed();
            return true;
        }
        catch (FinanceException ex) { Error = _ctx.Explain(ex); }
        catch (KeyNotFoundException) { Error = _ctx.Loc["Fin_ErrGone"]; }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(_ctx.Logger, ex, "Saving a transaction failed");
            Error = _ctx.Loc["Fin_ErrSave"];
        }
        return false;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (await SaveAsync()) Close(true);
    }

    /// <summary>Saves and clears the form for the next entry (type, date and account stay).</summary>
    [RelayCommand]
    private async Task SaveAndNewAsync()
    {
        if (!IsNew || !await SaveAsync()) return;
        SavedNote = _ctx.Loc.Format("Txn_SavedNote", Description.Length > 0 ? Description : AmountPreview ?? string.Empty);
        AmountText = string.Empty;
        Description = string.Empty;
        Payee = string.Empty;
        Notes = string.Empty;
        Tags = string.Empty;
        Documents.Clear();
        FocusAmountRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? FocusAmountRequested;

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_existing == null || !_ctx.Dialogs.Confirm(_ctx.Loc["Txn_ConfirmDelete"], _ctx.Loc["Confirm_Delete"])) return;
        if (await _ctx.TryAsync(() => _ctx.Store.DeleteTransactionAsync(_existing.Id), "Fin_ErrSave"))
        {
            Deleted = true;
            _ctx.Changed();
            Close(true);
        }
    }

    [RelayCommand] private void Cancel() => Close(false);
}

public sealed partial class AccountEditorViewModel : DialogViewModel
{
    public static readonly string[] IconChoices = ["Bank", "Wallet", "Cash", "Card", "Target", "Chart", "Home", "Suitcase", "Games", "Star", "Shield", "Library"];
    private readonly FinanceContext _ctx;
    private readonly FinanceAccount? _existing;

    public AccountEditorViewModel(FinanceContext ctx, FinanceAccount? existing, bool currencyLocked)
    {
        _ctx = ctx;
        _existing = existing;
        IsNew = existing == null;
        Title = ctx.Loc[IsNew ? "Acct_NewTitle" : "Acct_EditTitle"];
        TypeOptions = Enum.GetValues<AccountType>().Select(t => new Option<AccountType>(t, ctx.Loc["FinAcct_" + t])).ToList();
        CurrencyOptions = Arcadia.Core.Finance.Currency.Common;
        _name = existing?.Name ?? string.Empty;
        _type = TypeOptions.First(o => o.Value == (existing?.Type ?? AccountType.Checking));
        _currency = existing?.Currency ?? ctx.Prefs.DefaultCurrency;
        _openingText = existing != null ? ctx.Money.FormatInput(existing.OpeningBalance) : string.Empty;
        _description = existing?.Description ?? string.Empty;
        _glyph = existing?.Icon ?? "Bank";
        _accent = existing?.Accent ?? "Blue";
        CurrencyLocked = currencyLocked;
    }

    public override string Icon => "Wallet";
    public override double DialogWidth => 520;
    public bool IsNew { get; }
    public bool CurrencyLocked { get; }
    public int? SavedId { get; private set; }
    public IReadOnlyList<Option<AccountType>> TypeOptions { get; }
    public IReadOnlyList<string> CurrencyOptions { get; }
    public IReadOnlyList<string> Icons => IconChoices;
    public IReadOnlyList<string> Accents => Arcadia.Data.Finance.FinanceStore.Accents;
    [ObservableProperty] private string _name;
    [ObservableProperty] private Option<AccountType> _type;
    [ObservableProperty] private string _currency;
    [ObservableProperty] private string _openingText;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _glyph;
    [ObservableProperty] private string _accent;
    [ObservableProperty] private string? _error;

    partial void OnTypeChanged(Option<AccountType> value)
    {
        if (IsNew) Glyph = Arcadia.Data.Finance.FinanceStore.IconFor(value.Value);
    }

    [RelayCommand] private void ChooseIcon(string? icon) { if (icon != null) Glyph = icon; }
    [RelayCommand] private void ChooseAccent(string? accent) { if (accent != null) Accent = accent; }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        Error = null;
        var opening = 0m;
        if (!string.IsNullOrWhiteSpace(OpeningText))
        {
            var o = _ctx.ParseAmount(OpeningText, out opening);
            if (o != AmountParser.Outcome.Ok) { Error = _ctx.AmountError(o); return; }
        }
        var draft = new AccountDraft(Name, Type.Value, Currency, opening, Description, Glyph, Accent);
        try
        {
            if (_existing == null) SavedId = (await _ctx.Store.CreateAccountAsync(draft)).Id;
            else { await _ctx.Store.UpdateAccountAsync(_existing.Id, draft); SavedId = _existing.Id; }
            _ctx.Changed();
            Close(true);
        }
        catch (FinanceException ex) { Error = _ctx.Explain(ex); }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(_ctx.Logger, ex, "Saving an account failed");
            Error = _ctx.Loc["Fin_ErrSave"];
        }
    }

    [RelayCommand] private void Cancel() => Close(false);
}

public sealed partial class BudgetEditorViewModel : DialogViewModel
{
    private readonly FinanceContext _ctx;
    private readonly Budget? _existing;
    private readonly int? _tripId;

    public BudgetEditorViewModel(FinanceContext ctx, IReadOnlyList<CategoryInfo> categories, Budget? existing, int? categoryId = null, int? tripId = null, string? tripName = null,
        DateOnly? start = null, DateOnly? end = null)
    {
        _ctx = ctx;
        _existing = existing;
        _tripId = existing?.TripId ?? tripId;
        IsNew = existing == null;
        IsTrip = _tripId != null;
        TripName = tripName;
        Title = ctx.Loc[IsTrip ? "Bud_TripTitle" : IsNew ? "Bud_NewTitle" : "Bud_EditTitle"];
        CategoryOptions = [new FinanceOption(null, ctx.Loc["Fin_AllSpending"], "Finance", "Cyan"),
            .. categories.Where(c => c.Kind == CategoryKind.Expense && (!c.IsArchived || c.Id == existing?.CategoryId))
                .Select(c => new FinanceOption(c.Id, c.ParentId != null ? "    " + c.Name : c.Name, c.Icon, c.Accent, c.ParentId != null))];
        PeriodOptions = [new(BudgetPeriod.Monthly, ctx.Loc["Fin_Monthly"]), new(BudgetPeriod.Custom, ctx.Loc["Bud_CustomRange"])];
        CurrencyOptions = Arcadia.Core.Finance.Currency.Common;
        _category = CategoryOptions.FirstOrDefault(o => o.Id == (existing?.CategoryId ?? categoryId)) ?? CategoryOptions[0];
        _amountText = existing != null ? ctx.Money.FormatInput(existing.Amount) : string.Empty;
        _currency = existing?.Currency ?? ctx.Prefs.DefaultCurrency;
        _period = PeriodOptions.First(o => o.Value == (existing?.Period ?? (IsTrip ? BudgetPeriod.Custom : BudgetPeriod.Monthly)));
        _startText = DateText.Format(existing?.StartDate ?? start ?? FinanceMath.MonthStart(ctx.Today), ctx.Loc.Culture);
        _endText = DateText.Format(existing?.EndDate ?? end, ctx.Loc.Culture);
        _rollover = existing?.Rollover ?? false;
        _name = existing?.Name ?? string.Empty;
        _notes = existing?.Notes ?? string.Empty;
    }

    public override string Icon => "Chart";
    public override double DialogWidth => 540;
    public bool IsNew { get; }
    public bool IsTrip { get; }
    public string? TripName { get; }
    public IReadOnlyList<FinanceOption> CategoryOptions { get; }
    public IReadOnlyList<Option<BudgetPeriod>> PeriodOptions { get; }
    public IReadOnlyList<string> CurrencyOptions { get; }
    [ObservableProperty] private FinanceOption _category;
    [ObservableProperty] private string _amountText;
    [ObservableProperty] private string _currency;
    [ObservableProperty] private Option<BudgetPeriod> _period;
    [ObservableProperty] private string _startText;
    [ObservableProperty] private string _endText;
    [ObservableProperty] private bool _rollover;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _notes;
    [ObservableProperty] private string? _error;

    public bool IsMonthly => Period.Value == BudgetPeriod.Monthly;
    partial void OnPeriodChanged(Option<BudgetPeriod> value) => OnPropertyChanged(nameof(IsMonthly));

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        Error = null;
        var o = _ctx.ParseAmount(AmountText, out var amount);
        if (o != AmountParser.Outcome.Ok || amount <= 0) { Error = _ctx.AmountError(o == AmountParser.Outcome.Ok ? AmountParser.Outcome.Invalid : o); return; }
        if (!DateText.TryParse(StartText, _ctx.Loc.Culture, out var start) || start == null || !DateText.TryParse(EndText, _ctx.Loc.Culture, out var end)) { Error = _ctx.Loc["Proj_ErrDate"]; return; }
        var draft = new BudgetDraft(Category.Id, amount, Currency, Period.Value, start.Value, end, IsMonthly && Rollover, string.IsNullOrWhiteSpace(Name) ? null : Name, _tripId,
            string.IsNullOrWhiteSpace(Notes) ? null : Notes);
        try
        {
            if (_existing == null) await _ctx.Store.CreateBudgetAsync(draft);
            else await _ctx.Store.UpdateBudgetAsync(_existing.Id, draft);
            _ctx.Changed();
            Close(true);
        }
        catch (FinanceException ex) { Error = _ctx.Explain(ex); }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(_ctx.Logger, ex, "Saving a budget failed");
            Error = _ctx.Loc["Fin_ErrSave"];
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_existing == null || !_ctx.Dialogs.Confirm(_ctx.Loc["Bud_ConfirmDelete"], _ctx.Loc["Confirm_Delete"])) return;
        if (await _ctx.TryAsync(() => _ctx.Store.DeleteBudgetAsync(_existing.Id), "Fin_ErrSave")) { _ctx.Changed(); Close(true); }
    }

    [RelayCommand] private void Cancel() => Close(false);
}

public sealed partial class RecurringEditorViewModel : DialogViewModel
{
    private readonly FinanceContext _ctx;
    private readonly RecurringItem? _existing;
    private readonly IReadOnlyList<CategoryInfo> _categories;

    public RecurringEditorViewModel(FinanceContext ctx, IReadOnlyList<AccountBalance> accounts, IReadOnlyList<CategoryInfo> categories, RecurringItem? existing, RecurringKind kind = RecurringKind.Bill)
    {
        _ctx = ctx;
        _existing = existing;
        _categories = categories;
        IsNew = existing == null;
        Title = ctx.Loc[IsNew ? "Rec_NewTitle" : "Rec_EditTitle"];
        KindOptions = Enum.GetValues<RecurringKind>().Select(k => new Option<RecurringKind>(k, ctx.Loc["FinKind_" + k])).ToList();
        FrequencyOptions = new[] { RecurrenceFrequency.Weekly, RecurrenceFrequency.Monthly, RecurrenceFrequency.Yearly, RecurrenceFrequency.Daily }
            .Select(f => new Option<RecurrenceFrequency>(f, ctx.Loc["FinUnit_" + f])).ToList();
        AccountOptions = [new FinanceOption(null, ctx.Loc["Rec_NoAccount"], "Wallet"),
            .. accounts.Where(a => !a.Account.IsArchived || a.Account.Id == existing?.AccountId).Select(a => new FinanceOption(a.Account.Id, $"{a.Account.Name}  ·  {a.Account.Currency}", a.Account.Icon, a.Account.Accent, Currency: a.Account.Currency))];
        CurrencyOptions = Arcadia.Core.Finance.Currency.Common;
        _name = existing?.Name ?? string.Empty;
        _kind = KindOptions.First(o => o.Value == (existing?.Kind ?? kind));
        _amountText = existing != null ? ctx.Money.FormatInput(existing.Amount) : string.Empty;
        _account = AccountOptions.FirstOrDefault(o => o.Id == (existing != null ? existing.AccountId : ctx.Prefs.DefaultAccountId)) ?? AccountOptions[0];
        _currency = existing?.Currency ?? _account.Currency ?? ctx.Prefs.DefaultCurrency;
        _frequency = FrequencyOptions.First(o => o.Value == (existing?.Frequency ?? RecurrenceFrequency.Monthly));
        _intervalText = (existing?.Interval ?? 1).ToString(CultureInfo.InvariantCulture);
        _startText = DateText.Format(existing?.StartDate ?? ctx.Today, ctx.Loc.Culture);
        _endText = DateText.Format(existing?.EndDate, ctx.Loc.Culture);
        _payee = existing?.Payee ?? string.Empty;
        _isActive = existing?.IsActive ?? true;
        _showOnCalendar = existing?.ShowOnCalendar ?? true;
        _notes = existing?.Notes ?? string.Empty;
        RebuildCategories(existing?.CategoryId ?? (_kind.Value == RecurringKind.Subscription ? 11 : null));
    }

    public override string Icon => "Repeat";
    public override double DialogWidth => 560;
    public bool IsNew { get; }
    public IReadOnlyList<Option<RecurringKind>> KindOptions { get; }
    public IReadOnlyList<Option<RecurrenceFrequency>> FrequencyOptions { get; }
    public IReadOnlyList<FinanceOption> AccountOptions { get; }
    public IReadOnlyList<string> CurrencyOptions { get; }
    public ObservableCollection<FinanceOption> CategoryOptions { get; } = [];
    [ObservableProperty] private string _name;
    [ObservableProperty] private Option<RecurringKind> _kind;
    [ObservableProperty] private string _amountText;
    [ObservableProperty] private FinanceOption _account;
    [ObservableProperty] private string _currency;
    [ObservableProperty] private FinanceOption? _category;
    [ObservableProperty] private Option<RecurrenceFrequency> _frequency;
    [ObservableProperty] private string _intervalText;
    [ObservableProperty] private string _startText;
    [ObservableProperty] private string _endText;
    [ObservableProperty] private string _payee;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _showOnCalendar;
    [ObservableProperty] private string _notes;
    [ObservableProperty] private string? _error;

    /// <summary>The currency follows the account when one is chosen.</summary>
    public bool CurrencyFromAccount => Account.Id != null;

    partial void OnAccountChanged(FinanceOption value)
    {
        if (value.Currency != null) Currency = value.Currency;
        OnPropertyChanged(nameof(CurrencyFromAccount));
    }

    partial void OnKindChanged(Option<RecurringKind> value) => RebuildCategories(Category?.Id);

    private void RebuildCategories(int? keep)
    {
        var kind = Kind.Value == RecurringKind.Income ? CategoryKind.Income : CategoryKind.Expense;
        CategoryOptions.Clear();
        CategoryOptions.Add(new FinanceOption(null, _ctx.Loc["Fin_Uncategorized"]));
        foreach (var c in _categories.Where(c => c.Kind == kind && (!c.IsArchived || c.Id == keep)))
            CategoryOptions.Add(new FinanceOption(c.Id, c.ParentId != null ? "    " + c.Name : c.Name, c.Icon, c.Accent, c.ParentId != null));
        Category = CategoryOptions.FirstOrDefault(o => o.Id == keep) ?? CategoryOptions[0];
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        Error = null;
        var o = _ctx.ParseAmount(AmountText, out var amount);
        if (o != AmountParser.Outcome.Ok || amount <= 0) { Error = _ctx.AmountError(o == AmountParser.Outcome.Ok ? AmountParser.Outcome.Invalid : o); return; }
        if (!int.TryParse(IntervalText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var interval) || interval is < 1 or > 999) { Error = _ctx.Loc["Fin_ErrRecurrence"]; return; }
        if (!DateText.TryParse(StartText, _ctx.Loc.Culture, out var start) || start == null || !DateText.TryParse(EndText, _ctx.Loc.Culture, out var end)) { Error = _ctx.Loc["Proj_ErrDate"]; return; }
        var draft = new RecurringDraft(Name, Kind.Value, amount, Currency, Frequency.Value, interval, start.Value, end, Account.Id, Category?.Id,
            string.IsNullOrWhiteSpace(Payee) ? null : Payee, IsActive, ShowOnCalendar, string.IsNullOrWhiteSpace(Notes) ? null : Notes);
        try
        {
            if (_existing == null) await _ctx.Store.CreateRecurringItemAsync(draft);
            else await _ctx.Store.UpdateRecurringItemAsync(_existing.Id, draft);
            _ctx.Changed();
            Close(true);
        }
        catch (FinanceException ex) { Error = _ctx.Explain(ex); }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(_ctx.Logger, ex, "Saving a recurring item failed");
            Error = _ctx.Loc["Fin_ErrSave"];
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_existing == null || !_ctx.Dialogs.Confirm(_ctx.Loc.Format("Rec_ConfirmDelete", _existing.Name), _ctx.Loc["Confirm_Delete"])) return;
        if (await _ctx.TryAsync(() => _ctx.Store.DeleteRecurringItemAsync(_existing.Id), "Fin_ErrSave")) { _ctx.Changed(); Close(true); }
    }

    [RelayCommand] private void Cancel() => Close(false);
}

public sealed partial class GoalEditorViewModel : DialogViewModel
{
    public static readonly string[] IconChoices = ["Target", "Monitor", "Travel", "Shield", "Home", "Suitcase", "Games", "Camera", "Star", "Library", "Trophy", "Sparkle"];
    private readonly FinanceContext _ctx;
    private readonly SavingsGoal? _existing;

    public GoalEditorViewModel(FinanceContext ctx, IReadOnlyList<AccountBalance> accounts, SavingsGoal? existing)
    {
        _ctx = ctx;
        _existing = existing;
        IsNew = existing == null;
        Title = ctx.Loc[IsNew ? "Goal_NewTitle" : "Goal_EditTitle"];
        AccountOptions = [new FinanceOption(null, ctx.Loc["Goal_Manual"], "Target"),
            .. accounts.Where(a => !a.Account.IsArchived || a.Account.Id == existing?.AccountId).Select(a => new FinanceOption(a.Account.Id, $"{a.Account.Name}  ·  {a.Account.Currency}", a.Account.Icon, a.Account.Accent, Currency: a.Account.Currency))];
        CurrencyOptions = Arcadia.Core.Finance.Currency.Common;
        _name = existing?.Name ?? string.Empty;
        _targetText = existing != null ? ctx.Money.FormatInput(existing.TargetAmount) : string.Empty;
        _savedText = existing != null ? ctx.Money.FormatInput(existing.CurrentAmount) : string.Empty;
        _currency = existing?.Currency ?? ctx.Prefs.DefaultCurrency;
        _account = AccountOptions.FirstOrDefault(o => o.Id == existing?.AccountId) ?? AccountOptions[0];
        _dateInput = DateText.Format(existing?.TargetDate, ctx.Loc.Culture);
        _description = existing?.Description ?? string.Empty;
        _glyph = existing?.Icon ?? "Target";
        _accent = existing?.Accent ?? "Cyan";
    }

    public override string Icon => "Target";
    public override double DialogWidth => 520;
    public bool IsNew { get; }
    public IReadOnlyList<FinanceOption> AccountOptions { get; }
    public IReadOnlyList<string> CurrencyOptions { get; }
    public IReadOnlyList<string> Icons => IconChoices;
    public IReadOnlyList<string> Accents => Arcadia.Data.Finance.FinanceStore.Accents;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _targetText;
    [ObservableProperty] private string _savedText;
    [ObservableProperty] private string _currency;
    [ObservableProperty] private FinanceOption _account;
    [ObservableProperty] private string _dateInput;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _glyph;
    [ObservableProperty] private string _accent;
    [ObservableProperty] private string? _error;

    public bool IsManual => Account.Id == null;
    partial void OnAccountChanged(FinanceOption value)
    {
        if (value.Currency != null) Currency = value.Currency;
        OnPropertyChanged(nameof(IsManual));
    }

    [RelayCommand] private void ChooseIcon(string? icon) { if (icon != null) Glyph = icon; }
    [RelayCommand] private void ChooseAccent(string? accent) { if (accent != null) Accent = accent; }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        Error = null;
        var o = _ctx.ParseAmount(TargetText, out var target);
        if (o != AmountParser.Outcome.Ok || target <= 0) { Error = _ctx.AmountError(o == AmountParser.Outcome.Ok ? AmountParser.Outcome.Invalid : o); return; }
        var saved = 0m;
        if (IsManual && !string.IsNullOrWhiteSpace(SavedText))
        {
            var o2 = _ctx.ParseAmount(SavedText, out saved);
            if (o2 != AmountParser.Outcome.Ok || saved < 0) { Error = _ctx.AmountError(o2 == AmountParser.Outcome.Ok ? AmountParser.Outcome.Invalid : o2); return; }
        }
        if (!DateText.TryParse(DateInput, _ctx.Loc.Culture, out var date)) { Error = _ctx.Loc["Proj_ErrDate"]; return; }
        var draft = new GoalDraft(Name, target, Currency, saved, date, Account.Id, string.IsNullOrWhiteSpace(Description) ? null : Description, Glyph, Accent);
        try
        {
            if (_existing == null) await _ctx.Store.CreateGoalAsync(draft);
            else await _ctx.Store.UpdateGoalAsync(_existing.Id, draft);
            _ctx.Changed();
            Close(true);
        }
        catch (FinanceException ex) { Error = _ctx.Explain(ex); }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(_ctx.Logger, ex, "Saving a goal failed");
            Error = _ctx.Loc["Fin_ErrSave"];
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_existing == null || !_ctx.Dialogs.Confirm(_ctx.Loc.Format("Goal_ConfirmDelete", _existing.Name), _ctx.Loc["Confirm_Delete"])) return;
        if (await _ctx.TryAsync(() => _ctx.Store.DeleteGoalAsync(_existing.Id), "Fin_ErrSave")) { _ctx.Changed(); Close(true); }
    }

    [RelayCommand] private void Cancel() => Close(false);
}

/// <summary>A single amount (adding to a goal by hand).</summary>
public sealed partial class AmountPromptViewModel : DialogViewModel
{
    private readonly FinanceContext _ctx;

    public AmountPromptViewModel(FinanceContext ctx, string title, string message, string currency, bool allowNegative)
    {
        _ctx = ctx;
        Title = title;
        Message = message;
        CurrencySymbol = Arcadia.Core.Finance.Currency.SymbolOf(currency);
        AllowNegative = allowNegative;
    }

    public override string Icon => "Target";
    public override double DialogWidth => 420;
    public string Message { get; }
    public string CurrencySymbol { get; }
    public bool AllowNegative { get; }
    public decimal Amount { get; private set; }
    [ObservableProperty] private string _amountText = string.Empty;
    [ObservableProperty] private string? _error;

    [RelayCommand]
    private void Confirm()
    {
        var o = _ctx.ParseAmount(AmountText, out var v);
        if (o != AmountParser.Outcome.Ok || v == 0 || (v < 0 && !AllowNegative)) { Error = _ctx.AmountError(o == AmountParser.Outcome.Ok ? AmountParser.Outcome.Invalid : o); return; }
        Amount = v;
        Close(true);
    }

    [RelayCommand] private void Cancel() => Close(false);
}

/// <summary>Records a bill, subscription or income due date as paid: from which account, how much, on which day.</summary>
public sealed partial class PayBillViewModel : DialogViewModel
{
    private readonly FinanceContext _ctx;
    private readonly RecurringItem _item;
    private readonly DateOnly _due;

    public PayBillViewModel(FinanceContext ctx, RecurringItem item, DateOnly due, IReadOnlyList<AccountBalance> accounts)
    {
        _ctx = ctx;
        _item = item;
        _due = due;
        Title = ctx.Loc[item.Kind == RecurringKind.Income ? "Pay_ReceivedTitle" : "Pay_Title"];
        Message = ctx.Loc.Format("Pay_Body", item.Name, due.ToString("D", ctx.Loc.Culture));
        AccountOptions = accounts.Where(a => !a.Account.IsArchived && a.Account.Currency == item.Currency)
            .Select(a => new FinanceOption(a.Account.Id, a.Account.Name, a.Account.Icon, a.Account.Accent, Currency: a.Account.Currency)).ToList();
        _account = AccountOptions.FirstOrDefault(o => o.Id == item.AccountId) ?? AccountOptions.FirstOrDefault(o => o.Id == ctx.Prefs.DefaultAccountId) ?? AccountOptions.FirstOrDefault();
        _amountText = ctx.Money.FormatInput(item.Amount);
        _dateInput = DateText.Format(due <= ctx.Today ? due : ctx.Today, ctx.Loc.Culture);
        CurrencySymbol = Arcadia.Core.Finance.Currency.SymbolOf(item.Currency);
    }

    public override string Icon => "Receipt";
    public override double DialogWidth => 460;
    public string Message { get; }
    public string CurrencySymbol { get; }
    public IReadOnlyList<FinanceOption> AccountOptions { get; }
    public bool HasAccounts => AccountOptions.Count > 0;
    [ObservableProperty] private FinanceOption? _account;
    [ObservableProperty] private string _amountText;
    [ObservableProperty] private string _dateInput;
    [ObservableProperty] private string? _error;

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        Error = null;
        if (Account?.Id is not { } accountId) { Error = _ctx.Loc["Fin_ErrNeedAccount"]; return; }
        var o = _ctx.ParseAmount(AmountText, out var amount);
        if (o != AmountParser.Outcome.Ok || amount <= 0) { Error = _ctx.AmountError(o == AmountParser.Outcome.Ok ? AmountParser.Outcome.Invalid : o); return; }
        if (!DateText.TryParse(DateInput, _ctx.Loc.Culture, out var date) || date == null) { Error = _ctx.Loc["Proj_ErrDate"]; return; }
        try
        {
            await _ctx.Store.MarkPaidAsync(_item.Id, _due, accountId, amount, date);
            _ctx.Changed();
            Close(true);
        }
        catch (FinanceException ex) { Error = _ctx.Explain(ex); }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(_ctx.Logger, ex, "Recording a payment failed");
            Error = _ctx.Loc["Fin_ErrSave"];
        }
    }

    [RelayCommand] private void Cancel() => Close(false);
}

/// <summary>One category row in the manager (indented under its parent).</summary>
public sealed record CategoryRowItem(CategoryInfo Info, string UseText)
{
    public int Id => Info.Id;
    public string Name => Info.Name;
    public bool IsChild => Info.ParentId != null;
    public override string ToString() => Name;
}

/// <summary>
/// Categories: add, rename, re-colour, nest one level, archive or delete (moving their transactions elsewhere). Built-in
/// categories keep their translated names until renamed.
/// </summary>
public sealed partial class CategoryManagerViewModel : DialogViewModel
{
    public static readonly string[] IconChoices = ["Tag", "Home", "Food", "Route", "Star", "Games", "Travel", "Shield", "Repeat", "Bolt", "Library", "Target", "Layers",
        "Bank", "Income", "Card", "Cash", "Suitcase", "Camera", "Monitor", "Settings", "MapPin", "Trophy", "Sparkle"];
    private readonly FinanceContext _ctx;

    public CategoryManagerViewModel(FinanceContext ctx)
    {
        _ctx = ctx;
        Title = ctx.Loc["Cat_ManageTitle"];
        KindOptions = [new(CategoryKind.Expense, ctx.Loc["FinCatKind_Expense"]), new(CategoryKind.Income, ctx.Loc["FinCatKind_Income"])];
        _editKind = KindOptions[0];
    }

    public override string Icon => "Tag";
    public override double DialogWidth => 760;
    public ObservableCollection<CategoryRowItem> Items { get; } = [];
    public ObservableCollection<FinanceOption> ParentOptions { get; } = [];
    public ObservableCollection<FinanceOption> MoveOptions { get; } = [];
    public IReadOnlyList<Option<CategoryKind>> KindOptions { get; }
    public IReadOnlyList<string> Icons => IconChoices;
    public IReadOnlyList<string> Accents => Arcadia.Data.Finance.FinanceStore.Accents;
    public bool Changed { get; private set; }

    [ObservableProperty] private CategoryRowItem? _selected;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isNewCategory;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private Option<CategoryKind> _editKind;
    [ObservableProperty] private FinanceOption? _editParent;
    [ObservableProperty] private string _editGlyph = "Tag";
    [ObservableProperty] private string _editAccent = "Steel";
    [ObservableProperty] private FinanceOption? _moveTo;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _showArchived;

    public override Task OnShownAsync() => LoadAsync();

    partial void OnShowArchivedChanged(bool value) => _ = LoadAsync();

    partial void OnEditKindChanged(Option<CategoryKind> value) => RebuildParents();

    private IReadOnlyList<CategoryInfo> _all = [];

    private async Task LoadAsync(int? select = null)
    {
        try
        {
            _all = await _ctx.Store.GetCategoriesAsync(_ctx.Language, includeArchived: true);
            Items.Clear();
            foreach (var c in _all.Where(c => ShowArchived || !c.IsArchived))
                Items.Add(new CategoryRowItem(c, (c.IsArchived ? _ctx.Loc["Cat_Archived"] + " · " : string.Empty) + _ctx.Loc.Format("Fin_TxnCount", c.UseCount)));
            Selected = Items.FirstOrDefault(i => i.Id == (select ?? Selected?.Id)) ?? Items.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(_ctx.Logger, ex, "Loading categories failed");
            Error = _ctx.Loc["Fin_ErrLoad"];
        }
    }

    partial void OnSelectedChanged(CategoryRowItem? value)
    {
        IsEditing = false;
        Error = null;
        MoveOptions.Clear();
        MoveOptions.Add(new FinanceOption(null, _ctx.Loc["Fin_Uncategorized"]));
        if (value == null) return;
        foreach (var c in _all.Where(c => c.Id != value.Id && c.Kind == value.Info.Kind && !c.IsArchived))
            MoveOptions.Add(new FinanceOption(c.Id, c.ParentId != null ? "    " + c.Name : c.Name, c.Icon, c.Accent, c.ParentId != null));
        MoveTo = MoveOptions[0];
    }

    private void RebuildParents(int? keep = null)
    {
        ParentOptions.Clear();
        ParentOptions.Add(new FinanceOption(null, _ctx.Loc["Cat_TopLevel"]));
        foreach (var c in _all.Where(c => c.ParentId == null && c.Kind == EditKind.Value && c.Id != (IsNewCategory ? 0 : Selected?.Id) && !c.IsArchived))
            ParentOptions.Add(new FinanceOption(c.Id, c.Name, c.Icon, c.Accent));
        EditParent = ParentOptions.FirstOrDefault(o => o.Id == keep) ?? ParentOptions[0];
    }

    [RelayCommand]
    private void New(string? asChild)
    {
        IsNewCategory = true;
        EditName = string.Empty;
        EditKind = KindOptions.First(o => o.Value == (Selected?.Info.Kind ?? CategoryKind.Expense));
        RebuildParents(asChild == "child" ? (Selected?.Info.ParentId ?? Selected?.Id) : null);
        EditGlyph = "Tag";
        EditAccent = Selected?.Info.Accent ?? "Steel";
        Error = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void Edit()
    {
        if (Selected is not { } s) return;
        IsNewCategory = false;
        EditName = s.Name;
        EditKind = KindOptions.First(o => o.Value == s.Info.Kind);
        RebuildParents(s.Info.ParentId);
        EditGlyph = s.Info.Icon;
        EditAccent = s.Info.Accent;
        Error = null;
        IsEditing = true;
    }

    [RelayCommand] private void ChooseIcon(string? icon) { if (icon != null) EditGlyph = icon; }
    [RelayCommand] private void ChooseAccent(string? accent) { if (accent != null) EditAccent = accent; }
    [RelayCommand] private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        Error = null;
        var draft = new CategoryDraft(EditName, EditKind.Value, EditParent?.Id, EditGlyph, EditAccent);
        try
        {
            int id;
            if (IsNewCategory) id = (await _ctx.Store.CreateCategoryAsync(draft)).Id;
            else { id = Selected!.Id; await _ctx.Store.UpdateCategoryAsync(id, draft); }
            Changed = true;
            _ctx.Changed();
            IsEditing = false;
            await LoadAsync(id);
        }
        catch (FinanceException ex) { Error = _ctx.Explain(ex); }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(_ctx.Logger, ex, "Saving a category failed");
            Error = _ctx.Loc["Fin_ErrSave"];
        }
    }

    [RelayCommand]
    private async Task ToggleArchiveAsync()
    {
        if (Selected is not { } s) return;
        if (await _ctx.TryAsync(() => _ctx.Store.SetCategoryArchivedAsync(s.Id, !s.Info.IsArchived), "Fin_ErrSave"))
        {
            Changed = true;
            _ctx.Changed();
            await LoadAsync(s.Id);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } s) return;
        var target = MoveTo?.Id is { } m ? MoveOptions.First(o => o.Id == m).Label.Trim() : _ctx.Loc["Fin_Uncategorized"];
        if (!_ctx.Dialogs.Confirm(_ctx.Loc.Format("FinCat_ConfirmDelete", s.Name, s.Info.UseCount, target), _ctx.Loc["Confirm_Delete"])) return;
        try
        {
            await _ctx.Store.DeleteCategoryAsync(s.Id, MoveTo?.Id);
            Changed = true;
            _ctx.Changed();
            await LoadAsync();
        }
        catch (FinanceException ex) { Error = _ctx.Explain(ex); }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(_ctx.Logger, ex, "Deleting a category failed");
            Error = _ctx.Loc["Fin_ErrSave"];
        }
    }

    [RelayCommand] private void Done() => Close(Changed);
}
