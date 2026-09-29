using System.Globalization;
using Arcadia.Core.Finance;
using Arcadia.Core.Localization;
using Arcadia.Core.Organizer;
using Arcadia.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arcadia.App.Services;

/// <summary>
/// Finance privacy mode: while on, every monetary value on screen is shown as "••••" (the data is untouched). The
/// state is remembered, so a screen hidden before a restart stays hidden afterwards.
/// </summary>
public sealed partial class FinancePrivacy : ObservableObject
{
    public static FinancePrivacy Instance { get; } = new();

    private FinancePrivacy() { }

    [ObservableProperty] private bool _isHidden;

    public const string Mask = "••••••";

    private static readonly System.Text.RegularExpressions.Regex Numbers =
        new(@"[-+−]?\d[\d.,'  ٫٬ ]*\d|[-+−]?\d", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Hides the numbers in a money text ("Spent €380.00 of €450.00" → "Spent €•••••• of €••••••").</summary>
    public static string Apply(string? text, bool hidden) => text == null ? string.Empty : hidden ? Numbers.Replace(text, Mask) : text;
}

/// <summary>Number styles offered in Settings (separators and symbol position; the currency symbol always comes from the amount).</summary>
public static class FinanceFormats
{
    public static readonly IReadOnlyList<string> Keys = ["windows", "point", "comma", "space", "swiss"];

    /// <summary>The Windows regional format of this user (with their custom separators), independent of Arcadia's language.</summary>
    public static CultureInfo Regional()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\International");
            if (key?.GetValue("LocaleName") is string name && name.Length > 0) return new CultureInfo(name, useUserOverride: true);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or CultureNotFoundException or IOException) { }
        return CultureInfo.InstalledUICulture;
    }

    public static CultureInfo CultureFor(string? key) => key switch
    {
        "point" => CultureInfo.GetCultureInfo("en-US"),
        "comma" => CultureInfo.GetCultureInfo("de-DE"),
        "space" => CultureInfo.GetCultureInfo("fr-FR"),
        "swiss" => CultureInfo.GetCultureInfo("de-CH"),
        _ => Regional(),
    };
}

/// <summary>What the Finance pages, dialogs and integrations share.</summary>
public sealed class FinanceContext
{
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly StartupState _startup;

