using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Arcadia.Core.Metadata;
using Arcadia.Core.Security;
using Microsoft.Extensions.Logging;

namespace Arcadia.Infrastructure.Metadata;

/// <summary>SteamGridDB v2 client: box art (grids), heroes and transparent logos.</summary>
public sealed class SteamGridDbClient(IHttpClientFactory http, ISecretStore secrets, ILogger<SteamGridDbClient> logger) : IGameArtProvider
{
    public const string HttpClientName = "steamgriddb";
    private const string ApiBase = "https://www.steamgriddb.com/api/v2/";
    private const string SafeFilters = "types=static&nsfw=false&humor=false&epilepsy=false";
    private const int MaxResults = 24;

    private string? ApiKey => secrets.Get(SecretNames.SteamGridDbApiKey);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<long?> FindGameAsync(string title, int? steamAppId, CancellationToken cancellationToken = default)
    {
        if (steamAppId is { } appId)
        {
            using var bySteam = await GetAsync($"games/steam/{appId.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
            if (bySteam != null && bySteam.RootElement.GetProperty("data").TryGetProperty("id", out var id)) return id.GetInt64();
        }
        using var search = await GetAsync($"search/autocomplete/{Uri.EscapeDataString(title.Trim())}", cancellationToken).ConfigureAwait(false);
        if (search == null) return null;
        var hits = search.RootElement.GetProperty("data").EnumerateArray().ToList();
        // Prefer an exact (case-insensitive) title match, then the first (most relevant) hit.
        var exact = hits.FirstOrDefault(h => string.Equals(h.GetProperty("name").GetString(), title.Trim(), StringComparison.OrdinalIgnoreCase));
        var pick = exact.ValueKind == JsonValueKind.Object ? exact : hits.FirstOrDefault();
        return pick.ValueKind == JsonValueKind.Object ? pick.GetProperty("id").GetInt64() : null;
    }

    public async Task<IReadOnlyList<ArtCandidate>> GetArtAsync(long gameId, ArtKind kind, CancellationToken cancellationToken = default)
    {
        var id = gameId.ToString(CultureInfo.InvariantCulture);
        var path = kind switch
        {
            ArtKind.BoxArt => $"grids/game/{id}?dimensions=600x900&{SafeFilters}",
            ArtKind.Capsule => $"grids/game/{id}?dimensions=920x430&{SafeFilters}",
            ArtKind.Hero => $"heroes/game/{id}?{SafeFilters}",
            _ => $"logos/game/{id}?{SafeFilters}",
        };
        using var doc = await GetAsync(path, cancellationToken).ConfigureAwait(false);
        var results = doc == null ? [] : Parse(doc.RootElement, kind);
        if (results.Count == 0 && kind == ArtKind.Capsule)
        {
            // Only fall back to the low-resolution capsule size when no 920×430 art exists.
            using var small = await GetAsync($"grids/game/{id}?dimensions=460x215&{SafeFilters}", cancellationToken).ConfigureAwait(false);
            if (small != null) results = Parse(small.RootElement, kind);
        }
        return results;
    }

    internal static IReadOnlyList<ArtCandidate> Parse(JsonElement root, ArtKind kind) =>
        root.GetProperty("data").EnumerateArray().Take(MaxResults).Select(a => new ArtCandidate(
            a.GetProperty("id").GetInt64(),
            kind,
            a.GetProperty("url").GetString()!,
            a.TryGetProperty("thumb", out var t) ? t.GetString() ?? a.GetProperty("url").GetString()! : a.GetProperty("url").GetString()!,
            a.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0,
            a.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : 0,
            a.TryGetProperty("style", out var s) ? s.GetString() : null,
            a.TryGetProperty("author", out var au) && au.TryGetProperty("name", out var n) ? n.GetString() : null)).ToList();

    /// <summary>GET returning the parsed body, or null for 404 (no such game / no art).</summary>
    private async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        if (!IsConfigured) throw new MetadataException("SteamGridDB API key is not set.", MetadataFailure.NotConfigured);
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MetadataException("SteamGridDB could not be reached.", MetadataFailure.Unavailable, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new MetadataException("SteamGridDB rejected the API key.", MetadataFailure.Unauthorized);
            await IgdbClient.EnsureSuccessAsync(response, "SteamGridDB", ct).ConfigureAwait(false);
            var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("success", out var ok) && !ok.GetBoolean())
            {
                logger.LogWarning("SteamGridDB reported failure for {Path}", path);
                doc.Dispose();
                return null;
            }
            return doc;
        }
    }
}
