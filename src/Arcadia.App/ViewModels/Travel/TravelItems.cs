using System.Globalization;
using System.Windows.Media;
using Arcadia.App.Services;
using Arcadia.App.Views.Travel;
using Arcadia.Core.Localization;
using Arcadia.Core.Travel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Arcadia.App.ViewModels.Travel;

/// <summary>Labels, icons and tones for Travel values (one place, so the whole module speaks the same language).</summary>
public static class TravelText
{
    public static string PlaceIcon(PlaceType t) => t switch
    {
        PlaceType.Country => "Flag",
        PlaceType.City or PlaceType.Town => "City",
        PlaceType.Village => "Home",
        PlaceType.Mountain => "Mountain",
        PlaceType.Beach or PlaceType.Lake => "Wave",
        PlaceType.NationalPark => "Tree",
        PlaceType.Viewpoint => "Binoculars",
        PlaceType.Landmark or PlaceType.Museum => "Landmark",
        PlaceType.Restaurant => "Food",
        PlaceType.Hotel => "Bed",
        _ => "MapPin",
    };

    public static string StatusTone(PlaceStatus s) => s switch { PlaceStatus.Visited => "Cyan", PlaceStatus.Planned => "Blue", _ => "Amber" };

    public static string TripTone(TripStatus s) => s switch
    {
        TripStatus.Active => "Green",
        TripStatus.Upcoming => "Cyan",
        TripStatus.Planning => "Blue",
        TripStatus.Completed => "Gold",
        TripStatus.Cancelled => "Red",
        _ => "Steel",
    };

    public static string CategoryIcon(ItineraryCategory c) => c switch
    {
        ItineraryCategory.Transport => "Route",
        ItineraryCategory.Accommodation => "Bed",
        ItineraryCategory.Food => "Food",
        ItineraryCategory.Sight => "Landmark",
        ItineraryCategory.Activity => "Mountain",
        _ => "MapPin",
    };

    public static string MoodIcon(JournalMood m) => m switch
    {
        JournalMood.Joyful => "Sparkle",
        JournalMood.Calm => "Wave",
        JournalMood.Excited => "Star",
        JournalMood.Tired => "Clock",
        JournalMood.Reflective => "Journal",
        JournalMood.Adventurous => "Compass",
        _ => "Journal",
    };

    public static MapMarkerKind MarkerKind(PlaceStatus s) => s switch
    {
        PlaceStatus.Visited => MapMarkerKind.Visited,
        PlaceStatus.Planned => MapMarkerKind.Planned,
        _ => MapMarkerKind.Wanted,
    };

    /// <summary>Distinct, calm colours for itinerary days on the trip map.</summary>
    public static readonly Color[] DayColors =
    [
        Color.FromRgb(0x45, 0xD2, 0xFF), Color.FromRgb(0xF2, 0xB1, 0x3C), Color.FromRgb(0x6D, 0xE0, 0x9B), Color.FromRgb(0xB4, 0x8C, 0xFF),
        Color.FromRgb(0xFF, 0x86, 0x8C), Color.FromRgb(0x4F, 0x8D, 0xFF), Color.FromRgb(0xFF, 0xD2, 0x6A), Color.FromRgb(0x5F, 0xE3, 0xD4),
    ];

    public static string DayTone(int index) => ((index % 8) switch { 0 => "Cyan", 1 => "Amber", 2 => "Green", 3 => "Purple", 4 => "Red", 5 => "Blue", 6 => "Gold", _ => "Cyan" });

    public static string Date(DateOnly d, ILocalizer loc) => d.ToString("d MMM yyyy", loc.Culture);

    /// <summary>"3 – 5 Jul 2026", "30 Dec 2026 – 2 Jan 2027", "from 3 Jul 2026" or "No dates yet".</summary>
    public static string Range(DateOnly? start, DateOnly? end, ILocalizer loc)
    {
        var c = loc.Culture;
        if (start is { } s && end is { } e)
        {
            if (s == e) return s.ToString("d MMM yyyy", c);
            if (s.Year == e.Year && s.Month == e.Month) return $"{s.ToString("%d", c)} – {e.ToString("d MMM yyyy", c)}";
            if (s.Year == e.Year) return $"{s.ToString("d MMM", c)} – {e.ToString("d MMM yyyy", c)}";
            return $"{s.ToString("d MMM yyyy", c)} – {e.ToString("d MMM yyyy", c)}";
        }
        if (start is { } only) return loc.Format("Tr_FromDate", only.ToString("d MMM yyyy", c));
        if (end is { } until) return loc.Format("Tr_UntilDate", until.ToString("d MMM yyyy", c));
        return loc["Tr_NoDates"];
    }

