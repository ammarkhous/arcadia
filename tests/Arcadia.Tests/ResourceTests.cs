using System.Text.RegularExpressions;
using System.Xml.Linq;
using Arcadia.Data.Seeding;
using Arcadia.UI.Artwork;
using Arcadia.UI.Controls;

namespace Arcadia.Tests;

/// <summary>Guards the integrity of localization resources and asset references.</summary>
public class ResourceTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Arcadia.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    private static Dictionary<string, string> LoadResx(string name) =>
        XDocument.Load(Path.Combine(Root, "src", "Arcadia.App", "Resources", name))
            .Root!.Elements("data")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")?.Value ?? string.Empty);

    [Fact]
    public void Arabic_translation_covers_every_english_key()
    {
        var en = LoadResx("Strings.resx");
        var ar = LoadResx("Strings.ar.resx");
        Assert.Empty(en.Keys.Except(ar.Keys));
        Assert.Empty(ar.Keys.Except(en.Keys));
        Assert.All(ar, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{kv.Key} is empty"));
    }

    [Fact]
    public void Format_placeholders_match_between_languages()
    {
        var en = LoadResx("Strings.resx");
        var ar = LoadResx("Strings.ar.resx");
        foreach (var (key, value) in en)
        {
            var expected = Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).Order();
            var actual = Regex.Matches(ar[key], @"\{\d+\}").Select(m => m.Value).Order();
            Assert.True(expected.SequenceEqual(actual), $"Placeholder mismatch in {key}");
        }
    }

    [Fact]
    public void Every_localization_key_used_in_xaml_exists()
    {
        var en = LoadResx("Strings.resx");
        var xamlFiles = Directory.EnumerateFiles(Path.Combine(Root, "src", "Arcadia.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        var used = xamlFiles.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\{l:Loc (\w+)\}").Select(m => m.Groups[1].Value)).Distinct().ToList();
        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.True(en.ContainsKey(k), $"Missing resource key {k}"));
    }

    [Fact]
    public void Every_artwork_theme_used_by_sample_data_exists()
    {
        var themes = SceneThemes.Names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var games = SampleDataSeeder.CreateGames(DateTime.UtcNow);
        Assert.All(games, g => Assert.Contains(g.ArtworkTheme, themes));
        Assert.All(games.SelectMany(g => g.Screenshots), s => Assert.Contains(s.ArtworkTheme, themes));
    }

    [Fact]
    public void Every_icon_referenced_in_xaml_exists_in_the_icon_set()
    {
        var icons = XDocument.Load(Path.Combine(Root, "src", "Arcadia.UI", "Themes", "Icons.xaml")).Root!.Elements()
            .Select(e => e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Value)
            .Where(k => !k.EndsWith(".Accent", StringComparison.Ordinal))
            .Select(k => k["Icon.".Length..])
            .ToHashSet();

        var sources = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".xaml") || f.EndsWith(".cs")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        var referenced = sources.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"(?:Kind|Icon|TrailingIcon)=""(\w+)""").Select(m => m.Groups[1].Value))
            .Where(v => v != "Icon")
            .Distinct()
            .ToList();

        Assert.NotEmpty(referenced);
        Assert.All(referenced, r => Assert.True(icons.Contains(r), $"Missing icon {r}"));
    }

    [Theory]
    [InlineData("The Witcher 3: Wild Hunt", true)]
    [InlineData("3: Wild Hunt", true)]
    [InlineData("العودة إلى الكهف", false)]
    [InlineData("  «الكهف» cave", false)]
    [InlineData("", true)]
    public void Bidi_detects_direction_from_first_strong_character(string text, bool ltr) =>
        Assert.Equal(ltr, Bidi.IsLeftToRight(text));
}
