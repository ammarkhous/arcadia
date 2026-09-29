using Arcadia.Core.Metadata;
using Arcadia.Core.Security;
using Arcadia.Infrastructure.Metadata;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arcadia.Tests;

internal sealed class MemorySecrets(Dictionary<string, string?> values) : ISecretStore
{
    public string? Get(string name) => values.GetValueOrDefault(name);
    public void Set(string name, string? value) { values[name] = value; Changed?.Invoke(this, EventArgs.Empty); }
    public event EventHandler? Changed;
}

/// <summary>
/// Live calls against IGDB and SteamGridDB. They run only when ARCADIA_IGDB_ID / ARCADIA_IGDB_SECRET / ARCADIA_SGDB_KEY
/// are set in the environment (never in source); otherwise they return immediately.
/// </summary>
[Trait("Category", "Live")]
public class MetadataLiveTests
{
    private static (IgdbClient igdb, SteamGridDbClient sgdb)? Clients()
    {
        var id = Environment.GetEnvironmentVariable("ARCADIA_IGDB_ID");
        var secret = Environment.GetEnvironmentVariable("ARCADIA_IGDB_SECRET");
        var key = Environment.GetEnvironmentVariable("ARCADIA_SGDB_KEY");
        if (id == null || secret == null || key == null) return null;
        var secrets = new MemorySecrets(new()
        {
            [SecretNames.IgdbClientId] = id, [SecretNames.IgdbClientSecret] = secret, [SecretNames.SteamGridDbApiKey] = key,
        });
        var sp = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var http = sp.GetRequiredService<IHttpClientFactory>();
        return (new IgdbClient(http, secrets, TimeProvider.System, NullLogger<IgdbClient>.Instance),
                new SteamGridDbClient(http, secrets, NullLogger<SteamGridDbClient>.Instance));
    }

    [Fact]
    public async Task Igdb_finds_the_witcher_3_with_details_and_steam_id()
    {
        if (Clients() is not { } c) return;
        var matches = await c.igdb.SearchAsync("The Witcher 3: Wild Hunt");
        Assert.NotEmpty(matches);
        var match = matches[0];                                   // ranked: exact title first
        Assert.Equal("The Witcher 3: Wild Hunt", match.Name);
        Assert.Equal(2015, match.ReleaseYear);
        Assert.DoesNotContain(matches, m => m.Name.Contains("Dark Souls"));   // bundles are filtered out
        Assert.DoesNotContain(matches, m => m.Name.Contains("New Quest"));    // DLC is filtered out

        var details = await c.igdb.GetDetailsAsync(match.Id);
        Assert.NotNull(details);
        Assert.False(string.IsNullOrWhiteSpace(details!.Summary));
        Assert.Contains(details.Developers, d => d.Contains("CD PROJEKT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(292030, details.SteamAppId);
        Assert.NotNull(details.CoverUrl);
    }

    [Fact]
    public async Task SteamGridDb_returns_all_art_kinds_for_a_steam_game()
    {
        if (Clients() is not { } c) return;
        var id = await c.sgdb.FindGameAsync("The Witcher 3: Wild Hunt", 292030);
        Assert.NotNull(id);
        foreach (var kind in Enum.GetValues<ArtKind>())
        {
            var art = await c.sgdb.GetArtAsync(id!.Value, kind);
            Assert.NotEmpty(art);
            Assert.StartsWith("https://", art[0].Url);
        }
    }

    [Fact]
    public async Task SteamGridDb_title_search_works_without_a_steam_id()
    {
        if (Clients() is not { } c) return;
        Assert.NotNull(await c.sgdb.FindGameAsync("Minecraft", null));
    }
}
