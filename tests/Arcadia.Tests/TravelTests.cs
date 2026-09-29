using System.Net;
using System.Net.Http;
using Arcadia.Core.Documents;
using Arcadia.Core.Projects;
using Arcadia.Core.Search;
using Arcadia.Core.Security;
using Arcadia.Core.Settings;
using Arcadia.Core.Text;
using Arcadia.Core.Travel;
using Arcadia.Data;
using Arcadia.Infrastructure.Geo;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arcadia.Tests;

public sealed class GeoMathTests
{
    [Theory]
    [InlineData("46.0212, 7.7493", 46.0212, 7.7493)]
    [InlineData("46.0212 7.7493", 46.0212, 7.7493)]
    [InlineData("46,0212; 7,7493", 46.0212, 7.7493)]
    [InlineData("-33.8568, 151.2153", -33.8568, 151.2153)]
    [InlineData("46.02°, 7.75°", 46.02, 7.75)]
    [InlineData("46°01'16\"N 7°44'57\"E", 46.021111, 7.749167)]
    [InlineData("N 46.02 E 7.74", 46.02, 7.74)]
    [InlineData("7°44'57\"E 46°01'16\"N", 46.021111, 7.749167)] // longitude first, hemispheres decide
    [InlineData("34.05N, 118.25W", 34.05, -118.25)]
    public void Coordinates_are_read_in_common_formats(string text, double lat, double lon)
    {
        Assert.True(GeoMath.TryParse(text, out var a, out var o));
        Assert.Equal(lat, a, 4);
        Assert.Equal(lon, o, 4);
    }

    [Theory]
    [InlineData("91, 10")]
    [InlineData("45, 181")]
    [InlineData("Zermatt")]
    [InlineData("1, 2, 3")]
    [InlineData("")]
    [InlineData("NaN, 3")]
    public void Impossible_or_unreadable_coordinates_are_rejected(string text) => Assert.False(GeoMath.TryParse(text, out _, out _));

    [Fact]
    public void Validation_projection_and_distance()
    {
        Assert.True(GeoMath.IsValid(-90, 180));
        Assert.False(GeoMath.IsValid(double.NaN, 0));
        Assert.False(GeoMath.IsValid(0, double.PositiveInfinity));
        Assert.Equal(-170, GeoMath.NormalizeLongitude(190), 6);
        Assert.Equal(-180, GeoMath.NormalizeLongitude(180), 6);

        var (x, y) = GeoMath.ToWorld(46.0212, 7.7493);
        var (lat, lon) = GeoMath.FromWorld(x, y);
        Assert.Equal(46.0212, lat, 6);
        Assert.Equal(7.7493, lon, 6);
        Assert.Equal((0.5, 0.5), GeoMath.ToWorld(0, 0));
        Assert.InRange(GeoMath.ToWorld(89.9, 0).Y, 0, 0.0001); // clamped to the Mercator limit

        Assert.InRange(GeoMath.DistanceKm(47.3769, 8.5417, 46.2044, 6.1432), 220, 228); // Zürich – Geneva
        Assert.Equal("46.0212° N, 7.7493° E", GeoMath.Format(46.0212, 7.7493));
        Assert.Equal("33.8568° S, 151.2153° E", GeoMath.Format(-33.8568, 151.2153));
    }

    [Fact]
    public void Text_folding_ignores_accents_and_arabic_spelling_variants()
    {
        Assert.Equal("zurich", TextFold.Fold("Zürich"));
        Assert.Equal("strasse", TextFold.Fold("Straße"));
        Assert.Equal(TextFold.Fold("اسطنبول"), TextFold.Fold("إسطنبول"));
        Assert.Equal(TextFold.Fold("مكه"), TextFold.Fold("مكة"));
        Assert.Equal("sao paulo", TextFold.Fold("São-Paulo"));
    }
}

public sealed class TravelRulesTests
{
    private static readonly DateOnly Today = new(2026, 9, 27);

