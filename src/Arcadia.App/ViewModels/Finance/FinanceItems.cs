using System.Globalization;
using Arcadia.App.Services;
using Arcadia.App.ViewModels.Organizer;
using Arcadia.Core.Finance;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Arcadia.App.ViewModels.Finance;

public enum FinanceSection { Overview, Transactions, Accounts, Budgets, Recurring, Goals, Reports, History }

public sealed partial class FinanceSectionItem(FinanceSection section, string label, string icon) : ObservableObject
{
    public FinanceSection Section { get; } = section;
    public string Label { get; } = label;
    public string Icon { get; } = icon;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _count;
    public override string ToString() => Label;
}

/// <summary>A heading inside a finance list (a due day, "Overdue").</summary>
public sealed record FinanceGroupHeader(string Title, string? Tone)
{
    public override string ToString() => Title;
}

/// <summary>A labelled figure in a metric strip ("INCOME THIS MONTH  €3,200.00").</summary>
public sealed record MetricCell(string Label, string Value, string? Tone = null, string? Hint = null, string Icon = "Finance")
{
    public override string ToString() => $"{Label} {Value}";
}

/// <summary>A choice with a tone and icon (category and account pickers).</summary>
public sealed record FinanceOption(int? Id, string Label, string Icon = "Tag", string Accent = "Steel", bool IsChild = false, string? Currency = null)
{
    public override string ToString() => Label;
}

/// <summary>One line of the register.</summary>
public sealed class TransactionItem
{
    public TransactionItem(TransactionRow row, FinanceContext ctx, ContextChip? project, ContextChip? trip)
    {
        Row = row;
        var c = ctx.Loc.Culture;
        DateText = row.Date.ToString(row.Date.Year == ctx.Today.Year ? "ddd d MMM" : "d MMM yyyy", c);
        ShortDate = row.Date.ToString("d MMM", c);
        TimeText = row.Time is { } t ? OrgText.Time(t, c) : null;
        Title = string.IsNullOrWhiteSpace(row.Label) ? ctx.KindLabel(row.Type) : row.Label;
        Payee = !string.IsNullOrWhiteSpace(row.Description) && !string.IsNullOrWhiteSpace(row.Payee) ? row.Payee : null;
        CategoryText = row.Type == TransactionType.Transfer ? ctx.Loc["FinType_Transfer"] : ctx.CategoryLabel(row.CategoryName);
        CategoryIcon = row.Type == TransactionType.Transfer ? "Transfer" : row.CategoryIcon ?? "Tag";
        CategoryAccent = row.Type == TransactionType.Transfer ? "Steel" : row.CategoryAccent ?? "Steel";
        AccountText = row.Type == TransactionType.Transfer ? $"{row.AccountName} → {row.ToAccountName}" : row.AccountName;
        // expenses carry a minus, income a plus in the income tone; transfers stay neutral
        AmountText = row.Type switch
        {
            TransactionType.Expense => ctx.Format(-row.Amount, row.Currency),
            TransactionType.Income => ctx.FormatSigned(row.Amount, row.Currency),
            _ => ctx.Format(row.Amount, row.Currency),
        };
        ToAmountText = row.Type == TransactionType.Transfer && row.ToAmount is { } ta && row.ToCurrency != null ? "→ " + ctx.Format(ta, row.ToCurrency) : null;
        AmountTone = row.Type switch { TransactionType.Income => "Green", TransactionType.Transfer => "Steel", _ => null };
        Project = project;
        Trip = trip;
    }

