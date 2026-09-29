using Arcadia.App.Dialogs;
using System.Collections.ObjectModel;
using Arcadia.App.Services;
using Arcadia.App.ViewModels.Organizer;
using Arcadia.App.Views.Finance;
using Arcadia.Core.Finance;
using Arcadia.Core.Organizer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.ViewModels.Finance;

/// <summary>
/// The Finance landing screen: balances per currency, this month in one metric strip, budgets, bills, recent
/// transactions, spending by category and the six-month trend. Amounts never mix currencies: a currency switch picks
/// which one the month figures describe.
/// </summary>
public sealed partial class FinanceOverviewViewModel(FinanceActions actions) : FinanceSectionViewModel(actions)
{
    public ObservableCollection<CurrencyTotal> Balances { get; } = [];
    public ObservableCollection<AccountItem> AccountRows { get; } = [];
    public ObservableCollection<MetricCell> Metrics { get; } = [];
    public ObservableCollection<BudgetItem> BudgetRows { get; } = [];
    public ObservableCollection<BillItem> Bills { get; } = [];
    public ObservableCollection<TransactionItem> Recent { get; } = [];
    public ObservableCollection<CategoryBar> CategoryBars { get; } = [];
    public ObservableCollection<string> Currencies { get; } = [];

    [ObservableProperty] private string? _currency;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _trendSeries;
    [ObservableProperty] private IReadOnlyList<string>? _trendLabels;
    [ObservableProperty] private Func<double, string>? _chartFormatter;
    [ObservableProperty] private Func<double, string>? _axisFormatter;
    [ObservableProperty] private string _monthTitle = string.Empty;
    [ObservableProperty] private bool _hasBudgets;
    [ObservableProperty] private bool _hasBills;
    [ObservableProperty] private bool _hasRecent;
    [ObservableProperty] private bool _hasSpending;
    [ObservableProperty] private bool _hasSeveralCurrencies;
    [ObservableProperty] private string? _spendingTotal;

    private bool _quiet;

    partial void OnCurrencyChanged(string? value)
    {
        if (!_quiet && value != null) _ = RefreshAsync();
    }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        var today = Ctx.Today;
        var month = FinanceMath.MonthStart(today);
        MonthTitle = FinanceText.Month(month, Ctx.Loc.Culture);
        var balances = await Ctx.Store.GetAccountBalancesAsync(false, ct);
        var currencies = balances.Select(b => b.Account.Currency).Distinct().OrderByDescending(c => c == Ctx.Prefs.DefaultCurrency).ThenBy(c => c).ToList();
        _quiet = true;
        try
        {
            if (!currencies.SequenceEqual(Currencies)) { Currencies.Clear(); foreach (var c in currencies) Currencies.Add(c); }
            if (Currency == null || !currencies.Contains(Currency)) Currency = currencies.FirstOrDefault() ?? Ctx.Prefs.DefaultCurrency;
        }
        finally { _quiet = false; }
        HasSeveralCurrencies = currencies.Count > 1;
        var cur = Currency!;

        Balances.Clear();
        foreach (var g in balances.GroupBy(b => b.Account.Currency).OrderByDescending(g => g.Key == cur))
        {
            var total = g.Sum(b => b.Balance);
            Balances.Add(new CurrencyTotal(g.Key, Ctx.Format(total, g.Key), Ctx.Loc.Format("Fin_AccountsCount", g.Count()), total < 0 ? "Red" : null));
        }
        AccountRows.Clear();
        foreach (var b in balances.Where(b => b.Account.Currency == cur)) AccountRows.Add(new AccountItem(b, Ctx));

        // this month, in the chosen currency
        var flows = await Ctx.Store.GetMonthlyFlowsAsync(month.AddMonths(-5), month, ct);
        var mine = flows.Where(f => f.Currency == cur).OrderBy(f => f.Month).ToList();
        var now = mine.LastOrDefault() ?? new MonthFlow(month, cur, 0, 0, 0, 0);
        var balance = balances.Where(b => b.Account.Currency == cur).Sum(b => b.Balance);
        Metrics.Clear();
        Metrics.Add(new MetricCell(Ctx.Loc["Fin_CurrentBalance"], Ctx.Format(balance, cur), balance < 0 ? "Red" : null, Ctx.Loc.Format("Fin_AccountsCount", AccountRows.Count), "Wallet"));
        Metrics.Add(new MetricCell(Ctx.Loc["Fin_IncomeMonth"], Ctx.Format(now.Income, cur), "Green", null, "Income"));
        Metrics.Add(new MetricCell(Ctx.Loc["Fin_ExpensesMonth"], Ctx.Format(now.Expenses, cur), null, null, "Expense"));
        Metrics.Add(new MetricCell(Ctx.Loc["Fin_NetMonth"], Ctx.FormatSigned(now.Net, cur), now.Net < 0 ? "Red" : "Cyan", null, "Chart"));
        Metrics.Add(new MetricCell(Ctx.Loc["Fin_SavedMonth"], Ctx.Format(now.Saved, cur), "Cyan", null, "Target"));

