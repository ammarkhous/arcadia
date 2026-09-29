namespace Arcadia.Core.Oracle.Commands;

/// <summary>
/// The command registry. Adding a command = a class plus a line here; vocabulary lives in Lexicon/*.txt. Order only
/// breaks exact score ties (earlier wins).
/// </summary>
public static class OracleRegistry
{
    public static IReadOnlyList<OracleCommand> CreateDefault() =>
    [
        new HelpCommand(), new ResetCommand(), new NavigateCommand(),
        new GameListCommand(), new GameStatsCommand(), new GameUpdateCommand(),
        new ProjectListCommand(), new ProjectCreateCommand(), new ProjectUpdateCommand(), new ProjectActivityCommand(),
        new TaskListCommand(), new TaskCreateCommand(), new TaskUpdateCommand(),
        new NoteListCommand(), new NoteCreateCommand(),
        new AgendaCommand(), new EventCreateCommand(), new WhenCommand(),
        new DocumentListCommand(), new DocumentOpenCommand(), new DocumentLinkCommand(),
        new TripListCommand(), new TripCreateCommand(), new PlaceListCommand(), new PlaceUpdateCommand(), new TravelAddCommand(), new TravelJournalCommand(), new TravelStatsCommand(),
        new SpendingCommand(), new LargestExpensesCommand(), new TransactionListCommand(), new SubscriptionsCommand(), new BillsCommand(), new BudgetsCommand(), new GoalsCommand(), new BalancesCommand(), new ExpenseCreateCommand(),
        new PingCommand(), new DnsCommand(), new PortCommand(), new TraceCommand(), new SystemOverviewCommand(),
        new AutomationListCommand(), new ModeCommand(), new AutomationPauseCommand(), new AutomationCreateCommand(),
        new RelatedCommand(), new OpenNamedCommand(), new SearchCommand(),
    ];
}
