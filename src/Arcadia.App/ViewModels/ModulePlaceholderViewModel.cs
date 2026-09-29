using Arcadia.App.Services;
using Arcadia.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arcadia.App.ViewModels;

/// <summary>Elegant "coming later" screen for modules that are not part of Phase 1.</summary>
public sealed partial class ModulePlaceholderViewModel : ObservableObject
{
    private readonly ILocalizer _loc;
    private readonly INavigationService _navigation;

    public ModulePlaceholderViewModel(SectionDescriptor section, ILocalizer loc, INavigationService navigation)
    {
        Section = section;
        _loc = loc;
        _navigation = navigation;
        Refresh();
        loc.CultureChanged += (_, _) => Refresh();
    }

    public SectionDescriptor Section { get; }
    public string Icon => Section.Icon;
    public string? SecondaryIcon => Section.SecondaryIcon;
    public string Tone => Section.Tone;
    public string ArtTheme => Section.ArtTheme;
    public int Seed => Math.Abs(Section.Key.GetHashCode(StringComparison.Ordinal) % 1000);

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private IReadOnlyList<RoadmapItem> _features = [];

    [RelayCommand]
    private void Back() => _navigation.Navigate(Sections.Home);

    private void Refresh()
    {
        Title = _loc[Section.LabelKey];
        var key = Section.ModuleKey ?? Section.Key;
        Summary = _loc[$"Module_{key}_Summary"];
        Features = Enumerable.Range(1, 3).Select(i => new RoadmapItem(i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), _loc[$"Module_{key}_F{i}"])).ToList();
    }
}

public sealed record RoadmapItem(string Number, string Text);
