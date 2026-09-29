using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Arcadia.Core.Metadata;
using Arcadia.Core.Security;
using Microsoft.Extensions.Logging;

namespace Arcadia.Infrastructure.Metadata;

/// <summary>
/// IGDB v4 client. Authenticates with a Twitch app access token (client-credentials flow), cached until it expires.
/// Requests are spaced to respect IGDB's limit of 4 requests per second.
/// </summary>
public sealed class IgdbClient : IGameInfoProvider
{
    public const string HttpClientName = "igdb";
    private const string TokenUrl = "https://id.twitch.tv/oauth2/token";
    private const string ApiBase = "https://api.igdb.com/v4/";
    private static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(260);

    private readonly IHttpClientFactory _http;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _time;
    private readonly ILogger<IgdbClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTime _tokenExpiresUtc;
    private DateTime _lastRequestUtc;

    public IgdbClient(IHttpClientFactory http, ISecretStore secrets, TimeProvider time, ILogger<IgdbClient> logger)
    {
        _http = http;
        _secrets = secrets;
        _time = time;
        _logger = logger;
        secrets.Changed += (_, _) => _token = null;
    }

    private string? ClientId => _secrets.Get(SecretNames.IgdbClientId);
    private string? ClientSecret => _secrets.Get(SecretNames.IgdbClientSecret);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    // IGDB game_type values worth offering: main game, expansion, standalone expansion, remake, remaster, expanded game, port.
    // Excluded: DLC (1), bundle (3), mod (5), episode (6), season (7), fork (12), pack (13), update (14).
    private static readonly HashSet<int> PlayableTypes = [0, 2, 4, 8, 9, 10, 11];

    public async Task<IReadOnlyList<GameMatch>> SearchAsync(string title, CancellationToken cancellationToken = default)
    {
        var body = $"search \"{Escape(title)}\"; fields name,first_release_date,cover.image_id,platforms.abbreviation,version_parent,game_type,total_rating_count; limit 30;";
        using var doc = await QueryAsync("games", body, cancellationToken).ConfigureAwait(false);
        var wanted = Normalize(title);
        var ranked = new List<(GameMatch Match, bool Exact, int Votes, int Order)>();
        int order = 0;
        foreach (var g in doc.RootElement.EnumerateArray())
        {
            order++;
            if (g.TryGetProperty("version_parent", out _)) continue; // editions of another entry
            if (g.TryGetProperty("game_type", out var type) && type.ValueKind == JsonValueKind.Number && !PlayableTypes.Contains(type.GetInt32())) continue;
            var name = g.GetProperty("name").GetString() ?? "?";
            var votes = g.TryGetProperty("total_rating_count", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            ranked.Add((new GameMatch(
                g.GetProperty("id").GetInt64(),
                name,
                ReleaseDate(g)?.Year,
                string.Join(", ", Names(g, "platforms", "abbreviation")),
                ImageUrl(g, "cover", "cover_big")), Normalize(name) == wanted, votes, order));
        }
        return ranked.OrderByDescending(r => r.Exact).ThenByDescending(r => r.Votes).ThenBy(r => r.Order).Select(r => r.Match).Take(15).ToList();
    }

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public async Task<GameDetails?> GetDetailsAsync(long id, CancellationToken cancellationToken = default)
    {
        var body = "fields name,summary,storyline,first_release_date,genres.name,themes.name,platforms.name,total_rating," +
                   "involved_companies.developer,involved_companies.publisher,involved_companies.company.name," +
                   "cover.image_id,screenshots.image_id,external_games.category,external_games.external_game_source,external_games.uid; " +
                   $"where id = {id.ToString(CultureInfo.InvariantCulture)};";
        using var doc = await QueryAsync("games", body, cancellationToken).ConfigureAwait(false);
        var g = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (g.ValueKind != JsonValueKind.Object) return null;

        var developers = new List<string>();
        var publishers = new List<string>();
        if (g.TryGetProperty("involved_companies", out var companies))
        {
            foreach (var c in companies.EnumerateArray())
            {
                var name = c.TryGetProperty("company", out var co) && co.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name == null) continue;
                if (c.TryGetProperty("developer", out var dev) && dev.GetBoolean()) developers.Add(name);
                if (c.TryGetProperty("publisher", out var pub) && pub.GetBoolean()) publishers.Add(name);
            }
        }

        var genres = Names(g, "genres", "name").Concat(Names(g, "themes", "name")).Distinct().Take(6).ToList();
        var summary = g.TryGetProperty("summary", out var s) ? s.GetString() : g.TryGetProperty("storyline", out var st) ? st.GetString() : null;
        var screenshots = g.TryGetProperty("screenshots", out var shots)
            ? shots.EnumerateArray().Select(x => x.TryGetProperty("image_id", out var i) ? Url(i.GetString(), "1080p") : null).OfType<string>().Take(8).ToList()
            : [];

        return new GameDetails(
            g.GetProperty("id").GetInt64(),
            g.GetProperty("name").GetString() ?? "?",
            summary,
            ReleaseDate(g),
            genres,
            developers.Distinct().ToList(),
            publishers.Distinct().ToList(),
            Names(g, "platforms", "name").ToList(),
            ImageUrl(g, "cover", "cover_big"),
            screenshots,
            SteamAppId(g),
            g.TryGetProperty("total_rating", out var r) ? Math.Round(r.GetDouble()) : null);
    }

