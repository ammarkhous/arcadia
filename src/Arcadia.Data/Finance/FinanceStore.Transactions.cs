using Arcadia.Core.Finance;
using Arcadia.Core.Search;
using Arcadia.Data.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arcadia.Data.Finance;

public sealed partial class FinanceStore
{
    /// <summary>
    /// Checks a draft against the accounting model and the library: positive amount, known accounts, transfers between two
    /// different accounts (a destination amount only across currencies), no category on transfers, existing links.
    /// Returns the draft with normalised text.
    /// </summary>
    private static async Task<TransactionDraft> ValidateAsync(ArcadiaDbContext db, TransactionDraft d, bool isNew, Dictionary<int, FinanceAccount>? accountCache, CancellationToken ct)
    {
        CheckAmount(d.Amount);
        CheckDate(d.Date);
        async Task<FinanceAccount> Account(int id)
        {
            if (accountCache != null && accountCache.TryGetValue(id, out var cached)) return cached;
            var a = await db.FinanceAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false) ?? throw new FinanceException("Fin_ErrNoAccount");
            if (accountCache != null) accountCache[id] = a;
            return a;
        }
        var account = await Account(d.AccountId).ConfigureAwait(false);
        if (isNew && account.IsArchived) throw new FinanceException("Fin_ErrAccountArchived");