    [Fact]
    public void Trip_duration_counts_both_ends_and_crosses_months_and_years()
    {
        Assert.Equal(3, TravelRules.DurationDays(new DateOnly(2026, 7, 3), new DateOnly(2026, 7, 5)));
        Assert.Equal(1, TravelRules.DurationDays(new DateOnly(2026, 7, 3), new DateOnly(2026, 7, 3)));
        Assert.Equal(4, TravelRules.DurationDays(new DateOnly(2026, 12, 30), new DateOnly(2027, 1, 2)));
        Assert.Equal(3, TravelRules.DurationDays(new DateOnly(2028, 2, 28), new DateOnly(2028, 3, 1))); // leap year
        Assert.Null(TravelRules.DurationDays(new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 3)));
        Assert.Null(TravelRules.DurationDays(null, new DateOnly(2026, 7, 3)));
        Assert.False(TravelRules.DatesAreValid(new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 3)));
        Assert.True(TravelRules.DatesAreValid(null, new DateOnly(2026, 7, 3)));
    }

    [Fact]
    public void Phases_countdown_and_progress()
    {
        Assert.Equal(TripPhase.Undated, TravelRules.Phase(null, null, Today));
        Assert.Equal(TripPhase.Future, TravelRules.Phase(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5), Today));
        Assert.Equal(TripPhase.Current, TravelRules.Phase(new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 30), Today));
        Assert.Equal(TripPhase.Past, TravelRules.Phase(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 3), Today));
        Assert.Equal(TripPhase.Current, TravelRules.Phase(Today, null, Today)); // open-ended trip starting today
        Assert.Equal(4, TravelRules.DaysUntil(new DateOnly(2026, 10, 1), Today));
        Assert.Equal(0, TravelRules.DaysUntil(Today, Today));
        Assert.Null(TravelRules.DaysUntil(new DateOnly(2026, 9, 1), Today));
        var p = TravelRules.Progress(new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 30), Today)!.Value;
        Assert.Equal((3, 6), (p.Day, p.Of));
        Assert.Null(TravelRules.Progress(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5), Today));
        Assert.Equal(new DateOnly(2026, 7, 5), TravelRules.DayDate(new DateOnly(2026, 7, 3), 2));
        Assert.Null(TravelRules.DayDate(null, 2));
    }

    [Fact]
    public void Status_follows_the_calendar_only_for_scheduled_trips()
    {
        Assert.Equal(TripStatus.Active, TravelRules.SuggestStatus(TripStatus.Upcoming, new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 30), Today));
        Assert.Equal(TripStatus.Idea, TravelRules.SuggestStatus(TripStatus.Idea, new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 30), Today));
        Assert.Equal(TripStatus.Completed, TravelRules.SuggestStatus(TripStatus.Completed, new DateOnly(2026, 10, 25), null, Today));
        Assert.Equal(TripStatus.Planning, TravelRules.SuggestStatus(TripStatus.Planning, new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 30), Today));
    }

    [Fact]
    public void Visits_ratings_and_itinerary_ordering()
    {
        var (count, first, last) = TravelRules.Summarize([
            new PlaceVisit { VisitedOn = new DateOnly(2024, 5, 1) }, new PlaceVisit { VisitedOn = null }, new PlaceVisit { VisitedOn = new DateOnly(2026, 7, 14) }]);
        Assert.Equal((3, new DateOnly(2024, 5, 1), new DateOnly(2026, 7, 14)), (count, first, last));
        Assert.True(TravelRules.IsValidRating(null));
        Assert.False(TravelRules.IsValidRating(6));
        Assert.True(TravelRules.SortMinutes(TimeSlot.Exact, new TimeOnly(7, 30)) < TravelRules.SortMinutes(TimeSlot.Morning, null));
        Assert.True(TravelRules.SortMinutes(TimeSlot.Evening, null) < TravelRules.SortMinutes(TimeSlot.AnyTime, null));
        Assert.Equal(TimeSlot.Afternoon, TravelRules.SlotOf(new TimeOnly(14, 0)));
        Assert.Equal(ItineraryCategory.Food, TravelRules.CategoryFor(PlaceType.Restaurant));
    }

    [Fact]
    public void Photo_matches_the_city_it_was_taken_in_but_prefers_a_close_landmark()
    {
        var kyoto = ("Kyoto", 35.0116, 135.7681, PlaceType.City);
        var shrine = ("Fushimi Inari", 34.9671, 135.7727, PlaceType.Landmark);
        var japan = ("Japan", 35.0, 135.8, PlaceType.Country);
        (double, double, PlaceType)? Pos((string, double, double, PlaceType) p) => (p.Item2, p.Item3, p.Item4);
        // ~5 km from the city centre: the city covers it
        Assert.Equal("Kyoto", TravelRules.NearestForPhoto(34.9680, 135.7700, new[] { kyoto, japan }.Select(Box).ToList(), b => Pos(b.V))?.V.Item1);
        // a few hundred metres from the shrine: the landmark wins over the city
        Assert.Equal("Fushimi Inari", TravelRules.NearestForPhoto(34.9680, 135.7700, new[] { kyoto, shrine, japan }.Select(Box).ToList(), b => Pos(b.V))?.V.Item1);
        // Osaka is 40 km away: nothing, and a country is never matched
        Assert.Null(TravelRules.NearestForPhoto(34.6937, 135.5023, new[] { kyoto, shrine, japan }.Select(Box).ToList(), b => Pos(b.V)));
    }

    private sealed record Boxed((string, double, double, PlaceType) V);
    private static Boxed Box((string, double, double, PlaceType) v) => new(v);
}

public sealed class GeoDataTests
{
    private static readonly CountryCatalog Countries = new();
    private static readonly CityIndex Cities = new();

    [Fact]
    public void Bundled_countries_have_names_outlines_flags_and_the_un_count()
    {
        Assert.Equal(195, Countries.UnStateCount);
        Assert.True(Countries.All.Count >= 240);
        var ch = Countries.Find("ch")!;
        Assert.Equal("Switzerland", ch.NameEn);
        Assert.Equal("Schweiz", ch.NameDe);
        Assert.Equal("سويسرا", ch.NameAr);
        Assert.Equal("Europe", ch.Continent);
        Assert.NotEmpty(ch.Detail);
        Assert.NotEmpty(ch.Coarse);
        var flag = Countries.Flag("CH")!;
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, flag[..4]); // PNG
        Assert.Equal("AU", Countries.Find("AU")!.Code);
        Assert.True(Countries.Find("AU")!.IsUnState);
    }

    [Theory]
    [InlineData(46.95, 7.45, "CH")]   // Bern
    [InlineData(35.68, 139.69, "JP")] // Tokyo
    [InlineData(-33.87, 151.21, "AU")] // Sydney
    [InlineData(30.04, 31.24, "EG")]  // Cairo
    [InlineData(-15.79, -47.88, "BR")] // Brasília
    public void Points_are_placed_in_the_right_country(double lat, double lon, string code) => Assert.Equal(code, Countries.At(lat, lon)?.Code);

    [Fact]
    public void Open_sea_and_bad_coordinates_have_no_country()
    {
        Assert.Null(Countries.At(0, -30));
        Assert.Null(Countries.At(95, 0));
    }

    [Theory]
    [InlineData("Schweiz", "CH")]
    [InlineData("سويسرا", "CH")]
    [InlineData("japan", "JP")]
    [InlineData("Österreich", "AT")]
    [InlineData("osterreich", "AT")]
    public void Countries_are_found_in_three_languages(string text, string code) => Assert.Equal(code, Countries.Search(text).First().Code);

    [Fact]
    public void Cities_are_searchable_offline_and_nearest_lookup_works()
    {
        Assert.Equal("CH", Cities.Search("Zürich", 5).First().CountryCode);
        Assert.Equal("CH", Cities.Search("zurich", 5).First().CountryCode);
        Assert.Equal("EG", Cities.Search("القاهرة", 5).First().CountryCode);
        Assert.Equal("Geneva", Cities.Nearest(46.2, 6.15)!.Name);
        Assert.Null(Cities.Nearest(0, -30)); // Atlantic
        Assert.Empty(Cities.Search("z", 5)); // too short
    }
}

public sealed class GeocodingTests
{
    private const string ZermattJson = """
        [{"place_id":88983879,"licence":"Data © OpenStreetMap contributors","osm_type":"relation","osm_id":1685406,"lat":"46.0212076","lon":"7.7492540",
          "category":"boundary","type":"administrative","addresstype":"town","name":"Zermatt","display_name":"Zermatt, Visp, Valais/Wallis, 3920, Switzerland",
          "address":{"town":"Zermatt","state":"Valais/Wallis","country":"Switzerland","country_code":"ch"}}]
        """;

