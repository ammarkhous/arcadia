using System.Globalization;
using Arcadia.Core.Finance;
using Arcadia.Core.Organizer;

namespace Arcadia.Core.Oracle.Commands;

public static class FinanceCommands
{
    public static OracleItem Item(OracleCall c, TransactionRow t, string? group = null)
    {
        var r = new EntityRef(EntityKind.Transaction, t.Id);
        var sign = t.Type switch { TransactionType.Income => "+", TransactionType.Expense => "−", _ => "" };
        return new OracleItem
        {
            Title = t.Label.Length > 0 ? t.Label : c.T("Orc_Kind_Transaction"),
            Subtitle = string.Join(" · ", new[] { c.Env.Date(t.Date), t.CategoryName, t.AccountName }.Where(s => !string.IsNullOrEmpty(s))),
            Value = sign + c.Env.Money(t.Amount, t.Currency),
            ValueIsMoney = true,
            Icon = t.CategoryIcon ?? "Receipt",
            Accent = t.Type == TransactionType.Income ? "Green" : t.CategoryAccent ?? "Blue",
            Kind = OracleEntity.Transaction, Ref = r, Group = group,
            Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = r, IsPrimary = true }],
        };
    }

    /// <summary>A period for money questions: the one named, else this month.</summary>
    public static OraclePeriod PeriodOf(OracleCall c) => c.P.Period ?? OracleParser.PeriodOf("thismonth", c.Today);

    public static string RangeText(OracleCall c, OraclePeriod per) =>
        per.Key switch
        {
            "thismonth" or "lastmonth" or "nextmonth" => per.From.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", CultureInfo.CurrentCulture),
            "year" or "thisyear" or "lastyear" or "nextyear" => per.From.Year.ToString(CultureInfo.InvariantCulture),
            _ when per.IsSingleDay => c.Env.Date(per.From),
            _ => c.Env.Date(per.From) + " – " + c.Env.Date(per.To),
        };

    /// <summary>A category named in the text ("on groceries", "for fuel"), matched strongly by name.</summary>
    public static async Task<CategoryInfo?> CategoryAsync(OracleCall c, string? text, CategoryKind kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cats = await c.S.Finance.GetCategoriesAsync(c.Env.UiLanguage, false, ct).ConfigureAwait(false);
        var scored = cats.Where(x => x.Kind == kind).Select(x => (Cat: x, Score: Math.Max(OracleFuzzy.Similarity(text, x.Name), x.Key is { } k ? OracleFuzzy.Similarity(text, k) : 0)))
            .Where(x => x.Score >= OracleFuzzy.Strong).OrderByDescending(x => x.Score).ToList();
        return scored.Count == 1 || (scored.Count > 1 && scored[0].Score > scored[1].Score) ? scored[0].Cat : null;
    }

    public static OracleAction Section(OracleCall c, string tab, string labelKey) =>
        new() { Label = c.T(labelKey), Icon = "ArrowRight", Kind = OracleActionKind.Navigate, Section = "finance", Tab = tab };
}

/// <summary>
/// "how much did I spend this month", "spending on groceries last month", "income this year", "net this month",
/// "wie viel habe ich diesen Monat ausgegeben", "كم صرفت هذا الشهر". Sums per currency (never converted).
/// </summary>
public sealed class SpendingCommand : OracleCommandBase
{
    public override string Id => "finance.spending";
    public override string Module => "finance";
    public override OracleIntent Intent => OracleIntent.Calculate;
    public override OracleEntity[] Entities => [OracleEntity.Transaction];
    public override bool CanRefine => true;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["how much did I spend this month", "spending on groceries last month", "income this year", "net this month"];
    public override string[] ExamplesDe => ["wie viel habe ich diesen Monat ausgegeben", "Einnahmen dieses Jahr"];
    public override string[] ExamplesAr => ["كم صرفت هذا الشهر", "الدخل هذا العام"];

