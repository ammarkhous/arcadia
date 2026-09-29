using System.Collections.ObjectModel;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.App.ViewModels.Organizer;
using Arcadia.Core.Finance;
using Arcadia.Core.Organizer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.ViewModels.Finance;

/// <summary>
/// The register: a dense, sortable transaction table with text search, date range, account, category, type and
/// review filters. Totals (money in, money out, net) are given per currency for everything that matches.
/// </summary>
public sealed partial class FinanceRegisterViewModel : FinanceSectionViewModel
{
    public const int PageSize = 2000;
    private bool _quiet;

    public FinanceRegisterViewModel(FinanceActions actions) : base(actions)
    {
        var loc = actions.Ctx.Loc;
        PeriodOptions = [new(null, loc["Exp_AllTime"]), .. Enum.GetValues<FinancePeriod>().Select(p => new Option<FinancePeriod?>(p, loc["FinPeriod_" + p]))];
        TypeOptions = [new(null, loc["Reg_AllTypes"]), new(TransactionType.Expense, loc["FinType_Expense"]), new(TransactionType.Income, loc["FinType_Income"]), new(TransactionType.Transfer, loc["FinType_Transfer"])];
        _period = PeriodOptions[0];
        _typeFilter = TypeOptions[0];
        _fromText = DateText.Format(FinanceMath.MonthStart(actions.Ctx.Today), loc.Culture);
        _toText = DateText.Format(actions.Ctx.Today, loc.Culture);
    }

    public IReadOnlyList<Option<FinancePeriod?>> PeriodOptions { get; }
    public IReadOnlyList<Option<TransactionType?>> TypeOptions { get; }
    public ObservableCollection<FinanceOption> AccountOptions { get; } = [];
    public ObservableCollection<FinanceOption> CategoryOptions { get; } = [];
    public ObservableCollection<TransactionItem> Items { get; } = [];
    public ObservableCollection<CurrencyTotal> Totals { get; } = [];

    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private Option<FinancePeriod?> _period;
    [ObservableProperty] private string _fromText;
    [ObservableProperty] private string _toText;
    [ObservableProperty] private FinanceOption? _account;
    [ObservableProperty] private FinanceOption? _category;
    [ObservableProperty] private Option<TransactionType?> _typeFilter;
    [ObservableProperty] private bool _reviewOnly;
    [ObservableProperty] private TransactionSort _sort = TransactionSort.DateDesc;
    [ObservableProperty] private TransactionItem? _selected;
    [ObservableProperty] private string _countText = string.Empty;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyText = string.Empty;
    [ObservableProperty] private ContextChip? _scope;
    private int? _projectScope, _tripScope;

    public bool IsCustomPeriod => Period.Value == FinancePeriod.Custom;
    public bool HasFilters => Filter.Length > 0 || Period.Value != null || Account?.Id != null || Category?.Id is not null and not -1 || TypeFilter.Value != null || ReviewOnly || Scope != null
                              || Category?.Id == -1;

    partial void OnFilterChanged(string value) => Reload();
    partial void OnPeriodChanged(Option<FinancePeriod?> value) { OnPropertyChanged(nameof(IsCustomPeriod)); Reload(); }
    partial void OnFromTextChanged(string value) { if (IsCustomPeriod) Reload(); }
    partial void OnToTextChanged(string value) { if (IsCustomPeriod) Reload(); }
    partial void OnAccountChanged(FinanceOption? value) => Reload();
    partial void OnCategoryChanged(FinanceOption? value) => Reload();
    partial void OnTypeFilterChanged(Option<TransactionType?> value) => Reload();
    partial void OnReviewOnlyChanged(bool value) => Reload();
    partial void OnSortChanged(TransactionSort value) => Reload();

    private void Reload()
    {
        OnPropertyChanged(nameof(HasFilters));
        if (!_quiet) _ = RefreshAsync();
    }