    /// <summary>Steam app id from external_games (source/category 1 = Steam; IGDB renamed category → external_game_source).</summary>
    internal static int? SteamAppId(JsonElement game)
    {
        if (!game.TryGetProperty("external_games", out var ext)) return null;
        foreach (var e in ext.EnumerateArray())
        {
            bool steam = (e.TryGetProperty("external_game_source", out var src) && src.ValueKind == JsonValueKind.Number && src.GetInt32() == 1)
                         || (e.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.Number && cat.GetInt32() == 1);
            if (steam && e.TryGetProperty("uid", out var uid) && int.TryParse(uid.GetString(), out var appId)) return appId;
        }
        return null;
    }

    // ── transport ─────────────────────────────────────────────────

    private async Task<JsonDocument> QueryAsync(string endpoint, string body, CancellationToken ct)
    {
        if (!IsConfigured) throw new MetadataException("IGDB credentials are not set.", MetadataFailure.NotConfigured);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                var wait = _lastRequestUtc + MinSpacing - _time.GetUtcNow().UtcDateTime;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);

                var token = await GetTokenAsync(ct).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + endpoint) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
                request.Headers.Add("Client-ID", ClientId);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var response = await Send(request, ct).ConfigureAwait(false);
                _lastRequestUtc = _time.GetUtcNow().UtcDateTime;

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { _token = null; continue; }
                if (response.StatusCode == (HttpStatusCode)429 && attempt == 0) { await Task.Delay(1000, ct).ConfigureAwait(false); continue; }
                await EnsureSuccessAsync(response, "IGDB", ct).ConfigureAwait(false);

                var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token != null && _time.GetUtcNow().UtcDateTime < _tokenExpiresUtc) return _token;

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId!,
                ["client_secret"] = ClientSecret!,
                ["grant_type"] = "client_credentials",
            }),
        };
        using var response = await Send(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new MetadataException("Twitch rejected the IGDB Client ID or Client Secret.", MetadataFailure.Unauthorized);
        await EnsureSuccessAsync(response, "Twitch", ct).ConfigureAwait(false);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        _token = doc.RootElement.GetProperty("access_token").GetString();
        var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt64() : 3600;
        _tokenExpiresUtc = _time.GetUtcNow().UtcDateTime.AddSeconds(Math.Max(60, seconds - 300));
        _logger.LogInformation("IGDB access token obtained (valid ~{Days:0} days)", seconds / 86400.0);
        return _token!;
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _http.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MetadataException("IGDB could not be reached.", MetadataFailure.Unavailable, ex);
        }
    }

    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, string service, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var failure = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => MetadataFailure.Unauthorized,
            (HttpStatusCode)429 => MetadataFailure.RateLimited,
            >= HttpStatusCode.InternalServerError => MetadataFailure.Unavailable,
            _ => MetadataFailure.InvalidResponse,
        };
        throw new MetadataException($"{service} returned {(int)response.StatusCode}: {text[..Math.Min(200, text.Length)]}", failure);
    }

    // ── helpers ───────────────────────────────────────────────────

    internal static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Trim();

    private static IEnumerable<string> Names(JsonElement e, string array, string field) =>
        e.TryGetProperty(array, out var arr)
            ? arr.EnumerateArray().Select(x => x.TryGetProperty(field, out var v) ? v.GetString() : null).OfType<string>()
            : [];

    private static DateOnly? ReleaseDate(JsonElement g) =>
        g.TryGetProperty("first_release_date", out var d) && d.ValueKind == JsonValueKind.Number
            ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(d.GetInt64()).UtcDateTime)
            : null;

    private static string? ImageUrl(JsonElement g, string field, string size) =>
        g.TryGetProperty(field, out var img) && img.TryGetProperty("image_id", out var id) ? Url(id.GetString(), size) : null;

    private static string? Url(string? imageId, string size) =>
        string.IsNullOrEmpty(imageId) ? null : $"https://images.igdb.com/igdb/image/upload/t_{size}/{imageId}.jpg";
}
