using Arcadia.Core.Organizer;

namespace Arcadia.Core.Finance;

/// <summary>
/// A finance rule was not met (invalid amount, unknown currency, account in use…). <see cref="Key"/> is a resource key the
/// UI shows in the user's language; nothing was changed.
/// </summary>
public sealed class FinanceException(string key, params object[] args) : ArgumentException(key)
{
    public string Key { get; } = key;
    public IReadOnlyList<object> Args { get; } = args;
}

// ───────────────────────── drafts ─────────────────────────

public sealed record AccountDraft(string Name, AccountType Type, string Currency, decimal OpeningBalance = 0m, string? Description = null, string Icon = "Wallet", string Accent = "Blue");

public sealed record CategoryDraft(string Name, CategoryKind Kind, int? ParentId = null, string Icon = "Tag", string Accent = "Steel");

/// <summary>
/// A transaction as entered. <see cref="Amount"/> is positive; the type gives the direction. Transfers need
/// <see cref="ToAccountId"/>; <see cref="ToAmount"/> is required only between accounts with different currencies.
/// </summary>
public sealed record TransactionDraft(
    TransactionType Type,
    DateOnly Date,
    decimal Amount,
    int AccountId,
    int? CategoryId = null,
    string Description = "",
    string? Payee = null,
    TimeOnly? Time = null,
    int? ToAccountId = null,
    decimal? ToAmount = null,
    string Tags = "",
    string? Notes = null,
    int? ProjectId = null,
    int? TripId = null);

public sealed record BudgetDraft(
    int? CategoryId,
    decimal Amount,
    string Currency,
    BudgetPeriod Period,
    DateOnly StartDate,
    DateOnly? EndDate = null,
    bool Rollover = false,
    string? Name = null,
    int? TripId = null,
    string? Notes = null);

public sealed record RecurringDraft(
    string Name,
    RecurringKind Kind,
    decimal Amount,
    string Currency,
    RecurrenceFrequency Frequency,
    int Interval,
    DateOnly StartDate,
    DateOnly? EndDate = null,
    int? AccountId = null,
    int? CategoryId = null,
    string? Payee = null,
    bool IsActive = true,
    bool ShowOnCalendar = true,
    string? Notes = null);

public sealed record GoalDraft(
    string Name,
    decimal TargetAmount,
    string Currency,
    decimal CurrentAmount = 0m,
    DateOnly? TargetDate = null,
    int? AccountId = null,
    string? Description = null,
    string Icon = "Target",
    string Accent = "Cyan");

// ───────────────────────── rows & queries ─────────────────────────

public sealed record AccountBalance(FinanceAccount Account, decimal Balance, int TransactionCount, DateOnly? LastActivity);

/// <summary>A category as lists and pickers show it (the name is already resolved for the requested language).</summary>
public sealed record CategoryInfo(int Id, string? Key, string Name, CategoryKind Kind, int? ParentId, string Icon, string Accent, bool IsArchived, int SortOrder, int UseCount);

public enum TransactionSort { DateDesc, DateAsc, AmountDesc, AmountAsc, Description, Category, Account }

/// <param name="CategoryId">A category and its subcategories.</param>
public sealed record TransactionQuery(
    string? Text = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int? AccountId = null,
    int? CategoryId = null,
    TransactionType? Type = null,
    int? ProjectId = null,
    int? TripId = null,
    bool NeedsReviewOnly = false,
    bool UncategorizedOnly = false,
    TransactionSort Sort = TransactionSort.DateDesc,
    int Skip = 0,
    int Take = 2000);