    public TransactionRow Row { get; }
    public int Id => Row.Id;
    public string DateText { get; }
    public string ShortDate { get; }
    public string? TimeText { get; }
    public string Title { get; }
    public string? Payee { get; }
    public string CategoryText { get; }
    public string CategoryIcon { get; }
    public string CategoryAccent { get; }
    public string AccountText { get; }
    public string AmountText { get; }
    public string? ToAmountText { get; }
    public string? AmountTone { get; }
    public string Tags => Row.Tags;
    public bool HasTags => Row.Tags.Length > 0;
    public bool HasDocuments => Row.DocumentCount > 0;
    public bool NeedsReview => Row.NeedsReview;
    public bool HasNotes => Row.HasNotes;
    public bool IsRecurring => Row.RecurringItemId != null;
    public ContextChip? Project { get; }
    public ContextChip? Trip { get; }
    public bool IsTransfer => Row.Type == TransactionType.Transfer;
    public override string ToString() => $"{DateText} {Title} {CategoryText} {AccountText}";
}

public sealed class AccountItem(AccountBalance balance, FinanceContext ctx)
{
    public AccountBalance Balance { get; } = balance;
    public int Id => Balance.Account.Id;
    public string Name => Balance.Account.Name;
    public string Icon => Balance.Account.Icon;
    public string Accent => Balance.Account.Accent;
    public string Currency => Balance.Account.Currency;
    public string TypeText { get; } = ctx.Loc["FinAcct_" + balance.Account.Type];
    public string BalanceText { get; } = ctx.Format(balance.Balance, balance.Account.Currency);
    public string? BalanceTone { get; } = balance.Balance < 0 ? "Red" : null;
    public string OpeningText { get; } = ctx.Format(balance.Account.OpeningBalance, balance.Account.Currency);
    public string ActivityText { get; } = balance.TransactionCount == 0 ? ctx.Loc["Fin_NoActivity"]
        : ctx.Loc.Format("Fin_ActivityFmt", balance.TransactionCount, balance.LastActivity is { } d ? d.ToString("d MMM yyyy", ctx.Loc.Culture) : "—");
    public bool IsArchived => Balance.Account.IsArchived;
    public string? Description => Balance.Account.Description;
    public override string ToString() => $"{Name} {TypeText}";
}

/// <summary>Everything in one currency: accounts are only ever summed within their currency.</summary>
public sealed record CurrencyTotal(string Currency, string TotalText, string Detail, string? Tone)
{
    public override string ToString() => $"{Currency} {TotalText}";
}

public sealed class BudgetItem
{
    public BudgetItem(BudgetStatus s, FinanceContext ctx, IReadOnlyDictionary<int, CategoryInfo> categories, string? tripName = null)
    {
        Status = s;
        var b = s.Budget;
        var cat = b.CategoryId is { } id && categories.TryGetValue(id, out var c) ? c : null;
        Name = b.Name ?? (cat?.Name ?? ctx.Loc["Fin_AllSpending"]);
        Icon = cat?.Icon ?? "Finance";
        Accent = cat?.Accent ?? "Cyan";
        SpentText = ctx.Format(s.Spent, b.Currency);
        AvailableText = ctx.Format(s.Available, b.Currency);
        RemainingText = s.Remaining >= 0 ? ctx.Loc.Format("Fin_BudgetLeft", ctx.Format(s.Remaining, b.Currency)) : ctx.Loc.Format("Fin_BudgetOver", ctx.Format(-s.Remaining, b.Currency));
        UsedText = ctx.Loc.Format("Fin_BudgetOfFmt", SpentText, AvailableText);
        PercentText = s.Available > 0 ? (s.Percent / 100).ToString("P0", ctx.Loc.Culture) : "—";
        CarriedText = s.Carried > 0 ? ctx.Loc.Format("Fin_Carried", ctx.Format(s.Carried, b.Currency)) : null;
        LevelText = ctx.Loc["FinLevel_" + s.Level];
        LevelTone = s.Level switch { BudgetLevel.Over => "Red", BudgetLevel.Approaching => "Amber", _ => "Green" };
        MeterTone = s.Level == BudgetLevel.Approaching ? "Amber" : "Cyan";
        Meter = s.Available > 0 ? (double)(s.Spent / s.Available) : s.Spent > 0 ? 2 : 0;
        PeriodText = b.Period == BudgetPeriod.Monthly
            ? (b.Rollover ? ctx.Loc["Fin_MonthlyRollover"] : ctx.Loc["Fin_Monthly"])
            : $"{s.PeriodStart.ToString("d MMM", ctx.Loc.Culture)} – {s.PeriodEnd.ToString("d MMM yyyy", ctx.Loc.Culture)}";
        TripName = tripName;
        CountText = ctx.Loc.Format("Fin_TxnCount", s.TransactionCount);
    }

