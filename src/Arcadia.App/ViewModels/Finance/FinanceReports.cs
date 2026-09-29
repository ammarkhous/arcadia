using System.Collections.ObjectModel;
using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.App.ViewModels.Organizer;
using Arcadia.App.Views.Finance;
using Arcadia.Core.Finance;
using Arcadia.Core.Organizer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.ViewModels.Finance;

public sealed record BudgetPerformanceRow(string Name, string Icon, string Accent, string Budgeted, string Spent, string Difference, string? DifferenceTone, string MonthsText)
{
    public override string ToString() => Name;
}

/// <summary>
/// Reports for a period (this month … last year, or custom) in one currency: income vs expenses, spending by category,
/// net and savings over time, budget performance, subscription costs and account balances at each month's end.
/// </summary>
public sealed partial class FinanceReportsViewModel : FinanceSectionViewModel
{
    public FinanceReportsViewModel(FinanceActions actions) : base(actions)
    {
        PeriodOptions = Enum.GetValues<FinancePeriod>().Select(p => new Option<FinancePeriod>(p, actions.Ctx.Loc["FinPeriod_" + p])).ToList();
        _period = PeriodOptions.First(p => p.Value == FinancePeriod.Last6Months);
        _fromText = DateText.Format(new DateOnly(actions.Ctx.Today.Year, 1, 1), actions.Ctx.Loc.Culture);
        _toText = DateText.Format(actions.Ctx.Today, actions.Ctx.Loc.Culture);
    }

    public IReadOnlyList<Option<FinancePeriod>> PeriodOptions { get; }
    public ObservableCollection<string> Currencies { get; } = [];
    public ObservableCollection<CategoryBar> CategoryBars { get; } = [];
    public ObservableCollection<MetricCell> Summary { get; } = [];
    public ObservableCollection<BudgetPerformanceRow> BudgetRows { get; } = [];
    public ObservableCollection<SubscriptionItem> Subscriptions { get; } = [];
    public ObservableCollection<ChartLegend> BalanceLegend { get; } = [];

    [ObservableProperty] private Option<FinancePeriod> _period;
    [ObservableProperty] private string _fromText;
    [ObservableProperty] private string _toText;
    [ObservableProperty] private string? _currency;
    [ObservableProperty] private string _rangeText = string.Empty;
    [ObservableProperty] private IReadOnlyList<string>? _monthLabels;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _flowSeries;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _netSeries;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _balanceSeries;
    [ObservableProperty] private Func<double, string>? _chartFormatter;
    [ObservableProperty] private Func<double, string>? _axisFormatter;
    [ObservableProperty] private string? _subscriptionTotal;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _hasBudgets;
    [ObservableProperty] private bool _hasSubscriptions;
    private bool _quiet;

    public bool IsCustom => Period.Value == FinancePeriod.Custom;

    partial void OnPeriodChanged(Option<FinancePeriod> value) { OnPropertyChanged(nameof(IsCustom)); if (!_quiet) _ = RefreshAsync(); }
    partial void OnCurrencyChanged(string? value) { if (!_quiet && value != null) _ = RefreshAsync(); }
    [RelayCommand] private Task ApplyCustomAsync() => RefreshAsync();
    [RelayCommand] private void SetPeriod(Option<FinancePeriod>? p) { if (p != null) Period = p; }
    [RelayCommand] private void SetCurrency(string? c) { if (c != null) Currency = c; }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        DateText.TryParse(FromText, Ctx.Loc.Culture, out var cf);
        DateText.TryParse(ToText, Ctx.Loc.Culture, out var ctd);
        var (from, to) = FinancePeriods.Range(Period.Value, Ctx.Today, cf, ctd);
        RangeText = $"{from.ToString("d MMM yyyy", Ctx.Loc.Culture)} – {to.ToString("d MMM yyyy", Ctx.Loc.Culture)}";
        var accounts = await Ctx.Store.GetAccountBalancesAsync(true, ct);
        var currencies = accounts.Select(a => a.Account.Currency).Distinct().OrderByDescending(c => c == Ctx.Prefs.DefaultCurrency).ThenBy(c => c).ToList();
        _quiet = true;
        try
        {
            if (!currencies.SequenceEqual(Currencies)) { Currencies.Clear(); foreach (var c in currencies) Currencies.Add(c); }
            if (Currency == null || !currencies.Contains(Currency)) Currency = currencies.FirstOrDefault() ?? Ctx.Prefs.DefaultCurrency;
        }
        finally { _quiet = false; }
        var cur = Currency!;
        ChartFormatter = v => Ctx.Format((decimal)v, cur);
        AxisFormatter = v => Ctx.Money.FormatCompact((decimal)v, cur);