    /// <summary>"in 12 days", "today", "Day 3 of 6", "ended 4 Jun" — the trip's place in time.</summary>
    public static string When(TripSummary t, DateOnly today, ILocalizer loc)
    {
        if (t.Status == TripStatus.Cancelled) return loc["TrStatus_Cancelled"];
        var phase = TravelRules.Phase(t.StartDate, t.EndDate, today);
        // the user closed it early (or logged it ahead of its dates): no countdown for a finished trip
        if (t.Status == TripStatus.Completed && phase is TripPhase.Future or TripPhase.Current) return loc["TrStatus_Completed"];
        switch (phase)
        {
            case TripPhase.Future:
                var days = TravelRules.DaysUntil(t.StartDate, today)!.Value;
                return days == 1 ? loc["Tr_Tomorrow"] : loc.Format("Tr_InDays", days);
            case TripPhase.Current:
                return TravelRules.Progress(t.StartDate, t.EndDate, today) is { } p ? loc.Format("Tr_DayOf", p.Day, p.Of) : loc["Tr_Underway"];
            case TripPhase.Past:
                return (t.EndDate ?? t.StartDate)!.Value.ToString("MMM yyyy", loc.Culture);
            default:
                return loc["Tr_NoDates"];
        }
    }

    public static string Duration(DateOnly? start, DateOnly? end, ILocalizer loc) =>
        TravelRules.DurationDays(start, end) is { } d ? (d == 1 ? loc["Tr_OneDay"] : loc.Format("Tr_Days", d)) : string.Empty;

    public static string Visits(int count, ILocalizer loc) => count switch { 0 => loc["Tr_NotVisited"], 1 => loc["Tr_OneVisit"], _ => loc.Format("Tr_Visits", count) };

    public static string Number(int n, ILocalizer loc) => n.ToString("N0", loc.Culture);

    /// <summary>"1 place" / "3 places" (languages with richer plurals phrase both keys neutrally).</summary>
    public static string Count(int n, string oneKey, string manyKey, ILocalizer loc) => n == 1 ? loc[oneKey] : loc.Format(manyKey, Number(n, loc));
}

