using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Markup;
using Arcadia.Core.Localization;

namespace Arcadia.App.Localization;

/// <summary>
/// Single source of localized strings for XAML (via <see cref="LocExtension"/>) and view models (via <see cref="ILocalizer"/>).
/// Changing the culture re-evaluates every bound string and flips FlowDirection for right-to-left languages.
/// </summary>
public sealed class LocalizationManager : ILocalizer, INotifyPropertyChanged
{
    public static LocalizationManager Instance { get; } = new();

    public static readonly IReadOnlyList<LanguageOption> SupportedLanguages =
    [
        new("en", "English"),
        new("ar", "العربية"),
    ];

    private readonly ResourceManager _resources = new("Arcadia.App.Resources.Strings", typeof(LocalizationManager).Assembly);
    private readonly HashSet<string> _reportedMissing = [];

    private LocalizationManager() { }

    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");
    public bool IsRightToLeft => Culture.TextInfo.IsRightToLeft;
    public FlowDirection FlowDirection => IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    public XmlLanguage XmlLanguage => XmlLanguage.GetLanguage(Culture.IetfLanguageTag);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CultureChanged;

    public string this[string key]
    {
        get
        {
            var value = _resources.GetString(key, Culture);
            if (value != null) return value;
            if (_reportedMissing.Add(key)) System.Diagnostics.Trace.TraceWarning($"Arcadia: missing localized string '{key}'");
            return $"[{key}]";
        }
    }

    public string Format(string key, params object[] args) => string.Format(Culture, this[key], args);

    public void SetLanguage(string? languageTag)
    {
        var tag = SupportedLanguages.Any(l => l.Tag == languageTag) ? languageTag! : "en";
        var culture = CultureInfo.GetCultureInfo(tag);
        if (Equals(culture, Culture)) return;

        Culture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(XmlLanguage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRightToLeft)));
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record LanguageOption(string Tag, string DisplayName);
