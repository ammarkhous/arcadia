using Arcadia.App.Services;
using Arcadia.Core.Finance;
using Arcadia.Core.Organizer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels.Finance;

/// <summary>A Finance section: loads lazily, reloads only when data changed while it was hidden.</summary>
public abstract partial class FinanceSectionViewModel(FinanceActions actions) : ObservableObject
{
    private CancellationTokenSource? _cts;

    public FinanceActions Actions { get; } = actions;
    public FinanceContext Ctx => Actions.Ctx;
    public bool IsDirty { get; set; } = true;
    [ObservableProperty] private bool _isBusy;

    public Task EnsureAsync() => IsDirty ? RefreshAsync() : Task.CompletedTask;

    public async Task RefreshAsync()
    {
        IsDirty = false;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await Ctx.Ready;
            await LoadAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Ctx.Logger.LogError(ex, "Loading {Section} failed", GetType().Name);
            Ctx.Notifications.Show(Ctx.Loc["Fin_ErrLoad"], NotificationKind.Warning);
        }
        finally
        {
            if (_cts == cts) IsBusy = false;
        }
    }

    protected abstract Task LoadAsync(CancellationToken ct);

    /// <summary>Set by the page: shows the register with a filter (from accounts, budgets, reports…).</summary>
    public Func<TransactionQuery, Task>? ShowRegister { get; set; }
}

/// <summary>
/// Finance: a private financial terminal. Eight sections in a rail (overview, register, accounts, budgets, bills and
/// subscriptions, goals, reports, history). All data is local; privacy mode hides every amount.
/// </summary>
public sealed partial class FinanceViewModel : ObservableObject, IActivatable, IDeactivatable, IShortcutTarget, IRecipient<OrganizerChangedMessage>
{
    private bool _active;

    public FinanceViewModel(FinanceActions actions)
    {
        Actions = actions;
        var loc = actions.Ctx.Loc;
        Overview = new FinanceOverviewViewModel(actions);
        Register = new FinanceRegisterViewModel(actions);
        Accounts = new FinanceAccountsViewModel(actions);
        Budgets = new FinanceBudgetsViewModel(actions);
        Recurring = new FinanceRecurringViewModel(actions);
        Goals = new FinanceGoalsViewModel(actions);
        Reports = new FinanceReportsViewModel(actions);
        History = new FinanceHistoryViewModel(actions);
        foreach (var s in AllSections) s.ShowRegister = ShowRegisterAsync;
        Sections =
        [
            new(FinanceSection.Overview, loc["FinSec_Overview"], "Finance"),
            new(FinanceSection.Transactions, loc["FinSec_Transactions"], "Receipt"),
            new(FinanceSection.Accounts, loc["FinSec_Accounts"], "Wallet"),
            new(FinanceSection.Budgets, loc["FinSec_Budgets"], "Chart"),
            new(FinanceSection.Recurring, loc["FinSec_Recurring"], "Repeat"),
            new(FinanceSection.Goals, loc["FinSec_Goals"], "Target"),
            new(FinanceSection.Reports, loc["FinSec_Reports"], "Timeline"),
            new(FinanceSection.History, loc["FinSec_History"], "Calendar"),
        ];
        _section = Enum.TryParse<FinanceSection>(actions.Ctx.Prefs.LastSection, true, out var last) ? last : FinanceSection.Overview;
        foreach (var s in Sections) s.IsSelected = s.Section == _section;
        actions.Ctx.Messenger.Register(this);
        actions.Ctx.FormatChanged += (_, _) => Invalidate();
        actions.Ctx.Loc.CultureChanged += (_, _) => Invalidate();
    }

    public FinanceActions Actions { get; }
    public FinanceContext Ctx => Actions.Ctx;
    public FinancePrivacy Privacy => FinancePrivacy.Instance;
    public IReadOnlyList<FinanceSectionItem> Sections { get; }
    public FinanceOverviewViewModel Overview { get; }
    public FinanceRegisterViewModel Register { get; }
    public FinanceAccountsViewModel Accounts { get; }
    public FinanceBudgetsViewModel Budgets { get; }
    public FinanceRecurringViewModel Recurring { get; }
    public FinanceGoalsViewModel Goals { get; }
    public FinanceReportsViewModel Reports { get; }
    public FinanceHistoryViewModel History { get; }

    private IEnumerable<FinanceSectionViewModel> AllSections => [Overview, Register, Accounts, Budgets, Recurring, Goals, Reports, History];

    [ObservableProperty] private FinanceSection _section;
    [ObservableProperty] private bool _hasAccounts = true;
    [ObservableProperty] private bool _isReady;

