using System.Globalization;
using Arcadia.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.Shell;

/// <summary>
/// First run (Phase 10): welcome → language → data location → performance → start with Windows → enter.
/// Shown once, before anything is created in the data folder, so the chosen folder is used from the first byte.
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    public const int LastStep = 4;
    private readonly string _defaultFolder;

    public WelcomeViewModel(string defaultFolder)
    {
        _defaultFolder = defaultFolder;
        _dataFolder = defaultFolder;
        var osArabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
        _language = osArabic ? "ar" : "en";
        LocalizationManager.Instance.SetLanguage(_language);
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsFirst), nameof(IsLast), nameof(StepNumber), nameof(IsStep0), nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsStep4))]
    private int _step;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEnglish), nameof(IsArabic))] private string _language;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDefaultFolder))] private string _dataFolder;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPerfAuto), nameof(IsPerfNormal), nameof(IsPerfSaver))] private string _performanceMode = "auto";

    public bool IsStep0 => Step == 0;
    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool IsStep4 => Step == 4;
    public bool IsEnglish { get => Language == "en"; set { if (value) Language = "en"; } }
    public bool IsArabic { get => Language == "ar"; set { if (value) Language = "ar"; } }
    public bool IsPerfAuto { get => PerformanceMode == "auto"; set { if (value) PerformanceMode = "auto"; } }
    public bool IsPerfNormal { get => PerformanceMode == "normal"; set { if (value) PerformanceMode = "normal"; } }
    public bool IsPerfSaver { get => PerformanceMode == "powersaver"; set { if (value) PerformanceMode = "powersaver"; } }
    [ObservableProperty] private bool _startWithWindows;

    public bool IsFirst => Step == 0;
    public bool IsLast => Step == LastStep;
    public string StepNumber => (Step + 1).ToString(LocalizationManager.Instance.Culture);
    public bool IsDefaultFolder => string.Equals(DataFolder, _defaultFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>False when a --data-dir override decides the folder (development and QA runs).</summary>
    public bool CanChangeFolder { get; init; } = true;

    /// <summary>Set by the window's folder picker.</summary>
    public Func<string, string?>? PickFolder { get; set; }
    public event EventHandler? Finished;

    partial void OnLanguageChanged(string value) => LocalizationManager.Instance.SetLanguage(value);


    [RelayCommand]
    private void ChangeFolder()
    {
        if (PickFolder?.Invoke(DataFolder) is { } picked) DataFolder = Path.Combine(picked, "Arcadia");
    }

    [RelayCommand] private void UseDefaultFolder() => DataFolder = _defaultFolder;

    [RelayCommand]
    private void Next()
    {
        if (Step < LastStep) Step++;
        else Finished?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 0) Step--;
    }

    [RelayCommand]
    private void GoTo(string step)
    {
        if (int.TryParse(step, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s >= 0 && s <= LastStep) Step = s;
    }
}
