using Arcadia.App.Dialogs;
using Arcadia.App.Services;
using Arcadia.App.ViewModels.Organizer;
using Arcadia.Core.Finance;
using Arcadia.Core.Organizer;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Finance;

/// <summary>
/// Opens Finance's editors from anywhere (the Finance pages, the command palette, Quick Capture, trip and project
/// workspaces) so every entry point behaves the same. Returns true when something was saved.
/// </summary>
public sealed class FinanceActions(FinanceContext ctx, IFinanceExporter exporter)
{
    public FinanceContext Ctx { get; } = ctx;

    private async Task<(IReadOnlyList<AccountBalance> Accounts, IReadOnlyList<CategoryInfo> Categories)> ListsAsync()
    {
        await Ctx.Ready;
        return (await Ctx.Store.GetAccountBalancesAsync(true), await Ctx.Store.GetCategoriesAsync(Ctx.Language, includeArchived: true));
    }

    /// <summary>Without any account there is nothing to book on: offer to create one first.</summary>
    private async Task<bool> EnsureAccountAsync(IReadOnlyList<AccountBalance> accounts)
    {
        if (accounts.Any(a => !a.Account.IsArchived)) return true;
        if (!Ctx.Dialogs.Confirm(Ctx.Loc["Fin_NeedAccountFirst"], Ctx.Loc["Acct_New"])) return false;
        return await NewAccountAsync() != null;
    }

    public async Task<bool> NewTransactionAsync(TransactionType type = TransactionType.Expense, TransactionDraft? prefill = null)
    {
        try
        {
            var (accounts, categories) = await ListsAsync();
            if (!await EnsureAccountAsync(accounts)) return false;
            if (!accounts.Any(a => !a.Account.IsArchived)) (accounts, categories) = await ListsAsync();
            var draft = prefill ?? new TransactionDraft(type, Ctx.Today, 0m, Ctx.Prefs.DefaultAccountId ?? 0);
            var chips = await ChipsAsync(draft.ProjectId, draft.TripId);
            var vm = new TransactionEditorViewModel(Ctx, accounts, categories, null, draft, chips);
            return Ctx.Dialogs.Show(vm) || vm.SavedId != null;
        }
        catch (Exception ex) { return Failed(ex); }
    }

    public async Task<bool> EditTransactionAsync(int id)
    {
        try
        {
            var (accounts, categories) = await ListsAsync();
            var t = await Ctx.Store.GetTransactionAsync(id);
            if (t == null) { Ctx.Notifications.Show(Ctx.Loc["Fin_ErrGone"], NotificationKind.Warning); return false; }
            var chips = await ChipsAsync(t.ProjectId, t.TripId);
            var docs = t.Documents.Select(d => (d.DocumentId, d.Role, d.Document?.DisplayName ?? "#" + d.DocumentId)).ToList();
            return Ctx.Dialogs.Show(new TransactionEditorViewModel(Ctx, accounts, categories, t, null, chips, docs));
        }
        catch (Exception ex) { return Failed(ex); }
    }

    /// <summary>A copy of a transaction dated today, opened in the editor (nothing is saved until confirmed).</summary>
    public async Task<bool> DuplicateTransactionAsync(int id)
    {
        var t = await Ctx.Store.GetTransactionAsync(id);
        if (t == null) return false;
        return await NewTransactionAsync(t.Type, new TransactionDraft(t.Type, Ctx.Today, t.Amount, t.AccountId, t.CategoryId, t.Description, t.Payee, null, t.ToAccountId, t.ToAmount,
            t.Tags, t.Notes, t.ProjectId, t.TripId));
    }

    public async Task<bool> DeleteTransactionAsync(int id, string label)
    {
        if (!Ctx.Dialogs.Confirm(Ctx.Loc.Format("Txn_ConfirmDeleteNamed", label), Ctx.Loc["Confirm_Delete"])) return false;
        if (!await Ctx.TryAsync(() => Ctx.Store.DeleteTransactionAsync(id), "Fin_ErrSave")) return false;
        Ctx.Changed();
        return true;
    }

    /// <summary>Links a Documents-library file to a transaction as a receipt (or invoice, contract, statement).</summary>
    public async Task<bool> AttachReceiptAsync(int transactionId)
    {
        var picker = new EntityPickerViewModel(Ctx.Organizer, [EntityKind.Document]) { Title = Ctx.Loc["Txn_AttachTitle"] };
        if (!Ctx.Dialogs.Show(picker) || picker.Selected is not { } info) return false;
        if (!await Ctx.TryAsync(() => Ctx.Store.AttachDocumentAsync(transactionId, info.Ref.Id, DocumentRole.Receipt), "Fin_ErrSave")) return false;
        Ctx.Notifications.Show(Ctx.Loc.Format("Txn_Attached", info.Name), NotificationKind.Success);
        Ctx.Changed();
        return true;
    }

    public async Task<int?> NewAccountAsync()
    {
        await Ctx.Ready;
        var vm = new AccountEditorViewModel(Ctx, null, false);
        if (!Ctx.Dialogs.Show(vm)) return null;
        // the first account becomes the default for quick entry
        if (Ctx.Prefs.DefaultAccountId == null && vm.SavedId != null) { Ctx.Prefs.DefaultAccountId = vm.SavedId; Ctx.SavePrefs(); }
        return vm.SavedId;
    }