    public override double Match(OracleParse p, OracleContext context)
    {
        if (p.Amount != null || p.Verb is "create" or "open") return 0;
        if (p.Metric is "spending" or "income" or "net") return 0.9;
        if (p.Question == "howmuch" && p.Entity is OracleEntity.None or OracleEntity.Transaction) return 0.85;
        return 0;
    }

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        var per = FinanceCommands.PeriodOf(c);
        var metric = p.Metric is "income" or "net" ? p.Metric : "spending";
        var topic = p.Topic ?? p.Target ?? (p.Subject.Length > 0 ? p.Subject : null);
        var category = await FinanceCommands.CategoryAsync(c, topic, metric == "income" ? CategoryKind.Income : CategoryKind.Expense, ct).ConfigureAwait(false);
        var query = new TransactionQuery(category == null ? topic : null, per.From, per.To, CategoryId: category?.Id, Take: 1);
        var page = await c.S.Finance.QueryTransactionsAsync(query, c.Env.UiLanguage, ct).ConfigureAwait(false);
        var totals = page.Totals.Where(t => t.Count > 0).ToList();
        var range = FinanceCommands.RangeText(c, per);
        var label = category?.Name ?? topic;
        var metrics = totals.Select(t => metric switch
        {
            "income" => new OracleMetric(c.T("Orc_Income") + " · " + t.Currency, c.Env.Money(t.Income, t.Currency), "Green", true),
            "net" => new OracleMetric(c.T("Orc_Net") + " · " + t.Currency, c.Env.Money(t.Net, t.Currency), t.Net >= 0 ? "Green" : "Red", true),
            _ => new OracleMetric(c.T("Orc_Spent") + " · " + t.Currency, c.Env.Money(t.Expenses, t.Currency), "Amber", true),
        }).ToList();
        if (metric == "net")
            foreach (var t in totals)
            {
                metrics.Add(new OracleMetric(c.T("Orc_Income") + " · " + t.Currency, c.Env.Money(t.Income, t.Currency), "Green", true));
                metrics.Add(new OracleMetric(c.T("Orc_Spent") + " · " + t.Currency, c.Env.Money(t.Expenses, t.Currency), "Amber", true));
            }
        var headline = label != null ? c.T("Orc_SpendingOn_" + metric, label, range) : c.T("Orc_Spending_" + metric, range);
        var items = new List<OracleItem>();
        if (metric == "spending" && category == null && topic == null)
        {
            var byCat = await c.S.Finance.GetCategorySpendingAsync(per.From, per.To, c.Env.UiLanguage, null, true, ct).ConfigureAwait(false);
            var total = byCat.GroupBy(x => x.Currency).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            items.AddRange(byCat.OrderByDescending(x => x.Amount).Take(6).Select(x => new OracleItem
            {
                Title = x.Name, Subtitle = c.T("Orc_TransactionsN", x.Count), Value = c.Env.Money(x.Amount, x.Currency), ValueIsMoney = true,
                Icon = x.Icon, Accent = x.Accent, Group = c.T("Orc_Group_TopCategories"),
                Progress = total.GetValueOrDefault(x.Currency) is > 0 and var sum ? (double)(x.Amount / sum) : null,
            }));
        }
        if (totals.Count == 0) metrics.Add(new OracleMetric(c.T("Orc_Spent"), c.Env.Money(0, "EUR"), null, true));
        return new OracleResult
        {
            Kind = OracleResultKind.Metric,
            Headline = headline,
            Metrics = metrics,
            Items = items,
            Detail = totals.Count > 1 ? c.T("Orc_PerCurrency") : null,
            Actions = [FinanceCommands.Section(c, "transactions", "Orc_OpenTransactions")],
        };
    }
}

