using Arcadia.Core.Finance;
using Arcadia.Core.Organizer;
using Arcadia.Core.Search;
using Arcadia.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data.Finance;

public sealed partial class FinanceStore
{
    // ───────────── budgets ─────────────

    public async Task<IReadOnlyList<Budget>> GetBudgetsAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Budgets.AsNoTracking().Include(b => b.Category).Where(b => includeArchived || !b.IsArchived).OrderBy(b => b.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<BudgetDraft> ValidateAsync(ArcadiaDbContext db, BudgetDraft d, int? selfId, CancellationToken ct)
    {
        CheckAmount(d.Amount);
        var currency = CheckCurrency(d.Currency);
        CheckDate(d.StartDate);
        if (d.CategoryId is { } cid)
        {
            var kind = await db.FinanceCategories.Where(c => c.Id == cid).Select(c => (CategoryKind?)c.Kind).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (kind == null) throw new FinanceException("Fin_ErrNoCategory");
            if (kind != CategoryKind.Expense) throw new FinanceException("Fin_ErrBudgetIncome");
        }
        if (d.TripId is { } tid && !await db.Trips.AnyAsync(t => t.Id == tid, ct).ConfigureAwait(false)) throw new FinanceException("Fin_ErrGone");
        DateOnly start = d.StartDate;
        DateOnly? end = d.EndDate;
        if (d.Period == BudgetPeriod.Monthly)
        {
            start = FinanceMath.MonthStart(start);
            if (end is { } e) end = FinanceMath.MonthEnd(CheckDate(e));
            // one monthly budget per category and currency (trip budgets are separate)
            if (d.TripId == null && await db.Budgets.AnyAsync(b => b.Id != (selfId ?? 0) && !b.IsArchived && b.Period == BudgetPeriod.Monthly && b.TripId == null
                                                                && b.CategoryId == d.CategoryId && b.Currency == currency, ct).ConfigureAwait(false))
                throw new FinanceException("Fin_ErrBudgetExists");
        }
        else if (end == null) throw new FinanceException("Fin_ErrBudgetRange");
        if (end is { } last && CheckDate(last) < start) throw new FinanceException("Fin_ErrBudgetRange");
        return d with { Currency = currency, StartDate = start, EndDate = end, Name = Optional(d.Name, 120, "Fin_ErrTooLong"), Notes = Optional(d.Notes, 2000, "Fin_ErrTooLong"),
            Rollover = d.Period == BudgetPeriod.Monthly && d.Rollover };
    }

    private static void Apply(Budget b, BudgetDraft d)
    {
        b.CategoryId = d.CategoryId;
        b.Amount = d.Amount;
        b.Currency = d.Currency;
        b.Period = d.Period;
        b.StartDate = d.StartDate;
        b.EndDate = d.EndDate;
        b.Rollover = d.Rollover;
        b.Name = d.Name;
        b.TripId = d.TripId;
        b.Notes = d.Notes;
    }

    public async Task<Budget> CreateBudgetAsync(BudgetDraft draft, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var d = await ValidateAsync(db, draft, null, cancellationToken).ConfigureAwait(false);
        var now = UtcNow;
        var b = new Budget { CreatedAtUtc = now, UpdatedAtUtc = now };
        Apply(b, d);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.Budgets.Add(b);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexBudgetAsync(db, b, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
        return b;
    }

    public async Task UpdateBudgetAsync(int id, BudgetDraft draft, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var b = await db.Budgets.FirstOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        var d = await ValidateAsync(db, draft, id, cancellationToken).ConfigureAwait(false);
        Apply(b, d);
        b.UpdatedAtUtc = UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexBudgetAsync(db, b, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    public async Task DeleteBudgetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var n = await db.Budgets.Where(b => b.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (n == 0) throw new KeyNotFoundException();
        await SearchIndex.DeleteAsync(db, SearchResultKind.Budget, id, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    // ───────────── recurring bills, subscriptions, income ─────────────

    public async Task<IReadOnlyList<RecurringItem>> GetRecurringItemsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.RecurringItems.AsNoTracking().Include(r => r.Occurrences).Include(r => r.Account).Include(r => r.Category)
            .OrderBy(r => r.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecurringItem?> GetRecurringItemAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.RecurringItems.AsNoTracking().Include(r => r.Occurrences).Include(r => r.Account).Include(r => r.Category)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RecurringDraft> ValidateAsync(ArcadiaDbContext db, RecurringDraft d, CancellationToken ct)
    {
        var name = CheckName(d.Name, 200, "Fin_ErrRecurringName");
        CheckAmount(d.Amount);
        var currency = CheckCurrency(d.Currency);
        if (d.Frequency == RecurrenceFrequency.None) throw new FinanceException("Fin_ErrRecurrence");
        if (d.Interval is < 1 or > 999) throw new FinanceException("Fin_ErrRecurrence");
        CheckDate(d.StartDate);
        if (d.EndDate is { } end && CheckDate(end) < d.StartDate) throw new FinanceException("Fin_ErrRecurrenceEnd");
        if (d.AccountId is { } aid)
        {
            var accountCurrency = await db.FinanceAccounts.Where(a => a.Id == aid).Select(a => a.Currency).FirstOrDefaultAsync(ct).ConfigureAwait(false)
                                  ?? throw new FinanceException("Fin_ErrNoAccount");
            if (accountCurrency != currency) throw new FinanceException("Fin_ErrCurrencyMismatch");
        }
        if (d.CategoryId is { } cid && !await db.FinanceCategories.AnyAsync(c => c.Id == cid, ct).ConfigureAwait(false)) throw new FinanceException("Fin_ErrNoCategory");
        return d with { Name = name, Currency = currency, Payee = Optional(d.Payee, 200, "Fin_ErrTooLong"), Notes = Optional(d.Notes, 4000, "Fin_ErrTooLong") };
    }

    private static void Apply(RecurringItem r, RecurringDraft d)
    {
        r.Name = d.Name;
        r.Kind = d.Kind;
        r.Amount = d.Amount;
        r.Currency = d.Currency;
        r.Frequency = d.Frequency;
        r.Interval = d.Interval;
        r.StartDate = d.StartDate;
        r.EndDate = d.EndDate;
        r.AccountId = d.AccountId;
        r.CategoryId = d.CategoryId;
        r.Payee = d.Payee;
        r.IsActive = d.IsActive;
        r.ShowOnCalendar = d.ShowOnCalendar;
        r.Notes = d.Notes;
    }

    public async Task<RecurringItem> CreateRecurringItemAsync(RecurringDraft draft, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var d = await ValidateAsync(db, draft, cancellationToken).ConfigureAwait(false);
        var now = UtcNow;
        // due dates before today are history: a rent that started in 2024 does not appear as two years unpaid
        var r = new RecurringItem { CreatedAtUtc = now, UpdatedAtUtc = now, TrackedFrom = d.StartDate > Today ? d.StartDate : Today };
        Apply(r, d);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.RecurringItems.Add(r);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexRecurringAsync(db, r, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
        return r;
    }

    public async Task UpdateRecurringItemAsync(int id, RecurringDraft draft, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var r = await db.RecurringItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        var d = await ValidateAsync(db, draft, cancellationToken).ConfigureAwait(false);
        Apply(r, d);
        if (r.StartDate > r.TrackedFrom) r.TrackedFrom = r.StartDate;
        r.UpdatedAtUtc = UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexRecurringAsync(db, r, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    /// <summary>Removes the item and its paid/skipped marks; payments already recorded stay (they are real transactions).</summary>
    public async Task DeleteRecurringItemAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.FinanceTransactions.Where(t => t.RecurringItemId == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.RecurringItemId, (int?)null), cancellationToken).ConfigureAwait(false);
        await db.RecurringOccurrences.Where(o => o.RecurringItemId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var n = await db.RecurringItems.Where(r => r.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (n == 0) throw new KeyNotFoundException();
        await SearchIndex.DeleteAsync(db, SearchResultKind.Recurring, id, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    public async Task<FinanceTransaction> MarkPaidAsync(int itemId, DateOnly dueDate, int? accountId = null, decimal? amount = null, DateOnly? paidOn = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var item = await db.RecurringItems.Include(r => r.Occurrences).FirstOrDefaultAsync(r => r.Id == itemId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        if (!Recurrence.Dates(RecurringMath.RuleOf(item), dueDate, dueDate).Any()) throw new FinanceException("Fin_ErrNotDue");
        var existing = item.Occurrences.FirstOrDefault(o => o.DueDate == dueDate);
        if (existing?.Status == OccurrenceStatus.Paid) throw new FinanceException("Fin_ErrAlreadyPaid");
        var account = accountId ?? item.AccountId ?? throw new FinanceException("Fin_ErrNeedAccount");
        var accountCurrency = await db.FinanceAccounts.Where(a => a.Id == account).Select(a => a.Currency).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                              ?? throw new FinanceException("Fin_ErrNoAccount");
        if (accountCurrency != item.Currency) throw new FinanceException("Fin_ErrCurrencyMismatch");

        var draft = await ValidateAsync(db, new TransactionDraft(item.TransactionType, paidOn ?? dueDate, amount ?? item.Amount, account, item.CategoryId, item.Name, item.Payee), true, null, cancellationToken)
            .ConfigureAwait(false);
        var now = UtcNow;
        var t = new FinanceTransaction { CreatedAtUtc = now, UpdatedAtUtc = now, RecurringItemId = item.Id };
        Apply(t, draft);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.FinanceTransactions.Add(t);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (existing != null) db.RecurringOccurrences.Remove(existing);
        db.RecurringOccurrences.Add(new RecurringOccurrence { RecurringItemId = item.Id, DueDate = dueDate, Status = OccurrenceStatus.Paid, TransactionId = t.Id, CreatedAtUtc = now });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexTransactionAsync(db, t, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
        return t;
    }

    public async Task SkipAsync(int itemId, DateOnly dueDate, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var item = await db.RecurringItems.Include(r => r.Occurrences).FirstOrDefaultAsync(r => r.Id == itemId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        if (!Recurrence.Dates(RecurringMath.RuleOf(item), dueDate, dueDate).Any()) throw new FinanceException("Fin_ErrNotDue");
        var existing = item.Occurrences.FirstOrDefault(o => o.DueDate == dueDate);
        if (existing?.Status == OccurrenceStatus.Paid) throw new FinanceException("Fin_ErrAlreadyPaid");
        if (existing != null) return;
        db.RecurringOccurrences.Add(new RecurringOccurrence { RecurringItemId = itemId, DueDate = dueDate, Status = OccurrenceStatus.Skipped, CreatedAtUtc = UtcNow });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    public async Task ReopenAsync(int itemId, DateOnly dueDate, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var o = await db.RecurringOccurrences.FirstOrDefaultAsync(x => x.RecurringItemId == itemId && x.DueDate == dueDate, cancellationToken).ConfigureAwait(false);
        if (o == null) return;
        if (o.Status == OccurrenceStatus.Paid) throw new FinanceException("Fin_ErrReopenPaid");
        db.RecurringOccurrences.Remove(o);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    // ───────────── savings goals ─────────────

    public async Task<IReadOnlyList<SavingsGoal>> GetGoalsAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.SavingsGoals.AsNoTracking().Include(g => g.Account).Where(g => includeArchived || !g.IsArchived).OrderBy(g => g.TargetDate == null).ThenBy(g => g.TargetDate).ThenBy(g => g.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GoalDraft> ValidateAsync(ArcadiaDbContext db, GoalDraft d, CancellationToken ct)
    {
        var name = CheckName(d.Name, 200, "Fin_ErrGoalName");
        CheckAmount(d.TargetAmount);
        CheckAmount(d.CurrentAmount, allowZero: true);
        var currency = CheckCurrency(d.Currency);
        if (d.TargetDate is { } td) CheckDate(td);
        if (d.AccountId is { } aid)
        {
            var accountCurrency = await db.FinanceAccounts.Where(a => a.Id == aid).Select(a => a.Currency).FirstOrDefaultAsync(ct).ConfigureAwait(false)
                                  ?? throw new FinanceException("Fin_ErrNoAccount");
            if (accountCurrency != currency) throw new FinanceException("Fin_ErrCurrencyMismatch");
        }
        return d with { Name = name, Currency = currency, Description = Optional(d.Description, 4000, "Fin_ErrTooLong"), Icon = Icon(d.Icon, "Target"), Accent = Accent(d.Accent) };
    }

    private static void Apply(SavingsGoal g, GoalDraft d)
    {
        g.Name = d.Name;
        g.TargetAmount = d.TargetAmount;
        g.CurrentAmount = d.CurrentAmount;
        g.Currency = d.Currency;
        g.TargetDate = d.TargetDate;
        g.AccountId = d.AccountId;
        g.Description = d.Description;
        g.Icon = d.Icon;
        g.Accent = d.Accent;
    }

    public async Task<SavingsGoal> CreateGoalAsync(GoalDraft draft, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var d = await ValidateAsync(db, draft, cancellationToken).ConfigureAwait(false);
        var now = UtcNow;
        var g = new SavingsGoal { CreatedAtUtc = now, UpdatedAtUtc = now };
        Apply(g, d);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.SavingsGoals.Add(g);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexGoalAsync(db, g, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
        return g;
    }

    public async Task UpdateGoalAsync(int id, GoalDraft draft, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var g = await db.SavingsGoals.FirstOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        var d = await ValidateAsync(db, draft, cancellationToken).ConfigureAwait(false);
        Apply(g, d);
        g.UpdatedAtUtc = UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexGoalAsync(db, g, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    public async Task SetGoalArchivedAsync(int id, bool archived, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var n = await db.SavingsGoals.Where(g => g.Id == id).ExecuteUpdateAsync(s => s.SetProperty(g => g.IsArchived, archived).SetProperty(g => g.UpdatedAtUtc, UtcNow), cancellationToken).ConfigureAwait(false);
        if (n == 0) throw new KeyNotFoundException();
        Bump();
    }

    public async Task DeleteGoalAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var n = await db.SavingsGoals.Where(g => g.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (n == 0) throw new KeyNotFoundException();
        await SearchIndex.DeleteAsync(db, SearchResultKind.Goal, id, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    public async Task AddToGoalAsync(int id, decimal amount, CancellationToken cancellationToken = default)
    {
        CheckAmount(amount, allowNegative: true);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var g = await db.SavingsGoals.FirstOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        if (g.AccountId != null) throw new FinanceException("Fin_ErrGoalFollowsAccount");
        var next = g.CurrentAmount + amount;
        if (next < 0) throw new FinanceException("Fin_ErrGoalNegative");
        g.CurrentAmount = CheckAmount(next, allowZero: true);
        g.UpdatedAtUtc = UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }
}