    public FinanceSectionViewModel Current => Section switch
    {
        FinanceSection.Transactions => Register,
        FinanceSection.Accounts => Accounts,
        FinanceSection.Budgets => Budgets,
        FinanceSection.Recurring => Recurring,
        FinanceSection.Goals => Goals,
        FinanceSection.Reports => Reports,
        FinanceSection.History => History,
        _ => Overview,
    };

    public async Task ActivateAsync()
    {
        _active = true;
        await Ctx.Ready;
        await CheckAccountsAsync();
        IsReady = true;
        await Current.EnsureAsync();
    }

    public void Deactivate() => _active = false;

    public void Receive(OrganizerChangedMessage message)
    {
        if ((message.What & OrganizerChange.Finance) == 0) return;
        Invalidate();
    }

    private void Invalidate()
    {
        foreach (var s in AllSections) s.IsDirty = true;
        if (!_active) return;
        _ = CheckAccountsAsync();
        _ = Current.RefreshAsync();
    }

    private async Task CheckAccountsAsync()
    {
        try
        {
            var balances = await Ctx.Store.GetAccountBalancesAsync(true);
            HasAccounts = balances.Count > 0;
            var bills = await Ctx.Store.GetUpcomingBillsAsync(Ctx.Today, Ctx.Today.AddDays(7), Ctx.Language);
            Sections[4].Count = bills.Count > 0 ? bills.Count.ToString(Ctx.Loc.Culture) : null;
            var over = (await Ctx.Store.GetBudgetStatusAsync(Ctx.Today)).Count(b => b.Level == BudgetLevel.Over);
            Sections[3].Count = over > 0 ? over.ToString(Ctx.Loc.Culture) : null;
            Sections[2].Count = balances.Count(b => !b.Account.IsArchived) is var n and > 0 ? n.ToString(Ctx.Loc.Culture) : null;
        }
        catch (Exception ex) { Ctx.Logger.LogWarning(ex, "Finance counts could not be computed"); }
    }

    partial void OnSectionChanged(FinanceSection value)
    {
        foreach (var s in Sections) s.IsSelected = s.Section == value;
        OnPropertyChanged(nameof(Current));
        Ctx.Prefs.LastSection = value.ToString().ToLowerInvariant();
        Ctx.SavePrefs();
        if (_active) _ = Current.EnsureAsync();
    }

    [RelayCommand] private void SelectSection(FinanceSectionItem? item) { if (item != null) Section = item.Section; }

    public async Task ShowSectionAsync(FinanceSection section)
    {
        Section = section;
        await Ctx.Ready;
        await Current.EnsureAsync();
    }

    /// <summary>From other modules (trip and project expenses): the register scoped to them.</summary>
    public async Task ShowRegisterFromAsync(TransactionQuery query)
    {
        await ActivateAsync();
        await ShowRegisterAsync(query);
    }

    private async Task ShowRegisterAsync(TransactionQuery query)
    {
        Section = FinanceSection.Transactions;
        await Register.ApplyAsync(query);
    }

    /// <summary>Opens a finance record (search results, links, calendar bills).</summary>
    public async Task OpenAsync(EntityRef target)
    {
        switch (target.Kind)
        {
            case EntityKind.Transaction:
                Section = FinanceSection.Transactions;
                await Register.ShowTransactionAsync(target.Id);
                break;
            case EntityKind.Account:
                await ShowRegisterAsync(new TransactionQuery(AccountId: target.Id));
                break;
            case EntityKind.Budget:
                await ShowSectionAsync(FinanceSection.Budgets);
                break;
            case EntityKind.Recurring:
                await ShowSectionAsync(FinanceSection.Recurring);
                Recurring.Highlight(target.Id);
                break;
            case EntityKind.Goal:
                await ShowSectionAsync(FinanceSection.Goals);
                break;
        }
    }

    [RelayCommand] private Task NewTransactionAsync() => Actions.NewTransactionAsync(TransactionType.Expense);
    [RelayCommand] private Task NewIncomeAsync() => Actions.NewTransactionAsync(TransactionType.Income);
    [RelayCommand] private Task NewTransferAsync() => Actions.NewTransactionAsync(TransactionType.Transfer);
    [RelayCommand] private async Task NewAccountAsync() { if (await Actions.NewAccountAsync() != null) await CheckAccountsAsync(); }
    [RelayCommand] private Task ImportAsync() => Actions.ImportAsync();
    [RelayCommand] private Task ExportAsync() => Actions.ExportAsync(Section == FinanceSection.Transactions ? Register.CurrentQuery() : null);
    [RelayCommand] private Task ManageCategoriesAsync() => Actions.ManageCategoriesAsync();
    [RelayCommand] private void TogglePrivacy() => Privacy.IsHidden = !Privacy.IsHidden;

    public bool OnNew()
    {
        _ = Actions.NewTransactionAsync(TransactionType.Expense);
        return true;
    }

    public bool OnSave() => false;
}
