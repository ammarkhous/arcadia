using Arcadia.Core.Organizer;

namespace Arcadia.Core.Finance;

/// <summary>What an account is for. Accounts are records inside Arcadia; nothing connects to a bank.</summary>
public enum AccountType { Checking, Savings, Cash, CreditCard, Investment, Other }

/// <summary>
/// A local money account. Its balance is never stored: it is always the opening balance plus the account's income, minus
/// its expenses, minus transfers out, plus transfers in (<see cref="FinanceMath.Balance"/>). No bank identifiers,
/// card numbers, PINs or credentials are ever asked for or kept.
/// </summary>
public sealed class FinanceAccount
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public AccountType Type { get; set; } = AccountType.Checking;
    /// <summary>ISO 4217 code; every transaction of the account is in this currency.</summary>
    public string Currency { get; set; } = "EUR";
    /// <summary>What the account held when tracking began (may be negative, e.g. a credit card).</summary>
    public decimal OpeningBalance { get; set; }
    public string? Description { get; set; }
    public string Icon { get; set; } = "Wallet";
    public string Accent { get; set; } = "Blue";
    public bool IsArchived { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum CategoryKind { Expense, Income }

/// <summary>
/// A spending or income category; one optional level of subcategories (Transportation → Fuel). Built-in categories have
/// a <see cref="Key"/> and a localized name until the user renames them.
/// </summary>
public sealed class FinanceCategory
{
    public int Id { get; set; }
    public string? Key { get; set; }
    public string? Name { get; set; }
    public CategoryKind Kind { get; set; } = CategoryKind.Expense;
    /// <summary>The parent category (top-level categories only: the hierarchy is at most two levels deep).</summary>
    public int? ParentId { get; set; }
    public FinanceCategory? Parent { get; set; }
    public string Icon { get; set; } = "Tag";
    public string Accent { get; set; } = "Steel";
    public int SortOrder { get; set; }
    public bool IsArchived { get; set; }
}

public enum TransactionType { Expense, Income, Transfer }

/// <summary>
/// One money movement. The accounting model (documented in docs/DEVELOPMENT.md, Phase 7):
/// <list type="bullet">
/// <item><see cref="Amount"/> is always positive; <see cref="Type"/> says the direction. An expense lowers its account,
/// income raises it. There are no negative expenses.</item>
/// <item>A transfer is ONE row moving <see cref="Amount"/> out of <see cref="AccountId"/> and <see cref="ToAmount"/> into
/// <see cref="ToAccountId"/> (equal unless the accounts use different currencies). It is never income or expense, so
/// totals across accounts of one currency do not change and deleting it restores both balances.</item>
/// </list>
/// Dates are local calendar dates; the time is optional.
/// </summary>
public sealed class FinanceTransaction
{
    public int Id { get; set; }
    public TransactionType Type { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly? Time { get; set; }
    public decimal Amount { get; set; }
    public int AccountId { get; set; }
    public FinanceAccount? Account { get; set; }
    public int? ToAccountId { get; set; }
    public FinanceAccount? ToAccount { get; set; }
    /// <summary>What arrives in the destination account of a transfer (in its currency).</summary>
    public decimal? ToAmount { get; set; }
    public int? CategoryId { get; set; }
    public FinanceCategory? Category { get; set; }
    public string Description { get; set; } = string.Empty;
    /// <summary>Merchant for expenses, source for income.</summary>
    public string? Payee { get; set; }
    public string Tags { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public int? ProjectId { get; set; }
    public int? TripId { get; set; }
    /// <summary>The bill/subscription/income this payment settled (with the due date it settled).</summary>
    public int? RecurringItemId { get; set; }
    /// <summary>Set by CSV import (all rows of one import share it, so an import can be undone).</summary>
    public string? ImportBatch { get; set; }
    /// <summary>A possible duplicate kept on purpose (or anything else the user should look at). Never deleted automatically.</summary>
    public bool NeedsReview { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<TransactionDocument> Documents { get; set; } = [];

    public bool IsTransfer => Type == TransactionType.Transfer;
}

public enum DocumentRole { Receipt, Invoice, Contract, Statement, Other }

/// <summary>A Documents library file (Phase 4) attached to a transaction. The file is referenced, never copied.</summary>
public sealed class TransactionDocument
{
    public int TransactionId { get; set; }
    public FinanceTransaction? Transaction { get; set; }
    public int DocumentId { get; set; }
    public Arcadia.Core.Documents.Document? Document { get; set; }
    public DocumentRole Role { get; set; } = DocumentRole.Receipt;
    public DateTime AddedAtUtc { get; set; }
}

public enum BudgetPeriod { Monthly, Custom }

/// <summary>
/// A spending limit: monthly for a category (and its subcategories), or for a custom date range. Without a category it
/// covers all expenses. With <see cref="TripId"/> it is a trip budget (Phase 5 trips reuse this model). Only
/// transactions in <see cref="Currency"/> count. Rollover is explicit: when on, what was left in a month is added to the
/// next month's budget (unspent money only; an overspent month starts the next one at the plain amount).
/// </summary>
public sealed class Budget
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public int? CategoryId { get; set; }
    public FinanceCategory? Category { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public BudgetPeriod Period { get; set; } = BudgetPeriod.Monthly;
    /// <summary>Monthly: the first month it applies to (day 1). Custom: the first day.</summary>
    public DateOnly StartDate { get; set; }
    /// <summary>Monthly: optional last month; Custom: the last day (inclusive).</summary>
    public DateOnly? EndDate { get; set; }
    public bool Rollover { get; set; }
    public int? TripId { get; set; }
    public string? Notes { get; set; }
    public bool IsArchived { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum RecurringKind { Bill, Subscription, Income }

/// <summary>
/// A repeating bill, subscription or income (rent, salary, streaming). Due dates come from the Phase 6 recurrence engine
/// (<see cref="Recurrence"/>); nothing is pre-generated. Paying or skipping a due date writes a
/// <see cref="RecurringOccurrence"/>; paying creates a real transaction.
/// </summary>
public sealed class RecurringItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public RecurringKind Kind { get; set; } = RecurringKind.Bill;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public int? AccountId { get; set; }
    public FinanceAccount? Account { get; set; }
    public int? CategoryId { get; set; }
    public FinanceCategory? Category { get; set; }
    public string? Payee { get; set; }
    public RecurrenceFrequency Frequency { get; set; } = RecurrenceFrequency.Monthly;
    public int Interval { get; set; } = 1;
    /// <summary>The first due date (the rule repeats from it).</summary>
    public DateOnly StartDate { get; set; }
    /// <summary>Last possible due date (inclusive); null = open-ended.</summary>
    public DateOnly? EndDate { get; set; }
    /// <summary>Due dates before this are history and never shown as unpaid.</summary>
    public DateOnly TrackedFrom { get; set; }
    /// <summary>Paused/cancelled items keep their history but have no upcoming due dates.</summary>
    public bool IsActive { get; set; } = true;
    public bool ShowOnCalendar { get; set; } = true;
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<RecurringOccurrence> Occurrences { get; set; } = [];

    public TransactionType TransactionType => Kind == RecurringKind.Income ? TransactionType.Income : TransactionType.Expense;
}

public enum OccurrenceStatus { Paid, Skipped }

/// <summary>What happened to one due date of a recurring item. Deleting the payment transaction deletes this row, so the date becomes due again.</summary>
public sealed class RecurringOccurrence
{
    public int Id { get; set; }
    public int RecurringItemId { get; set; }
    public RecurringItem? RecurringItem { get; set; }
    public DateOnly DueDate { get; set; }
    public OccurrenceStatus Status { get; set; }
    public int? TransactionId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// Something being saved for. The saved amount is typed by the user, or — when <see cref="AccountId"/> is set — follows
/// that account's balance. Progress is arithmetic on what was entered; Arcadia gives no financial advice.
/// </summary>
public sealed class SavingsGoal
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal TargetAmount { get; set; }
    public decimal CurrentAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public DateOnly? TargetDate { get; set; }
    public string? Description { get; set; }
    public int? AccountId { get; set; }
    public FinanceAccount? Account { get; set; }
    public string Icon { get; set; } = "Target";
    public string Accent { get; set; } = "Cyan";
    public bool IsArchived { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