        var categories = (await Ctx.Store.GetCategoriesAsync(Ctx.Language, true, ct)).ToDictionary(c => c.Id);
        var budgets = await Ctx.Store.GetBudgetStatusAsync(today, ct);
        BudgetRows.Clear();
        foreach (var b in budgets.Where(b => b.Budget.Currency == cur).Take(5)) BudgetRows.Add(new BudgetItem(b, Ctx, categories));
        HasBudgets = BudgetRows.Count > 0;

        var bills = await Ctx.Store.GetUpcomingBillsAsync(today, today.AddDays(14), Ctx.Language, 60, ct);
        Bills.Clear();
        foreach (var b in bills.Take(6)) Bills.Add(new BillItem(b, Ctx));
        HasBills = Bills.Count > 0;

        var recent = await Ctx.Store.QueryTransactionsAsync(new TransactionQuery(Take: 8), Ctx.Language, ct);
        Recent.Clear();
        foreach (var r in recent.Rows) Recent.Add(new TransactionItem(r, Ctx, null, null));
        HasRecent = Recent.Count > 0;

        var spending = await Ctx.Store.GetCategorySpendingAsync(month, FinanceMath.MonthEnd(month), Ctx.Language, cur, true, ct);
        CategoryBars.Clear();
        foreach (var bar in FinanceText.Bars(spending, Ctx, 6)) CategoryBars.Add(bar);
        HasSpending = CategoryBars.Count > 0;
        SpendingTotal = HasSpending ? Ctx.Format(spending.Sum(s => s.Amount), cur) : null;

        ChartFormatter = v => Ctx.Format((decimal)v, cur);
        AxisFormatter = v => Ctx.Money.FormatCompact((decimal)v, cur);
        TrendLabels = mine.Select(m => m.Month.ToString("MMM", Ctx.Loc.Culture)).ToList();
        TrendSeries =
        [
            new ChartSeries(Ctx.Loc["Fin_Income"], mine.Select(m => (double)m.Income).ToList(), "Green"),
            new ChartSeries(Ctx.Loc["Fin_Expenses"], mine.Select(m => (double)m.Expenses).ToList(), "Blue"),
            new ChartSeries(Ctx.Loc["Fin_Net"], mine.Select(m => (double)m.Net).ToList(), "Cyan", IsLine: true),
        ];
    }

    [RelayCommand] private Task NewTransactionAsync() => Actions.NewTransactionAsync();
    [RelayCommand] private Task EditTransactionAsync(TransactionItem? item) => item == null ? Task.CompletedTask : Actions.EditTransactionAsync(item.Id);
    [RelayCommand] private Task MarkPaidAsync(BillItem? bill) => bill == null ? Task.CompletedTask : Actions.MarkPaidAsync(bill.ItemId, bill.Date);
    [RelayCommand] private Task ShowAccountAsync(AccountItem? a) => a == null || ShowRegister == null ? Task.CompletedTask : ShowRegister(new TransactionQuery(AccountId: a.Id));
    [RelayCommand] private void SetCurrency(string? c) { if (c != null) Currency = c; }

    [RelayCommand]
    private Task ShowCategoryAsync(CategoryBar? bar)
    {
        if (bar == null || ShowRegister == null) return Task.CompletedTask;
        var month = FinanceMath.MonthStart(Ctx.Today);
        return ShowRegister(new TransactionQuery(From: month, To: FinanceMath.MonthEnd(month), CategoryId: bar.CategoryId, UncategorizedOnly: bar.CategoryId == null, Type: TransactionType.Expense));
    }
}

public sealed partial class FinanceAccountsViewModel(FinanceActions actions) : FinanceSectionViewModel(actions)
{
    public ObservableCollection<AccountItem> Items { get; } = [];
    public ObservableCollection<CurrencyTotal> Totals { get; } = [];
    [ObservableProperty] private AccountItem? _selected;
    [ObservableProperty] private bool _showArchived;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private int? _defaultAccountId;

    partial void OnShowArchivedChanged(bool value) => _ = RefreshAsync();

