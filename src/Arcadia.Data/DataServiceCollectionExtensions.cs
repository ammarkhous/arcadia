using Arcadia.Core.Games;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Arcadia.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddArcadiaData(this IServiceCollection services, string databasePath)
    {
        services.AddDbContextFactory<ArcadiaDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<IGameLibraryService, GameLibraryService>();
        services.AddSingleton<IGameCatalog, GameCatalog>();
        services.AddSingleton<Arcadia.Core.ItTools.Diagnostics.IDiagnosticSessionStore, DiagnosticSessionStore>();
        services.AddSingleton<Arcadia.Core.Projects.IProjectStore, ProjectStore>();
        services.AddSingleton<Arcadia.Core.Documents.IDocumentStore, DocumentStore>();
        services.AddSingleton<Arcadia.Core.Search.ISearchProvider, Search.IndexSearchProvider>();
        services.AddSingleton<Arcadia.Core.Search.ISearchProvider, Search.GameSearchProvider>();
        services.AddSingleton<Arcadia.Core.Travel.ITravelStore>(sp => new TravelStore(
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ArcadiaDbContext>>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetService<Arcadia.Core.Travel.ICountryCatalog>()));
        services.AddSingleton<Arcadia.Core.Search.ISearchProvider>(sp => new Search.CountrySearchProvider(sp.GetRequiredService<Arcadia.Core.Travel.ICountryCatalog>()));
        // Phase 6 organizer: one store per concept; the query/capture interfaces are what dashboards and future command engines use
        services.AddSingleton<Organizer.TaskStore>();
        services.AddSingleton<Arcadia.Core.Organizer.ITaskStore>(sp => sp.GetRequiredService<Organizer.TaskStore>());
        services.AddSingleton<Arcadia.Core.Organizer.ITaskQueryService>(sp => sp.GetRequiredService<Organizer.TaskStore>());
        services.AddSingleton<Organizer.NoteStore>();
        services.AddSingleton<Arcadia.Core.Organizer.INoteStore>(sp => sp.GetRequiredService<Organizer.NoteStore>());
        services.AddSingleton<Arcadia.Core.Organizer.INoteQueryService>(sp => sp.GetRequiredService<Organizer.NoteStore>());
        services.AddSingleton<Arcadia.Core.Organizer.ICalendarStore, Organizer.CalendarStore>();
        services.AddSingleton<Arcadia.Core.Organizer.ICalendarQueryService, Organizer.CalendarQueryService>();
        services.AddSingleton<Arcadia.Core.Organizer.IEntityDirectory, Organizer.EntityDirectory>();
        services.AddSingleton<Arcadia.Core.Organizer.IQuickCaptureService, Organizer.QuickCaptureService>();
        // Phase 7 finance: one store; the query interface is what dashboards and a future command engine use
        services.AddSingleton<Finance.FinanceStore>();
        services.AddSingleton<Arcadia.Core.Finance.IFinanceStore>(sp => sp.GetRequiredService<Finance.FinanceStore>());
        services.AddSingleton<Arcadia.Core.Finance.IFinanceQueryService>(sp => sp.GetRequiredService<Finance.FinanceStore>());
        services.AddSingleton<Arcadia.Core.Finance.IQuickExpenseService, Finance.QuickExpenseService>();
        services.AddSingleton<Arcadia.Core.Finance.IFinanceExporter, Finance.FinanceJsonExporter>();
        services.AddSingleton<Arcadia.Core.Search.IGlobalSearchService, Arcadia.Core.Search.GlobalSearchService>();
        services.AddSingleton<Arcadia.Core.Automation.IAutomationStore, AutomationStore>();
        services.AddSingleton<Arcadia.Core.Automation.IWorkSessionStore, WorkSessionStore>();
        services.AddSingleton<DatabaseInitializer>();
        return services;
    }
}