public sealed record TransactionRow(
    int Id,
    TransactionType Type,
    DateOnly Date,
    TimeOnly? Time,
    decimal Amount,
    string Currency,
    int AccountId,
    string AccountName,
    int? ToAccountId,
    string? ToAccountName,
    decimal? ToAmount,
    string? ToCurrency,
    int? CategoryId,
    string? CategoryName,
    string? CategoryIcon,
    string? CategoryAccent,
    string Description,
    string? Payee,
    string Tags,
    bool HasNotes,
    int? ProjectId,
    int? TripId,
    int? RecurringItemId,
    int DocumentCount,
    bool NeedsReview,
    DateTime UpdatedAtUtc)
{
    /// <summary>Description, else payee, else the category (what a row is called).</summary>
    public string Label => !string.IsNullOrWhiteSpace(Description) ? Description : !string.IsNullOrWhiteSpace(Payee) ? Payee! : CategoryName ?? string.Empty;
}

/// <summary>Income, expenses and transfers of a set of transactions, per currency.</summary>
public sealed record CurrencyFlow(string Currency, decimal Income, decimal Expenses, int Count)
{
    public decimal Net => Income - Expenses;
}

public sealed record TransactionPage(IReadOnlyList<TransactionRow> Rows, int TotalCount, IReadOnlyList<CurrencyFlow> Totals);

public sealed record CategorySpending(int? CategoryId, string? Key, string Name, string Icon, string Accent, string Currency, decimal Amount, int Count);

/// <summary>One month of one currency: money in, out, and put aside.</summary>
/// <param name="Saved">Net transfers into savings/investment accounts plus expenses in the Savings category.</param>
public sealed record MonthFlow(DateOnly Month, string Currency, decimal Income, decimal Expenses, decimal Saved, int Count)
{
    public decimal Net => Income - Expenses;
}

public sealed record MonthSummary(
    DateOnly Month,
    string Currency,
    decimal Income,
    decimal Expenses,
    decimal Saved,
    int TransactionCount,
    CategorySpending? LargestCategory,
    int BudgetsOverLimit,
    int BudgetCount)
{
    public decimal Net => Income - Expenses;
}

public sealed record BillDue(RecurringItem Item, DateOnly DueDate, bool IsOverdue, string? AccountName, string? CategoryName);

public sealed record SubscriptionRow(RecurringItem Item, DateOnly? NextDue, decimal MonthlyEquivalent, decimal AnnualCost, string? AccountName, string? CategoryName);

public sealed record ExpenseSummary(IReadOnlyList<MoneyTotal> Totals, IReadOnlyList<CategorySpending> ByCategory, IReadOnlyList<TransactionRow> Transactions, IReadOnlyList<BudgetStatus> Budgets);

/// <summary>Month-end balances of one account (the opening balance counts from before its first transaction).</summary>
public sealed record BalanceHistory(int AccountId, string AccountName, string Currency, string Accent, IReadOnlyList<(DateOnly Month, decimal Balance)> Points);

// ───────────────────────── import ─────────────────────────

/// <summary>A row ready to import (parsed and validated by the wizard).</summary>
public sealed record ImportTransaction(TransactionDraft Draft, bool NeedsReview);

public enum DuplicateMatch { None, Possible, Likely }

// ───────────────────────── services ─────────────────────────

