using System.Globalization;
using System.IO;
using System.Text.Json;
using Arcadia.App.Messages;
using Arcadia.Core.Finance;
using Arcadia.Core.ItTools.SystemInfo;
using Arcadia.Core.Localization;
using Arcadia.Core.Oracle;
using Arcadia.Core.Oracle.Commands;
using Arcadia.Core.Organizer;
using Arcadia.Core.Platform;
using Arcadia.Core.Settings;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.Services;

/// <summary>Oracle's view of the app: localized templates, money/date formats, finance defaults and the confirmation level.</summary>
public sealed class AppOracleEnvironment(ILocalizer loc, ISettingsService settings, FinanceContext finance, TimeProvider time) : IOracleEnvironment
{
    public DateTime Now => time.GetLocalNow().DateTime;
    public string UiLanguage => loc.Culture.TwoLetterISOLanguageName == "ar" ? "ar" : "en";

    public string T(string key, params object?[] args) => args.Length == 0 ? loc[key] : loc.Format(key, args.Select(a => a ?? string.Empty).ToArray());

    public string Money(decimal amount, string currency) => finance.Format(amount, currency);

    public string Date(DateOnly date)
    {
        var today = DateOnly.FromDateTime(Now);
        var format = date.Year == today.Year ? "ddd d MMM" : "d MMM yyyy";
        return date.ToString(format, loc.Culture);
    }

    public string Time(TimeOnly t) => t.ToString("t", loc.Culture);
    public DecimalStyle DecimalStyle => finance.Money.DecimalStyle;
    public int? DefaultAccountId => settings.Current.Finance.DefaultAccountId;

    public OracleConfirmation Confirmation => settings.Current.Oracle.Confirmation switch
    {
        "always" => OracleConfirmation.Always,
        "minimal" => OracleConfirmation.Minimal,
        _ => OracleConfirmation.Smart,
    };
}

/// <summary>A command the user ran (history) or one Oracle could not match (unknown list).</summary>
public sealed record OracleLogEntry(string Text, DateTime At, string? Handler = null);

