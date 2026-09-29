using Arcadia.Core.Finance;
using Arcadia.Core.Search;
using Arcadia.Data.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Arcadia.Data.Finance;

/// <summary>
/// Finance persistence and queries (Phase 7). Everything is local SQLite; nothing is sent anywhere and log messages never
/// contain amounts, descriptions or account names. Every write keeps the FTS index current in the same database
/// transaction and bumps <see cref="Version"/> (a cheap cache key for balances and dashboards).
/// </summary>
public sealed partial class FinanceStore(IDbContextFactory<ArcadiaDbContext> factory, TimeProvider time, ILogger<FinanceStore> logger) : IFinanceStore
{
    public static readonly string[] Accents = ["Blue", "Cyan", "Green", "Amber", "Gold", "Purple", "Red", "Steel"];

    private long _version;
    public long Version => Interlocked.Read(ref _version);
    private void Bump() => Interlocked.Increment(ref _version);

    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    // ───────────── validation helpers ─────────────

    /// <summary>Positive (or, where allowed, zero/negative), at most four decimals, at most a trillion.</summary>
    internal static decimal CheckAmount(decimal value, string key = "Fin_ErrAmount", bool allowZero = false, bool allowNegative = false)
    {
        if (decimal.Round(value, AmountParser.MaxDecimals) != value) throw new FinanceException("Fin_ErrPrecision");
        if (Math.Abs(value) > AmountParser.MaxAmount) throw new FinanceException("Fin_ErrTooLarge");
        if (value < 0 && !allowNegative) throw new FinanceException(key);
        if (value == 0 && !allowZero) throw new FinanceException(key);
        return value;
    }

    internal static string CheckCurrency(string? code)
    {
        var c = Currency.Normalize(code);
        if (!Currency.IsSupported(c)) throw new FinanceException("Fin_ErrCurrency", code ?? string.Empty);
        return c;
    }