/// <summary>"largest expenses this month", "biggest purchases last year".</summary>
public sealed class LargestExpensesCommand : OracleCommandBase
{
    public override string Id => "finance.largest";
    public override string Module => "finance";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Transaction];
    public override bool CanRefine => true;
    public override string[] Examples => ["largest expenses this month", "biggest expenses this year"];
    public override string[] ExamplesDe => ["größte Ausgaben diesen Monat"];
    public override string[] ExamplesAr => ["اكبر المصروفات هذا الشهر"];

    public override double Match(OracleParse p, OracleContext context) => p.Metric == "largest" && p.Entity is OracleEntity.None or OracleEntity.Transaction ? 0.9 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var per = FinanceCommands.PeriodOf(c);
        var rows = await c.S.Finance.GetLargestExpensesAsync(per.From, per.To, c.Env.UiLanguage, 10, ct).ConfigureAwait(false);
        var items = rows.Select(t => FinanceCommands.Item(c, t)).ToList();
        return ListResult(c, c.T("Orc_Largest", FinanceCommands.RangeText(c, per)), items, "Orc_NoTransactions", FinanceCommands.Section(c, "transactions", "Orc_OpenTransactions"));
    }
}

/// <summary>"show transactions this week", "transactions about Amazon", "expenses for Italy trip".</summary>
public sealed class TransactionListCommand : OracleCommandBase
{
    public override string Id => "finance.transactions";
    public override string Module => "finance";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Transaction];
    public override bool CanRefine => true;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["show transactions this week", "transactions about Amazon", "expenses for Italy trip"];
    public override string[] ExamplesDe => ["zeige Buchungen diese Woche"];
    public override string[] ExamplesAr => ["اعرض المصروفات هذا الاسبوع"];

    public override double Match(OracleParse p, OracleContext context) =>
        Is(p, OracleEntity.Transaction) && IsQuery(p) && p.Amount == null && p.Metric == null && !p.AllRelated ? 0.8 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        if (await UnresolvedTargetAsync(c, ct, x => $"show expenses for {EntityRef.KindName(x.Ref.Kind)} \"{x.Name}\"", EntityKind.Project, EntityKind.Trip).ConfigureAwait(false) is { } ask) return ask;
        var scope = await ScopeAsync(c, ct, EntityKind.Project, EntityKind.Trip).ConfigureAwait(false);
        var per = scope != null && p.Period == null ? null : FinanceCommands.PeriodOf(c);
        var text = p.Topic ?? (p.Subject.Length > 0 ? p.Subject : null);
        var type = p.Input.Tokens.Any(t => t.Norm is "expense" or "expenses" or "ausgaben" or "ausgabe" or "مصروفات" or "مصاريف") ? TransactionType.Expense : (TransactionType?)null;
        var page = await c.S.Finance.QueryTransactionsAsync(new TransactionQuery(text, per?.From, per?.To, Type: type,
            ProjectId: scope is { Ref.Kind: EntityKind.Project } sp ? sp.Ref.Id : null, TripId: scope is { Ref.Kind: EntityKind.Trip } st ? st.Ref.Id : null, Take: 50), c.Env.UiLanguage, ct).ConfigureAwait(false);
        var items = page.Rows.Select(t => FinanceCommands.Item(c, t)).ToList();
        var result = ListResult(c, c.T("Orc_ListTransactions", page.TotalCount), items, "Orc_NoTransactions",
            scope is { } s ? OpenAction(c, s.Ref, "Expenses", false) : FinanceCommands.Section(c, "transactions", "Orc_OpenTransactions"), scope?.Ref, scope?.Name);
        foreach (var t in page.Totals.Where(t => t.Count > 0))
            result.Metrics.Add(new OracleMetric(c.T("Orc_Spent") + " · " + t.Currency, c.Env.Money(t.Expenses, t.Currency), "Amber", true));
        return result;
    }
}