        // months shown (at most five years of columns)
        var firstMonth = FinanceMath.MonthStart(from);
        var lastMonth = FinanceMath.MonthStart(to);
        if (FinanceMath.MonthIndex(lastMonth) - FinanceMath.MonthIndex(firstMonth) > 59) firstMonth = lastMonth.AddMonths(-59);
        var flows = (await Ctx.Store.GetMonthlyFlowsAsync(firstMonth, lastMonth, ct)).Where(f => f.Currency == cur).OrderBy(f => f.Month).ToList();
        var multiYear = firstMonth.Year != lastMonth.Year;
        MonthLabels = flows.Select(f => f.Month.ToString(multiYear ? "MMM yy" : "MMM", Ctx.Loc.Culture)).ToList();
        FlowSeries =
        [
            new ChartSeries(Ctx.Loc["Fin_Income"], flows.Select(f => (double)f.Income).ToList(), "Green"),
            new ChartSeries(Ctx.Loc["Fin_Expenses"], flows.Select(f => (double)f.Expenses).ToList(), "Blue"),
        ];
        NetSeries =
        [
            new ChartSeries(Ctx.Loc["Fin_Net"], flows.Select(f => (double)f.Net).ToList(), "Cyan", IsLine: true),
            new ChartSeries(Ctx.Loc["Fin_Saved"], flows.Select(f => (double)f.Saved).ToList(), "Gold", IsLine: true),
        ];

        // exact period totals (days, not whole months)
        var page = await Ctx.Store.QueryTransactionsAsync(new TransactionQuery(From: from, To: to, Take: 1), Ctx.Language, ct);
        var totals = page.Totals.FirstOrDefault(t => t.Currency == cur) ?? new CurrencyFlow(cur, 0, 0, 0);
        var saved = flows.Sum(f => f.Saved);
        Summary.Clear();
        Summary.Add(new MetricCell(Ctx.Loc["Fin_Income"], Ctx.Format(totals.Income, cur), "Green", null, "Income"));
        Summary.Add(new MetricCell(Ctx.Loc["Fin_Expenses"], Ctx.Format(totals.Expenses, cur), null, null, "Expense"));
        Summary.Add(new MetricCell(Ctx.Loc["Fin_Net"], Ctx.FormatSigned(totals.Net, cur), totals.Net < 0 ? "Red" : "Cyan", null, "Chart"));
        Summary.Add(new MetricCell(Ctx.Loc["Fin_Saved"], Ctx.Format(saved, cur), "Cyan", Ctx.Loc["Rep_SavedHint"], "Target"));
        Summary.Add(new MetricCell(Ctx.Loc["Rep_Transactions"], totals.Count.ToString("N0", Ctx.Loc.Culture), null, null, "Receipt"));
        HasData = totals.Count > 0 || flows.Any(f => f.Count > 0);

        var spending = await Ctx.Store.GetCategorySpendingAsync(from, to, Ctx.Language, cur, true, ct);
        CategoryBars.Clear();
        foreach (var b in FinanceText.Bars(spending, Ctx, 12)) CategoryBars.Add(b);

        // budget performance: each monthly budget, month by month through the period (rollover included)
        BudgetRows.Clear();
        var perBudget = new Dictionary<int, (Budget Budget, decimal Available, decimal Spent, int Months, int Over)>();
        var categories = (await Ctx.Store.GetCategoriesAsync(Ctx.Language, true, ct)).ToDictionary(c => c.Id);
        for (var m = firstMonth; m <= lastMonth && FinanceMath.MonthIndex(m) - FinanceMath.MonthIndex(firstMonth) < 36; m = m.AddMonths(1))
            foreach (var s in (await Ctx.Store.GetBudgetStatusAsync(m, ct)).Where(s => s.Budget.Period == BudgetPeriod.Monthly && s.Budget.Currency == cur))
            {
                perBudget.TryGetValue(s.Budget.Id, out var v);
                perBudget[s.Budget.Id] = (s.Budget, v.Available + s.Available, v.Spent + s.Spent, v.Months + 1, v.Over + (s.Level == BudgetLevel.Over ? 1 : 0));
            }
        foreach (var (budget, available, spent, months, over) in perBudget.Values.OrderByDescending(v => v.Spent))
        {
            var cat = budget.CategoryId is { } id && categories.TryGetValue(id, out var c) ? c : null;
            var diff = available - spent;
            BudgetRows.Add(new BudgetPerformanceRow(budget.Name ?? cat?.Name ?? Ctx.Loc["Fin_AllSpending"], cat?.Icon ?? "Finance", cat?.Accent ?? "Cyan", Ctx.Format(available, cur),
                Ctx.Format(spent, cur), Ctx.FormatSigned(diff, cur), diff < 0 ? "Red" : "Green", Ctx.Loc.Format("Rep_OverMonths", over, months)));
        }
        HasBudgets = BudgetRows.Count > 0;