    public BudgetStatus Status { get; }
    public int Id => Status.Budget.Id;
    public string Name { get; }
    public string Icon { get; }
    public string Accent { get; }
    public string SpentText { get; }
    public string AvailableText { get; }
    public string RemainingText { get; }
    public string UsedText { get; }
    public string PercentText { get; }
    public string? CarriedText { get; }
    public string LevelText { get; }
    public string LevelTone { get; }
    public string MeterTone { get; }
    public double Meter { get; }
    public string PeriodText { get; }
    public string? TripName { get; }
    public string CountText { get; }
    public override string ToString() => $"{Name} {LevelText}";
}

public sealed class BillItem(BillDue due, FinanceContext ctx)
{
    public BillDue Due { get; } = due;
    public int ItemId => Due.Item.Id;
    public DateOnly Date => Due.DueDate;
    public string Name => Due.Item.Name;
    public string Icon { get; } = due.Item.Kind switch { RecurringKind.Income => "Income", RecurringKind.Subscription => "Repeat", _ => "Receipt" };
    public string AmountText { get; } = due.Item.Kind == RecurringKind.Income ? ctx.FormatSigned(due.Item.Amount, due.Item.Currency) : ctx.Format(due.Item.Amount, due.Item.Currency);
    public string? AmountTone { get; } = due.Item.Kind == RecurringKind.Income ? "Green" : null;
    public string DueText { get; } = due.IsOverdue ? ctx.Loc.Format("Fin_OverdueSince", OrgText.Day(due.DueDate, ctx.Today, ctx.Loc)) : OrgText.Day(due.DueDate, ctx.Today, ctx.Loc);
    public string DueTone { get; } = due.IsOverdue ? "Red" : due.DueDate == ctx.Today ? "Amber" : "Steel";
    public string Meta { get; } = string.Join(" · ", new[] { ctx.Loc["FinKind_" + due.Item.Kind], due.AccountName, due.CategoryName }.Where(s => !string.IsNullOrEmpty(s)));
    public bool IsOverdue => Due.IsOverdue;
    public override string ToString() => $"{Name} {DueText}";
}

public sealed class SubscriptionItem(SubscriptionRow row, FinanceContext ctx)
{
    public SubscriptionRow Row { get; } = row;
    public int Id => Row.Item.Id;
    public string Name => Row.Item.Name;
    public string Icon { get; } = row.Item.Kind switch { RecurringKind.Income => "Income", RecurringKind.Subscription => "Repeat", _ => "Receipt" };
    public string PriceText { get; } = ctx.Format(row.Item.Amount, row.Item.Currency);
    public string FrequencyText { get; } = FinanceText.Frequency(row.Item.Frequency, row.Item.Interval, ctx);
    public string NextText { get; } = row.NextDue is { } d ? OrgText.Day(d, ctx.Today, ctx.Loc) : "—";
    public string CategoryText { get; } = row.CategoryName ?? ctx.Loc["Fin_Uncategorized"];
    public string AccountText { get; } = row.AccountName ?? "—";
    public string MonthlyText { get; } = ctx.Format(row.MonthlyEquivalent, row.Item.Currency);
    public string AnnualText { get; } = ctx.Format(row.AnnualCost, row.Item.Currency);
    public string StatusText { get; } = ctx.Loc[row.Item.IsActive ? "Fin_Active" : "Fin_Paused"];
    public string StatusTone { get; } = row.Item.IsActive ? "Green" : "Steel";
    public bool IsActive => Row.Item.IsActive;
    public override string ToString() => $"{Name} {FrequencyText}";
}