/// <summary>"show my subscriptions", "how much do subscriptions cost per month", "zeige meine Abos".</summary>
public sealed class SubscriptionsCommand : OracleCommandBase
{
    public override string Id => "finance.subscriptions";
    public override string Module => "finance";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Subscription];
    public override string[] Examples => ["show my subscriptions", "how much do my subscriptions cost"];
    public override string[] ExamplesDe => ["zeige meine Abos"];
    public override string[] ExamplesAr => ["اعرض اشتراكاتي"];

    public override double Match(OracleParse p, OracleContext context) => Is(p, OracleEntity.Subscription) && (IsQuery(p) || p.Question == "howmuch") && p.Verb != "create" ? 0.9 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var rows = await c.S.Finance.GetSubscriptionsAsync(c.Today, c.Env.UiLanguage, RecurringKind.Subscription, false, ct).ConfigureAwait(false);
        var items = rows.OrderByDescending(r => r.MonthlyEquivalent).Select(r => new OracleItem
        {
            Title = r.Item.Name,
            Subtitle = string.Join(" · ", new[] { r.NextDue is { } d ? c.T("Orc_NextDue", c.Env.Date(d)) : null, r.CategoryName, r.AccountName }.Where(s => s != null)),
            Value = c.Env.Money(r.Item.Amount, r.Item.Currency), ValueIsMoney = true, Icon = "Repeat", Accent = "Violet", Kind = OracleEntity.Subscription,
            Ref = new EntityRef(EntityKind.Recurring, r.Item.Id),
            Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = new EntityRef(EntityKind.Recurring, r.Item.Id), IsPrimary = true }],
        }).ToList();
        var result = ListResult(c, c.T("Orc_ListSubscriptions", items.Count), items, "Orc_NoSubscriptions", FinanceCommands.Section(c, "recurring", "Orc_OpenRecurring"));
        result = new OracleResult { Kind = items.Count == 0 ? OracleResultKind.List : OracleResultKind.Metric, Headline = result.Headline, Items = result.Items, Actions = result.Actions };
        foreach (var g in rows.GroupBy(r => r.Item.Currency))
        {
            result.Metrics.Add(new OracleMetric(c.T("Orc_PerMonth") + " · " + g.Key, c.Env.Money(g.Sum(r => r.MonthlyEquivalent), g.Key), "Violet", true));
            result.Metrics.Add(new OracleMetric(c.T("Orc_PerYear") + " · " + g.Key, c.Env.Money(g.Sum(r => r.AnnualCost), g.Key), null, true));
        }
        return result;
    }
}

/// <summary>"upcoming bills", "bills due this week", "overdue bills".</summary>
public sealed class BillsCommand : OracleCommandBase
{
    public override string Id => "finance.bills";
    public override string Module => "finance";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Bill];
    public override bool CanRefine => true;
    public override string[] Examples => ["upcoming bills", "bills due this week", "overdue bills"];
    public override string[] ExamplesDe => ["kommende Rechnungen"];
    public override string[] ExamplesAr => ["الفواتير القادمة"];

    public override double Match(OracleParse p, OracleContext context) => Is(p, OracleEntity.Bill) && IsQuery(p) && p.Verb != "create" ? 0.9 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        var per = p.Period ?? new OraclePeriod(c.Today, c.Today.AddDays(30), "within");
        var rows = await c.S.Finance.GetUpcomingBillsAsync(per.From, per.To, c.Env.UiLanguage, 60, ct).ConfigureAwait(false);
        IEnumerable<BillDue> list = rows;
        if (p.HasFilter("overdue")) list = list.Where(b => b.IsOverdue);
        var items = list.OrderBy(b => b.DueDate).Select(b => new OracleItem
        {
            Title = b.Item.Name,
            Subtitle = (b.IsOverdue ? c.T("Orc_Overdue", c.Env.Date(b.DueDate)) : c.T("Orc_Due", c.Env.Date(b.DueDate))) + (b.AccountName != null ? " · " + b.AccountName : ""),
            Value = c.Env.Money(b.Item.Amount, b.Item.Currency), ValueIsMoney = true, Icon = "Receipt", Accent = b.IsOverdue ? "Red" : "Amber",
            Kind = OracleEntity.Bill, Ref = new EntityRef(EntityKind.Recurring, b.Item.Id),
            Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = new EntityRef(EntityKind.Recurring, b.Item.Id), IsPrimary = true }],
        }).ToList();
        var result = ListResult(c, c.T("Orc_ListBills", items.Count), items, "Orc_NoBills", FinanceCommands.Section(c, "recurring", "Orc_OpenRecurring"));
        foreach (var g in list.GroupBy(b => b.Item.Currency))
            result.Metrics.Add(new OracleMetric(c.T("Orc_Total") + " · " + g.Key, c.Env.Money(g.Sum(b => b.Item.Amount), g.Key), "Amber", true));
        return result;
    }
}

