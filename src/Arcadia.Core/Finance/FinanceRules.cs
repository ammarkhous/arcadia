using Arcadia.Core.Organizer;

namespace Arcadia.Core.Finance;

/// <summary>An amount in one currency. Totals are lists of these: amounts in different currencies are never added.</summary>
public readonly record struct MoneyTotal(string Currency, decimal Amount);

/// <summary>What a transaction does to balances (the minimum a balance calculation needs).</summary>
public readonly record struct TransactionFlow(TransactionType Type, int AccountId, int? ToAccountId, decimal Amount, decimal? ToAmount);

/// <summary>The built-in categories: keys, icons, accents and names in Arcadia's languages (renaming stores a custom name).</summary>
public static class FinanceDefaults
{
    public sealed record DefaultCategory(int Id, string Key, CategoryKind Kind, string Icon, string Accent, string En, string De, string Ar, int? ParentId = null);

    public const string SavingsKey = "savings";

    public static readonly IReadOnlyList<DefaultCategory> Categories =
    [
        new(1, "housing", CategoryKind.Expense, "Home", "Blue", "Housing", "Wohnen", "السكن"),
        new(2, "groceries", CategoryKind.Expense, "Food", "Green", "Groceries", "Lebensmittel", "البقالة"),
        new(3, "restaurants", CategoryKind.Expense, "Food", "Amber", "Restaurants", "Restaurants", "المطاعم"),
        new(4, "transport", CategoryKind.Expense, "Route", "Cyan", "Transportation", "Mobilität", "المواصلات"),
        new(5, "shopping", CategoryKind.Expense, "Tag", "Purple", "Shopping", "Einkäufe", "التسوق"),
        new(6, "entertainment", CategoryKind.Expense, "Star", "Gold", "Entertainment", "Unterhaltung", "الترفيه"),
        new(7, "gaming", CategoryKind.Expense, "Games", "Purple", "Gaming", "Gaming", "الألعاب"),
        new(8, "travel", CategoryKind.Expense, "Travel", "Green", "Travel", "Reisen", "السفر"),
        new(9, "health", CategoryKind.Expense, "Shield", "Red", "Health", "Gesundheit", "الصحة"),
        new(10, "insurance", CategoryKind.Expense, "Shield", "Steel", "Insurance", "Versicherungen", "التأمين"),
        new(11, "subscriptions", CategoryKind.Expense, "Repeat", "Cyan", "Subscriptions", "Abonnements", "الاشتراكات"),
        new(12, "utilities", CategoryKind.Expense, "Bolt", "Amber", "Utilities", "Nebenkosten", "المرافق"),
        new(13, "education", CategoryKind.Expense, "Library", "Blue", "Education", "Bildung", "التعليم"),
        new(14, SavingsKey, CategoryKind.Expense, "Target", "Cyan", "Savings", "Sparen", "الادخار"),
        new(15, "other", CategoryKind.Expense, "Layers", "Steel", "Other expenses", "Sonstige Ausgaben", "مصروفات أخرى"),
        new(16, "salary", CategoryKind.Income, "Bank", "Green", "Salary", "Gehalt", "الراتب"),
        new(17, "other-income", CategoryKind.Income, "Income", "Green", "Other income", "Sonstige Einnahmen", "دخل آخر"),
        new(18, "fuel", CategoryKind.Expense, "Bolt", "Cyan", "Fuel", "Tanken", "الوقود", 4),
        new(19, "public-transport", CategoryKind.Expense, "Route", "Cyan", "Public transport", "Öffentlicher Verkehr", "النقل العام", 4),
        new(20, "parking", CategoryKind.Expense, "MapPin", "Cyan", "Parking", "Parken", "المواقف", 4),
        new(21, "repairs", CategoryKind.Expense, "Settings", "Cyan", "Repairs", "Reparaturen", "الإصلاحات", 4),
    ];

    private static readonly Dictionary<string, DefaultCategory> ByKey = Categories.ToDictionary(c => c.Key, StringComparer.Ordinal);

    public static DefaultCategory? Find(string? key) => key != null && ByKey.TryGetValue(key, out var c) ? c : null;

    /// <summary>The display name of a category: the user's name, else the built-in name in the language ("en", "de", "ar").</summary>
    public static string NameOf(string? key, string? customName, string language)
    {
        if (!string.IsNullOrWhiteSpace(customName)) return customName;
        if (Find(key) is not { } d) return key ?? "—";
        return language switch { "ar" => d.Ar, "de" => d.De, _ => d.En };
    }

    /// <summary>Every name a built-in category is known by (for search and quick-capture matching in any language).</summary>
    public static IEnumerable<string> AliasesOf(string? key) => Find(key) is { } d ? [d.En, d.De, d.Ar, d.Key.Replace('-', ' ')] : [];
}

/// <summary>Deterministic money arithmetic shared by the stores, the UI and the tests. All values are <see cref="decimal"/>.</summary>
public static class FinanceMath
{
    /// <summary>Opening balance + income − expenses − transfers out + transfers in, for one account.</summary>
    public static decimal Balance(int accountId, decimal openingBalance, IEnumerable<TransactionFlow> flows)
    {
        var balance = openingBalance;
        foreach (var f in flows) balance += Effect(accountId, f);
        return balance;
    }