/// <summary>
/// Owns the Oracle engine (created on first use, not at startup), the shared conversation context, the name index
/// (invalidated when Arcadia reports changes) and the small local history file. Nothing leaves the computer.
/// </summary>
public sealed class OracleHost :
    IRecipient<LibraryChangedMessage>, IRecipient<OrganizerChangedMessage>, IRecipient<ProjectsChangedMessage>, IRecipient<TravelChangedMessage>
{
    private readonly IServiceProvider _services;
    private readonly ISettingsService _settings;
    private readonly ILogger<OracleHost> _logger;
    private readonly string _file;
    private readonly Lazy<OracleEngine> _engine;
    private OracleEntityIndex? _index;
    private bool _loaded;

    public OracleHost(IServiceProvider services, ISettingsService settings, IAppPaths paths, IMessenger messenger, ILogger<OracleHost> logger)
    {
        _services = services;
        _settings = settings;
        _logger = logger;
        _file = Path.Combine(paths.DataDirectory, "Oracle", "oracle-history.json");
        _engine = new Lazy<OracleEngine>(Create);
        messenger.RegisterAll(this);
    }

    public OracleContext Context { get; } = new();
    public OracleSettings Prefs => _settings.Current.Oracle;
    public OracleEngine Engine => _engine.Value;
    public bool IsCreated => _engine.IsValueCreated;
    public List<OracleLogEntry> History { get; private set; } = [];
    public List<OracleLogEntry> Unknown { get; private set; } = [];

    private OracleEngine Create()
    {
        var sp = _services;
        var processes = sp.GetService<IProcessService>();
        var services = new OracleServices
        {
            Games = sp.GetRequiredService<Arcadia.Core.Games.IGameCatalog>(),
            Projects = sp.GetRequiredService<Arcadia.Core.Projects.IProjectStore>(),
            Documents = sp.GetRequiredService<Arcadia.Core.Documents.IDocumentStore>(),
            Travel = sp.GetRequiredService<Arcadia.Core.Travel.ITravelStore>(),
            Tasks = sp.GetRequiredService<ITaskStore>(),
            Notes = sp.GetRequiredService<INoteStore>(),
            Calendar = sp.GetRequiredService<ICalendarStore>(),
            Agenda = sp.GetRequiredService<ICalendarQueryService>(),
            Directory = sp.GetRequiredService<IEntityDirectory>(),
            Search = sp.GetRequiredService<Arcadia.Core.Search.IGlobalSearchService>(),
            Finance = sp.GetRequiredService<IFinanceStore>(),
            Ping = sp.GetService<Arcadia.Core.ItTools.Network.IPingService>(),
            Dns = sp.GetService<Arcadia.Core.ItTools.Network.IDnsLookupService>(),
            Ports = sp.GetService<Arcadia.Core.ItTools.Network.IPortTestService>(),
            SystemInfo = sp.GetService<ISystemInformationService>(),
            Network = sp.GetService<Arcadia.Core.ItTools.Network.INetworkInformationService>(),
            Storage = sp.GetService<Arcadia.Core.ItTools.Files.IStorageService>(),
            // listing only: Oracle never gets the process service itself, so it cannot end a process
            ListProcesses = processes == null ? null : ct => processes.GetProcessesAsync(ct),
            Automation = sp.GetService<Arcadia.Core.Automation.IAutomationService>(),
        };
        _index = new OracleEntityIndex(services.LoadNamesAsync);
        var env = sp.GetRequiredService<AppOracleEnvironment>();
        _logger.LogInformation("Oracle engine created ({Commands} commands, {Phrases} phrases)", OracleRegistry.CreateDefault().Count, OracleLexicon.Default.EntryCount);
        return new OracleEngine(OracleRegistry.CreateDefault(), new OracleRuntime(services, env, _index));
    }

    public OracleLanguage? ForcedLanguage => Prefs.Language switch
    {
        "en" => OracleLanguage.English,
        "de" => OracleLanguage.German,
        "ar" => OracleLanguage.Arabic,
        _ => null,
    };

    // ── index invalidation (only when the engine exists; otherwise nothing is cached) ──
    public void Receive(LibraryChangedMessage message) => _index?.Invalidate(EntityKind.Game);
    public void Receive(OrganizerChangedMessage message) => _index?.Invalidate(EntityKind.Account, EntityKind.Goal, EntityKind.Recurring, EntityKind.Budget);
    public void Receive(ProjectsChangedMessage message) => _index?.Invalidate(EntityKind.Project);
    public void Receive(TravelChangedMessage message) => _index?.Invalidate(EntityKind.Trip, EntityKind.Place);
    public void InvalidateAll() => _index?.Invalidate();

    // ── history & unknown commands (bounded JSON file) ──

    private sealed record Stored(List<OracleLogEntry> History, List<OracleLogEntry> Unknown);

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_file)) return;
            await using var s = File.OpenRead(_file);
            var stored = await JsonSerializer.DeserializeAsync<Stored>(s);
            History = stored?.History ?? [];
            Unknown = stored?.Unknown ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Oracle history could not be read; starting empty");
        }
    }

    public void Record(string text, string? handler, bool unknown)
    {
        if (Prefs.KeepHistory)
        {
            History.RemoveAll(h => string.Equals(h.Text, text, StringComparison.Ordinal));
            History.Add(new OracleLogEntry(text, DateTime.Now, handler));
            var limit = Math.Clamp(Prefs.HistoryLimit, 10, 1000);
            if (History.Count > limit) History.RemoveRange(0, History.Count - limit);
        }
        if (unknown && Prefs.CollectUnknown)
        {
            Unknown.Add(new OracleLogEntry(text, DateTime.Now));
            if (Unknown.Count > 200) Unknown.RemoveRange(0, Unknown.Count - 200);
        }
        _ = SaveAsync();
    }

    public Task ClearHistoryAsync() { History.Clear(); return SaveAsync(); }
    public Task ClearUnknownAsync() { Unknown.Clear(); return SaveAsync(); }

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            await using (var s = File.Create(tmp))
                await JsonSerializer.SerializeAsync(s, new Stored(Prefs.KeepHistory ? History : [], Unknown), new JsonSerializerOptions { WriteIndented = true });
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Oracle history could not be saved");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>"Ctrl+Space" etc. as a key gesture; unknown values fall back to the default.</summary>
    public static readonly string[] Shortcuts = ["Ctrl+Space", "Ctrl+Shift+Space", "Ctrl+Shift+O", "Ctrl+Alt+Space"];

    public static string DescribeTrace(OracleTrace t) => string.Create(CultureInfo.InvariantCulture,
        $"in: {t.Input}\nnorm: {t.Normalized}   lang: {t.Language}\nconcepts: {t.Concepts}\nintent: {t.Intent}   entity: {t.Entity}   handler: {t.Handler}\nparams: {t.Parameters}\ncontext: {t.Context}\nconfidence: {t.Confidence} ({t.Score})   parse {t.ParseMs:0.00} ms   total {t.TotalMs:0.0} ms");
}
