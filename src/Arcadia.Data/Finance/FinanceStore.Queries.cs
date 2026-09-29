using Arcadia.Core.Finance;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data.Finance;

/// <summary>The deterministic read side (<see cref="IFinanceQueryService"/>): every number is computed from the rows, per currency.</summary>
public sealed partial class FinanceStore
{
    private readonly object _cacheLock = new();
    private (long Version, List<AccountBalance> Rows)? _balances;

    // ───────────── balances ─────────────

    /// <summary>
    /// Every account's balance = opening + income − expenses − transfers out + transfers in. Computed in one pass over a
    /// narrow projection and cached until the next write (see <see cref="Version"/>); never stored.
    /// </summary>
    public async Task<IReadOnlyList<AccountBalance>> GetAccountBalancesAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        var version = Version;
        lock (_cacheLock)
            if (_balances is { } cached && cached.Version == version) return Pick(cached.Rows);

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var accounts = await db.FinanceAccounts.AsNoTracking().OrderBy(a => a.IsArchived).ThenBy(a => a.SortOrder).ThenBy(a => a.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var flows = await db.FinanceTransactions.AsNoTracking()
            .Select(t => new { t.Type, t.AccountId, t.ToAccountId, t.Amount, t.ToAmount, t.Date }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var balances = FinanceMath.Balances(accounts.Select(a => (a.Id, a.OpeningBalance)), flows.Select(f => new TransactionFlow(f.Type, f.AccountId, f.ToAccountId, f.Amount, f.ToAmount)));
        var counts = new Dictionary<int, (int Count, DateOnly Last)>();
        void Touch(int id, DateOnly date)
        {
            counts.TryGetValue(id, out var c);
            counts[id] = (c.Count + 1, c.Count == 0 || date > c.Last ? date : c.Last);
        }
        foreach (var f in flows)
        {
            Touch(f.AccountId, f.Date);
            if (f.ToAccountId is { } to && to != f.AccountId) Touch(to, f.Date);
        }
        var rows = accounts.Select(a => new AccountBalance(a, balances[a.Id], counts.TryGetValue(a.Id, out var c) ? c.Count : 0, counts.TryGetValue(a.Id, out var l) ? l.Last : null)).ToList();
        lock (_cacheLock) _balances = (version, rows);
        return Pick(rows);

        IReadOnlyList<AccountBalance> Pick(List<AccountBalance> all) => includeArchived ? all : all.Where(r => !r.Account.IsArchived).ToList();
    }

    // ───────────── months ─────────────

    private sealed record Flow(int Id, TransactionType Type, DateOnly Date, decimal Amount, decimal? ToAmount, int AccountId, int? ToAccountId, int? CategoryId,
        string Currency, string? ToCurrency, AccountType AccountType, AccountType? ToAccountType, int? TripId);

    private static Task<List<Flow>> FlowsAsync(ArcadiaDbContext db, DateOnly from, DateOnly to, CancellationToken ct) =>
        db.FinanceTransactions.AsNoTracking().Where(t => t.Date >= from && t.Date <= to)
            .Select(t => new Flow(t.Id, t.Type, t.Date, t.Amount, t.ToAmount, t.AccountId, t.ToAccountId, t.CategoryId, t.Account!.Currency,
                t.ToAccount != null ? t.ToAccount.Currency : null, t.Account.Type, t.ToAccount != null ? t.ToAccount.Type : null, t.TripId))
            .ToListAsync(ct);

    private static async Task<IReadOnlyList<MoneyTotal>> SumAsync(ArcadiaDbContext db, TransactionType type, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await db.FinanceTransactions.AsNoTracking().Where(t => t.Type == type && t.Date >= from && t.Date <= to)
            .Select(t => new { t.Amount, t.Account!.Currency }).ToListAsync(ct).ConfigureAwait(false);
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var r in rows) FinanceMath.Add(totals, r.Currency, r.Amount);
        return FinanceMath.ToTotals(totals);
    }