    internal sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public event EventHandler? SettingsChanged;
        public void NotifyChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal sealed class FakeSecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Get(string name) => _values.GetValueOrDefault(name);
        public void Set(string name, string? value) { if (value == null) _values.Remove(name); else _values[name] = value; Changed?.Invoke(this, EventArgs.Empty); }
        public event EventHandler? Changed;
    }

    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    internal sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "arcadia-geo-" + Guid.NewGuid().ToString("N"));

    private static GeocodingService Service(Handler handler, FakeSettings settings, string cache) =>
        new(new CountryCatalog(), new CityIndex(), new Factory(handler), settings, cache, TimeProvider.System, NullLogger<GeocodingService>.Instance);

    [Fact]
    public void Nominatim_answers_are_mapped_to_places()
    {
        var place = Assert.Single(GeocodingService.Parse(ZermattJson, isReverse: false));
        Assert.Equal("Zermatt", place.Name);
        Assert.Equal("CH", place.CountryCode);
        Assert.Equal("Valais/Wallis", place.Region);
        Assert.Equal(PlaceType.Town, place.SuggestedType);
        Assert.Equal("osm:relation/1685406", place.SourceRef);
        Assert.Equal(46.0212076, place.Latitude, 6);
        Assert.Empty(GeocodingService.Parse("{\"error\":\"Unable to geocode\"}", isReverse: true));
        Assert.Equal(PlaceType.Mountain, GeocodingService.TypeOf("natural", "peak", "peak"));
        Assert.Equal(PlaceType.Restaurant, GeocodingService.TypeOf("amenity", "cafe", "amenity"));
        Assert.Equal(PlaceType.Country, GeocodingService.TypeOf("boundary", "administrative", "country"));
    }

    [Fact]
    public async Task Online_search_is_cached_identifies_itself_and_respects_the_switch()
    {
        var cache = TempDir();
        try
        {
            var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ZermattJson) });
            var settings = new FakeSettings();
            var geo = Service(handler, settings, cache);

            var first = await geo.SearchOnlineAsync("Zermatt", "de");
            Assert.Equal(GeoSearchError.None, first.Error);
            Assert.Single(first.Places);
            var url = handler.Requests.Single().RequestUri!.ToString();
            Assert.StartsWith("https://nominatim.openstreetmap.org/search?q=Zermatt", url);
            Assert.Contains("accept-language=de,en", url);

            var second = await geo.SearchOnlineAsync("Zermatt", "de");
            Assert.True(second.FromCache);
            Assert.Single(handler.Requests); // no second request

            settings.Current.Travel.OnlineSearch = false;
            Assert.Equal(GeoSearchError.Disabled, (await geo.SearchOnlineAsync("Paris", "en")).Error);
            Assert.Single(handler.Requests);
        }
        finally { try { Directory.Delete(cache, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Online_errors_are_reported_not_thrown()
    {
        var cache = TempDir();
        try
        {
            var limited = Service(new Handler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)), new FakeSettings(), cache);
            Assert.Equal(GeoSearchError.RateLimited, (await limited.SearchOnlineAsync("Rome", "en")).Error);
            var down = Service(new Handler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), new FakeSettings(), cache);
            Assert.Equal(GeoSearchError.ServiceUnavailable, (await down.SearchOnlineAsync("Oslo", "en")).Error);
            var offline = Service(new Handler(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.")), new FakeSettings(), cache);
            Assert.Equal(GeoSearchError.Offline, (await offline.SearchOnlineAsync("Lima", "en")).Error);
        }
        finally { try { Directory.Delete(cache, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Requests_are_spaced_at_least_a_second_apart()
    {
        var cache = TempDir();
        try
        {
            var times = new List<DateTime>();
            var handler = new Handler(_ => { times.Add(DateTime.UtcNow); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }; });
            var geo = Service(handler, new FakeSettings(), cache);
            await Task.WhenAll(geo.SearchOnlineAsync("aaa", "en"), geo.SearchOnlineAsync("bbb", "en"));
            Assert.Equal(2, times.Count);
            Assert.True((times[1] - times[0]).TotalMilliseconds >= 1000, $"only {(times[1] - times[0]).TotalMilliseconds} ms apart");
        }
        finally { try { Directory.Delete(cache, true); } catch (IOException) { } }
    }

    [Fact]
    public void Offline_search_and_description_never_touch_the_network()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("no network expected"));
        var geo = Service(handler, new FakeSettings(), TempDir());
        var results = geo.SearchOffline("Zürich", "en");
        Assert.Contains(results, r => r.CountryCode == "CH" && r.SuggestedType == PlaceType.City);
        Assert.Equal(PlaceType.Country, geo.SearchOffline("Japan", "en").First().SuggestedType);
        var here = geo.DescribeOffline(46.205, 6.145, "en")!;
        Assert.Equal("Geneva", here.Name);
        Assert.Equal("CH", here.CountryCode);
        Assert.Equal("46.0212, 7.7493".Length > 0, geo.SearchOffline("46.0212, 7.7493", "en").Count == 1);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Tiles_are_cached_and_reused_offline()
    {
        var cache = TempDir();
        try
        {
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
            var online = true;
            var handler = new Handler(_ => online
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png), Headers = { CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.Zero } } }
                : throw new HttpRequestException(HttpRequestError.ConnectionError, "offline"));
            var settings = new FakeSettings();
            var tiles = new MapTileService(new Factory(handler), settings, new FakeSecrets(), cache, TimeProvider.System, NullLogger<MapTileService>.Instance);
            Assert.Equal("osm", tiles.Source!.Id);
            Assert.True(tiles.Source.Darken);

            var first = await tiles.GetTileAsync(3, 4, 2);
            Assert.Equal(TileStatus.Ok, first.Status);
            Assert.Equal("https://tile.openstreetmap.org/3/4/2.png", handler.Requests.Single().RequestUri!.ToString());
            Assert.Equal(png, tiles.GetCachedTile(3, 4, 2));

            online = false; // expired (max-age 0) and now offline: the cached copy is used
            var stale = await tiles.GetTileAsync(3, 4, 2);
            Assert.Equal(TileStatus.Ok, stale.Status);
            Assert.True(stale.FromCache);
            Assert.Equal(TileStatus.Offline, (await tiles.GetTileAsync(3, 5, 2)).Status);
            Assert.Equal(TileStatus.NotFound, (await tiles.GetTileAsync(3, 1, 9)).Status); // y outside the world

            settings.Current.Travel.OnlineMapTiles = false;
            settings.NotifyChanged();
            Assert.Null(tiles.Source);
            Assert.Equal(TileStatus.Disabled, (await tiles.GetTileAsync(3, 4, 2)).Status);
        }
        finally { try { Directory.Delete(cache, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Custom_tile_servers_use_the_secret_key()
    {
        var cache = TempDir();
        try
        {
            var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
            var settings = new FakeSettings();
            settings.Current.Travel.TileStyle = "custom";
            settings.Current.Travel.CustomTileUrl = "https://tiles.example.com/{z}/{x}/{y}.png?key={key}";
            var secrets = new FakeSecrets();
            secrets.Set(SecretNames.MapTilesApiKey, "s3cr3t");
            var tiles = new MapTileService(new Factory(handler), settings, secrets, cache, TimeProvider.System, NullLogger<MapTileService>.Instance);
            await tiles.GetTileAsync(1, 0, 0);
            Assert.Equal("https://tiles.example.com/1/0/0.png?key=s3cr3t", handler.Requests.Single().RequestUri!.ToString());
            Assert.False(MapTileService.IsValidTemplate("ftp://x/{z}/{x}/{y}"));
            Assert.False(MapTileService.IsValidTemplate("https://x/{z}/{x}.png"));
        }
        finally { try { Directory.Delete(cache, true); } catch (IOException) { } }
    }
}

public sealed class PhotoMetadataTests
{
    [Fact]
    public void Exif_date_camera_orientation_and_gps_are_read_locally()
    {
        var file = Path.Combine(Path.GetTempPath(), $"arcadia-exif-{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(file, TestImages.Jpeg(lat: 46.0212, lon: 7.7493, orientation: 6, taken: new DateTime(2026, 7, 14, 9, 30, 0), make: "Fujifilm", model: "X-T5"));
            var before = File.ReadAllBytes(file);
            var meta = new PhotoMetadataReader(NullLogger<PhotoMetadataReader>.Instance).Read(file);
            Assert.Equal(new DateTime(2026, 7, 14, 9, 30, 0), meta.TakenAt);
            Assert.Equal("Fujifilm X-T5", meta.Camera);
            Assert.Equal(6, meta.Orientation);
            Assert.Equal(46.0212, meta.Latitude!.Value, 3);
            Assert.Equal(7.7493, meta.Longitude!.Value, 3);
            Assert.Equal(before, File.ReadAllBytes(file)); // never modified
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Missing_or_corrupt_files_give_empty_metadata()
    {
        var reader = new PhotoMetadataReader(NullLogger<PhotoMetadataReader>.Instance);
        Assert.Equal(PhotoMetadata.Empty, reader.Read(@"Z:\nowhere\photo.jpg"));
        var file = Path.Combine(Path.GetTempPath(), $"arcadia-bad-{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllText(file, "not an image");
            Assert.Equal(PhotoMetadata.Empty, reader.Read(file));
        }
        finally { File.Delete(file); }
    }
}

public sealed class TravelStoreTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private IDbContextFactory<ArcadiaDbContext> _factory = null!;
    private TravelStore _travel = null!;
    private DocumentStore _documents = null!;
    private ProjectStore _projects = null!;
    private GlobalSearchService _search = null!;
    private static readonly CountryCatalog Countries = new();
    private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 9, 27);

    private sealed class Factory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new Factory(new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite(_connection).Options);
        await new DatabaseInitializer(_factory, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: false);
        _travel = new TravelStore(_factory, new Clock(), Countries);
        _documents = new DocumentStore(_factory, new Clock());
        _projects = new ProjectStore(_factory, new Clock());
        _search = new GlobalSearchService([new Arcadia.Data.Search.IndexSearchProvider(_factory), new Arcadia.Data.Search.CountrySearchProvider(Countries)]);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private Task<TravelPlace> Place(string name, double lat, double lon, PlaceType type = PlaceType.City, PlaceStatus status = PlaceStatus.WantToVisit, string? city = null) =>
        _travel.CreatePlaceAsync(new PlaceDraft(name, type, status, lat, lon, City: city));

    private static FileFacts File(string path, bool exists = true) => new(path, exists, false, 2048, Now.AddDays(-3), Now.AddDays(-1));

    [Fact]
    public async Task Places_are_validated_saved_and_placed_in_their_country()
    {
        var zermatt = await Place("Zermatt", 46.0212, 7.7493, PlaceType.Town);
        Assert.Equal("CH", zermatt.CountryCode); // from the coordinates
        Assert.Equal(PlaceStatus.WantToVisit, zermatt.Status);

        await Assert.ThrowsAsync<ArgumentException>(() => _travel.CreatePlaceAsync(new PlaceDraft(" ", PlaceType.City, PlaceStatus.Visited, 1, 1)));
        await Assert.ThrowsAsync<ArgumentException>(() => _travel.CreatePlaceAsync(new PlaceDraft("Nowhere", PlaceType.City, PlaceStatus.Visited, 95, 10)));
        await Assert.ThrowsAsync<ArgumentException>(() => _travel.CreatePlaceAsync(new PlaceDraft("Half", PlaceType.City, PlaceStatus.Visited, 45, null)));
        await Assert.ThrowsAsync<ArgumentException>(() => _travel.CreatePlaceAsync(new PlaceDraft("Stars", PlaceType.City, PlaceStatus.Visited, null, null, Rating: 7)));

        var noPosition = await _travel.CreatePlaceAsync(new PlaceDraft("Grandma's village", PlaceType.Village, PlaceStatus.Visited, null, null, CountryCode: "de"));
        Assert.Equal("DE", noPosition.CountryCode);
        Assert.False(noPosition.HasPosition);

        await _travel.UpdatePlaceAsync(zermatt.Id, new PlaceDraft("Zermatt", PlaceType.Town, PlaceStatus.WantToVisit, 46.0212, 7.7493, Region: "Valais", Notes: "Matterhorn at sunrise", Rating: 5, IsFavorite: true, Tags: "Alps, hiking"));
        var loaded = (await _travel.GetPlaceAsync(zermatt.Id))!;
        Assert.Equal("Valais", loaded.Region);
        Assert.Equal(5, loaded.Rating);
        Assert.Equal("Alps, hiking", loaded.Tags);
        Assert.Single(await _travel.ListPlacesAsync(new PlaceQuery(FavoritesOnly: true)));
        Assert.Single(await _travel.ListPlacesAsync(new PlaceQuery(Text: "schweiz"))); // country name in another language
        Assert.Single(await _travel.ListPlacesAsync(new PlaceQuery(CountryCode: "ch")));
    }

    [Fact]
    public async Task Visits_mark_places_visited_and_are_summarised()
    {
        var rome = await Place("Rome", 41.9, 12.5);
        await _travel.AddVisitAsync(rome.Id, new DateOnly(2019, 4, 2), null, "Easter");
        await _travel.AddVisitAsync(rome.Id, new DateOnly(2025, 6, 10), null, null);
        var summary = (await _travel.ListPlacesAsync(new PlaceQuery())).Single();
        Assert.Equal(PlaceStatus.Visited, summary.Status);
        Assert.Equal(2, summary.VisitCount);
        Assert.Equal(new DateOnly(2019, 4, 2), summary.FirstVisit);
        Assert.Equal(new DateOnly(2025, 6, 10), summary.LastVisit);
        var visit = (await _travel.GetPlaceAsync(rome.Id))!.Visits.First();
        Assert.Equal(new DateOnly(2025, 6, 10), visit.VisitedOn); // newest first
        await _travel.DeleteVisitAsync(visit.Id);
        Assert.Equal(1, (await _travel.ListPlacesAsync(new PlaceQuery())).Single().VisitCount);
    }

    [Fact]
    public async Task Trips_places_days_and_itinerary()
    {
        var zermatt = await Place("Zermatt", 46.0212, 7.7493, PlaceType.Town);
        var gornergrat = await Place("Gornergrat", 45.983, 7.785, PlaceType.Viewpoint);
        var hotel = await Place("Hotel Bellevue", 46.02, 7.75, PlaceType.Hotel, city: "Zermatt");
        await Assert.ThrowsAsync<ArgumentException>(() => _travel.CreateTripAsync(new TripDraft("Backwards", StartDate: new DateOnly(2026, 7, 5), EndDate: new DateOnly(2026, 7, 1))));

        var trip = await _travel.CreateTripAsync(new TripDraft("Swiss Alps", TripStatus.Upcoming, new DateOnly(2026, 12, 30), new DateOnly(2027, 1, 2), PrimaryPlaceId: zermatt.Id),
            [gornergrat.Id]);
        Assert.Equal(4, await _travel.EnsureDaysForDatesAsync(trip.Id));
        Assert.Equal(0, await _travel.EnsureDaysForDatesAsync(trip.Id)); // idempotent

        var loaded = (await _travel.GetTripAsync(trip.Id))!;
        Assert.Equal(new[] { gornergrat.Id, zermatt.Id }, loaded.Places.Select(p => p.PlaceId));
        Assert.Equal(PlaceStatus.WantToVisit, (await _travel.GetPlaceAsync(zermatt.Id))!.Status);
        Assert.Equal(4, loaded.Days.Count);
        var day1 = loaded.Days[0];
        var day2 = loaded.Days[1];
        await _travel.UpdateDayAsync(day1.Id, "Arrival", null);

        var dinner = await _travel.AddItemAsync(day1.Id, new ItineraryItemDraft("Dinner", TimeSlot.Exact, new TimeOnly(18, 30), ItineraryCategory.Food));
        var checkIn = await _travel.AddItemAsync(day1.Id, new ItineraryItemDraft("Check in", TimeSlot.Afternoon, null, ItineraryCategory.Accommodation, hotel.Id));
        var train = await _travel.AddItemAsync(day1.Id, new ItineraryItemDraft("Train from Zürich", TimeSlot.Exact, new TimeOnly(9, 0), ItineraryCategory.Transport, Url: "sbb.ch"));
        await Assert.ThrowsAsync<ArgumentException>(() => _travel.AddItemAsync(day1.Id, new ItineraryItemDraft("No time", TimeSlot.Exact)));
        await Assert.ThrowsAsync<ArgumentException>(() => _travel.AddItemAsync(day1.Id, new ItineraryItemDraft("Bad link", Url: "javascript:alert(1)")));

        // an itinerary place joins the trip and a wished-for place becomes planned
        loaded = (await _travel.GetTripAsync(trip.Id))!;
        Assert.Contains(loaded.Places, p => p.PlaceId == hotel.Id);
        Assert.Equal(PlaceStatus.Planned, (await _travel.GetPlaceAsync(hotel.Id))!.Status);
        Assert.Equal("https://sbb.ch/", loaded.Days[0].Items.Single(i => i.Id == train.Id).Url);
        Assert.Equal("Arrival", loaded.Days[0].Title);

        await _travel.SortDayByTimeAsync(day1.Id);
        loaded = (await _travel.GetTripAsync(trip.Id))!;
        Assert.Equal(new[] { "Train from Zürich", "Check in", "Dinner" }, loaded.Days[0].Items.Select(i => i.Title));

        await _travel.MoveItemAsync(dinner.Id, day2.Id, 0);
        await _travel.MoveItemAsync(checkIn.Id, day1.Id, 0);
        loaded = (await _travel.GetTripAsync(trip.Id))!;
        Assert.Equal(new[] { "Check in", "Train from Zürich" }, loaded.Days[0].Items.Select(i => i.Title));
        Assert.Equal(new[] { 0, 1 }, loaded.Days[0].Items.Select(i => i.SortOrder));
        Assert.Equal("Dinner", loaded.Days[1].Items.Single().Title);
        await _travel.SetItemDoneAsync(train.Id, true);

        await _travel.DeleteDayAsync(day1.Id);
        loaded = (await _travel.GetTripAsync(trip.Id))!;
        Assert.Equal(new[] { 0, 1, 2 }, loaded.Days.Select(d => d.Index));
        Assert.Equal("Dinner", loaded.Days[0].Items.Single().Title);

        var summary = (await _travel.GetTripSummaryAsync(trip.Id))!;
        Assert.Equal(3, summary.PlaceCount);
        Assert.Equal(3, summary.DayCount);
        Assert.Equal(new[] { "CH" }, summary.CountryCodes);
        Assert.Contains("Zermatt", summary.Cities);
        Assert.Equal("Zermatt", summary.PrimaryPlaceName);

        await _travel.ReorderTripPlacesAsync(trip.Id, [hotel.Id, zermatt.Id, gornergrat.Id]);
        Assert.Equal(new[] { hotel.Id, zermatt.Id, gornergrat.Id }, (await _travel.GetTripAsync(trip.Id))!.Places.Select(p => p.PlaceId));
        await _travel.RemovePlaceFromTripAsync(trip.Id, zermatt.Id);
        Assert.NotEqual("Zermatt", (await _travel.GetTripSummaryAsync(trip.Id))!.PrimaryPlaceName); // no longer the main destination
    }

    [Fact]
    public async Task Adding_a_day_past_the_last_date_lengthens_a_dated_trip_only()
    {
        var dated = await _travel.CreateTripAsync(new TripDraft("Kyoto", TripStatus.Upcoming, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 13)));
        await _travel.EnsureDaysForDatesAsync(dated.Id);
        await _travel.AddDayAsync(dated.Id, "Nara side trip");
        var trip = (await _travel.GetTripAsync(dated.Id))!;
        Assert.Equal(5, trip.Days.Count);
        Assert.Equal(new DateOnly(2026, 10, 14), trip.EndDate);
        Assert.Equal(new DateOnly(2026, 10, 10), trip.StartDate);

        var undated = await _travel.CreateTripAsync(new TripDraft("Someday", TripStatus.Idea));
        await _travel.AddDayAsync(undated.Id, null);
        var idea = (await _travel.GetTripAsync(undated.Id))!;
        Assert.Single(idea.Days);
        Assert.Null(idea.StartDate);
        Assert.Null(idea.EndDate);
    }

    [Fact]
    public async Task Journal_entries_persist_exactly_and_are_searchable()
    {
        var rome = await Place("Rome", 41.9, 12.5);
        var trip = await _travel.CreateTripAsync(new TripDraft("Italy 2026", TripStatus.Completed, new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 8)));
        var text = "# Erster Tag\n**Gelato** am Trevi-Brunnen. يوم رائع في روما\n- [x] Kolosseum";
        var entry = await _travel.CreateJournalAsync(new JournalDraft("First day in Rome", new DateOnly(2026, 5, 1), text, trip.Id, rome.Id, JournalMood.Joyful, "food"));
        Assert.Equal(text, (await _travel.GetJournalAsync(entry.Id))!.Text);

        await _travel.UpdateJournalAsync(entry.Id, new JournalDraft("First day in Rome", new DateOnly(2026, 5, 1), text + "\nMore.", trip.Id, rome.Id, JournalMood.Joyful, "food", true));
        var summary = (await _travel.ListJournalAsync(new JournalQuery(TripId: trip.Id))).Single();
        Assert.Equal("Italy 2026", summary.TripName);
        Assert.Equal("Rome", summary.PlaceName);
        Assert.True(summary.IsHighlight);
        Assert.DoesNotContain("**", summary.Excerpt);

        var hits = await _search.SearchAsync(new SearchRequest("gelato"));
        Assert.Equal(SearchResultKind.Journal, Assert.Single(hits).Kind);
        Assert.Contains(await _search.SearchAsync(new SearchRequest("روما")), h => h.Kind == SearchResultKind.Journal);

        await _travel.DeleteJournalAsync(entry.Id);
        Assert.Empty(await _search.SearchAsync(new SearchRequest("gelato")));
    }

    [Fact]
    public async Task Photos_reference_files_link_to_trips_and_places_and_removal_never_deletes_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "arcadia-photos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "matterhorn.jpg");
            var b = Path.Combine(dir, "train.jpg");
            System.IO.File.WriteAllBytes(a, TestImages.Jpeg(lat: 45.9763, lon: 7.6586, taken: new DateTime(2026, 7, 14, 6, 10, 0)));
            System.IO.File.WriteAllBytes(b, TestImages.Jpeg(taken: new DateTime(2026, 7, 13, 9, 0, 0)));
            var reader = new PhotoMetadataReader(NullLogger<PhotoMetadataReader>.Instance);
            var trip = await _travel.CreateTripAsync(new TripDraft("Swiss Alps", TripStatus.Active, new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 16)));
            var zermatt = await Place("Zermatt", 46.0212, 7.7493, PlaceType.Town);

            var added = await _documents.AddAsync([File(a), File(b)], null, null);
            var ids = await _travel.AttachPhotosAsync(added.DocumentIds.Select((d, i) => (d, reader.Read(i == 0 ? a : b))).ToList(), new PhotoLink(TripId: trip.Id));
            Assert.Equal(2, ids.Count);
            var photos = await _travel.ListPhotosAsync(new PhotoQuery(TripId: trip.Id));
            Assert.Equal(new[] { "train.jpg", "matterhorn.jpg" }, photos.Select(p => p.FileName)); // by date taken
            var withGps = await _travel.ListPhotosAsync(new PhotoQuery(UnassignedWithGpsOnly: true));
            Assert.Equal("matterhorn.jpg", Assert.Single(withGps).FileName);

            // attaching again updates the same record (one per file)
            Assert.Equal(ids[0], (await _travel.AttachPhotosAsync([(added.DocumentIds[0], PhotoMetadata.Empty)], new PhotoLink(PlaceId: zermatt.Id))).Single());
            Assert.Equal(trip.Id, (await _travel.GetPhotoAsync(ids[0]))!.TripId);
            Assert.Equal(zermatt.Id, (await _travel.GetPhotoAsync(ids[0]))!.PlaceId);

            await _travel.SetTripCoverAsync(trip.Id, ids[0]);
            await _travel.SetPlaceCoverAsync(zermatt.Id, ids[0]);
            Assert.Equal(a, (await _travel.GetTripSummaryAsync(trip.Id))!.CoverPath);
            Assert.Equal(a, (await _travel.ListPlacesAsync(new PlaceQuery())).Single().CoverPath);

            // a missing file is flagged, not dropped
            await _documents.UpdateAvailabilityAsync([(added.DocumentIds[1], new FileFacts(b, false, false, null, null, null))], Now);
            Assert.True((await _travel.GetPhotoAsync(ids[1]))!.IsMissing);

            // photo b is also a project document: removing it from Travel keeps the library reference
            var project = await _projects.CreateAsync(new ProjectDraft("Album", null, ProjectStatus.Active, ProjectPriority.Normal, null, null, "Blue", "Projects", "", "", null));
            await _documents.AssociateAsync(added.DocumentIds[1], project.Id);
            await _travel.RemovePhotosAsync(ids);
            Assert.Empty(await _travel.ListPhotosAsync(new PhotoQuery()));
            Assert.Null((await _travel.GetTripSummaryAsync(trip.Id))!.CoverPath);
            Assert.Null(await _documents.GetAsync(added.DocumentIds[0]));
            Assert.NotNull(await _documents.GetAsync(added.DocumentIds[1]));
            Assert.True(System.IO.File.Exists(a) && System.IO.File.Exists(b)); // the files are never touched
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Trip_documents_and_project_links()
    {
        var project = await _projects.CreateAsync(new ProjectDraft("Japan 2027 planning", null, ProjectStatus.Active, ProjectPriority.Normal, null, null, "Blue", "Projects", "", "", null));
        var trip = await _travel.CreateTripAsync(new TripDraft("Japan 2027", TripStatus.Planning, ProjectId: project.Id));
        Assert.Equal(project.Id, (await _travel.GetTripAsync(trip.Id))!.ProjectId);
        var bogus = await _travel.CreateTripAsync(new TripDraft("Bogus project", ProjectId: 999));
        Assert.Null((await _travel.GetTripAsync(bogus.Id))!.ProjectId);

        var docs = await _documents.AddAsync([File(@"C:\Travel\JR Pass.pdf"), File(@"C:\Travel\Insurance.pdf")], null, null);
        await _travel.AssociateDocumentAsync(trip.Id, docs.DocumentIds[0]);
        await _travel.AssociateDocumentAsync(trip.Id, docs.DocumentIds[0]); // no duplicate
        await _travel.AssociateDocumentAsync(trip.Id, docs.DocumentIds[1]);
        await _travel.SetDocumentImportantAsync(trip.Id, docs.DocumentIds[1], true);
        var rows = await _travel.ListTripDocumentsAsync(trip.Id);
        Assert.Equal(new[] { "Insurance.pdf", "JR Pass.pdf" }, rows.Select(r => r.Document.DisplayName));
        Assert.True(rows[0].IsImportant);
        // documents are found by the trip they belong to
        Assert.Contains(await _search.SearchAsync(new SearchRequest("Japan 2027", new HashSet<SearchResultKind> { SearchResultKind.Document })), h => h.Id == docs.DocumentIds[0]);

        await _travel.DissociateDocumentAsync(trip.Id, docs.DocumentIds[0]);
        Assert.Single(await _travel.ListTripDocumentsAsync(trip.Id));
        Assert.NotNull(await _documents.GetAsync(docs.DocumentIds[0])); // still in the library

        await _projects.DeleteAsync(project.Id);
        Assert.Null((await _travel.GetTripAsync(trip.Id))!.ProjectId); // the trip survives its project
    }

    [Fact]
    public async Task Deleting_trips_and_places_unlinks_but_keeps_memories()
    {
        var rome = await Place("Rome", 41.9, 12.5);
        var trip = await _travel.CreateTripAsync(new TripDraft("Italy", TripStatus.Completed, new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 3)), [rome.Id]);
        var entry = await _travel.CreateJournalAsync(new JournalDraft("Ciao", new DateOnly(2026, 5, 1), "…", trip.Id, rome.Id));
        await _travel.AddVisitAsync(rome.Id, new DateOnly(2026, 5, 1), trip.Id, null);
        var bucket = await _travel.CreateBucketAsync(new BucketDraft("See the Colosseum", BucketKind.Place, PlaceId: rome.Id, TripId: trip.Id));

        await _travel.DeleteTripAsync(trip.Id);
        Assert.NotNull(await _travel.GetPlaceAsync(rome.Id));
        var kept = (await _travel.GetJournalAsync(entry.Id))!;
        Assert.Null(kept.TripId);
        Assert.Equal(rome.Id, kept.PlaceId);
        Assert.Null((await _travel.GetPlaceAsync(rome.Id))!.Visits.Single().TripId);
        Assert.Empty(await _search.SearchAsync(new SearchRequest("Italy", new HashSet<SearchResultKind> { SearchResultKind.Trip })));

        await _travel.DeletePlaceAsync(rome.Id);
        Assert.Null((await _travel.GetJournalAsync(entry.Id))!.PlaceId);
        Assert.Null((await _travel.ListBucketAsync()).Single(b => b.Id == bucket.Id).PlaceId);
    }

    [Fact]
    public async Task Bucket_list_can_be_ordered_and_completed()
    {
        var a = await _travel.CreateBucketAsync(new BucketDraft("See the northern lights", BucketKind.Experience, BucketPriority.Dream, 2027));
        var b = await _travel.CreateBucketAsync(new BucketDraft("Visit Japan", BucketKind.Country, CountryCode: "jp"));
        await Assert.ThrowsAsync<ArgumentException>(() => _travel.CreateBucketAsync(new BucketDraft("Old", TargetYear: 1200)));
        await _travel.ReorderBucketAsync([b.Id, a.Id]);
        Assert.Equal(new[] { b.Id, a.Id }, (await _travel.ListBucketAsync()).Select(x => x.Id));
        await _travel.SetBucketCompletedAsync(b.Id, true, null);
        var rows = await _travel.ListBucketAsync();
        Assert.Equal(a.Id, rows[0].Id); // open items first
        Assert.Equal(Today, rows[1].CompletedOn);
        Assert.Equal("JP", rows[1].CountryCode);
        await _travel.SetBucketCompletedAsync(b.Id, false, null);
        Assert.Null((await _travel.ListBucketAsync()).Single(x => x.Id == b.Id).CompletedOn);
    }

    [Fact]
    public async Task Countries_statistics_and_memories()
    {
        var rome = await Place("Rome", 41.9, 12.5);
        var colosseum = await Place("Colosseum", 41.89, 12.49, PlaceType.Landmark, city: "Rome");
        var tokyo = await Place("Tokyo", 35.68, 139.69);
        await Place("Reykjavík", 64.14, -21.94); // wanted, not visited
        await _travel.SetCountryAsync("AT", markedVisited: true); // "I've been to Austria" without a saved place
        await _travel.SetCountryAsync("JP", favorite: true);

        var italy = await _travel.CreateTripAsync(new TripDraft("Italy", TripStatus.Completed, new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 8)), [rome.Id, colosseum.Id]);
        var overlap = await _travel.CreateTripAsync(new TripDraft("Rome again", TripStatus.Completed, new DateOnly(2026, 5, 7), new DateOnly(2026, 5, 10)));
        await _travel.CreateTripAsync(new TripDraft("Japan", TripStatus.Upcoming, new DateOnly(2027, 4, 1), new DateOnly(2027, 4, 14)), [tokyo.Id]);
        await _travel.CreateTripAsync(new TripDraft("Maybe Peru", TripStatus.Idea));
        await _travel.AddVisitAsync(rome.Id, new DateOnly(2026, 5, 1), italy.Id, "Arrival");
        await _travel.AddVisitAsync(colosseum.Id, new DateOnly(2026, 5, 2), italy.Id, null);
        await _travel.AddVisitAsync(rome.Id, new DateOnly(2026, 5, 8), overlap.Id, null);
        await _travel.CreateJournalAsync(new JournalDraft("Sunset", new DateOnly(2026, 5, 3), "Golden light", italy.Id, rome.Id, IsHighlight: true));
        await _travel.CreateJournalAsync(new JournalDraft("Home thoughts", new DateOnly(2026, 8, 20), "Planning more"));
        await _travel.CreateBucketAsync(new BucketDraft("Visit Japan"));

        var countries = await _travel.GetCountryProgressAsync();
        Assert.True(countries.Single(c => c.Code == "IT").Visited);
        Assert.True(countries.Single(c => c.Code == "AT").Visited);
        Assert.False(countries.Single(c => c.Code == "JP").Visited);
        Assert.True(countries.Single(c => c.Code == "JP").WantToVisit); // a saved place there
        Assert.True(countries.Single(c => c.Code == "IS").WantToVisit);
        Assert.Equal(3, countries.Single(c => c.Code == "IT").VisitCount);

        var stats = await _travel.GetStatsAsync(Today);
        Assert.Equal(2, stats.CountriesVisited);
        Assert.Equal(2, stats.UnCountriesVisited);
        Assert.Equal(1, stats.ContinentsVisited);
        Assert.Equal(1, stats.CitiesVisited); // Rome and the Colosseum are one city
        Assert.Equal(4, stats.PlacesSaved);
        Assert.Equal(2, stats.PlacesVisited);
        Assert.Equal(2, stats.TripsCompleted);
        Assert.Equal(1, stats.TripsUpcoming);
        Assert.Equal(10, stats.DaysTraveling); // 1–8 May and 7–10 May overlap
        Assert.Equal("JP", stats.FavoriteCountryCode);
        Assert.Equal("IT", stats.MostVisitedCountryCode);
        Assert.Equal("Rome", stats.MostVisitedCity);
        Assert.Equal(1, stats.BucketTotal);
        Assert.Equal(2, stats.JournalEntries);

        var memories = await _travel.GetMemoriesAsync();
        var italyChapter = memories.Single(c => c.TripId == italy.Id);
        Assert.Equal("Italy", italyChapter.Title);
        Assert.Equal(new[] { "IT" }, italyChapter.CountryCodes);
        Assert.Equal(new[] { MemoryKind.Visit, MemoryKind.Visit, MemoryKind.Journal }, italyChapter.Items.Select(i => i.Kind));
        var august = memories.Single(c => c.Key == "month:2026-08");
        Assert.Equal("Home thoughts", august.Items.Single().Title);
        Assert.Equal(august, memories[0]); // newest chapter first
        Assert.Single(await _travel.GetMemoriesAsync(italy.Id));
    }

    [Fact]
    public async Task Global_search_finds_trips_places_journal_and_countries()
    {
        var alps = await Place("Swiss Alps", 46.5, 8.0, PlaceType.Mountain);
        await _travel.CreateTripAsync(new TripDraft("Italy 2026", Description: "Pasta and old stones"), [alps.Id]);
        await _travel.CreateJournalAsync(new JournalDraft("First Day in Rome", new DateOnly(2026, 5, 1), "Trevi"));

        Assert.Equal(SearchResultKind.Place, (await _search.SearchAsync(new SearchRequest("swiss alps"))).First().Kind);
        Assert.Contains(await _search.SearchAsync(new SearchRequest("pasta")), h => h.Kind == SearchResultKind.Trip && h.Title == "Italy 2026");
        Assert.Contains(await _search.SearchAsync(new SearchRequest("first day")), h => h.Kind == SearchResultKind.Journal);
        var alias = Assert.Single(await _search.SearchAsync(new SearchRequest("Schweiz")), h => h.Kind == SearchResultKind.Place); // country name indexed
        Assert.True(alias.Snippet == null || (!alias.Snippet.Contains("Schweiz", StringComparison.Ordinal) && !alias.Snippet.Contains('\n'))); // …but not shown
        var country = (await _search.SearchAsync(new SearchRequest("Island"))).First(h => h.Kind == SearchResultKind.Country); // German for Iceland
        Assert.Equal("IS", country.Key);
    }
}

