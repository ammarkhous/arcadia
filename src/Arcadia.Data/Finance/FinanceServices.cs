using System.Text.Json;
using Arcadia.Core.Finance;
using Arcadia.Core.Organizer;
using Microsoft.EntityFrameworkCore;

namespace Arcadia.Data.Finance;

/// <summary>
/// "€35 groceries" from Quick Capture. Saves only when everything is clear: the pattern (<see cref="QuickExpenseParser"/>),
/// the account (the default one, or the only active account in the typed currency) and at most one matching category.
/// Otherwise nothing is saved and the caller opens the transaction editor with what was understood.
/// </summary>
public sealed class QuickExpenseService(IFinanceStore store, IDbContextFactory<ArcadiaDbContext> factory) : IQuickExpenseService
{
    public async Task<QuickExpenseResult> CaptureAsync(string text, DateTime now, DecimalStyle style, int? defaultAccountId, EntityRef? context = null, CancellationToken cancellationToken = default)
    {
        var parsed = QuickExpenseParser.Parse(text, now, style);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var accounts = await db.FinanceAccounts.AsNoTracking().Where(a => !a.IsArchived).Select(a => new { a.Id, a.Currency }).ToListAsync(cancellationToken).ConfigureAwait(false);
        int? account = null;
        if (parsed.Currency is { } cur)
        {
            if (defaultAccountId is { } d && accounts.Any(a => a.Id == d && a.Currency == cur)) account = d;
            else if (accounts.Count(a => a.Currency == cur) == 1) account = accounts.Single(a => a.Currency == cur).Id;
        }
        else if (defaultAccountId is { } d && accounts.Any(a => a.Id == d)) account = d;
        else if (accounts.Count == 1) account = accounts[0].Id;

        var category = parsed.IsClear ? await MatchCategoryAsync(db, parsed.Description, cancellationToken).ConfigureAwait(false) : null;
        var draft = new TransactionDraft(TransactionType.Expense, parsed.Date ?? DateOnly.FromDateTime(now), parsed.Amount ?? 0m, account ?? 0, category,
            parsed.IsClear ? parsed.Description : text.Trim(),
            ProjectId: context is { Kind: EntityKind.Project } p ? p.Id : null, TripId: context is { Kind: EntityKind.Trip } t ? t.Id : null);
        if (!parsed.IsClear || account == null || parsed.DateAmbiguous) return new QuickExpenseResult(null, draft, parsed);
        var saved = await store.CreateTransactionAsync(draft, cancellationToken).ConfigureAwait(false);
        return new QuickExpenseResult(saved, draft, parsed);
    }

    /// <summary>The one expense category named in the text (any language, custom names too); null when none or several match.</summary>
    internal static async Task<int?> MatchCategoryAsync(ArcadiaDbContext db, string description, CancellationToken ct)
    {
        var words = DuplicateRules.Normalize(description);
        if (words.Length == 0) return null;
        var padded = " " + words + " ";
        var cats = await db.FinanceCategories.AsNoTracking().Where(c => !c.IsArchived && c.Kind == CategoryKind.Expense).ToListAsync(ct).ConfigureAwait(false);
        var hits = cats.Where(c => new[] { c.Name }.Concat(FinanceDefaults.AliasesOf(c.Key)).Select(DuplicateRules.Normalize)
                .Any(n => n.Length > 0 && padded.Contains(" " + n + " ", StringComparison.Ordinal)))
            .Select(c => c.Id).Distinct().ToList();
        // a subcategory and its parent both matching: the more specific one wins
        if (hits.Count > 1)
        {
            var specific = hits.Where(id => cats.First(c => c.Id == id).ParentId is { } p && hits.Contains(p)).ToList();
            if (specific.Count == 1) return specific[0];
        }
        return hits.Count == 1 ? hits[0] : null;
    }
}

/// <summary>
/// A readable, versioned JSON copy of all finance data (accounts with balances, categories, transactions with their
/// document links, budgets, bills, goals). Written only when the user asks for it, to a place the user chooses.
/// </summary>
public sealed class FinanceJsonExporter(IDbContextFactory<ArcadiaDbContext> factory, IFinanceQueryService queries, TimeProvider time) : IFinanceExporter
{
    public const string Schema = "arcadia-finance";
    public const int SchemaVersion = 1;

    public async Task WriteJsonAsync(Stream target, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var balances = (await queries.GetAccountBalancesAsync(true, cancellationToken).ConfigureAwait(false)).ToDictionary(b => b.Account.Id, b => b.Balance);
        var accounts = await db.FinanceAccounts.AsNoTracking().OrderBy(a => a.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var categories = await db.FinanceCategories.AsNoTracking().OrderBy(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var transactions = await db.FinanceTransactions.AsNoTracking().Include(t => t.Documents).ThenInclude(d => d.Document).OrderBy(t => t.Date).ThenBy(t => t.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var budgets = await db.Budgets.AsNoTracking().OrderBy(b => b.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var recurring = await db.RecurringItems.AsNoTracking().Include(r => r.Occurrences).OrderBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var goals = await db.SavingsGoals.AsNoTracking().OrderBy(g => g.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

        var document = new
        {
            schema = Schema,
            version = SchemaVersion,
            exportedAtUtc = time.GetUtcNow().UtcDateTime,
            note = "Amounts are positive; 'type' gives the direction. A transfer moves 'amount' out of 'accountId' and 'toAmount' (or 'amount') into 'toAccountId'.",
            accounts = accounts.Select(a => new { a.Id, a.Name, type = a.Type.ToString(), a.Currency, a.OpeningBalance, balance = balances.GetValueOrDefault(a.Id), a.Description, a.IsArchived, a.CreatedAtUtc }),
            categories = categories.Select(c => new { c.Id, c.Key, name = FinanceDefaults.NameOf(c.Key, c.Name, "en"), customName = c.Name, kind = c.Kind.ToString(), c.ParentId, c.IsArchived }),
            transactions = transactions.Select(t => new
            {
                t.Id, type = t.Type.ToString(), date = t.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                time = t.Time?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), t.Amount, t.AccountId, t.ToAccountId, t.ToAmount, t.CategoryId,
                t.Description, t.Payee, t.Tags, t.Notes, t.ProjectId, t.TripId, t.RecurringItemId, t.NeedsReview, t.CreatedAtUtc, t.UpdatedAtUtc,
                documents = t.Documents.Select(d => new { d.DocumentId, role = d.Role.ToString(), name = d.Document?.DisplayName, path = d.Document?.Path }),
            }),
            budgets = budgets.Select(b => new { b.Id, b.Name, b.CategoryId, b.Amount, b.Currency, period = b.Period.ToString(), b.StartDate, b.EndDate, b.Rollover, b.TripId, b.Notes, b.IsArchived }),
            recurring = recurring.Select(r => new
            {
                r.Id, r.Name, kind = r.Kind.ToString(), r.Amount, r.Currency, r.AccountId, r.CategoryId, r.Payee, frequency = r.Frequency.ToString(), r.Interval,
                r.StartDate, r.EndDate, r.IsActive, r.Notes,
                occurrences = r.Occurrences.OrderBy(o => o.DueDate).Select(o => new { o.DueDate, status = o.Status.ToString(), o.TransactionId }),
            }),
            goals = goals.Select(g => new { g.Id, g.Name, g.TargetAmount, g.CurrentAmount, g.Currency, g.TargetDate, g.AccountId, g.Description, g.IsArchived }),
        };
        await JsonSerializer.SerializeAsync(target, document, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping },
            cancellationToken).ConfigureAwait(false);
    }
}