/// <summary>
/// Read side of Finance: every question ("how much did I spend this month?", "what bills are coming?", "show expenses from
/// my Italy trip") is answered by these deterministic queries over local data — never by a language model. Amounts are
/// returned per currency.
/// </summary>
public interface IFinanceQueryService
{
    Task<IReadOnlyList<AccountBalance>> GetAccountBalancesAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CategoryInfo>> GetCategoriesAsync(string language, bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<TransactionPage> QueryTransactionsAsync(TransactionQuery query, string language, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MoneyTotal>> GetMonthlyExpensesAsync(int year, int month, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MoneyTotal>> GetMonthlyIncomeAsync(int year, int month, CancellationToken cancellationToken = default);
    /// <summary>Expenses per category in the range; subcategories roll up into their parent when <paramref name="rollUp"/>.</summary>
    Task<IReadOnlyList<CategorySpending>> GetCategorySpendingAsync(DateOnly from, DateOnly to, string language, string? currency = null, bool rollUp = true, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MonthFlow>> GetMonthlyFlowsAsync(DateOnly fromMonth, DateOnly toMonth, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MonthSummary>> GetMonthlySummaryAsync(int year, int month, string language, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransactionRow>> GetLargestExpensesAsync(DateOnly from, DateOnly to, string language, int take = 5, CancellationToken cancellationToken = default);

    /// <summary>Unpaid due dates of active bills, subscriptions and income from <paramref name="from"/> to <paramref name="to"/>, plus overdue ones up to <paramref name="overdueDays"/> back.</summary>
    Task<IReadOnlyList<BillDue>> GetUpcomingBillsAsync(DateOnly from, DateOnly to, string language, int overdueDays = 60, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SubscriptionRow>> GetSubscriptionsAsync(DateOnly today, string language, RecurringKind? kind = RecurringKind.Subscription, bool includeInactive = true, CancellationToken cancellationToken = default);
    /// <summary>Every monthly budget that applies in the month, plus custom-range budgets overlapping it.</summary>
    Task<IReadOnlyList<BudgetStatus>> GetBudgetStatusAsync(DateOnly month, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GoalProgress>> GetSavingsGoalsAsync(DateOnly today, bool includeArchived = false, CancellationToken cancellationToken = default);

    Task<ExpenseSummary> GetTripExpensesAsync(int tripId, string language, CancellationToken cancellationToken = default);
    Task<ExpenseSummary> GetProjectExpensesAsync(int projectId, string language, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BalanceHistory>> GetBalanceHistoryAsync(DateOnly fromMonth, DateOnly toMonth, string? currency = null, CancellationToken cancellationToken = default);
}

public interface IFinanceStore : IFinanceQueryService
{
    // accounts
    Task<FinanceAccount?> GetAccountAsync(int id, CancellationToken cancellationToken = default);
    Task<FinanceAccount> CreateAccountAsync(AccountDraft draft, CancellationToken cancellationToken = default);
    /// <summary>The currency can only change while the account has no transactions.</summary>
    Task UpdateAccountAsync(int id, AccountDraft draft, CancellationToken cancellationToken = default);
    Task SetAccountArchivedAsync(int id, bool archived, CancellationToken cancellationToken = default);
    /// <summary>Only an account without transactions can be deleted (otherwise archive it).</summary>
    Task DeleteAccountAsync(int id, CancellationToken cancellationToken = default);

    // categories
    Task<FinanceCategory> CreateCategoryAsync(CategoryDraft draft, CancellationToken cancellationToken = default);
    Task UpdateCategoryAsync(int id, CategoryDraft draft, CancellationToken cancellationToken = default);
    Task SetCategoryArchivedAsync(int id, bool archived, CancellationToken cancellationToken = default);
    /// <summary>Transactions, budgets and bills of the category move to <paramref name="moveTo"/> (or become uncategorized). A category with subcategories cannot be deleted.</summary>
    Task DeleteCategoryAsync(int id, int? moveTo, CancellationToken cancellationToken = default);

    // transactions
    Task<FinanceTransaction?> GetTransactionAsync(int id, CancellationToken cancellationToken = default);
    Task<FinanceTransaction> CreateTransactionAsync(TransactionDraft draft, CancellationToken cancellationToken = default);
    Task UpdateTransactionAsync(int id, TransactionDraft draft, CancellationToken cancellationToken = default);
    Task DeleteTransactionAsync(int id, CancellationToken cancellationToken = default);
    Task SetNeedsReviewAsync(int id, bool needsReview, CancellationToken cancellationToken = default);
    Task AttachDocumentAsync(int transactionId, int documentId, DocumentRole role, CancellationToken cancellationToken = default);
    Task DetachDocumentAsync(int transactionId, int documentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(int DocumentId, DocumentRole Role, string Name, string Path)>> GetDocumentsAsync(int transactionId, CancellationToken cancellationToken = default);
    /// <summary>The notes of several transactions (exports).</summary>
    Task<IReadOnlyDictionary<int, string>> GetNotesAsync(IReadOnlyCollection<int> transactionIds, CancellationToken cancellationToken = default);

    // budgets
    Task<IReadOnlyList<Budget>> GetBudgetsAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<Budget> CreateBudgetAsync(BudgetDraft draft, CancellationToken cancellationToken = default);
    Task UpdateBudgetAsync(int id, BudgetDraft draft, CancellationToken cancellationToken = default);
    Task DeleteBudgetAsync(int id, CancellationToken cancellationToken = default);

    // recurring
    Task<IReadOnlyList<RecurringItem>> GetRecurringItemsAsync(CancellationToken cancellationToken = default);
    Task<RecurringItem?> GetRecurringItemAsync(int id, CancellationToken cancellationToken = default);
    Task<RecurringItem> CreateRecurringItemAsync(RecurringDraft draft, CancellationToken cancellationToken = default);
    Task UpdateRecurringItemAsync(int id, RecurringDraft draft, CancellationToken cancellationToken = default);
    Task DeleteRecurringItemAsync(int id, CancellationToken cancellationToken = default);
    /// <summary>Records the payment of one due date as a real transaction (defaults: the item's amount and account, dated on the due date).</summary>
    Task<FinanceTransaction> MarkPaidAsync(int itemId, DateOnly dueDate, int? accountId = null, decimal? amount = null, DateOnly? paidOn = null, CancellationToken cancellationToken = default);
    Task SkipAsync(int itemId, DateOnly dueDate, CancellationToken cancellationToken = default);
    /// <summary>Makes a skipped date due again (a paid date is reopened by deleting its transaction).</summary>
    Task ReopenAsync(int itemId, DateOnly dueDate, CancellationToken cancellationToken = default);

    // goals
    Task<IReadOnlyList<SavingsGoal>> GetGoalsAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<SavingsGoal> CreateGoalAsync(GoalDraft draft, CancellationToken cancellationToken = default);
    Task UpdateGoalAsync(int id, GoalDraft draft, CancellationToken cancellationToken = default);
    Task SetGoalArchivedAsync(int id, bool archived, CancellationToken cancellationToken = default);
    Task DeleteGoalAsync(int id, CancellationToken cancellationToken = default);
    /// <summary>Adds (or with a negative amount, removes) money from a goal tracked by hand.</summary>
    Task AddToGoalAsync(int id, decimal amount, CancellationToken cancellationToken = default);

    // import
    /// <summary>How closely each candidate matches a transaction already in its account (same date and amount; similar text).</summary>
    Task<IReadOnlyList<DuplicateMatch>> FindDuplicatesAsync(IReadOnlyList<TransactionDraft> candidates, CancellationToken cancellationToken = default);
    /// <summary>Imports all rows in one database transaction; returns the batch id (for undo).</summary>
    Task<string> ImportAsync(IReadOnlyList<ImportTransaction> rows, CancellationToken cancellationToken = default);
    Task<int> UndoImportAsync(string batch, CancellationToken cancellationToken = default);

    /// <summary>Changes whenever finance data changes (cheap cache key for dashboards).</summary>
    long Version { get; }
}

/// <param name="Saved">The transaction, when the text was clear and saved; otherwise null.</param>
/// <param name="Draft">What was understood (for the editor when nothing was saved; account 0 = none chosen).</param>
public sealed record QuickExpenseResult(FinanceTransaction? Saved, TransactionDraft Draft, QuickExpense Parsed);

/// <summary>Quick Capture's "Expense": saves clear one-liners, hands everything else to the transaction editor.</summary>
public interface IQuickExpenseService
{
    Task<QuickExpenseResult> CaptureAsync(string text, DateTime now, DecimalStyle style, int? defaultAccountId, EntityRef? context = null, CancellationToken cancellationToken = default);
}

/// <summary>Finance data as a readable JSON document (local backups and exports the user asks for).</summary>
public interface IFinanceExporter
{
    Task WriteJsonAsync(Stream target, CancellationToken cancellationToken = default);
}