/// <summary>"budget status", "how are my budgets", "budgets over limit".</summary>
public sealed class BudgetsCommand : OracleCommandBase
{
    public override string Id => "finance.budgets";
    public override string Module => "finance";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Budget];
    public override string[] Examples => ["budget status", "how are my budgets"];
    public override string[] ExamplesDe => ["Budget Status"];
    public override string[] ExamplesAr => ["الميزانيات"];

    public override double Match(OracleParse p, OracleContext context) =>
        p.Metric == "budgetstatus" ? 0.95 : Is(p, OracleEntity.Budget) && IsQuery(p) && p.Target == null && p.Verb != "create" ? 0.85 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var month = c.P.Period?.From ?? c.Today;
        var rows = await c.S.Finance.GetBudgetStatusAsync(new DateOnly(month.Year, month.Month, 1), ct).ConfigureAwait(false);
        var items = rows.OrderByDescending(b => b.Percent).Select(b => new OracleItem
        {
            Title = b.Budget.Name ?? b.Budget.Category?.Name ?? c.T("Orc_Kind_Budget"),
            Subtitle = c.T("Orc_BudgetLeft", c.Env.Money(b.Remaining, b.Budget.Currency)),
            Value = c.Env.Money(b.Spent, b.Budget.Currency) + " / " + c.Env.Money(b.Available, b.Budget.Currency),
            ValueIsMoney = true,
            Progress = Math.Min(1, b.Percent / 100.0),
            Icon = "Chart",
            Accent = b.Level switch { BudgetLevel.Over => "Red", BudgetLevel.Approaching => "Amber", _ => "Green" },
            Kind = OracleEntity.Budget, Ref = new EntityRef(EntityKind.Budget, b.Budget.Id),
            Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = new EntityRef(EntityKind.Budget, b.Budget.Id), IsPrimary = true }],
        }).ToList();
        var over = rows.Count(b => b.Level == BudgetLevel.Over);
        var result = ListResult(c, over > 0 ? c.T("Orc_BudgetsOver", over, items.Count) : c.T("Orc_BudgetsOk", items.Count), items, "Orc_NoBudgets", FinanceCommands.Section(c, "budgets", "Orc_OpenBudgets"));
        return result;
    }
}

/// <summary>"savings goals", "how are my goals".</summary>
public sealed class GoalsCommand : OracleCommandBase
{
    public override string Id => "finance.goals";
    public override string Module => "finance";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Goal];
    public override string[] Examples => ["show savings goals"];
    public override string[] ExamplesDe => ["zeige Sparziele"];
    public override string[] ExamplesAr => ["اعرض اهداف الادخار"];

    public override double Match(OracleParse p, OracleContext context) => Is(p, OracleEntity.Goal) && IsQuery(p) && p.Verb != "create" ? 0.85 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var rows = await c.S.Finance.GetSavingsGoalsAsync(c.Today, false, ct).ConfigureAwait(false);
        var items = rows.Select(g => new OracleItem
        {
            Title = g.Goal.Name,
            Subtitle = g.Goal.TargetDate is { } d ? c.T("Orc_TargetDate", c.Env.Date(d)) : null,
            Value = c.Env.Money(g.Saved, g.Goal.Currency) + " / " + c.Env.Money(g.Target, g.Goal.Currency),
            ValueIsMoney = true,
            Progress = g.Target > 0 ? Math.Min(1, (double)(g.Saved / g.Target)) : 0,
            Icon = g.Goal.Icon, Accent = g.Goal.Accent, Kind = OracleEntity.Goal, Ref = new EntityRef(EntityKind.Goal, g.Goal.Id),
            Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = new EntityRef(EntityKind.Goal, g.Goal.Id), IsPrimary = true }],
        }).ToList();
        return ListResult(c, c.T("Orc_ListGoals", items.Count), items, "Orc_NoGoals", FinanceCommands.Section(c, "goals", "Orc_OpenGoals"));
    }
}