        var subs = (await Ctx.Store.GetSubscriptionsAsync(Ctx.Today, Ctx.Language, RecurringKind.Subscription, false, ct)).Where(s => s.Item.Currency == cur).OrderByDescending(s => s.AnnualCost).ToList();
        Subscriptions.Clear();
        foreach (var s in subs) Subscriptions.Add(new SubscriptionItem(s, Ctx));
        HasSubscriptions = subs.Count > 0;
        SubscriptionTotal = HasSubscriptions ? Ctx.Loc.Format("Rec_TotalsFmt", Ctx.Format(subs.Sum(s => s.MonthlyEquivalent), cur), Ctx.Format(subs.Sum(s => s.AnnualCost), cur)) : null;

        var history = await Ctx.Store.GetBalanceHistoryAsync(firstMonth, lastMonth, cur, ct);
        BalanceSeries = history.Select(h => new ChartSeries(h.AccountName, h.Points.Select(p => (double)p.Balance).ToList(), h.Accent, IsLine: true)).ToList();
        BalanceLegend.Clear();
        foreach (var h in history) BalanceLegend.Add(new ChartLegend(h.AccountName, h.Accent));
    }

    [RelayCommand]
    private Task ShowCategoryAsync(CategoryBar? bar)
    {
        if (bar == null || ShowRegister == null) return Task.CompletedTask;
        DateText.TryParse(FromText, Ctx.Loc.Culture, out var cf);
        DateText.TryParse(ToText, Ctx.Loc.Culture, out var ctd);
        var (from, to) = FinancePeriods.Range(Period.Value, Ctx.Today, cf, ctd);
        return ShowRegister(new TransactionQuery(From: from, To: to, CategoryId: bar.CategoryId, UncategorizedOnly: bar.CategoryId == null, Type: TransactionType.Expense));
    }
}

public sealed record ChartLegend(string Label, string Tone)
{
    public override string ToString() => Label;
}

/// <summary>A month in plain figures and a year at a glance. Factual wording only; no forecasts.</summary>
public sealed partial class FinanceHistoryViewModel(FinanceActions actions) : FinanceSectionViewModel(actions)
{
    public ObservableCollection<string> Currencies { get; } = [];
    public ObservableCollection<MetricCell> MonthLines { get; } = [];
    public ObservableCollection<MonthRow> YearRows { get; } = [];
    public ObservableCollection<CategoryBar> YearCategories { get; } = [];

    [ObservableProperty] private DateOnly _month;
    [ObservableProperty] private int _year;
    [ObservableProperty] private string _monthTitle = string.Empty;
    [ObservableProperty] private string? _currency;
    [ObservableProperty] private IReadOnlyList<string>? _yearLabels;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _yearSeries;
    [ObservableProperty] private Func<double, string>? _chartFormatter;
    [ObservableProperty] private Func<double, string>? _axisFormatter;
    [ObservableProperty] private MonthRow? _yearTotal;
    [ObservableProperty] private bool _monthIsEmpty;
    private bool _quiet;

    partial void OnCurrencyChanged(string? value) { if (!_quiet && value != null) _ = RefreshAsync(); }
    [RelayCommand] private void SetCurrency(string? c) { if (c != null) Currency = c; }
    [RelayCommand] private async Task PreviousMonthAsync() { Month = Month.AddMonths(-1); Year = Month.Year; await RefreshAsync(); }
    [RelayCommand] private async Task NextMonthAsync() { Month = Month.AddMonths(1); Year = Month.Year; await RefreshAsync(); }
    [RelayCommand] private async Task PreviousYearAsync() { Year--; await RefreshAsync(); }
    [RelayCommand] private async Task NextYearAsync() { Year++; await RefreshAsync(); }