    public async Task<bool> EditAccountAsync(int id)
    {
        var balances = await Ctx.Store.GetAccountBalancesAsync(true);
        var b = balances.FirstOrDefault(x => x.Account.Id == id);
        if (b == null) return false;
        return Ctx.Dialogs.Show(new AccountEditorViewModel(Ctx, b.Account, b.TransactionCount > 0));
    }

    public async Task<bool> NewBudgetAsync(int? categoryId = null, int? tripId = null, string? tripName = null, DateOnly? start = null, DateOnly? end = null)
    {
        var (_, categories) = await ListsAsync();
        return Ctx.Dialogs.Show(new BudgetEditorViewModel(Ctx, categories, null, categoryId, tripId, tripName, start, end));
    }

    public async Task<bool> EditBudgetAsync(int id, string? tripName = null)
    {
        var (_, categories) = await ListsAsync();
        var budget = (await Ctx.Store.GetBudgetsAsync(true)).FirstOrDefault(b => b.Id == id);
        return budget != null && Ctx.Dialogs.Show(new BudgetEditorViewModel(Ctx, categories, budget, tripName: tripName));
    }

    public async Task<bool> NewRecurringAsync(RecurringKind kind)
    {
        var (accounts, categories) = await ListsAsync();
        return Ctx.Dialogs.Show(new RecurringEditorViewModel(Ctx, accounts, categories, null, kind));
    }

    public async Task<bool> EditRecurringAsync(int id)
    {
        var (accounts, categories) = await ListsAsync();
        var item = await Ctx.Store.GetRecurringItemAsync(id);
        return item != null && Ctx.Dialogs.Show(new RecurringEditorViewModel(Ctx, accounts, categories, item));
    }

    /// <summary>One click when the item has an account; otherwise (or with <paramref name="ask"/>) the payment dialog.</summary>
    public async Task<bool> MarkPaidAsync(int itemId, DateOnly due, bool ask = false)
    {
        var item = await Ctx.Store.GetRecurringItemAsync(itemId);
        if (item == null) return false;
        if (!ask && item.AccountId != null)
        {
            if (!await Ctx.TryAsync(() => Ctx.Store.MarkPaidAsync(itemId, due, paidOn: due <= Ctx.Today ? due : Ctx.Today), "Fin_ErrSave")) return false;
            Ctx.Notifications.Show(Ctx.Loc.Format(item.Kind == RecurringKind.Income ? "Pay_DoneIncome" : "Pay_Done", item.Name), NotificationKind.Success);
            Ctx.Changed();
            return true;
        }
        var accounts = await Ctx.Store.GetAccountBalancesAsync();
        return Ctx.Dialogs.Show(new PayBillViewModel(Ctx, item, due, accounts));
    }

    public async Task<bool> SkipAsync(int itemId, DateOnly due)
    {
        if (!await Ctx.TryAsync(() => Ctx.Store.SkipAsync(itemId, due), "Fin_ErrSave")) return false;
        Ctx.Changed();
        return true;
    }

    public async Task<bool> NewGoalAsync()
    {
        var (accounts, _) = await ListsAsync();
        return Ctx.Dialogs.Show(new GoalEditorViewModel(Ctx, accounts, null));
    }

    public async Task<bool> EditGoalAsync(int id)
    {
        var (accounts, _) = await ListsAsync();
        var goal = (await Ctx.Store.GetGoalsAsync(true)).FirstOrDefault(g => g.Id == id);
        return goal != null && Ctx.Dialogs.Show(new GoalEditorViewModel(Ctx, accounts, goal));
    }

    public async Task<bool> AddToGoalAsync(GoalItem goal)
    {
        var vm = new AmountPromptViewModel(Ctx, Ctx.Loc["Goal_AddTitle"], Ctx.Loc.Format("Goal_AddBody", goal.Name), goal.Progress.Goal.Currency, allowNegative: true);
        if (!Ctx.Dialogs.Show(vm)) return false;
        if (!await Ctx.TryAsync(() => Ctx.Store.AddToGoalAsync(goal.Id, vm.Amount), "Fin_ErrSave")) return false;
        Ctx.Changed();
        return true;
    }

    public async Task<bool> ManageCategoriesAsync()
    {
        await Ctx.Ready;
        return Ctx.Dialogs.Show(new CategoryManagerViewModel(Ctx));
    }

    public async Task<bool> ImportAsync()
    {
        var (accounts, categories) = await ListsAsync();
        if (!await EnsureAccountAsync(accounts)) return false;
        (accounts, categories) = await ListsAsync();
        return Ctx.Dialogs.Show(new ImportWizardViewModel(Ctx, accounts, categories));
    }

    public async Task ExportAsync(TransactionQuery? preset = null)
    {
        var (accounts, categories) = await ListsAsync();
        Ctx.Dialogs.Show(new FinanceExportViewModel(Ctx, exporter, accounts, categories, preset));
    }

    private async Task<IReadOnlyDictionary<EntityRef, ContextChip>> ChipsAsync(int? projectId, int? tripId)
    {
        var refs = new List<EntityRef>();
        if (projectId is { } p) refs.Add(new EntityRef(EntityKind.Project, p));
        if (tripId is { } t) refs.Add(new EntityRef(EntityKind.Trip, t));
        return refs.Count == 0 ? new Dictionary<EntityRef, ContextChip>() : await ContextChip.ResolveAsync(Ctx.Organizer, refs);
    }

    private bool Failed(Exception ex)
    {
        Ctx.Logger.LogError(ex, "Opening a finance editor failed");
        Ctx.Notifications.Show(Ctx.Loc["Fin_ErrLoad"], NotificationKind.Warning);
        return false;
    }
}