/// <summary>"account balances", "what is my balance", "Kontostand".</summary>
public sealed class BalancesCommand : OracleCommandBase
{
    public override string Id => "finance.accounts";
    public override string Module => "finance";
    public override OracleIntent Intent => OracleIntent.Show;
    public override OracleEntity[] Entities => [OracleEntity.Account];
    public override string[] Examples => ["account balances", "what is my balance"];
    public override string[] ExamplesDe => ["Kontostand"];
    public override string[] ExamplesAr => ["رصيد الحسابات"];

    public override double Match(OracleParse p, OracleContext context) => Is(p, OracleEntity.Account) && IsQuery(p) && p.Verb != "create" && p.Amount == null ? 0.85 : 0;

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var rows = await c.S.Finance.GetAccountBalancesAsync(false, ct).ConfigureAwait(false);
        var items = rows.Select(a => new OracleItem
        {
            Title = a.Account.Name,
            Subtitle = a.LastActivity is { } d ? c.T("Orc_LastActivity", c.Env.Date(d)) : null,
            Value = c.Env.Money(a.Balance, a.Account.Currency), ValueIsMoney = true,
            Icon = "Bank", Accent = a.Balance < 0 ? "Red" : "Cyan", Kind = OracleEntity.Account, Ref = new EntityRef(EntityKind.Account, a.Account.Id),
            Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = new EntityRef(EntityKind.Account, a.Account.Id), IsPrimary = true }],
        }).ToList();
        var result = ListResult(c, c.T("Orc_ListAccounts", items.Count), items, "Orc_NoAccounts", FinanceCommands.Section(c, "accounts", "Orc_OpenAccounts"));
        foreach (var g in rows.GroupBy(a => a.Account.Currency))
            result.Metrics.Add(new OracleMetric(c.T("Orc_Total") + " · " + g.Key, c.Env.Money(g.Sum(a => a.Balance), g.Key), "Cyan", true));
        return result;
    }
}

/// <summary>
/// "I spent 35 euro on groceries", "add expense 12.50 coffee from Visa", "Ausgabe 20 € Tanken", "صرفت 35 يورو على البقالة".
/// Always a preview. The account must be named, set as default, or the only one — never guessed; otherwise the preview
/// can only open the full editor.
/// </summary>
public sealed class ExpenseCreateCommand : OracleCommandBase
{
    public override string Id => "finance.expense";
    public override string Module => "finance";
    public override OracleIntent Intent => OracleIntent.Create;
    public override OracleEntity[] Entities => [OracleEntity.Transaction];
    public override OracleSafety Safety => OracleSafety.Create;
    public override bool UsesFreeText => true;
    public override string[] Examples => ["I spent 35 euro on groceries", "add expense 12.50 coffee", "paid 60 EUR for fuel from Visa"];
    public override string[] ExamplesDe => ["Ausgabe 20 € Tanken", "ich habe 35 Euro für Lebensmittel ausgegeben"];
    public override string[] ExamplesAr => ["صرفت 35 يورو على البقالة"];

    public override double Match(OracleParse p, OracleContext context)
    {
        if (p.Amount == null) return 0;
        if (p.Verb == "create" && p.Entity is OracleEntity.Transaction or OracleEntity.None) return 0.9;
        if (p.Metric == "spending" && p.Question == null) return 0.9;
        if (p.Input.Tokens.Any(t => t.Norm is "paid" or "bought" or "bezahlt" or "gekauft" or "دفعت" or "اشتريت")) return 0.85;
        return 0;
    }