    protected override async Task LoadAsync(CancellationToken ct)
    {
        if (Month == default) { Month = FinanceMath.MonthStart(Ctx.Today); Year = Month.Year; }
        var c = Ctx.Loc.Culture;
        MonthTitle = Month.ToString("MMMM yyyy", c).ToUpper(c);
        var accounts = await Ctx.Store.GetAccountBalancesAsync(true, ct);
        var currencies = accounts.Select(a => a.Account.Currency).Distinct().OrderByDescending(x => x == Ctx.Prefs.DefaultCurrency).ThenBy(x => x).ToList();
        _quiet = true;
        try
        {
            if (!currencies.SequenceEqual(Currencies)) { Currencies.Clear(); foreach (var x in currencies) Currencies.Add(x); }
            if (Currency == null || !currencies.Contains(Currency)) Currency = currencies.FirstOrDefault() ?? Ctx.Prefs.DefaultCurrency;
        }
        finally { _quiet = false; }
        var cur = Currency!;

        var summary = (await Ctx.Store.GetMonthlySummaryAsync(Month.Year, Month.Month, Ctx.Language, ct)).FirstOrDefault(s => s.Currency == cur);
        MonthLines.Clear();
        MonthIsEmpty = summary == null || summary.TransactionCount == 0;
        var s = summary ?? new MonthSummary(Month, cur, 0, 0, 0, 0, null, 0, 0);
        MonthLines.Add(new MetricCell(Ctx.Loc["Fin_Income"], Ctx.Format(s.Income, cur), "Green"));
        MonthLines.Add(new MetricCell(Ctx.Loc["Fin_Expenses"], Ctx.Format(s.Expenses, cur)));
        MonthLines.Add(new MetricCell(Ctx.Loc["Fin_Net"], Ctx.FormatSigned(s.Net, cur), s.Net < 0 ? "Red" : "Cyan"));
        MonthLines.Add(new MetricCell(Ctx.Loc["Fin_Saved"], Ctx.Format(s.Saved, cur), "Cyan"));
        MonthLines.Add(new MetricCell(Ctx.Loc["Hist_Largest"], s.LargestCategory is { } l ? $"{Ctx.CategoryLabel(l.Name)}  ·  {Ctx.Format(l.Amount, cur)}" : "—"));
        MonthLines.Add(new MetricCell(Ctx.Loc["Rep_Transactions"], s.TransactionCount.ToString("N0", c)));
        MonthLines.Add(new MetricCell(Ctx.Loc["Hist_OverLimit"], s.BudgetCount == 0 ? "—" : Ctx.Loc.Format("Hist_OverLimitFmt", s.BudgetsOverLimit, s.BudgetCount), s.BudgetsOverLimit > 0 ? "Amber" : null));

        var first = new DateOnly(Year, 1, 1);
        var flows = (await Ctx.Store.GetMonthlyFlowsAsync(first, new DateOnly(Year, 12, 1), ct)).Where(f => f.Currency == cur).OrderBy(f => f.Month).ToList();
        YearRows.Clear();
        foreach (var f in flows)
            YearRows.Add(new MonthRow(f.Month.ToString("MMMM", c), Ctx.Format(f.Income, cur), Ctx.Format(f.Expenses, cur), Ctx.FormatSigned(f.Net, cur), f.Net < 0 ? "Red" : null,
                Ctx.Format(f.Saved, cur), f.Count.ToString("N0", c), f.Month == FinanceMath.MonthStart(Ctx.Today)));
        YearTotal = new MonthRow(Ctx.Loc["Hist_Total"], Ctx.Format(flows.Sum(f => f.Income), cur), Ctx.Format(flows.Sum(f => f.Expenses), cur), Ctx.FormatSigned(flows.Sum(f => f.Net), cur),
            flows.Sum(f => f.Net) < 0 ? "Red" : null, Ctx.Format(flows.Sum(f => f.Saved), cur), flows.Sum(f => f.Count).ToString("N0", c), false);
        ChartFormatter = v => Ctx.Format((decimal)v, cur);
        AxisFormatter = v => Ctx.Money.FormatCompact((decimal)v, cur);
        YearLabels = flows.Select(f => f.Month.ToString("MMM", c)).ToList();
        YearSeries =
        [
            new ChartSeries(Ctx.Loc["Fin_Income"], flows.Select(f => (double)f.Income).ToList(), "Green"),
            new ChartSeries(Ctx.Loc["Fin_Expenses"], flows.Select(f => (double)f.Expenses).ToList(), "Blue"),
            new ChartSeries(Ctx.Loc["Fin_Saved"], flows.Select(f => (double)f.Saved).ToList(), "Gold", IsLine: true),
        ];
        var spending = await Ctx.Store.GetCategorySpendingAsync(first, new DateOnly(Year, 12, 31), Ctx.Language, cur, true, ct);
        YearCategories.Clear();
        foreach (var b in FinanceText.Bars(spending, Ctx, 6)) YearCategories.Add(b);
    }
}