    protected override async Task LoadAsync(CancellationToken ct)
    {
        var balances = await Ctx.Store.GetAccountBalancesAsync(true, ct);
        var keep = Selected?.Id;
        Items.Clear();
        foreach (var b in balances.Where(b => ShowArchived || !b.Account.IsArchived)) Items.Add(new AccountItem(b, Ctx));
        Totals.Clear();
        foreach (var g in balances.Where(b => !b.Account.IsArchived).GroupBy(b => b.Account.Currency).OrderBy(g => g.Key))
        {
            var total = g.Sum(b => b.Balance);
            Totals.Add(new CurrencyTotal(g.Key, Ctx.Format(total, g.Key), Ctx.Loc.Format("Fin_AccountsCount", g.Count()), total < 0 ? "Red" : null));
        }
        IsEmpty = Items.Count == 0;
        DefaultAccountId = Ctx.Prefs.DefaultAccountId;
        Selected = Items.FirstOrDefault(i => i.Id == keep);
    }

    [RelayCommand] private async Task NewAsync() { if (await Actions.NewAccountAsync() != null) await RefreshAsync(); }
    [RelayCommand] private async Task EditAsync(AccountItem? a) { a ??= Selected; if (a != null && await Actions.EditAccountAsync(a.Id)) await RefreshAsync(); }
    [RelayCommand] private Task ShowTransactionsAsync(AccountItem? a) => (a ?? Selected) is { } it && ShowRegister != null ? ShowRegister(new TransactionQuery(AccountId: it.Id)) : Task.CompletedTask;

    [RelayCommand]
    private Task NewTransactionInAsync(AccountItem? a) => (a ?? Selected) is { } it
        ? Actions.NewTransactionAsync(TransactionType.Expense, new TransactionDraft(TransactionType.Expense, Ctx.Today, 0m, it.Id)) : Task.CompletedTask;

    [RelayCommand]
    private void SetDefault(AccountItem? a)
    {
        a ??= Selected; if (a == null) return;
        Ctx.Prefs.DefaultAccountId = a.Id;
        Ctx.SavePrefs();
        DefaultAccountId = a.Id;
        Ctx.Notifications.Show(Ctx.Loc.Format("Acct_DefaultSet", a.Name), NotificationKind.Success);
    }

    [RelayCommand]
    private async Task ToggleArchiveAsync(AccountItem? a)
    {
        a ??= Selected; if (a == null) return;
        if (await Ctx.TryAsync(() => Ctx.Store.SetAccountArchivedAsync(a.Id, !a.IsArchived), "Fin_ErrSave")) Ctx.Changed();
    }

    [RelayCommand]
    private async Task DeleteAsync(AccountItem? a)
    {
        a ??= Selected; if (a == null) return;
        if (a.Balance.TransactionCount > 0) { Ctx.Notifications.Show(Ctx.Loc["Fin_ErrAccountInUse"], NotificationKind.Warning); return; }
        if (!Ctx.Dialogs.Confirm(Ctx.Loc.Format("Acct_ConfirmDelete", a.Name), Ctx.Loc["Confirm_Delete"])) return;
        if (await Ctx.TryAsync(() => Ctx.Store.DeleteAccountAsync(a.Id), "Fin_ErrSave")) Ctx.Changed();
    }
}

public sealed partial class FinanceBudgetsViewModel(FinanceActions actions) : FinanceSectionViewModel(actions)
{
    public ObservableCollection<BudgetItem> Monthly { get; } = [];
    public ObservableCollection<BudgetItem> Ranges { get; } = [];
    public ObservableCollection<CurrencyTotal> Totals { get; } = [];
    [ObservableProperty] private DateOnly _month;
    [ObservableProperty] private string _monthTitle = string.Empty;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _hasRanges;
    [ObservableProperty] private BudgetItem? _selected;

    protected override async Task LoadAsync(CancellationToken ct)
    {
        if (Month == default) Month = FinanceMath.MonthStart(Ctx.Today);
        MonthTitle = FinanceText.Month(Month, Ctx.Loc.Culture);
        var categories = (await Ctx.Store.GetCategoriesAsync(Ctx.Language, true, ct)).ToDictionary(c => c.Id);
        var status = await Ctx.Store.GetBudgetStatusAsync(Month, ct);
        Monthly.Clear();
        Ranges.Clear();
        foreach (var s in status) (s.Budget.Period == BudgetPeriod.Monthly ? Monthly : Ranges).Add(new BudgetItem(s, Ctx, categories));
        Totals.Clear();
        foreach (var g in status.Where(s => s.Budget.Period == BudgetPeriod.Monthly).GroupBy(s => s.Budget.Currency))
            Totals.Add(new CurrencyTotal(g.Key, Ctx.Loc.Format("Fin_BudgetOfFmt", Ctx.Format(g.Sum(s => s.Spent), g.Key), Ctx.Format(g.Sum(s => s.Available), g.Key)),
                Ctx.Loc.Format("Bud_OverCount", g.Count(s => s.Level == BudgetLevel.Over), g.Count()), g.Any(s => s.Level == BudgetLevel.Over) ? "Red" : null));
        IsEmpty = Monthly.Count == 0 && Ranges.Count == 0;
        HasRanges = Ranges.Count > 0;
    }