    public override async Task<OracleResult> ExecuteAsync(OracleCall c, CancellationToken ct)
    {
        var p = c.P;
        if (p.AmbiguousDate is { } amb) return CalendarCommands.AskDate(c, amb, p.Input.Original);
        var amount = p.Amount!.Value;
        var words = (p.Payload != null ? [p.Payload.Trim()] : new List<string>()).Concat(p.Segments).Where(s => s.Length > 0).ToList();
        var date = p.Period?.From ?? c.Today;

        // account: named ("from Visa"), else the default, else the only one
        var accounts = await c.S.Finance.GetAccountBalancesAsync(false, ct).ConfigureAwait(false);
        AccountBalance? account = null;
        string? accountWords = null;
        foreach (var w in words)
        {
            var hit = accounts.Select(a => (a, s: OracleFuzzy.Similarity(w, a.Account.Name))).Where(x => x.s >= OracleFuzzy.Strong).OrderByDescending(x => x.s).ToList();
            if (hit.Count == 1) { account = hit[0].a; accountWords = w; break; }
        }
        if (account == null && c.Env.DefaultAccountId is { } def) account = accounts.FirstOrDefault(a => a.Account.Id == def);
        if (account == null && accounts.Count == 1) account = accounts[0];
        if (account != null && accountWords == null && c.Env.DefaultAccountId == account.Account.Id) c.Notes.Add(c.T("Orc_DefaultAccountUsed", account.Account.Name));
        words.Remove(accountWords ?? "\u0000");

        CategoryInfo? category = null;
        string? categoryWords = null;
        foreach (var w in words)
            if (await FinanceCommands.CategoryAsync(c, w, CategoryKind.Expense, ct).ConfigureAwait(false) is { } cat) { category = cat; categoryWords = w; break; }
        var description = string.Join(' ', words.Where(w => w != categoryWords)).Trim();
        if (description.Length == 0) description = category?.Name ?? string.Empty;

        var currency = p.Currency ?? account?.Account.Currency ?? "EUR";
        if (account != null && p.Currency != null && p.Currency != account.Account.Currency) c.Notes.Add(c.T("Orc_CurrencyDiffers", p.Currency, account.Account.Currency));
        var draft = new TransactionDraft(TransactionType.Expense, date, amount, account?.Account.Id ?? 0, category?.Id, description,
            ProjectId: c.Context.Current is { Kind: EntityKind.Project } pr ? pr.Id : null, TripId: c.Context.Current is { Kind: EntityKind.Trip } tr ? tr.Id : null);
        if (draft.ProjectId != null || draft.TripId != null) c.Notes.Add(c.T("Orc_NoteFromPage", c.Context.CurrentName));

        var preview = new OraclePreview
        {
            Title = c.T("Orc_CreateExpenseTitle"),
            Icon = "Receipt",
            Fields =
            [
                (c.T("Orc_Field_Amount"), c.Env.Money(amount, currency)),
                (c.T("Orc_Field_Description"), description.Length > 0 ? description : "—"),
                (c.T("Orc_Field_Category"), category?.Name ?? c.T("Orc_Uncategorized")),
                (c.T("Orc_Field_Account"), account?.Account.Name ?? c.T("Orc_ChooseAccount")),
                (c.T("Orc_Field_Date"), c.Env.Date(date)),
            ],
            Confirm = account == null
                ? new OracleAction { Label = c.T("Orc_Confirm"), Icon = "Check", Kind = OracleActionKind.Confirm, IsPrimary = true, IsEnabled = false }
                : c.ConfirmAction(async t =>
                {
                    var saved = await c.S.Finance.CreateTransactionAsync(draft, t).ConfigureAwait(false);
                    var r = new EntityRef(EntityKind.Transaction, saved.Id);
                    return new OracleResult
                    {
                        Kind = OracleResultKind.Done, Headline = c.T("Orc_ExpenseSaved", c.Env.Money(amount, currency)), HeadlineIsMoney = true,
                        Actions = [new OracleAction { Label = c.T("Orc_Open"), Icon = "ArrowRight", Kind = OracleActionKind.Open, Target = r, IsPrimary = true }],
                    };
                }),
            Edit = new OracleAction { Label = c.T("Orc_Edit"), Icon = "Journal", Kind = OracleActionKind.EditTransaction, Payload = draft },
        };
        if (account == null) preview.Notes.Add(c.T("Orc_AccountRequired"));
        return await c.ChangeAsync(preview, false, ct, alwaysConfirm: true).ConfigureAwait(false);
    }
}