public sealed class GoalItem(GoalProgress p, FinanceContext ctx)
{
    public GoalProgress Progress { get; } = p;
    public int Id => Progress.Goal.Id;
    public string Name => Progress.Goal.Name;
    public string Icon => Progress.Goal.Icon;
    public string Accent => Progress.Goal.Accent;
    public string SavedText { get; } = ctx.Format(p.Saved, p.Goal.Currency);
    public string TargetText { get; } = ctx.Format(p.Target, p.Goal.Currency);
    public string OfText { get; } = ctx.Loc.Format("Fin_BudgetOfFmt", ctx.Format(p.Saved, p.Goal.Currency), ctx.Format(p.Target, p.Goal.Currency));
    public string RemainingText { get; } = p.IsReached ? ctx.Loc["Fin_GoalReached"] : ctx.Loc.Format("Fin_GoalRemaining", ctx.Format(p.Remaining, p.Goal.Currency));
    public string PercentText { get; } = (Math.Min(p.Percent, 999) / 100).ToString("P0", ctx.Loc.Culture);
    public double Meter { get; } = p.Target > 0 ? Math.Min(1, (double)(p.Saved / p.Target)) : 0;
    public string? DateText { get; } = p.Goal.TargetDate is { } d ? d.ToString("d MMM yyyy", ctx.Loc.Culture) : null;
    public string? PaceText { get; } = p.PerMonthToTarget is { } m ? ctx.Loc.Format("Fin_GoalPace", ctx.Format(m, p.Goal.Currency), p.MonthsLeft ?? 0) : null;
    public string? AccountText { get; } = p.Goal.Account?.Name is { } a ? ctx.Loc.Format("Fin_GoalFollows", a) : null;
    public bool IsManual => Progress.Goal.AccountId == null;
    public bool IsReached => Progress.IsReached;
    public string? Description => Progress.Goal.Description;
    public override string ToString() => $"{Name} {PercentText}";
}

/// <summary>A horizontal share bar row (spending by category).</summary>
public sealed record CategoryBar(int? CategoryId, string Name, string Icon, string Accent, string AmountText, double Share, string PercentText, string CountText)
{
    public override string ToString() => $"{Name} {PercentText}";
}

/// <summary>One row of a year or budget table.</summary>
public sealed record MonthRow(string Month, string Income, string Expenses, string Net, string? NetTone, string Saved, string Count, bool IsCurrent)
{
    public override string ToString() => Month;
}

public static class FinanceText
{
    public static string Frequency(Arcadia.Core.Organizer.RecurrenceFrequency f, int interval, FinanceContext ctx) =>
        interval <= 1 ? ctx.Loc["FinFreq_" + f] : ctx.Loc.Format("FinFreqEvery_" + f, interval);

    public static string Month(DateOnly month, CultureInfo c) => month.ToString("MMMM yyyy", c);

    public static IReadOnlyList<CategoryBar> Bars(IEnumerable<CategorySpending> spending, FinanceContext ctx, int take = 8)
    {
        var list = spending.ToList();
        var total = list.Sum(s => s.Amount);
        var max = list.Count == 0 ? 0 : list.Max(s => s.Amount);
        return list.Take(take).Select(s => new CategoryBar(s.CategoryId, ctx.CategoryLabel(s.Name), s.Icon, s.Accent, ctx.Format(s.Amount, s.Currency),
            max > 0 ? (double)(s.Amount / max) : 0, total > 0 ? (s.Amount / total).ToString("P0", ctx.Loc.Culture) : "—", ctx.Loc.Format("Fin_TxnCount", s.Count))).ToList();
    }
}