public sealed class TravelMigrationTests
{
    [Fact]
    public async Task Existing_games_projects_documents_and_sessions_survive_the_travel_migration()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"arcadia-p5-{Guid.NewGuid():N}")).FullName; // own folder: the upgrade writes Backups\ next to the file
        var file = Path.Combine(dir, "arcadia.db");
        try
        {
            var options = new DbContextOptionsBuilder<ArcadiaDbContext>().UseSqlite($"Data Source={file};Pooling=False").Options;
            await using (var db = new ArcadiaDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("ProjectsDocuments");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO Games (Title, Platform, Tags, Status, Priority, CompletionPercent, PlaytimeHours, EstimatedHours, AchievementsUnlocked, AchievementsTotal, AddedAtUtc, IsFeatured, ArtworkTheme, ArtworkSeed, IsSample) " +
                    "VALUES ('Elden Ring', 'PC', 'RPG', 'Playing', 'High', 45, 61.5, 100, 12, 42, '2026-01-01 00:00:00', 1, 'castle-dusk', 7, 0)");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO DiagnosticSessions (Title, Reference, ComputerName, CreatedAtUtc, UpdatedAtUtc) VALUES ('Printer', 'T-1', 'PC', '2026-09-01 10:00:00', '2026-09-01 10:00:00')");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO Projects (Name, Status, Priority, Accent, Icon, ArtworkTheme, ArtworkSeed, Tags, ProgressMode, ManualProgress, IsPinned, CreatedAtUtc, UpdatedAtUtc, IsSample) " +
                    "VALUES ('Home Lab', 'Active', 'High', 'Cyan', 'Projects', 'neon-city', 3, '', 'TaskBased', 0, 0, '2026-09-01 10:00:00', '2026-09-01 10:00:00', 0)");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO Documents (DisplayName, Path, Extension, IsFolder, AddedAtUtc, IsFavorite, IsMissing) VALUES ('Plan.md', 'C:\\Plan.md', '.md', 0, '2026-09-01 10:00:00', 0, 0)");
            }

            var factory = new FileFactory(options);
            await new DatabaseInitializer(factory, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(seedSampleData: false);

            await using var after = new ArcadiaDbContext(options);
            Assert.Equal("Elden Ring", (await after.Games.SingleAsync()).Title);
            Assert.Equal("Printer", (await after.DiagnosticSessions.SingleAsync()).Title);
            Assert.Equal("Home Lab", (await after.Projects.SingleAsync()).Name);
            Assert.Equal("Plan.md", (await after.Documents.SingleAsync()).DisplayName);
            Assert.Contains(await after.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_Travel", StringComparison.Ordinal));
            Assert.Equal(0, await after.Trips.CountAsync());

            // the library as it was before the upgrade was copied aside first
            var backup = Assert.Single(Directory.GetFiles(Path.Combine(dir, "Backups"), "arcadia-before-*.db"));
            await using (var copy = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False"))
            {
                await copy.OpenAsync();
                await using var cmd = copy.CreateCommand();
                cmd.CommandText = "SELECT (SELECT Title FROM Games) || '|' || (SELECT COUNT(*) FROM sqlite_master WHERE name = 'Trips')";
                Assert.Equal("Elden Ring|0", await cmd.ExecuteScalarAsync());
            }

            var travel = new TravelStore(factory, TimeProvider.System);
            var place = await travel.CreatePlaceAsync(new PlaceDraft("Lisbon", PlaceType.City, PlaceStatus.WantToVisit, 38.72, -9.14, "PT"));
            var trip = await travel.CreateTripAsync(new TripDraft("Portugal"), [place.Id]);
            Assert.Single((await travel.GetTripAsync(trip.Id))!.Places);
            Assert.Contains(await new GlobalSearchService([new Arcadia.Data.Search.IndexSearchProvider(factory)]).SearchAsync(new SearchRequest("lisbon")),
                h => h.Kind == SearchResultKind.Place);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FileFactory(DbContextOptions<ArcadiaDbContext> options) : IDbContextFactory<ArcadiaDbContext>
    {
        public ArcadiaDbContext CreateDbContext() => new(options);
    }
}