/// <summary>A saved place in lists, on the map and in pickers.</summary>
public sealed partial class PlaceItem(PlaceSummary s, ILocalizer loc, ICountryCatalog countries, FlagImages flags) : ObservableObject
{
    public PlaceSummary Summary { get; } = s;
    public int Id => Summary.Id;
    public string Name => Summary.Name;
    public string Icon => TravelText.PlaceIcon(Summary.Type);
    public string TypeText => loc["PlaceType_" + Summary.Type];
    public string StatusText => loc["PlaceStatus_" + Summary.Status];
    public string StatusTone => TravelText.StatusTone(Summary.Status);
    public bool IsFavorite => Summary.IsFavorite;
    public bool IsVisited => Summary.Status == PlaceStatus.Visited;
    public bool HasPosition => Summary.Latitude.HasValue;
    public string CountryName => countries.Find(Summary.CountryCode)?.Name(loc.Culture.TwoLetterISOLanguageName) ?? Summary.CountryCode ?? string.Empty;
    public ImageSource? Flag => flags.Get(Summary.CountryCode);
    /// <summary>"Zermatt · Switzerland" or the region/country alone.</summary>
    public string Where => string.Join(" · ", new[] { Summary.City is { } c && !string.Equals(c, Name, StringComparison.CurrentCultureIgnoreCase) ? c : null, Summary.Region, CountryName }
        .Where(x => !string.IsNullOrWhiteSpace(x)));
    public string VisitsText => Summary.VisitCount == 0 && IsVisited ? loc["Tr_NoVisitDates"] : TravelText.Visits(Summary.VisitCount, loc);
    public string TypeAndWhere => string.Join(" · ", new[] { TypeText, Where }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string LastVisitText => Summary.LastVisit is { } d ? TravelText.Date(d, loc) : string.Empty;
    public string CoordinatesText => Summary.Latitude is { } lat && Summary.Longitude is { } lon ? GeoMath.Format(lat, lon, 4) : loc["Tr_NoPosition"];
    public string? CoverPath => Summary.CoverPath;
    public int? Rating => Summary.Rating;
    public string RatingText => Summary.Rating is { } r ? new string('★', r) + new string('☆', 5 - r) : string.Empty;
    public string Tags => Summary.Tags;

    public MapMarker? ToMarker() => Summary.Latitude is { } lat && Summary.Longitude is { } lon
        ? new MapMarker(("place", Id), lat, lon, TravelText.MarkerKind(Summary.Status), Name, IsFavorite) : null;

    public override string ToString() => Name;
}

/// <summary>A trip card (desk, trips list, timeline, pickers).</summary>
public sealed class TripCard(TripSummary s, ILocalizer loc, DateOnly today, FlagImages flags)
{
    public TripSummary Summary { get; } = s;
    public int Id => Summary.Id;
    public string Name => Summary.Name;
    public string? Description => Summary.Description;
    public string StatusText => loc["TrStatus_" + Summary.Status];
    public string StatusTone => TravelText.TripTone(Summary.Status);
    public string Accent => Summary.Accent;
    public string Theme => Summary.ArtworkTheme;
    public int Seed => Summary.ArtworkSeed;
    public string? CoverPath => Summary.CoverPath;
    public bool HasCover => Summary.CoverPath != null;
    public string DatesText => TravelText.Range(Summary.StartDate, Summary.EndDate, loc);
    public string WhenText => TravelText.When(Summary, today, loc);
    public string DurationText => TravelText.Duration(Summary.StartDate, Summary.EndDate, loc);
    public TripPhase Phase => TravelRules.Phase(Summary.StartDate, Summary.EndDate, today);
    public bool IsFavorite => Summary.IsFavorite;
    public string Destination => Summary.PrimaryPlaceName ?? (Summary.Cities.Count > 0 ? Summary.Cities[0] : string.Empty);
    public IReadOnlyList<ImageSource> Flags => Summary.CountryCodes.Select(flags.Get).Where(f => f != null).Cast<ImageSource>().Take(6).ToList();
    public string CountsText => string.Join(" · ", new[]
    {
        TravelText.Count(Summary.PlaceCount, "Tr_PlacesOne", "Tr_PlacesMany", loc),
        DurationText.Length > 0 ? DurationText : Summary.DayCount > 0 ? TravelText.Count(Summary.DayCount, "Tr_OneDay", "Tr_Days", loc) : null,
        Summary.PhotoCount > 0 ? TravelText.Count(Summary.PhotoCount, "Tr_PhotosOne", "Tr_PhotosMany", loc) : null,
    }.Where(x => !string.IsNullOrEmpty(x)));
    /// <summary>Share of the trip lived so far (active trips) or of itinerary items done.</summary>
    public double Progress => TravelRules.Progress(Summary.StartDate, Summary.EndDate, today) is { } p ? p.Fraction * 100
        : Summary.ItemCount > 0 ? Summary.ItemsDone * 100.0 / Summary.ItemCount : 0;
    public bool ShowProgress => Phase == TripPhase.Current || Summary.Status == TripStatus.Active;
    public int Year => (Summary.StartDate ?? Summary.EndDate)?.Year ?? 0;
    public override string ToString() => Name;
}

/// <summary>A search hit on the map (bundled data or OpenStreetMap).</summary>
public sealed class GeoResultItem(GeoPlace place, ILocalizer loc, FlagImages flags)
{
    public GeoPlace Place { get; } = place;
    public string Name => Place.Name;
    public string Detail => Place.Detail ?? string.Empty;
    public string Icon => TravelText.PlaceIcon(Place.SuggestedType);
    public ImageSource? Flag => flags.Get(Place.CountryCode);
    public string SourceText => Place.Origin == GeoOrigin.Online ? "OpenStreetMap" : loc["Tr_Offline"];
    public string CoordinatesText => GeoMath.Format(Place.Latitude, Place.Longitude, 3);
    public override string ToString() => Name;
}

/// <summary>A label/value line (bindable, unlike tuples).</summary>
public sealed record InfoLine(string Label, string Value);

public sealed record TravelStatTile(string Label, string Value, string Icon, string Tone, string? Hint = null)
{
    public override string ToString() => $"{Label}: {Value}";
}

/// <summary>A navigation entry of the Travel rail.</summary>
public sealed partial class TravelSectionItem(TravelSection section, string icon) : ObservableObject
{
    public TravelSection Section { get; } = section;
    public string Icon { get; } = icon;
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private string? _count;
    [ObservableProperty] private bool _isSelected;
    public override string ToString() => Label;
}

public enum TravelSection { Desk, Map, Trips, Places, Journal, Memories, Bucket, Countries, Timeline }

/// <summary>One row of the Countries view.</summary>
public sealed class CountryRow(CountryInfo c, CountryProgress? p, ILocalizer loc, FlagImages flags)
{
    public CountryInfo Country { get; } = c;
    public CountryProgress? Progress { get; } = p;
    public string Code => Country.Code;
    public string Name => Country.Name(loc.Culture.TwoLetterISOLanguageName);
    public string Continent => loc["Continent_" + Country.Continent.Replace(" ", string.Empty).Replace("(", string.Empty).Replace(")", string.Empty)];
    public ImageSource? Flag => flags.Get(Country.Code);
    public bool Visited => Progress?.Visited ?? false;
    public bool WantToVisit => !Visited && (Progress?.WantToVisit ?? false);
    public bool IsFavorite => Progress?.IsFavorite ?? false;
    public string StateText => Visited ? loc["Tr_CountryVisited"] : WantToVisit ? loc["Tr_CountryWanted"] : loc["Tr_CountryNotYet"];
    public string StateTone => Visited ? "Cyan" : WantToVisit ? "Amber" : "Steel";
    public int PlaceCount => Progress?.PlaceCount ?? 0;
    public int VisitCount => Progress?.VisitCount ?? 0;
    public int TripCount => Progress?.TripCount ?? 0;
    public string LastVisitText => Progress?.LastVisit is { } d ? TravelText.Date(d, loc) : "—";
    public string PlacesText => PlaceCount == 0 ? "—" : TravelText.Number(PlaceCount, loc);
    public string TripsText => TripCount == 0 ? "—" : TravelText.Number(TripCount, loc);
    public string VisitsText => VisitCount == 0 ? "—" : TravelText.Number(VisitCount, loc);
    public override string ToString() => Name;
}

/// <summary>A memory card (photo, journal excerpt or visit) inside a chapter.</summary>
public sealed class MemoryCard(MemoryItem m, ILocalizer loc)
{
    public MemoryItem Item { get; } = m;
    public MemoryKind Kind => Item.Kind;
    public string Title => Item.Title;
    public string? Text => Item.Text;
    public string? PhotoPath => Item.PhotoPath;
    public int Orientation => Item.Orientation;
    public bool IsPhoto => Item.Kind == MemoryKind.Photo;
    public bool IsJournal => Item.Kind == MemoryKind.Journal;
    public bool IsVisit => Item.Kind == MemoryKind.Visit;
    public bool IsHighlight => Item.IsHighlight;
    public string WhenText => Item.When.ToString(Item.Kind == MemoryKind.Photo ? "d MMM yyyy · HH:mm" : "d MMM yyyy", loc.Culture);
    public string? PlaceName => Item.PlaceName;
    public string Icon => Item.Kind switch { MemoryKind.Journal => "Journal", MemoryKind.Visit => "MapPin", _ => "Image" };
}

public sealed class MemoryChapterItem(MemoryChapter c, ILocalizer loc, FlagImages flags)
{
    public MemoryChapter Chapter { get; } = c;
    public int? TripId => Chapter.TripId;
    /// <summary>"Italy 2026" for a trip, or "July 2026" for loose moments.</summary>
    public string Title => Chapter.TripId != null ? Chapter.Title : Chapter.From?.ToString("MMMM yyyy", loc.Culture) ?? string.Empty;
    public string DatesText => Chapter.TripId != null ? TravelText.Range(Chapter.From, Chapter.To, loc) : loc["Tr_Moments"];
    public IReadOnlyList<ImageSource> Flags => Chapter.CountryCodes.Select(flags.Get).Where(f => f != null).Cast<ImageSource>().Take(6).ToList();
    public IReadOnlyList<MemoryCard> Hero => Chapter.Items.Where(i => i.Kind == MemoryKind.Photo).OrderByDescending(i => i.IsHighlight).Take(1).Select(i => new MemoryCard(i, loc)).ToList();
    public IReadOnlyList<MemoryCard> Photos => Chapter.Items.Where(i => i.Kind == MemoryKind.Photo).OrderByDescending(i => i.IsHighlight).Skip(1).Take(8).Select(i => new MemoryCard(i, loc)).ToList();
    public IReadOnlyList<MemoryCard> Words => Chapter.Items.Where(i => i.Kind != MemoryKind.Photo).Take(6).Select(i => new MemoryCard(i, loc)).ToList();
    public bool HasPhotos => Chapter.Items.Any(i => i.Kind == MemoryKind.Photo);
    public string CountsText => string.Join(" · ", new[]
    {
        Chapter.Items.Count(i => i.Kind == MemoryKind.Photo) is var p and > 0 ? TravelText.Count(p, "Tr_PhotosOne", "Tr_PhotosMany", loc) : null,
        Chapter.Items.Count(i => i.Kind == MemoryKind.Journal) is var j and > 0 ? TravelText.Count(j, "Tr_EntriesOne", "Tr_EntriesMany", loc) : null,
        Chapter.Items.Count(i => i.Kind == MemoryKind.Visit) is var v and > 0 ? TravelText.Count(v, "Tr_OneVisit", "Tr_Visits", loc) : null,
    }.Where(x => x != null));
}

/// <summary>A year of the timeline with its trips.</summary>
public sealed partial class TimelineYear(int year, IReadOnlyList<TripCard> trips, ILocalizer loc) : ObservableObject
{
    public int Year { get; } = year;
    public string YearText => Year == 0 ? loc["Tr_Undated"] : Year.ToString(CultureInfo.InvariantCulture);
    public IReadOnlyList<TripCard> Trips { get; } = trips;
    public string Summary => TravelText.Count(Trips.Count, "Tr_TripsOne", "Tr_TripsMany", loc) + " · " +
        TravelText.Count(Trips.SelectMany(t => t.Summary.CountryCodes).Distinct().Count(), "Tr_CountriesOne", "Tr_CountriesMany", loc);
    [ObservableProperty] private bool _isExpanded = true;
}

public sealed class BucketItem(BucketRow r, ILocalizer loc, FlagImages flags)
{
    public BucketRow Row { get; } = r;
    public int Id => Row.Id;
    public string Title => Row.Title;
    public bool IsCompleted => Row.IsCompleted;
    public string Icon => Row.Kind switch { BucketKind.Country => "Flag", BucketKind.Place => "MapPin", _ => "Sparkle" };
    public string KindText => loc["BucketKind_" + Row.Kind];
    public string PriorityText => loc["BucketPriority_" + Row.Priority];
    public string PriorityTone => Row.Priority switch { BucketPriority.Dream => "Gold", BucketPriority.High => "Amber", BucketPriority.Normal => "Blue", _ => "Steel" };
    public ImageSource? Flag => flags.Get(Row.CountryCode);
    public string TargetText => Row.IsCompleted && Row.CompletedOn is { } d ? loc.Format("Tr_DoneOn", TravelText.Date(d, loc))
        : Row.TargetYear is { } y ? loc.Format("Tr_TargetYearValue", y.ToString(CultureInfo.InvariantCulture)) : string.Empty;
    public string LinksText => string.Join(" · ", new[] { Row.PlaceName, Row.TripName }.Where(x => !string.IsNullOrEmpty(x)));
    public string? Notes => Row.Notes;
    public override string ToString() => Title;
}
