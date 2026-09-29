using Arcadia.Core.Documents;
using Arcadia.Core.Finance;
using Arcadia.Core.Games;
using Arcadia.Core.ItTools.Files;
using Arcadia.Core.ItTools.Network;
using Arcadia.Core.ItTools.SystemInfo;
using Arcadia.Core.Organizer;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Core.Travel;

namespace Arcadia.Core.Oracle;

/// <summary>
/// The existing Arcadia services Oracle works through (never the database directly). IT tools are exposed read-only:
/// processes are a listing function, so nothing in Oracle can reach Terminate.
/// </summary>
public sealed class OracleServices
{
    public required IGameCatalog Games { get; init; }
    public required IProjectStore Projects { get; init; }
    public required IDocumentStore Documents { get; init; }
    public required ITravelStore Travel { get; init; }
    public required ITaskStore Tasks { get; init; }
    public required INoteStore Notes { get; init; }
    public required ICalendarStore Calendar { get; init; }
    public required ICalendarQueryService Agenda { get; init; }
    public required IEntityDirectory Directory { get; init; }
    public required IGlobalSearchService Search { get; init; }
    public required IFinanceStore Finance { get; init; }

    public IPingService? Ping { get; init; }
    public IDnsLookupService? Dns { get; init; }
    public IPortTestService? Ports { get; init; }
    public ISystemInformationService? SystemInfo { get; init; }
    public INetworkInformationService? Network { get; init; }
    public IStorageService? Storage { get; init; }
    public Func<CancellationToken, Task<IReadOnlyList<ProcessEntry>>>? ListProcesses { get; init; }

    /// <summary>Automation (Phase 9): listing, pause/resume, Gaming Mode and confirmed creation — never bypassing its safety rules.</summary>
    public Arcadia.Core.Automation.IAutomationService? Automation { get; init; }

    /// <summary>Name lists for <see cref="OracleEntityIndex"/>: the directory for most kinds, the finance store for the rest.</summary>
    public async Task<IReadOnlyList<(Organizer.EntityRef Ref, string Name)>> LoadNamesAsync(EntityKind kind, string? text, CancellationToken ct)
    {
        switch (kind)
        {
            case EntityKind.Account:
                return (await Finance.GetAccountBalancesAsync(false, ct).ConfigureAwait(false)).Select(a => (new EntityRef(kind, a.Account.Id), a.Account.Name)).ToList();
            case EntityKind.Goal:
                return (await Finance.GetGoalsAsync(false, ct).ConfigureAwait(false)).Select(g => (new EntityRef(kind, g.Id), g.Name)).ToList();
            case EntityKind.Recurring:
                return (await Finance.GetRecurringItemsAsync(ct).ConfigureAwait(false)).Select(r => (new EntityRef(kind, r.Id), r.Name)).ToList();
            case EntityKind.Budget:
                return [];
            default:
                return (await Directory.FindAsync(kind, text, text == null ? 5000 : 40, ct).ConfigureAwait(false)).Select(i => (i.Ref, i.Name)).ToList();
        }
    }
}