    public FinanceContext(IFinanceStore store, ILocalizer loc, IDialogService dialogs, INotificationService notifications, INavigationService navigation, IMessenger messenger,
        ISettingsService settings, IEntityDirectory directory, TimeProvider time, StartupState startup, ViewModels.Organizer.OrganizerContext organizer, ILogger<FinanceContext> logger)
    {
        Store = store;
        Loc = loc;
        Dialogs = dialogs;
        Notifications = notifications;
        Navigation = navigation;
        Messenger = messenger;
        Directory = directory;
        Organizer = organizer;
        Logger = logger;
        _settings = settings;
        _time = time;
        _startup = startup;
        Money = new MoneyFormatter(FinanceFormats.CultureFor(settings.Current.Finance.NumberFormat));
        FinancePrivacy.Instance.IsHidden = settings.Current.Finance.PrivacyMode;
        FinancePrivacy.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(FinancePrivacy.IsHidden) || Prefs.PrivacyMode == FinancePrivacy.Instance.IsHidden) return;
            Prefs.PrivacyMode = FinancePrivacy.Instance.IsHidden;
            SavePrefs();
        };
    }

    public IFinanceStore Store { get; }
    public ILocalizer Loc { get; }
    public IDialogService Dialogs { get; }
    public INotificationService Notifications { get; }
    public INavigationService Navigation { get; }
    public IMessenger Messenger { get; }
    public IEntityDirectory Directory { get; }
    /// <summary>The organizer's shared services (entity picker, context chips) — reused, not duplicated.</summary>
    public ViewModels.Organizer.OrganizerContext Organizer { get; }
    public ILogger Logger { get; }
    public FinanceSettings Prefs => _settings.Current.Finance;
    public FinancePrivacy Privacy => FinancePrivacy.Instance;

    public Task Ready => _startup.DatabaseReady;

    /// <summary>Formats amounts in the chosen number style; recreated when the style changes.</summary>
    public MoneyFormatter Money { get; private set; }

    /// <summary>"en", "ar" (names of built-in categories).</summary>
    public string Language => Loc.Culture.TwoLetterISOLanguageName;

    public DateTime Now => _time.GetLocalNow().DateTime;
    public DateOnly Today => DateOnly.FromDateTime(Now);

    public event EventHandler? FormatChanged;

    public void RefreshFormat()
    {
        Money = new MoneyFormatter(FinanceFormats.CultureFor(Prefs.NumberFormat));
        FormatChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Format(decimal amount, string currency) => Money.Format(amount, currency);
    public string FormatSigned(decimal amount, string currency) => Money.FormatSigned(amount, currency);

    /// <summary>Totals in several currencies side by side ("€1,200.00 · $80.00"); never summed.</summary>
    public string FormatTotals(IEnumerable<MoneyTotal> totals) => string.Join("  ·  ", totals.Select(t => Money.Format(t.Amount, t.Currency)));

    /// <summary>Reads a typed amount the way the user writes numbers; unambiguous input is accepted either way.</summary>
    public AmountParser.Outcome ParseAmount(string? text, out decimal value)
    {
        var outcome = AmountParser.TryParse(text, null, out value);
        return outcome == AmountParser.Outcome.Ambiguous ? AmountParser.TryParse(text, Money.DecimalStyle, out value) : outcome;
    }

    public string AmountError(AmountParser.Outcome outcome) => Loc[outcome switch
    {
        AmountParser.Outcome.TooPrecise => "Fin_ErrPrecision",
        AmountParser.Outcome.TooLarge => "Fin_ErrTooLarge",
        AmountParser.Outcome.Empty => "Fin_ErrAmountEmpty",
        _ => "Fin_ErrAmount",
    }];

    public string CategoryLabel(string? name) => string.IsNullOrEmpty(name) ? Loc["Fin_Uncategorized"] : name;

    public string KindLabel(TransactionType type) => Loc["FinType_" + type];

    /// <summary>Announces a change: Finance pages refresh, the calendar re-projects bills.</summary>
    public void Changed() => Messenger.Send(new OrganizerChangedMessage(OrganizerChange.Finance));

    public void SavePrefs() => _ = SaveSafelyAsync();

    private async Task SaveSafelyAsync()
    {
        try { await _settings.SaveAsync(); }
        catch (Exception ex) { Logger.LogWarning(ex, "Saving finance preferences failed"); }
    }

    /// <summary>Runs a store call; finance rules are explained in the user's language; nothing crashes.</summary>
    public async Task<bool> TryAsync(Func<Task> action, string errorKey)
    {
        try
        {
            await action();
            return true;
        }
        catch (FinanceException ex)
        {
            Notifications.Show(Explain(ex), NotificationKind.Warning);
            return false;
        }
        catch (KeyNotFoundException)
        {
            Notifications.Show(Loc["Fin_ErrGone"], NotificationKind.Warning);
            return false;
        }
        catch (OverflowException ex)
        {
            Logger.LogWarning(ex, "Finance calculation overflow ({Key})", errorKey);
            Notifications.Show(Loc["Fin_ErrTooLarge"], NotificationKind.Warning);
            return false;
        }
        catch (Exception ex)
        {
            // never log amounts or descriptions: the exception message may contain them only if thrown by our own code, which it is not
            Logger.LogError(ex, "Finance action failed ({Key})", errorKey);
            Notifications.Show(Loc[errorKey], NotificationKind.Warning);
            return false;
        }
    }

    public string Explain(FinanceException ex) => ex.Args.Count == 0 ? Loc[ex.Key] : Loc.Format(ex.Key, [.. ex.Args]);
}