/// <summary>
/// "Expenses" inside a trip dossier or a project workspace: what was recorded for it (per currency, by category), the
/// trip's budget, and quick entry pre-linked to the trip or project. Projects stay projects: this is a read-out, not
/// accounting.
/// </summary>
public sealed partial class ExpensePanelViewModel(FinanceActions actions, EntityKind kind, int id, string name) : ObservableObject
{
    public FinanceActions Actions { get; } = actions;
    public FinanceContext Ctx => Actions.Ctx;
    public EntityKind Kind { get; } = kind;
    public int Id { get; } = id;
    public string Name { get; } = name;
    public bool IsTrip => Kind == EntityKind.Trip;
    public ObservableCollection<TransactionItem> Items { get; } = [];
    public ObservableCollection<CategoryBar> CategoryBars { get; } = [];
    public ObservableCollection<CurrencyTotal> Totals { get; } = [];
    [ObservableProperty] private BudgetItem? _budget;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string _countText = string.Empty;

    public async Task LoadAsync()
    {
        try
        {
            await Ctx.Ready;
            var summary = IsTrip ? await Ctx.Store.GetTripExpensesAsync(Id, Ctx.Language) : await Ctx.Store.GetProjectExpensesAsync(Id, Ctx.Language);
            Totals.Clear();
            foreach (var t in summary.Totals) Totals.Add(new CurrencyTotal(t.Currency, Ctx.Format(t.Amount, t.Currency), Ctx.Loc.Format("Fin_TxnCount", summary.Transactions.Count(x => x.Currency == t.Currency)), null));
            CategoryBars.Clear();
            foreach (var b in FinanceText.Bars(summary.ByCategory, Ctx, 8)) CategoryBars.Add(b);
            Items.Clear();
            foreach (var r in summary.Transactions) Items.Add(new TransactionItem(r, Ctx, null, null));
            var categories = (await Ctx.Store.GetCategoriesAsync(Ctx.Language, true)).ToDictionary(c => c.Id);
            Budget = summary.Budgets.FirstOrDefault() is { } s ? new BudgetItem(s, Ctx, categories) : null;
            IsEmpty = Items.Count == 0;
            CountText = Ctx.Loc.Format("Fin_TxnCount", Items.Count);
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogError(Ctx.Logger, ex, "Loading {Kind} expenses failed", Kind);
            Ctx.Notifications.Show(Ctx.Loc["Fin_ErrLoad"], NotificationKind.Warning);
        }
    }

    [RelayCommand]
    private async Task AddExpenseAsync()
    {
        var draft = new TransactionDraft(TransactionType.Expense, Ctx.Today, 0m, Ctx.Prefs.DefaultAccountId ?? 0, IsTrip ? 8 : null,
            ProjectId: IsTrip ? null : Id, TripId: IsTrip ? Id : null);
        if (await Actions.NewTransactionAsync(TransactionType.Expense, draft)) await LoadAsync();
    }

    [RelayCommand] private async Task EditAsync(TransactionItem? t) { if (t != null && await Actions.EditTransactionAsync(t.Id)) await LoadAsync(); }

    [RelayCommand]
    private async Task SetBudgetAsync()
    {
        if (!IsTrip) return;
        var saved = Budget != null ? await Actions.EditBudgetAsync(Budget.Id, Name) : await Actions.NewBudgetAsync(tripId: Id, tripName: Name, start: TripStart, end: TripEnd);
        if (saved) await LoadAsync();
    }

    /// <summary>The trip's dates, used as the default range of a new trip budget.</summary>
    public DateOnly? TripStart { get; set; }
    public DateOnly? TripEnd { get; set; }

    [RelayCommand]
    private void OpenInFinance()
    {
        Ctx.Navigation.Navigate(Sections.Finance);
        OpenRegisterRequested?.Invoke(new TransactionQuery(ProjectId: IsTrip ? null : Id, TripId: IsTrip ? Id : null));
    }

    /// <summary>Set at startup: the finance actions trip and project workspaces use (they are created outside DI).</summary>
    public static FinanceActions? Shared { get; set; }

    public static ExpensePanelViewModel? For(EntityKind kind, int id, string name) => Shared is { } a ? new ExpensePanelViewModel(a, kind, id, name) : null;

    /// <summary>Set by the shell: shows the Finance register scoped to this trip or project.</summary>
    public static Action<TransactionQuery>? OpenRegisterRequested { get; set; }
}