    public async Task<IReadOnlyList<MoneyTotal>> GetMonthlyExpensesAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var start = new DateOnly(year, month, 1);
        return await SumAsync(db, TransactionType.Expense, start, FinanceMath.MonthEnd(start), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MoneyTotal>> GetMonthlyIncomeAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var start = new DateOnly(year, month, 1);
        return await SumAsync(db, TransactionType.Income, start, FinanceMath.MonthEnd(start), cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSavingsAccount(AccountType? t) => t is AccountType.Savings or AccountType.Investment;

    /// <summary>
    /// Monthly income, expenses and savings per currency. "Saved" = transfers into savings/investment accounts from
    /// other accounts, minus transfers back out of them, plus expenses in the Savings category (for people who record
    /// saving as a spending line instead of keeping a savings account). Months without activity are included as zero.
    /// </summary>
    public async Task<IReadOnlyList<MonthFlow>> GetMonthlyFlowsAsync(DateOnly fromMonth, DateOnly toMonth, CancellationToken cancellationToken = default)
    {
        var first = FinanceMath.MonthIndex(fromMonth);
        var last = FinanceMath.MonthIndex(toMonth);
        if (last < first) (first, last) = (last, first);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var savingsCategories = await SavingsCategoryIdsAsync(db, cancellationToken).ConfigureAwait(false);
        var flows = await FlowsAsync(db, FinanceMath.FromMonthIndex(first), FinanceMath.MonthEnd(FinanceMath.FromMonthIndex(last)), cancellationToken).ConfigureAwait(false);
        return MonthlyFlows(flows, first, last, savingsCategories);
    }

    private static List<MonthFlow> MonthlyFlows(IEnumerable<Flow> flows, int first, int last, HashSet<int> savingsCategories)
    {
        var acc = new Dictionary<(int Month, string Currency), (decimal In, decimal Out, decimal Saved, int Count)>();
        void Add(int m, string cur, decimal income, decimal expense, decimal saved, int count)
        {
            acc.TryGetValue((m, cur), out var v);
            acc[(m, cur)] = (v.In + income, v.Out + expense, v.Saved + saved, v.Count + count);
        }
        foreach (var f in flows)
        {
            var m = FinanceMath.MonthIndex(f.Date);
            switch (f.Type)
            {
                case TransactionType.Income: Add(m, f.Currency, f.Amount, 0, 0, 1); break;
                case TransactionType.Expense: Add(m, f.Currency, 0, f.Amount, f.CategoryId is { } c && savingsCategories.Contains(c) ? f.Amount : 0, 1); break;
                case TransactionType.Transfer:
                    var fromSavings = IsSavingsAccount(f.AccountType);
                    var toSavings = IsSavingsAccount(f.ToAccountType);
                    if (!fromSavings && toSavings) Add(m, f.ToCurrency ?? f.Currency, 0, 0, f.ToAmount ?? f.Amount, 0);
                    else if (fromSavings && !toSavings) Add(m, f.Currency, 0, 0, -f.Amount, 0);
                    Add(m, f.Currency, 0, 0, 0, 1);
                    break;
            }
        }
        var currencies = acc.Keys.Select(k => k.Currency).Distinct().ToList();
        var list = new List<MonthFlow>();
        foreach (var cur in currencies)
            for (var m = first; m <= last; m++)
            {
                acc.TryGetValue((m, cur), out var v);
                list.Add(new MonthFlow(FinanceMath.FromMonthIndex(m), cur, v.In, v.Out, v.Saved, v.Count));
            }
        return list;
    }

    private static async Task<HashSet<int>> SavingsCategoryIdsAsync(ArcadiaDbContext db, CancellationToken ct)
    {
        var ids = await db.FinanceCategories.Where(c => c.Key == FinanceDefaults.SavingsKey || (c.Parent != null && c.Parent.Key == FinanceDefaults.SavingsKey))
            .Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
        return ids.ToHashSet();
    }

    public async Task<IReadOnlyList<CategorySpending>> GetCategorySpendingAsync(DateOnly from, DateOnly to, string language, string? currency = null, bool rollUp = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var cats = await db.FinanceCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, cancellationToken).ConfigureAwait(false);
        var rows = await db.FinanceTransactions.AsNoTracking().Where(t => t.Type == TransactionType.Expense && t.Date >= from && t.Date <= to && (currency == null || t.Account!.Currency == currency))
            .Select(t => new { t.CategoryId, t.Amount, t.Account!.Currency }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return Spending(rows.Select(r => (r.CategoryId, r.Amount, r.Currency)), cats, language, rollUp);
    }

    private static List<CategorySpending> Spending(IEnumerable<(int? CategoryId, decimal Amount, string Currency)> rows, IReadOnlyDictionary<int, FinanceCategory> cats, string language, bool rollUp)
    {
        var sums = new Dictionary<(int?, string), (decimal Amount, int Count)>();
        foreach (var (categoryId, amount, cur) in rows)
        {
            int? key = categoryId;
            if (rollUp && key is { } id && cats.TryGetValue(id, out var c) && c.ParentId is { } parent) key = parent;
            sums.TryGetValue((key, cur), out var v);
            sums[(key, cur)] = (v.Amount + amount, v.Count + 1);
        }
        return sums.Select(kv =>
        {
            var (id, cur) = kv.Key;
            var c = id is { } i && cats.TryGetValue(i, out var cat) ? cat : null;
            return new CategorySpending(id, c?.Key, c == null ? string.Empty : FinanceDefaults.NameOf(c.Key, c.Name, language), c?.Icon ?? "Tag", c?.Accent ?? "Steel", cur, kv.Value.Amount, kv.Value.Count);
        }).OrderByDescending(s => s.Amount).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<MonthSummary>> GetMonthlySummaryAsync(int year, int month, string language, CancellationToken cancellationToken = default)
    {
        var start = new DateOnly(year, month, 1);
        var end = FinanceMath.MonthEnd(start);
        var flows = await GetMonthlyFlowsAsync(start, start, cancellationToken).ConfigureAwait(false);
        var spending = await GetCategorySpendingAsync(start, end, language, null, true, cancellationToken).ConfigureAwait(false);
        var budgets = await GetBudgetStatusAsync(start, cancellationToken).ConfigureAwait(false);
        return flows.Select(f => new MonthSummary(start, f.Currency, f.Income, f.Expenses, f.Saved, f.Count,
                spending.Where(s => s.Currency == f.Currency).OrderByDescending(s => s.Amount).FirstOrDefault(),
                budgets.Count(b => b.Budget.Currency == f.Currency && b.Level == BudgetLevel.Over), budgets.Count(b => b.Budget.Currency == f.Currency)))
            .OrderByDescending(s => s.TransactionCount).ToList();
    }

    public async Task<IReadOnlyList<TransactionRow>> GetLargestExpensesAsync(DateOnly from, DateOnly to, string language, int take = 5, CancellationToken cancellationToken = default)
    {
        var page = await QueryTransactionsAsync(new TransactionQuery(From: from, To: to, Type: TransactionType.Expense, Sort: TransactionSort.AmountDesc, Take: Math.Clamp(take, 1, 100)),
            language, cancellationToken).ConfigureAwait(false);
        return page.Rows;
    }

    // ───────────── budgets ─────────────

    public async Task<IReadOnlyList<BudgetStatus>> GetBudgetStatusAsync(DateOnly month, CancellationToken cancellationToken = default)
    {
        var monthStart = FinanceMath.MonthStart(month);
        var monthEnd = FinanceMath.MonthEnd(month);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var budgets = await db.Budgets.AsNoTracking().Include(b => b.Category).Where(b => !b.IsArchived).ToListAsync(cancellationToken).ConfigureAwait(false);
        var relevant = budgets.Where(b => b.TripId == null && (BudgetMath.AppliesTo(b, monthStart)
                                                               || (b.Period == BudgetPeriod.Custom && b.StartDate <= monthEnd && (b.EndDate ?? b.StartDate) >= monthStart))).ToList();
        if (relevant.Count == 0) return [];
        var from = relevant.Min(b => b.Period == BudgetPeriod.Monthly && b.Rollover
            ? FinanceMath.FromMonthIndex(Math.Max(FinanceMath.MonthIndex(b.StartDate), FinanceMath.MonthIndex(monthStart) - BudgetMath.MaxRolloverMonths))
            : b.Period == BudgetPeriod.Custom ? b.StartDate : monthStart);
        var to = relevant.Max(b => b.Period == BudgetPeriod.Custom ? b.EndDate ?? b.StartDate : monthEnd);
        var expenses = await db.FinanceTransactions.AsNoTracking().Where(t => t.Type == TransactionType.Expense && t.Date >= from && t.Date <= to)
            .Select(t => new { t.Date, t.Amount, t.CategoryId, t.Account!.Currency }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var children = await db.FinanceCategories.AsNoTracking().Where(c => c.ParentId != null).Select(c => new { c.Id, Parent = c.ParentId!.Value }).ToListAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<BudgetStatus>();
        foreach (var b in relevant)
        {
            HashSet<int>? scope = b.CategoryId is { } cid ? [cid, .. children.Where(c => c.Parent == cid).Select(c => c.Id)] : null;
            var mine = expenses.Where(e => e.Currency == b.Currency && (scope == null || (e.CategoryId is { } c && scope.Contains(c)))).ToList();
            if (b.Period == BudgetPeriod.Monthly)
            {
                var byMonth = mine.GroupBy(e => FinanceMath.MonthIndex(e.Date)).ToDictionary(g => g.Key, g => (g.Sum(e => e.Amount), g.Count()));
                result.Add(BudgetMath.Monthly(b, monthStart, byMonth));
            }
            else
            {
                var inRange = mine.Where(e => e.Date >= b.StartDate && e.Date <= (b.EndDate ?? b.StartDate)).ToList();
                result.Add(BudgetMath.Custom(b, inRange.Sum(e => e.Amount), inRange.Count));
            }
        }
        return result.OrderByDescending(s => s.Level).ThenByDescending(s => s.Percent).ToList();
    }

    // ───────────── bills & subscriptions ─────────────

    public async Task<IReadOnlyList<BillDue>> GetUpcomingBillsAsync(DateOnly from, DateOnly to, string language, int overdueDays = 60, CancellationToken cancellationToken = default)
    {
        var items = await GetRecurringItemsAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<BillDue>();
        foreach (var item in items.Where(i => i.IsActive))
            foreach (var d in RecurringMath.OpenDueDates(item, from.AddDays(-Math.Max(0, overdueDays)), to))
                list.Add(new BillDue(item, d, d < from, item.Account?.Name, CategoryName(item.Category, language)));
        return list.OrderBy(b => b.DueDate).ThenBy(b => b.Item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? CategoryName(FinanceCategory? c, string language) => c == null ? null : FinanceDefaults.NameOf(c.Key, c.Name, language);

    public async Task<IReadOnlyList<SubscriptionRow>> GetSubscriptionsAsync(DateOnly today, string language, RecurringKind? kind = RecurringKind.Subscription, bool includeInactive = true,
        CancellationToken cancellationToken = default)
    {
        var items = await GetRecurringItemsAsync(cancellationToken).ConfigureAwait(false);
        return items.Where(i => (kind == null || i.Kind == kind) && (includeInactive || i.IsActive))
            .Select(i => new SubscriptionRow(i, i.IsActive ? RecurringMath.NextDue(i, today) : null, RecurringMath.MonthlyEquivalent(i.Amount, i.Frequency, i.Interval),
                RecurringMath.AnnualCost(i.Amount, i.Frequency, i.Interval), i.Account?.Name, CategoryName(i.Category, language)))
            .OrderByDescending(r => r.Item.IsActive).ThenBy(r => r.NextDue ?? DateOnly.MaxValue).ThenBy(r => r.Item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ───────────── goals ─────────────

    public async Task<IReadOnlyList<GoalProgress>> GetSavingsGoalsAsync(DateOnly today, bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        var goals = await GetGoalsAsync(includeArchived, cancellationToken).ConfigureAwait(false);
        var balances = goals.Any(g => g.AccountId != null)
            ? (await GetAccountBalancesAsync(true, cancellationToken).ConfigureAwait(false)).ToDictionary(b => b.Account.Id, b => b.Balance)
            : [];
        return goals.Select(g => GoalProgress.Of(g, g.AccountId is { } a && balances.TryGetValue(a, out var bal) ? Math.Max(0m, bal) : g.CurrentAmount, today)).ToList();
    }

    // ───────────── trips & projects ─────────────

    public async Task<ExpenseSummary> GetTripExpensesAsync(int tripId, string language, CancellationToken cancellationToken = default)
    {
        var page = await QueryTransactionsAsync(new TransactionQuery(TripId: tripId, Type: TransactionType.Expense, Take: 5000), language, cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var cats = await db.FinanceCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, cancellationToken).ConfigureAwait(false);
        var budgets = await db.Budgets.AsNoTracking().Include(b => b.Category).Where(b => b.TripId == tripId && !b.IsArchived).ToListAsync(cancellationToken).ConfigureAwait(false);
        var statuses = new List<BudgetStatus>();
        foreach (var b in budgets)
        {
            // a trip budget covers everything recorded for the trip (in its currency and category), whenever it was paid
            HashSet<int>? scope = b.CategoryId is { } cid ? [cid, .. cats.Values.Where(c => c.ParentId == cid).Select(c => c.Id)] : null;
            var mine = page.Rows.Where(r => r.Currency == b.Currency && (scope == null || (r.CategoryId is { } c && scope.Contains(c)))).ToList();
            statuses.Add(BudgetMath.Custom(b, mine.Sum(r => r.Amount), mine.Count));
        }
        return Summary(page.Rows, cats, language, statuses);
    }

    public async Task<ExpenseSummary> GetProjectExpensesAsync(int projectId, string language, CancellationToken cancellationToken = default)
    {
        var page = await QueryTransactionsAsync(new TransactionQuery(ProjectId: projectId, Type: TransactionType.Expense, Take: 5000), language, cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var cats = await db.FinanceCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, cancellationToken).ConfigureAwait(false);
        return Summary(page.Rows, cats, language, []);
    }

    private static ExpenseSummary Summary(IReadOnlyList<TransactionRow> rows, IReadOnlyDictionary<int, FinanceCategory> cats, string language, IReadOnlyList<BudgetStatus> budgets)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var r in rows) FinanceMath.Add(totals, r.Currency, r.Amount);
        return new ExpenseSummary(FinanceMath.ToTotals(totals), Spending(rows.Select(r => (r.CategoryId, r.Amount, r.Currency)), cats, language, true), rows, budgets);
    }

    // ───────────── history ─────────────

    public async Task<IReadOnlyList<BalanceHistory>> GetBalanceHistoryAsync(DateOnly fromMonth, DateOnly toMonth, string? currency = null, CancellationToken cancellationToken = default)
    {
        var first = FinanceMath.MonthIndex(fromMonth);
        var last = FinanceMath.MonthIndex(toMonth);
        if (last < first) (first, last) = (last, first);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var accounts = await db.FinanceAccounts.AsNoTracking().Where(a => !a.IsArchived && (currency == null || a.Currency == currency)).OrderBy(a => a.SortOrder).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (accounts.Count == 0) return [];
        var lastDay = FinanceMath.MonthEnd(FinanceMath.FromMonthIndex(last));
        var flows = await db.FinanceTransactions.AsNoTracking().Where(t => t.Date <= lastDay)
            .Select(t => new { t.Type, t.AccountId, t.ToAccountId, t.Amount, t.ToAmount, t.Date }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<BalanceHistory>();
        foreach (var a in accounts)
        {
            // month-end balances: everything up to the end of each month (the opening balance counts from the start)
            var byMonth = flows.Where(f => f.AccountId == a.Id || f.ToAccountId == a.Id)
                .GroupBy(f => FinanceMath.MonthIndex(f.Date))
                .ToDictionary(g => g.Key, g => g.Sum(f => FinanceMath.Effect(a.Id, new TransactionFlow(f.Type, f.AccountId, f.ToAccountId, f.Amount, f.ToAmount))));
            var running = a.OpeningBalance + byMonth.Where(kv => kv.Key < first).Sum(kv => kv.Value);
            var points = new List<(DateOnly, decimal)>();
            for (var m = first; m <= last; m++)
            {
                running += byMonth.GetValueOrDefault(m);
                points.Add((FinanceMath.FromMonthIndex(m), running));
            }
            result.Add(new BalanceHistory(a.Id, a.Name, a.Currency, a.Accent, points));
        }
        return result;
    }
}