    [RelayCommand] private async Task PreviousAsync() { Month = Month.AddMonths(-1); await RefreshAsync(); }
    [RelayCommand] private async Task NextAsync() { Month = Month.AddMonths(1); await RefreshAsync(); }
    [RelayCommand] private async Task ThisMonthAsync() { Month = FinanceMath.MonthStart(Ctx.Today); await RefreshAsync(); }
    [RelayCommand] private Task NewAsync() => Actions.NewBudgetAsync(start: Month);
    [RelayCommand] private Task EditAsync(BudgetItem? b) => (b ?? Selected) is { } it ? Actions.EditBudgetAsync(it.Id) : Task.CompletedTask;
    [RelayCommand] private Task ManageCategoriesAsync() => Actions.ManageCategoriesAsync();

    [RelayCommand]
    private Task ShowTransactionsAsync(BudgetItem? b)
    {
        b ??= Selected; if (b == null || ShowRegister == null) return Task.CompletedTask;
        var s = b.Status;
        return ShowRegister(new TransactionQuery(From: s.PeriodStart, To: s.PeriodEnd, CategoryId: s.Budget.CategoryId, Type: TransactionType.Expense));
    }
}

/// <summary>Bills, subscriptions and income: what is due (mark paid, skip, edit) and the tracker with yearly costs.</summary>
public sealed partial class FinanceRecurringViewModel(FinanceActions actions) : FinanceSectionViewModel(actions)
{
    public ObservableCollection<object> Upcoming { get; } = [];
    public ObservableCollection<SubscriptionItem> Items { get; } = [];
    public ObservableCollection<CurrencyTotal> Totals { get; } = [];
    public IReadOnlyList<Option<RecurringKind?>> Tabs { get; } =
    [
        new(RecurringKind.Subscription, actions.Ctx.Loc["Rec_Subscriptions"]), new(RecurringKind.Bill, actions.Ctx.Loc["Rec_Bills"]),
        new(RecurringKind.Income, actions.Ctx.Loc["Rec_Income"]), new(null, actions.Ctx.Loc["Rec_All"]),
    ];

    [ObservableProperty] private Option<RecurringKind?>? _tab;
    [ObservableProperty] private SubscriptionItem? _selected;
    [ObservableProperty] private bool _hasUpcoming;
    [ObservableProperty] private bool _isEmpty;
    private int? _highlight;

    partial void OnTabChanged(Option<RecurringKind?>? value) { if (value != null) _ = RefreshAsync(); }

    [RelayCommand] private void SetTab(Option<RecurringKind?>? tab) { if (tab != null) Tab = tab; }

    public void Highlight(int itemId)
    {
        _highlight = itemId;
        Selected = Items.FirstOrDefault(i => i.Id == itemId);
        if (Selected == null) { Tab = Tabs[3]; }
    }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        Tab ??= Tabs[0];
        var today = Ctx.Today;
        var bills = await Ctx.Store.GetUpcomingBillsAsync(today, today.AddDays(45), Ctx.Language, 60, ct);
        Upcoming.Clear();
        string? group = null;
        foreach (var b in bills)
        {
            var item = new BillItem(b, Ctx);
            var heading = b.IsOverdue ? Ctx.Loc["Rec_Overdue"] : OrgText.Day(b.DueDate, today, Ctx.Loc);
            if (heading != group) { Upcoming.Add(new FinanceGroupHeader(heading, b.IsOverdue ? "Red" : null)); group = heading; }
            Upcoming.Add(item);
        }
        HasUpcoming = bills.Count > 0;