    /// <summary>Balances of many accounts in one pass over the flows.</summary>
    public static Dictionary<int, decimal> Balances(IEnumerable<(int Id, decimal Opening)> accounts, IEnumerable<TransactionFlow> flows)
    {
        var result = accounts.ToDictionary(a => a.Id, a => a.Opening);
        foreach (var f in flows)
        {
            if (result.ContainsKey(f.AccountId)) result[f.AccountId] += Effect(f.AccountId, f);
            if (f.Type == TransactionType.Transfer && f.ToAccountId is { } to && to != f.AccountId && result.ContainsKey(to)) result[to] += f.ToAmount ?? f.Amount;
        }
        return result;
    }

    /// <summary>The change one transaction makes to one account's balance.</summary>
    public static decimal Effect(int accountId, TransactionFlow f) => f.Type switch
    {
        TransactionType.Income when f.AccountId == accountId => f.Amount,
        TransactionType.Expense when f.AccountId == accountId => -f.Amount,
        TransactionType.Transfer when f.AccountId == accountId && f.ToAccountId == accountId => 0m,
        TransactionType.Transfer when f.AccountId == accountId => -f.Amount,
        TransactionType.Transfer when f.ToAccountId == accountId => f.ToAmount ?? f.Amount,
        _ => 0m,
    };

    /// <summary>Adds an amount to per-currency totals (never across currencies).</summary>
    public static void Add(Dictionary<string, decimal> totals, string currency, decimal amount)
    {
        totals.TryGetValue(currency, out var v);
        totals[currency] = v + amount;
    }

    public static IReadOnlyList<MoneyTotal> ToTotals(Dictionary<string, decimal> totals) =>
        totals.OrderByDescending(kv => Math.Abs(kv.Value)).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new MoneyTotal(kv.Key, kv.Value)).ToList();

    /// <summary>0–100+ (not clamped: 130 means 30 % over).</summary>
    public static double Percent(decimal part, decimal whole) => whole <= 0 ? (part > 0 ? 100 : 0) : (double)(part / whole * 100m);

    public static DateOnly MonthStart(DateOnly d) => new(d.Year, d.Month, 1);
    public static DateOnly MonthEnd(DateOnly d) => new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));
    public static int MonthIndex(DateOnly d) => d.Year * 12 + d.Month - 1;
    public static DateOnly FromMonthIndex(int i) => new(i / 12, i % 12 + 1, 1);
}

public enum BudgetLevel { Within, Approaching, Over }

/// <summary>A budget in one period: what was available (budget + carried over), spent and left.</summary>
public sealed record BudgetStatus(
    Budget Budget,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal BaseAmount,
    decimal Carried,
    decimal Spent,
    int TransactionCount)
{
    public decimal Available => BaseAmount + Carried;
    public decimal Remaining => Available - Spent;
    public double Percent => FinanceMath.Percent(Spent, Available);
    public BudgetLevel Level => BudgetMath.LevelOf(Spent, Available);
}

public static class BudgetMath
{
    /// <summary>From this share of the budget on, a budget is "approaching its limit".</summary>
    public const decimal ApproachingShare = 0.85m;

    /// <summary>How many earlier months rollover looks back at most (twenty years).</summary>
    public const int MaxRolloverMonths = 240;

    public static BudgetLevel LevelOf(decimal spent, decimal available) =>
        spent > available ? BudgetLevel.Over : available > 0 && spent >= available * ApproachingShare ? BudgetLevel.Approaching : BudgetLevel.Within;

    /// <summary>True when a monthly budget applies to the month that starts at <paramref name="month"/>.</summary>
    public static bool AppliesTo(Budget b, DateOnly month)
    {
        if (b.Period != BudgetPeriod.Monthly) return false;
        var m = FinanceMath.MonthIndex(month);
        return m >= FinanceMath.MonthIndex(b.StartDate) && (b.EndDate is not { } end || m <= FinanceMath.MonthIndex(end));
    }

    /// <summary>
    /// A monthly budget's status in <paramref name="month"/>. <paramref name="spentByMonth"/> maps a month index
    /// (<see cref="FinanceMath.MonthIndex"/>) to what was spent in it. With rollover, every earlier month since the budget
    /// began passes its unspent remainder on (never a deficit).
    /// </summary>
    public static BudgetStatus Monthly(Budget b, DateOnly month, IReadOnlyDictionary<int, (decimal Spent, int Count)> spentByMonth)
    {
        var target = FinanceMath.MonthIndex(month);
        var carried = 0m;
        if (b.Rollover)
        {
            var first = Math.Max(FinanceMath.MonthIndex(b.StartDate), target - MaxRolloverMonths);
            for (var m = first; m < target; m++)
            {
                var spent = spentByMonth.TryGetValue(m, out var s) ? s.Spent : 0m;
                carried = Math.Max(0m, b.Amount + carried - spent);
            }
        }
        var here = spentByMonth.TryGetValue(target, out var t) ? t : (Spent: 0m, Count: 0);
        var start = FinanceMath.FromMonthIndex(target);
        return new BudgetStatus(b, start, FinanceMath.MonthEnd(start), b.Amount, carried, here.Spent, here.Count);
    }