    internal static string CheckName(string? name, int max, string key)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length == 0 || n.Length > max) throw new FinanceException(key, max);
        return n;
    }

    private static string? Optional(string? text, int max, string key)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (t.Length > max) throw new FinanceException(key, max);
        return t;
    }

    private static string Accent(string? accent) => accent != null && Accents.Contains(accent) ? accent : "Blue";

    private static string Icon(string? icon, string fallback) => string.IsNullOrWhiteSpace(icon) || icon.Length > 32 ? fallback : icon;

    /// <summary>Comma-separated, trimmed, without duplicates.</summary>
    internal static string NormalizeTags(string? tags)
    {
        var list = (tags ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var joined = string.Join(", ", list);
        if (joined.Length > 500) throw new FinanceException("Fin_ErrTags");
        return joined;
    }

    private static DateOnly CheckDate(DateOnly d) =>
        d.Year is >= 1900 and <= 2200 ? d : throw new FinanceException("Fin_ErrDate");

    // ───────────── accounts ─────────────

    public async Task<FinanceAccount?> GetAccountAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.FinanceAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CheckUniqueAccountNameAsync(ArcadiaDbContext db, string name, int? exceptId, CancellationToken ct)
    {
        // names identify accounts in CSV files, so they are unique (case-insensitive: the column uses NOCASE)
        if (await db.FinanceAccounts.AnyAsync(a => a.Name == name && a.Id != (exceptId ?? 0), ct).ConfigureAwait(false))
            throw new FinanceException("Fin_ErrAccountName");
    }

    public async Task<FinanceAccount> CreateAccountAsync(AccountDraft draft, CancellationToken cancellationToken = default)
    {
        var name = CheckName(draft.Name, 120, "Fin_ErrAccountNameEmpty");
        var currency = CheckCurrency(draft.Currency);
        var opening = CheckAmount(draft.OpeningBalance, allowZero: true, allowNegative: true);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await CheckUniqueAccountNameAsync(db, name, null, cancellationToken).ConfigureAwait(false);
        var now = UtcNow;
        var account = new FinanceAccount
        {
            Name = name, Type = draft.Type, Currency = currency, OpeningBalance = opening, Description = Optional(draft.Description, 2000, "Fin_ErrTooLong"),
            Icon = Icon(draft.Icon, IconFor(draft.Type)), Accent = Accent(draft.Accent), CreatedAtUtc = now, UpdatedAtUtc = now,
            SortOrder = (await db.FinanceAccounts.MaxAsync(a => (int?)a.SortOrder, cancellationToken).ConfigureAwait(false) ?? 0) + 1,
        };
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.FinanceAccounts.Add(account);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexAccountAsync(db, account, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
        return account;
    }

    public static string IconFor(AccountType type) => type switch
    {
        AccountType.Checking => "Bank",
        AccountType.Savings => "Target",
        AccountType.Cash => "Cash",
        AccountType.CreditCard => "Card",
        AccountType.Investment => "Chart",
        _ => "Wallet",
    };

    public async Task UpdateAccountAsync(int id, AccountDraft draft, CancellationToken cancellationToken = default)
    {
        var name = CheckName(draft.Name, 120, "Fin_ErrAccountNameEmpty");
        var currency = CheckCurrency(draft.Currency);
        var opening = CheckAmount(draft.OpeningBalance, allowZero: true, allowNegative: true);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var account = await db.FinanceAccounts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        await CheckUniqueAccountNameAsync(db, name, id, cancellationToken).ConfigureAwait(false);
        if (currency != account.Currency && (await db.FinanceTransactions.AnyAsync(t => t.AccountId == id || t.ToAccountId == id, cancellationToken).ConfigureAwait(false)
                                             || await db.RecurringItems.AnyAsync(r => r.AccountId == id, cancellationToken).ConfigureAwait(false)
                                             || await db.SavingsGoals.AnyAsync(g => g.AccountId == id, cancellationToken).ConfigureAwait(false)))
            throw new FinanceException("Fin_ErrCurrencyLocked");
        var renamed = account.Name != name;
        account.Name = name;
        account.Type = draft.Type;
        account.Currency = currency;
        account.OpeningBalance = opening;
        account.Description = Optional(draft.Description, 2000, "Fin_ErrTooLong");
        account.Icon = Icon(draft.Icon, IconFor(draft.Type));
        account.Accent = Accent(draft.Accent);
        account.UpdatedAtUtc = UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexAccountAsync(db, account, cancellationToken).ConfigureAwait(false);
        if (renamed) await ReindexTransactionsAsync(db, db.FinanceTransactions.Where(t => t.AccountId == id || t.ToAccountId == id), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    public async Task SetAccountArchivedAsync(int id, bool archived, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var n = await db.FinanceAccounts.Where(a => a.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsArchived, archived).SetProperty(a => a.UpdatedAtUtc, UtcNow), cancellationToken).ConfigureAwait(false);
        if (n == 0) throw new KeyNotFoundException();
        Bump();
    }

    public async Task DeleteAccountAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.FinanceTransactions.AnyAsync(t => t.AccountId == id || t.ToAccountId == id, cancellationToken).ConfigureAwait(false))
            throw new FinanceException("Fin_ErrAccountInUse");
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var n = await db.FinanceAccounts.Where(a => a.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (n == 0) throw new KeyNotFoundException();
        await SearchIndex.DeleteAsync(db, SearchResultKind.Account, id, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    // ───────────── categories ─────────────

    public async Task<IReadOnlyList<CategoryInfo>> GetCategoriesAsync(string language, bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var cats = await db.FinanceCategories.AsNoTracking().Where(c => includeArchived || !c.IsArchived).ToListAsync(cancellationToken).ConfigureAwait(false);
        var uses = await db.FinanceTransactions.Where(t => t.CategoryId != null).GroupBy(t => t.CategoryId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false);
        var rows = cats.Select(c => new CategoryInfo(c.Id, c.Key, FinanceDefaults.NameOf(c.Key, c.Name, language), c.Kind, c.ParentId, c.Icon, c.Accent, c.IsArchived, c.SortOrder,
            uses.GetValueOrDefault(c.Id))).ToList();
        // parents first (by kind, then order), each followed by its children
        var byParent = rows.Where(r => r.ParentId != null).ToLookup(r => r.ParentId!.Value);
        var ordered = new List<CategoryInfo>();
        foreach (var top in rows.Where(r => r.ParentId == null || rows.All(p => p.Id != r.ParentId)).OrderBy(r => r.Kind).ThenBy(r => r.SortOrder).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            ordered.Add(top);
            ordered.AddRange(byParent[top.Id].OrderBy(r => r.SortOrder).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase));
        }
        return ordered;
    }

    private static async Task<FinanceCategory?> ValidParentAsync(ArcadiaDbContext db, int? parentId, CategoryKind kind, int? selfId, CancellationToken ct)
    {
        if (parentId is not { } pid) return null;
        if (pid == selfId) throw new FinanceException("Fin_ErrCategoryParent");
        var parent = await db.FinanceCategories.FirstOrDefaultAsync(c => c.Id == pid, ct).ConfigureAwait(false) ?? throw new FinanceException("Fin_ErrCategoryParent");
        // one level of subcategories only, and within one kind
        if (parent.ParentId != null || parent.Kind != kind) throw new FinanceException("Fin_ErrCategoryParent");
        return parent;
    }

    public async Task<FinanceCategory> CreateCategoryAsync(CategoryDraft draft, CancellationToken cancellationToken = default)
    {
        var name = CheckName(draft.Name, 80, "Fin_ErrCategoryName");
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await ValidParentAsync(db, draft.ParentId, draft.Kind, null, cancellationToken).ConfigureAwait(false);
        var category = new FinanceCategory
        {
            Name = name, Kind = draft.Kind, ParentId = draft.ParentId, Icon = Icon(draft.Icon, "Tag"), Accent = Accent(draft.Accent),
            SortOrder = (await db.FinanceCategories.MaxAsync(c => (int?)c.SortOrder, cancellationToken).ConfigureAwait(false) ?? 0) + 1,
        };
        db.FinanceCategories.Add(category);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Bump();
        return category;
    }

    public async Task UpdateCategoryAsync(int id, CategoryDraft draft, CancellationToken cancellationToken = default)
    {
        var name = CheckName(draft.Name, 80, "Fin_ErrCategoryName");
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var category = await db.FinanceCategories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        await ValidParentAsync(db, draft.ParentId, draft.Kind, id, cancellationToken).ConfigureAwait(false);
        var children = await db.FinanceCategories.Where(c => c.ParentId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (draft.ParentId != null && children.Count > 0) throw new FinanceException("Fin_ErrCategoryParent");
        // a built-in category keeps its key; its name becomes custom only when it differs from the localized default
        category.Name = category.Key != null && FinanceDefaults.AliasesOf(category.Key).Contains(name, StringComparer.CurrentCultureIgnoreCase) ? null : name;
        category.Kind = draft.Kind;
        category.ParentId = draft.ParentId;
        category.Icon = Icon(draft.Icon, category.Icon);
        category.Accent = Accent(draft.Accent);
        foreach (var child in children) child.Kind = draft.Kind;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await ReindexTransactionsAsync(db, db.FinanceTransactions.Where(t => t.CategoryId == id), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    public async Task SetCategoryArchivedAsync(int id, bool archived, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var n = await db.FinanceCategories.Where(c => c.Id == id || c.ParentId == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsArchived, archived), cancellationToken).ConfigureAwait(false);
        if (n == 0) throw new KeyNotFoundException();
        Bump();
    }

    public async Task DeleteCategoryAsync(int id, int? moveTo, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var category = await db.FinanceCategories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        if (await db.FinanceCategories.AnyAsync(c => c.ParentId == id, cancellationToken).ConfigureAwait(false)) throw new FinanceException("Fin_ErrCategoryHasChildren");
        if (moveTo == id) moveTo = null;
        if (moveTo is { } target && !await db.FinanceCategories.AnyAsync(c => c.Id == target, cancellationToken).ConfigureAwait(false)) throw new FinanceException("Fin_ErrCategoryParent");
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var moved = await db.FinanceTransactions.Where(t => t.CategoryId == id).Select(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        await db.FinanceTransactions.Where(t => t.CategoryId == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, moveTo), cancellationToken).ConfigureAwait(false);
        await db.RecurringItems.Where(r => r.CategoryId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.CategoryId, moveTo), cancellationToken).ConfigureAwait(false);
        // a budget without its category would silently start covering all spending: it moves along, or goes
        if (moveTo != null) await db.Budgets.Where(b => b.CategoryId == id).ExecuteUpdateAsync(s => s.SetProperty(b => b.CategoryId, moveTo), cancellationToken).ConfigureAwait(false);
        else await db.Budgets.Where(b => b.CategoryId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.FinanceCategories.Remove(category);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await ReindexTransactionsAsync(db, db.FinanceTransactions.Where(t => moved.Contains(t.Id)), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        Bump();
    }

    // ───────────── search index ─────────────

    /// <summary>
    /// A transaction is found by its description, payee, notes, tags, category (every language's name for built-in ones)
    /// and account name. Amounts are not indexed.
    /// </summary>
    internal static async Task IndexTransactionAsync(ArcadiaDbContext db, FinanceTransaction t, CancellationToken ct)
    {
        var names = await db.FinanceAccounts.Where(a => a.Id == t.AccountId || a.Id == t.ToAccountId).Select(a => a.Name).ToListAsync(ct).ConfigureAwait(false);
        var category = t.CategoryId is { } cid
            ? await db.FinanceCategories.Where(c => c.Id == cid).Select(c => new { c.Key, c.Name, Parent = c.Parent != null ? c.Parent.Key : null, ParentName = c.Parent != null ? c.Parent.Name : null })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        var categoryWords = category == null ? [] : new[] { category.Name, category.ParentName }.Concat(FinanceDefaults.AliasesOf(category.Key)).Concat(FinanceDefaults.AliasesOf(category.Parent));
        var title = !string.IsNullOrWhiteSpace(t.Description) ? t.Description : t.Payee ?? string.Empty;
        var body = string.Join('\n', new[] { t.Payee, t.Notes, string.Join(' ', names) }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var tags = string.Join(' ', new[] { t.Tags }.Concat(categoryWords).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        await SearchIndex.UpsertAsync(db, SearchResultKind.Transaction, t.Id, null, title, body, tags, ct).ConfigureAwait(false);
    }

    private static async Task ReindexTransactionsAsync(ArcadiaDbContext db, IQueryable<FinanceTransaction> query, CancellationToken ct)
    {
        foreach (var t in await query.AsNoTracking().ToListAsync(ct).ConfigureAwait(false))
            await IndexTransactionAsync(db, t, ct).ConfigureAwait(false);
    }

    internal static Task IndexAccountAsync(ArcadiaDbContext db, FinanceAccount a, CancellationToken ct) =>
        SearchIndex.UpsertAsync(db, SearchResultKind.Account, a.Id, null, a.Name, a.Description ?? string.Empty, a.Currency + " " + a.Type, ct);

    internal static async Task IndexBudgetAsync(ArcadiaDbContext db, Budget b, CancellationToken ct)
    {
        var category = b.CategoryId is { } cid ? await db.FinanceCategories.Where(c => c.Id == cid).Select(c => new { c.Key, c.Name }).FirstOrDefaultAsync(ct).ConfigureAwait(false) : null;
        var title = b.Name ?? (category == null ? "Budget" : FinanceDefaults.NameOf(category.Key, category.Name, "en"));
        var tags = string.Join(' ', new[] { "budget", category?.Name }.Concat(FinanceDefaults.AliasesOf(category?.Key)).Where(s => !string.IsNullOrWhiteSpace(s)));
        await SearchIndex.UpsertAsync(db, SearchResultKind.Budget, b.Id, null, title, b.Notes ?? string.Empty, tags, ct).ConfigureAwait(false);
    }

    internal static async Task IndexRecurringAsync(ArcadiaDbContext db, RecurringItem r, CancellationToken ct)
    {
        var category = r.CategoryId is { } cid ? await db.FinanceCategories.Where(c => c.Id == cid).Select(c => new { c.Key, c.Name }).FirstOrDefaultAsync(ct).ConfigureAwait(false) : null;
        var tags = string.Join(' ', new[] { r.Kind.ToString().ToLowerInvariant(), category?.Name }.Concat(FinanceDefaults.AliasesOf(category?.Key)).Where(s => !string.IsNullOrWhiteSpace(s)));
        await SearchIndex.UpsertAsync(db, SearchResultKind.Recurring, r.Id, null, r.Name, string.Join('\n', new[] { r.Payee, r.Notes }.Where(s => !string.IsNullOrWhiteSpace(s))), tags, ct).ConfigureAwait(false);
    }

    internal static Task IndexGoalAsync(ArcadiaDbContext db, SavingsGoal g, CancellationToken ct) =>
        SearchIndex.UpsertAsync(db, SearchResultKind.Goal, g.Id, null, g.Name, g.Description ?? string.Empty, "goal savings", ct);

    /// <summary>Re-creates every finance row of the search index (after a restore or an index rebuild).</summary>
    internal static async Task ReindexAllAsync(ArcadiaDbContext db, CancellationToken ct)
    {
        foreach (var a in await db.FinanceAccounts.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)) await IndexAccountAsync(db, a, ct).ConfigureAwait(false);
        await ReindexTransactionsAsync(db, db.FinanceTransactions, ct).ConfigureAwait(false);
        foreach (var b in await db.Budgets.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)) await IndexBudgetAsync(db, b, ct).ConfigureAwait(false);
        foreach (var r in await db.RecurringItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)) await IndexRecurringAsync(db, r, ct).ConfigureAwait(false);
        foreach (var g in await db.SavingsGoals.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)) await IndexGoalAsync(db, g, ct).ConfigureAwait(false);
    }
}