        var rows = await Ctx.Store.GetSubscriptionsAsync(today, Ctx.Language, Tab.Value, true, ct);
        Items.Clear();
        foreach (var r in rows) Items.Add(new SubscriptionItem(r, Ctx));
        IsEmpty = Items.Count == 0;
        Totals.Clear();
        foreach (var g in rows.Where(r => r.Item.IsActive && r.Item.Kind != RecurringKind.Income).GroupBy(r => r.Item.Currency))
            Totals.Add(new CurrencyTotal(g.Key, Ctx.Loc.Format("Rec_TotalsFmt", Ctx.Format(g.Sum(r => r.MonthlyEquivalent), g.Key), Ctx.Format(g.Sum(r => r.AnnualCost), g.Key)),
                Ctx.Loc.Format("Rec_ActiveCount", g.Count()), null));
        foreach (var g in rows.Where(r => r.Item.IsActive && r.Item.Kind == RecurringKind.Income).GroupBy(r => r.Item.Currency))
            Totals.Add(new CurrencyTotal(g.Key, Ctx.Loc.Format("Rec_IncomeTotalsFmt", Ctx.Format(g.Sum(r => r.MonthlyEquivalent), g.Key), Ctx.Format(g.Sum(r => r.AnnualCost), g.Key)),
                Ctx.Loc.Format("Rec_ActiveCount", g.Count()), "Green"));
        if (_highlight is { } h) { Selected = Items.FirstOrDefault(i => i.Id == h); _highlight = null; }
    }

    [RelayCommand] private Task MarkPaidAsync(BillItem? b) => b == null ? Task.CompletedTask : Actions.MarkPaidAsync(b.ItemId, b.Date);
    [RelayCommand] private Task MarkPaidWithOptionsAsync(BillItem? b) => b == null ? Task.CompletedTask : Actions.MarkPaidAsync(b.ItemId, b.Date, ask: true);
    [RelayCommand] private Task SkipAsync(BillItem? b) => b == null ? Task.CompletedTask : Actions.SkipAsync(b.ItemId, b.Date);
    [RelayCommand] private Task EditBillAsync(BillItem? b) => b == null ? Task.CompletedTask : Actions.EditRecurringAsync(b.ItemId);
    [RelayCommand] private Task EditAsync(SubscriptionItem? s) => (s ?? Selected) is { } it ? Actions.EditRecurringAsync(it.Id) : Task.CompletedTask;
    [RelayCommand] private Task NewAsync() => Actions.NewRecurringAsync(Tab?.Value ?? RecurringKind.Bill);

    [RelayCommand]
    private async Task ToggleActiveAsync(SubscriptionItem? s)
    {
        s ??= Selected; if (s == null) return;
        var i = s.Row.Item;
        var draft = new RecurringDraft(i.Name, i.Kind, i.Amount, i.Currency, i.Frequency, i.Interval, i.StartDate, i.EndDate, i.AccountId, i.CategoryId, i.Payee, !i.IsActive, i.ShowOnCalendar, i.Notes);
        if (await Ctx.TryAsync(() => Ctx.Store.UpdateRecurringItemAsync(i.Id, draft), "Fin_ErrSave")) Ctx.Changed();
    }

    [RelayCommand]
    private async Task DeleteAsync(SubscriptionItem? s)
    {
        s ??= Selected; if (s == null || !Ctx.Dialogs.Confirm(Ctx.Loc.Format("Rec_ConfirmDelete", s.Name), Ctx.Loc["Confirm_Delete"])) return;
        if (await Ctx.TryAsync(() => Ctx.Store.DeleteRecurringItemAsync(s.Id), "Fin_ErrSave")) Ctx.Changed();
    }
}

public sealed partial class FinanceGoalsViewModel(FinanceActions actions) : FinanceSectionViewModel(actions)
{
    public ObservableCollection<GoalItem> Items { get; } = [];
    [ObservableProperty] private bool _showArchived;
    [ObservableProperty] private bool _isEmpty;

    partial void OnShowArchivedChanged(bool value) => _ = RefreshAsync();

    protected override async Task LoadAsync(CancellationToken ct)
    {
        var goals = await Ctx.Store.GetSavingsGoalsAsync(Ctx.Today, ShowArchived, ct);
        Items.Clear();
        foreach (var g in goals) Items.Add(new GoalItem(g, Ctx));
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand] private Task NewAsync() => Actions.NewGoalAsync();
    [RelayCommand] private Task EditAsync(GoalItem? g) => g == null ? Task.CompletedTask : Actions.EditGoalAsync(g.Id);
    [RelayCommand] private Task AddMoneyAsync(GoalItem? g) => g == null ? Task.CompletedTask : Actions.AddToGoalAsync(g);

    [RelayCommand]
    private async Task ToggleArchiveAsync(GoalItem? g)
    {
        if (g == null) return;
        if (await Ctx.TryAsync(() => Ctx.Store.SetGoalArchivedAsync(g.Id, !g.Progress.Goal.IsArchived), "Fin_ErrSave")) Ctx.Changed();
    }
}