    /// <summary>A custom-range budget: everything spent between its first and last day.</summary>
    public static BudgetStatus Custom(Budget b, decimal spent, int count) =>
        new(b, b.StartDate, b.EndDate ?? b.StartDate, b.Amount, 0m, spent, count);
}

/// <summary>Due dates and yearly costs of recurring bills, subscriptions and income.</summary>
public static class RecurringMath
{
    public static RecurrenceRule RuleOf(RecurringItem item) => new(item.StartDate, item.Frequency, Math.Max(1, item.Interval), WeekdayMask.None, item.EndDate);

    /// <summary>How many times a year the item is due (nominal: 365 days, 52 weeks, 12 months).</summary>
    public static decimal TimesPerYear(RecurrenceFrequency frequency, int interval)
    {
        var n = Math.Max(1, interval);
        return frequency switch
        {
            RecurrenceFrequency.Daily => 365m / n,
            RecurrenceFrequency.Weekly => 52m / n,
            RecurrenceFrequency.Monthly => 12m / n,
            RecurrenceFrequency.Yearly => 1m / n,
            _ => 0m,
        };
    }

    public static decimal AnnualCost(decimal amount, RecurrenceFrequency frequency, int interval) => amount * TimesPerYear(frequency, interval);

    public static decimal MonthlyEquivalent(decimal amount, RecurrenceFrequency frequency, int interval) => AnnualCost(amount, frequency, interval) / 12m;

    /// <summary>Due dates in the range that were neither paid nor skipped (and not before tracking began).</summary>
    public static IEnumerable<DateOnly> OpenDueDates(RecurringItem item, DateOnly from, DateOnly to)
    {
        if (!item.IsActive || item.Frequency == RecurrenceFrequency.None) yield break;
        if (from < item.TrackedFrom) from = item.TrackedFrom;
        var handled = item.Occurrences.Select(o => o.DueDate).ToHashSet();
        foreach (var d in Recurrence.Dates(RuleOf(item), from, to))
            if (!handled.Contains(d)) yield return d;
    }

    /// <summary>The next due date that is not handled yet, on or after <paramref name="from"/>.</summary>
    public static DateOnly? NextDue(RecurringItem item, DateOnly from)
    {
        foreach (var d in OpenDueDates(item, from, from.AddYears(Math.Max(2, item.Interval * (item.Frequency == RecurrenceFrequency.Yearly ? 2 : 1)))))
            return d;
        return null;
    }
}

/// <summary>A savings goal's progress, computed only from what was entered (no forecasts, no advice).</summary>
public sealed record GoalProgress(SavingsGoal Goal, decimal Saved, int? MonthsLeft)
{
    public decimal Target => Goal.TargetAmount;
    public decimal Remaining => Math.Max(0m, Target - Saved);
    public double Percent => FinanceMath.Percent(Saved, Target);
    public bool IsReached => Saved >= Target && Target > 0;
    /// <summary>What would have to be put aside each remaining month to reach the target by its date (null without a date).</summary>
    public decimal? PerMonthToTarget => IsReached || MonthsLeft is not { } m ? null : m <= 0 ? Remaining : Remaining / m;

    public static GoalProgress Of(SavingsGoal goal, decimal saved, DateOnly today)
    {
        // months still available including the current one (a date in the past leaves none)
        int? months = goal.TargetDate is { } d ? (d < today ? 0 : FinanceMath.MonthIndex(d) - FinanceMath.MonthIndex(today) + 1) : null;
        return new GoalProgress(goal, saved, months);
    }
}

/// <summary>Report periods. "Last 3/6 months" are the current month and the ones before it.</summary>
public enum FinancePeriod { ThisMonth, LastMonth, Last3Months, Last6Months, ThisYear, LastYear, Custom }

public static class FinancePeriods
{
    /// <summary>First and last day (inclusive) of a period relative to <paramref name="today"/>.</summary>
    public static (DateOnly From, DateOnly To) Range(FinancePeriod period, DateOnly today, DateOnly? customFrom = null, DateOnly? customTo = null)
    {
        var month = FinanceMath.MonthStart(today);
        return period switch
        {
            FinancePeriod.ThisMonth => (month, FinanceMath.MonthEnd(today)),
            FinancePeriod.LastMonth => (month.AddMonths(-1), month.AddDays(-1)),
            FinancePeriod.Last3Months => (month.AddMonths(-2), FinanceMath.MonthEnd(today)),
            FinancePeriod.Last6Months => (month.AddMonths(-5), FinanceMath.MonthEnd(today)),
            FinancePeriod.ThisYear => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
            FinancePeriod.LastYear => (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
            _ => Ordered(customFrom ?? month, customTo ?? FinanceMath.MonthEnd(today)),
        };
    }

    private static (DateOnly, DateOnly) Ordered(DateOnly a, DateOnly b) => a <= b ? (a, b) : (b, a);
}