    /// <summary>The filters as a query (also used by Export).</summary>
    public TransactionQuery CurrentQuery()
    {
        DateOnly? from = null, to = null;
        if (Period.Value is { } p)
        {
            DateText.TryParse(FromText, Ctx.Loc.Culture, out var cf);
            DateText.TryParse(ToText, Ctx.Loc.Culture, out var ct);
            (from, to) = FinancePeriods.Range(p, Ctx.Today, cf, ct);
        }
        return new TransactionQuery(Filter.Trim().Length == 0 ? null : Filter.Trim(), from, to, Account?.Id, Category?.Id is { } c and > 0 ? c : null, TypeFilter.Value,
            _projectScope, _tripScope, ReviewOnly, Category?.Id == -1, Sort, 0, PageSize);
    }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        await LoadOptionsAsync(ct);
        var query = CurrentQuery();
        var page = await Ctx.Store.QueryTransactionsAsync(query, Ctx.Language, ct);
        var refs = page.Rows.SelectMany(r => new EntityRef?[]
        {
            r.ProjectId is { } p ? new EntityRef(EntityKind.Project, p) : null,
            r.TripId is { } t ? new EntityRef(EntityKind.Trip, t) : null,
        }).OfType<EntityRef>().Distinct().ToList();
        var chips = refs.Count == 0 ? new Dictionary<EntityRef, ContextChip>() : await ContextChip.ResolveAsync(Ctx.Organizer, refs);
        ct.ThrowIfCancellationRequested();
        var keep = Selected?.Id;
        Items.Clear();
        foreach (var r in page.Rows)
            Items.Add(new TransactionItem(r, Ctx, r.ProjectId is { } p ? chips.GetValueOrDefault(new EntityRef(EntityKind.Project, p)) : null,
                r.TripId is { } t ? chips.GetValueOrDefault(new EntityRef(EntityKind.Trip, t)) : null));
        Totals.Clear();
        foreach (var f in page.Totals)
            Totals.Add(new CurrencyTotal(f.Currency, Ctx.Loc.Format("Reg_TotalsFmt", Ctx.Format(f.Income, f.Currency), Ctx.Format(f.Expenses, f.Currency), Ctx.FormatSigned(f.Net, f.Currency)),
                Ctx.Loc.Format("Fin_TxnCount", f.Count), f.Net < 0 ? "Red" : null));
        CountText = page.TotalCount > page.Rows.Count ? Ctx.Loc.Format("Reg_CountLimited", page.Rows.Count, page.TotalCount) : Ctx.Loc.Format("Fin_TxnCount", page.TotalCount);
        IsEmpty = Items.Count == 0;
        EmptyText = Ctx.Loc[HasFilters ? "Reg_NoMatch" : "Reg_Empty"];
        Selected = Items.FirstOrDefault(i => i.Id == keep);
    }

    private async Task LoadOptionsAsync(CancellationToken ct)
    {
        var accounts = await Ctx.Store.GetAccountBalancesAsync(true, ct);
        var categories = await Ctx.Store.GetCategoriesAsync(Ctx.Language, true, ct);
        _quiet = true;
        try
        {
            var accountId = Account?.Id;
            var categoryId = Category?.Id;
            AccountOptions.Clear();
            AccountOptions.Add(new FinanceOption(null, Ctx.Loc["Exp_AllAccounts"], "Wallet"));
            foreach (var a in accounts) AccountOptions.Add(new FinanceOption(a.Account.Id, a.Account.Name, a.Account.Icon, a.Account.Accent, Currency: a.Account.Currency));
            CategoryOptions.Clear();
            CategoryOptions.Add(new FinanceOption(null, Ctx.Loc["Exp_AllCategories"]));
            CategoryOptions.Add(new FinanceOption(-1, Ctx.Loc["Fin_Uncategorized"]));
            foreach (var c in categories) CategoryOptions.Add(new FinanceOption(c.Id, c.ParentId != null ? "    " + c.Name : c.Name, c.Icon, c.Accent, c.ParentId != null));
            Account = AccountOptions.FirstOrDefault(o => o.Id == accountId) ?? AccountOptions[0];
            Category = CategoryOptions.FirstOrDefault(o => o.Id == categoryId) ?? CategoryOptions[0];
        }
        finally { _quiet = false; }
    }

    /// <summary>Replaces the filters (from accounts, budgets, reports, trips and projects) and loads.</summary>
    public async Task ApplyAsync(TransactionQuery q)
    {
        await Ctx.Ready;
        await LoadOptionsAsync(CancellationToken.None);
        _quiet = true;
        try
        {
            Filter = q.Text ?? string.Empty;
            if (q.From != null || q.To != null)
            {
                Period = PeriodOptions.First(o => o.Value == FinancePeriod.Custom);
                FromText = DateText.Format(q.From, Ctx.Loc.Culture);
                ToText = DateText.Format(q.To, Ctx.Loc.Culture);
            }
            else Period = PeriodOptions[0];
            Account = AccountOptions.FirstOrDefault(o => o.Id == q.AccountId) ?? AccountOptions[0];
            Category = q.UncategorizedOnly ? CategoryOptions.First(o => o.Id == -1) : CategoryOptions.FirstOrDefault(o => o.Id == q.CategoryId) ?? CategoryOptions[0];
            TypeFilter = TypeOptions.FirstOrDefault(o => o.Value == q.Type) ?? TypeOptions[0];
            ReviewOnly = q.NeedsReviewOnly;
            _projectScope = q.ProjectId;
            _tripScope = q.TripId;
            var scopeRef = q.ProjectId is { } p ? new EntityRef(EntityKind.Project, p) : q.TripId is { } t ? new EntityRef(EntityKind.Trip, t) : (EntityRef?)null;
            Scope = scopeRef is { } r ? (await ContextChip.ResolveAsync(Ctx.Organizer, [r])).GetValueOrDefault(r) : null;
        }
        finally { _quiet = false; }
        OnPropertyChanged(nameof(HasFilters));
        await RefreshAsync();
    }

    /// <summary>Shows one transaction (search result, link): clears filters, selects it and opens it.</summary>
    public async Task ShowTransactionAsync(int id)
    {
        await ApplyAsync(new TransactionQuery());
        Selected = Items.FirstOrDefault(i => i.Id == id);
        if (Selected != null) SelectRequested?.Invoke(this, EventArgs.Empty);
        await Actions.EditTransactionAsync(id);
    }

    public event EventHandler? SelectRequested;

    [RelayCommand] private Task ClearFiltersAsync() => ApplyAsync(new TransactionQuery());
    [RelayCommand] private Task ClearScopeAsync() { _projectScope = null; _tripScope = null; Scope = null; OnPropertyChanged(nameof(HasFilters)); return RefreshAsync(); }

    /// <summary>A column header click: sort by it, a second click reverses.</summary>
    [RelayCommand]
    private void SortBy(string? column)
    {
        Sort = column switch
        {
            "date" => Sort == TransactionSort.DateDesc ? TransactionSort.DateAsc : TransactionSort.DateDesc,
            "amount" => Sort == TransactionSort.AmountDesc ? TransactionSort.AmountAsc : TransactionSort.AmountDesc,
            "description" => TransactionSort.Description,
            "category" => TransactionSort.Category,
            "account" => TransactionSort.Account,
            _ => TransactionSort.DateDesc,
        };
    }

    [RelayCommand] private Task NewAsync() => Actions.NewTransactionAsync(TransactionType.Expense, Prefill(TransactionType.Expense));
    [RelayCommand] private Task NewIncomeAsync() => Actions.NewTransactionAsync(TransactionType.Income, Prefill(TransactionType.Income));

    /// <summary>New transactions inherit the register's account, category, project or trip filter.</summary>
    private TransactionDraft Prefill(TransactionType type) => new(type, Ctx.Today, 0m, Account?.Id ?? Ctx.Prefs.DefaultAccountId ?? 0,
        Category?.Id is { } c and > 0 ? c : null, ProjectId: _projectScope, TripId: _tripScope);

    [RelayCommand] private Task EditAsync(TransactionItem? t) => (t ?? Selected) is { } it ? Actions.EditTransactionAsync(it.Id) : Task.CompletedTask;
    [RelayCommand] private Task DuplicateAsync(TransactionItem? t) => (t ?? Selected) is { } it ? Actions.DuplicateTransactionAsync(it.Id) : Task.CompletedTask;
    [RelayCommand] private Task AttachReceiptAsync(TransactionItem? t) => (t ?? Selected) is { } it ? Actions.AttachReceiptAsync(it.Id) : Task.CompletedTask;
    [RelayCommand] private Task DeleteAsync(TransactionItem? t) => (t ?? Selected) is { } it ? Actions.DeleteTransactionAsync(it.Id, it.Title) : Task.CompletedTask;
    [RelayCommand] private Task ExportAsync() => Actions.ExportAsync(CurrentQuery());
    [RelayCommand] private Task ImportAsync() => Actions.ImportAsync();

    [RelayCommand]
    private async Task ToggleReviewAsync(TransactionItem? t)
    {
        t ??= Selected; if (t == null) return;
        if (await Ctx.TryAsync(() => Ctx.Store.SetNeedsReviewAsync(t.Id, !t.NeedsReview), "Fin_ErrSave")) Ctx.Changed();
    }
}