        int? toAccount = null;
        decimal? toAmount = null;
        int? category = d.CategoryId;
        if (d.Type == TransactionType.Transfer)
        {
            if (d.ToAccountId is not { } toId || toId == d.AccountId) throw new FinanceException("Fin_ErrTransferAccounts");
            var to = await Account(toId).ConfigureAwait(false);
            if (isNew && to.IsArchived) throw new FinanceException("Fin_ErrAccountArchived");
            toAccount = toId;
            if (to.Currency != account.Currency)
            {
                if (d.ToAmount is not { } ta) throw new FinanceException("Fin_ErrToAmount");
                toAmount = CheckAmount(ta, "Fin_ErrToAmount");
            }
            category = null; // a transfer is neither income nor expense
        }
        if (category is { } cid && !await db.FinanceCategories.AnyAsync(c => c.Id == cid, ct).ConfigureAwait(false)) throw new FinanceException("Fin_ErrNoCategory");
        if (d.ProjectId is { } pid && !await db.Projects.AnyAsync(p => p.Id == pid, ct).ConfigureAwait(false)) throw new FinanceException("Fin_ErrGone");
        if (d.TripId is { } tid && !await db.Trips.AnyAsync(t => t.Id == tid, ct).ConfigureAwait(false)) throw new FinanceException("Fin_ErrGone");
        return d with
        {
            ToAccountId = toAccount,
            ToAmount = toAmount,
            CategoryId = category,
            Description = Optional(d.Description, 300, "Fin_ErrTooLong") ?? string.Empty,
            Payee = Optional(d.Payee, 200, "Fin_ErrTooLong"),
            Tags = NormalizeTags(d.Tags),
            Notes = Optional(d.Notes, 8000, "Fin_ErrTooLong"),
            Time = d.Time is { } t ? new TimeOnly(t.Hour, t.Minute) : null,
        };
    }

    private static void Apply(FinanceTransaction t, TransactionDraft d)
    {
        t.Type = d.Type;
        t.Date = d.Date;
        t.Time = d.Time;
        t.Amount = d.Amount;
        t.AccountId = d.AccountId;
        t.ToAccountId = d.ToAccountId;
        t.ToAmount = d.ToAmount;
        t.CategoryId = d.CategoryId;
        t.Description = d.Description;
        t.Payee = d.Payee;
        t.Tags = d.Tags;
        t.Notes = d.Notes;
        t.ProjectId = d.ProjectId;
        t.TripId = d.TripId;
    }

    public async Task<FinanceTransaction?> GetTransactionAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.FinanceTransactions.AsNoTracking().Include(t => t.Account).Include(t => t.ToAccount).Include(t => t.Category).Include(t => t.Documents).ThenInclude(d => d.Document)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FinanceTransaction> CreateTransactionAsync(TransactionDraft draft, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var d = await ValidateAsync(db, draft, true, null, cancellationToken).ConfigureAwait(false);
        var now = UtcNow;
        var t = new FinanceTransaction { CreatedAtUtc = now, UpdatedAtUtc = now };
        Apply(t, d);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.FinanceTransactions.Add(t);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexTransactionAsync(db, t, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
        return t;
    }

    public async Task UpdateTransactionAsync(int id, TransactionDraft draft, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var t = await db.FinanceTransactions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        var d = await ValidateAsync(db, draft, false, null, cancellationToken).ConfigureAwait(false);
        Apply(t, d);
        t.UpdatedAtUtc = UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexTransactionAsync(db, t, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    public async Task DeleteTransactionAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // a bill payment's due date becomes open again (its occurrence row goes with the transaction)
        await db.RecurringOccurrences.Where(o => o.TransactionId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var n = await db.FinanceTransactions.Where(t => t.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (n == 0) throw new KeyNotFoundException();
        await SearchIndex.DeleteAsync(db, SearchResultKind.Transaction, id, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    public async Task SetNeedsReviewAsync(int id, bool needsReview, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var n = await db.FinanceTransactions.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.NeedsReview, needsReview), cancellationToken).ConfigureAwait(false);
        if (n == 0) throw new KeyNotFoundException();
        Bump();
    }

    // ───────────── receipts & documents ─────────────

    public async Task AttachDocumentAsync(int transactionId, int documentId, DocumentRole role, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.FinanceTransactions.AnyAsync(t => t.Id == transactionId, cancellationToken).ConfigureAwait(false)) throw new KeyNotFoundException();
        if (!await db.Documents.AnyAsync(d => d.Id == documentId, cancellationToken).ConfigureAwait(false)) throw new FinanceException("Fin_ErrNoDocument");
        var link = await db.TransactionDocuments.FirstOrDefaultAsync(l => l.TransactionId == transactionId && l.DocumentId == documentId, cancellationToken).ConfigureAwait(false);
        if (link == null) db.TransactionDocuments.Add(new TransactionDocument { TransactionId = transactionId, DocumentId = documentId, Role = role, AddedAtUtc = UtcNow });
        else link.Role = role;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    public async Task DetachDocumentAsync(int transactionId, int documentId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.TransactionDocuments.Where(l => l.TransactionId == transactionId && l.DocumentId == documentId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    public async Task<IReadOnlyList<(int DocumentId, DocumentRole Role, string Name, string Path)>> GetDocumentsAsync(int transactionId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.TransactionDocuments.AsNoTracking().Where(l => l.TransactionId == transactionId).OrderBy(l => l.AddedAtUtc)
            .Select(l => new { l.DocumentId, l.Role, l.Document!.DisplayName, l.Document.Path }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => (r.DocumentId, r.Role, r.DisplayName, r.Path)).ToList();
    }

    public async Task<IReadOnlyDictionary<int, string>> GetNotesAsync(IReadOnlyCollection<int> transactionIds, CancellationToken cancellationToken = default)
    {
        if (transactionIds.Count == 0) return new Dictionary<int, string>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<int, string>();
        foreach (var chunk in transactionIds.Chunk(500))
            foreach (var row in await db.FinanceTransactions.AsNoTracking().Where(t => chunk.Contains(t.Id) && t.Notes != null).Select(t => new { t.Id, t.Notes }).ToListAsync(cancellationToken).ConfigureAwait(false))
                result[row.Id] = row.Notes!;
        return result;
    }

    // ───────────── the register ─────────────

    private static IQueryable<FinanceTransaction> Filter(ArcadiaDbContext db, TransactionQuery q, IReadOnlyCollection<int>? categoryIds, IReadOnlyCollection<int>? aliasCategoryIds)
    {
        var query = db.FinanceTransactions.AsNoTracking();
        if (q.From is { } from) query = query.Where(t => t.Date >= from);
        if (q.To is { } to) query = query.Where(t => t.Date <= to);
        if (q.AccountId is { } a) query = query.Where(t => t.AccountId == a || t.ToAccountId == a);
        if (categoryIds != null) query = query.Where(t => t.CategoryId != null && categoryIds.Contains(t.CategoryId.Value));
        if (q.UncategorizedOnly) query = query.Where(t => t.CategoryId == null && t.Type != TransactionType.Transfer);
        if (q.Type is { } type) query = query.Where(t => t.Type == type);
        if (q.ProjectId is { } p) query = query.Where(t => t.ProjectId == p);
        if (q.TripId is { } tr) query = query.Where(t => t.TripId == tr);
        if (q.NeedsReviewOnly) query = query.Where(t => t.NeedsReview);
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var pattern = "%" + q.Text.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            var ids = aliasCategoryIds ?? [];
            query = query.Where(t => EF.Functions.Like(t.Description, pattern, "\\") || EF.Functions.Like(t.Payee ?? "", pattern, "\\") || EF.Functions.Like(t.Notes ?? "", pattern, "\\")
                                     || EF.Functions.Like(t.Tags, pattern, "\\") || EF.Functions.Like(t.Account!.Name, pattern, "\\")
                                     || (t.Category != null && (EF.Functions.Like(t.Category.Name ?? "", pattern, "\\") || ids.Contains(t.Category.Id))));
        }
        return query;
    }

    /// <summary>Built-in categories whose name in any language contains the text (so "Lebensmittel" finds groceries).</summary>
    private static List<int> CategoriesMatching(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var t = text.Trim();
        return FinanceDefaults.Categories.Where(c => FinanceDefaults.AliasesOf(c.Key).Any(a => a.Contains(t, StringComparison.CurrentCultureIgnoreCase))).Select(c => c.Id).ToList();
    }

    private static async Task<List<int>?> CategoryScopeAsync(ArcadiaDbContext db, int? categoryId, CancellationToken ct)
    {
        if (categoryId is not { } id) return null;
        var ids = await db.FinanceCategories.Where(c => c.Id == id || c.ParentId == id).Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
        return ids.Count == 0 ? [id] : ids;
    }

    public async Task<TransactionPage> QueryTransactionsAsync(TransactionQuery query, string language, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scope = await CategoryScopeAsync(db, query.CategoryId, cancellationToken).ConfigureAwait(false);
        var filtered = Filter(db, query, scope, CategoriesMatching(query.Text));

        // totals over everything that matches (not just the page), per currency
        var flows = await filtered.Select(t => new { t.Type, t.Amount, t.Account!.Currency }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var totals = flows.GroupBy(f => f.Currency).Select(g => new CurrencyFlow(g.Key,
                g.Where(f => f.Type == TransactionType.Income).Sum(f => f.Amount), g.Where(f => f.Type == TransactionType.Expense).Sum(f => f.Amount), g.Count()))
            .OrderByDescending(c => c.Count).ToList();

        var ordered = query.Sort switch
        {
            TransactionSort.DateAsc => filtered.OrderBy(t => t.Date).ThenBy(t => t.Time).ThenBy(t => t.Id),
            TransactionSort.AmountDesc => filtered.OrderByDescending(t => t.Amount).ThenByDescending(t => t.Date),
            TransactionSort.AmountAsc => filtered.OrderBy(t => t.Amount).ThenByDescending(t => t.Date),
            TransactionSort.Description => filtered.OrderBy(t => t.Description).ThenByDescending(t => t.Date),
            TransactionSort.Category => filtered.OrderBy(t => t.Category == null ? 1 : 0).ThenBy(t => t.Category!.SortOrder).ThenByDescending(t => t.Date),
            TransactionSort.Account => filtered.OrderBy(t => t.Account!.SortOrder).ThenByDescending(t => t.Date),
            _ => filtered.OrderByDescending(t => t.Date).ThenByDescending(t => t.Time).ThenByDescending(t => t.Id),
        };
        var rows = await Project(ordered.Skip(Math.Max(0, query.Skip)).Take(Math.Clamp(query.Take, 1, 20000))).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new TransactionPage(rows.Select(r => r.ToRow(language)).ToList(), flows.Count, totals);
    }

    /// <summary>The register's columns in one query (names resolved later for the language).</summary>
    private sealed record RowData(int Id, TransactionType Type, DateOnly Date, TimeOnly? Time, decimal Amount, string Currency, int AccountId, string AccountName,
        int? ToAccountId, string? ToAccountName, decimal? ToAmount, string? ToCurrency, int? CategoryId, string? CategoryKey, string? CategoryName, string? CategoryIcon,
        string? CategoryAccent, string Description, string? Payee, string Tags, bool HasNotes, int? ProjectId, int? TripId, int? RecurringItemId, int DocumentCount,
        bool NeedsReview, DateTime UpdatedAtUtc)
    {
        public TransactionRow ToRow(string language) => new(Id, Type, Date, Time, Amount, Currency, AccountId, AccountName, ToAccountId, ToAccountName, ToAmount, ToCurrency,
            CategoryId, CategoryId == null ? null : FinanceDefaults.NameOf(CategoryKey, CategoryName, language), CategoryIcon, CategoryAccent, Description, Payee, Tags, HasNotes,
            ProjectId, TripId, RecurringItemId, DocumentCount, NeedsReview, UpdatedAtUtc);
    }

    private static IQueryable<RowData> Project(IQueryable<FinanceTransaction> q) => q.Select(t => new RowData(
        t.Id, t.Type, t.Date, t.Time, t.Amount, t.Account!.Currency, t.AccountId, t.Account.Name,
        t.ToAccountId, t.ToAccount != null ? t.ToAccount.Name : null, t.ToAmount, t.ToAccount != null ? t.ToAccount.Currency : null,
        t.CategoryId, t.Category != null ? t.Category.Key : null, t.Category != null ? t.Category.Name : null, t.Category != null ? t.Category.Icon : null,
        t.Category != null ? t.Category.Accent : null, t.Description, t.Payee, t.Tags, t.Notes != null && t.Notes != "", t.ProjectId, t.TripId, t.RecurringItemId,
        t.Documents.Count, t.NeedsReview, t.UpdatedAtUtc));

    // ───────────── import ─────────────

    public async Task<IReadOnlyList<DuplicateMatch>> FindDuplicatesAsync(IReadOnlyList<TransactionDraft> candidates, CancellationToken cancellationToken = default)
    {
        var result = new DuplicateMatch[candidates.Count];
        if (candidates.Count == 0) return result;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        foreach (var group in candidates.Select((c, i) => (c, i)).GroupBy(x => x.c.AccountId))
        {
            var from = group.Min(x => x.c.Date).AddDays(-1);
            var to = group.Max(x => x.c.Date).AddDays(1);
            var existing = await db.FinanceTransactions.AsNoTracking().Where(t => t.AccountId == group.Key && t.Date >= from && t.Date <= to)
                .Select(t => new { t.Type, t.Date, t.Amount, t.Description, t.Payee }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var byAmount = existing.ToLookup(e => e.Amount);
            foreach (var (c, i) in group)
            {
                var best = DuplicateMatch.None;
                foreach (var e in byAmount[c.Amount])
                {
                    var m = DuplicateRules.Compare(c, group.Key, e.Type, e.Date, e.Amount, string.IsNullOrWhiteSpace(e.Description) ? e.Payee : e.Description);
                    if (m > best) best = m;
                    if (best == DuplicateMatch.Likely) break;
                }
                result[i] = best;
            }
        }
        // repeated lines inside the file itself are possible duplicates too (never dropped silently)
        var seen = new HashSet<(int, TransactionType, DateOnly, decimal, string)>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (!seen.Add((c.AccountId, c.Type, c.Date, c.Amount, DuplicateRules.Normalize(c.Description.Length > 0 ? c.Description : c.Payee))) && result[i] == DuplicateMatch.None)
                result[i] = DuplicateMatch.Possible;
        }
        return result;
    }

    public async Task<string> ImportAsync(IReadOnlyList<ImportTransaction> rows, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0) throw new FinanceException("Imp_ErrNothing");
        var batch = "imp-" + UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var accounts = new Dictionary<int, FinanceAccount>();
        var now = UtcNow;
        var entities = new List<FinanceTransaction>(rows.Count);
        foreach (var row in rows)
        {
            var d = await ValidateAsync(db, row.Draft, true, accounts, cancellationToken).ConfigureAwait(false);
            var t = new FinanceTransaction { CreatedAtUtc = now, UpdatedAtUtc = now, ImportBatch = batch, NeedsReview = row.NeedsReview };
            Apply(t, d);
            entities.Add(t);
        }
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.FinanceTransactions.AddRange(entities);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var t in entities) await IndexTransactionAsync(db, t, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
        logger.LogInformation("Imported {Count} transactions (batch {Batch})", entities.Count, batch);
        return batch;
    }

    public async Task<int> UndoImportAsync(string batch, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(batch)) return 0;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var ids = await db.FinanceTransactions.Where(t => t.ImportBatch == batch).Select(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        await db.RecurringOccurrences.Where(o => o.TransactionId != null && ids.Contains(o.TransactionId.Value)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var n = await db.FinanceTransactions.Where(t => t.ImportBatch == batch).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in ids) await SearchIndex.DeleteAsync(db, SearchResultKind.Transaction, id, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
        logger.LogInformation("Undid import batch {Batch} ({Count} transactions removed)", batch, n);
        return n;
    }
}
