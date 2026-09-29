using System.Collections.ObjectModel;
using Arcadia.App.Services;
using Arcadia.App.ViewModels.Projects;
using Arcadia.Core.Localization;
using Arcadia.Core.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Arcadia.App.ViewModels;

/// <summary>A command the palette can run.</summary>
public sealed record PaletteCommand(string Key, string Icon, string TitleKey, string[] Keywords, string? Shortcut, Func<Task> Run);

/// <summary>A row in the palette: a command or a content result.</summary>
public sealed partial class PaletteItem(string icon, string title, string? subtitle, string kind, string? shortcut, Func<Task> run) : ObservableObject
{
    public string Icon { get; } = icon;
    public string Title { get; } = title;
    public string? Subtitle { get; } = subtitle;
    public string Kind { get; } = kind;
    public string? Shortcut { get; } = shortcut;
    public Func<Task> Run { get; } = run;
    [ObservableProperty] private bool _isSelected;

    /// <summary>What screen readers (and UI tests) announce for the row.</summary>
    public override string ToString() => string.IsNullOrEmpty(Subtitle) ? Title : $"{Title} — {Subtitle}";
}

/// <summary>
/// Ctrl+K: a keyboard-first launcher. Commands are matched instantly; content (projects, tasks, notes,
/// documents, games) is searched through <see cref="IGlobalSearchService"/> after a short pause in typing.
/// Up/Down move, Enter runs, Escape closes.
/// </summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly IGlobalSearchService _search;
    private readonly ILocalizer _loc;
    private readonly ILogger<CommandPaletteViewModel> _logger;
    private readonly List<PaletteCommand> _commands = [];
    private CancellationTokenSource? _cts;

    public CommandPaletteViewModel(IGlobalSearchService search, ILocalizer loc, ILogger<CommandPaletteViewModel> logger)
    {
        _search = search;
        _loc = loc;
        _logger = logger;
    }

    public ObservableCollection<PaletteItem> Items { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _hint = string.Empty;

    /// <summary>Content results open here (set by the shell).</summary>
    public Func<SearchResult, Task>? OpenResult { get; set; }

    /// <summary>Optional one-line descriptions for results (e.g. an event's next date), replacing the text excerpt.</summary>
    public Func<IReadOnlyList<SearchResult>, Task<IReadOnlyDictionary<(SearchResultKind, int), string>>>? Describe { get; set; }

    public void Register(PaletteCommand command) => _commands.Add(command);

    /// <summary>Recent places, listed when the palette opens empty (Phase 9).</summary>
    public Func<IReadOnlyList<PaletteItem>>? Recent { get; set; }

    public IReadOnlyList<PaletteCommand> Commands => _commands;

    partial void OnQueryChanged(string value)
    {
        Rebuild(value, []);
        _ = SearchAsync(value);
    }

    partial void OnSelectedIndexChanged(int value)
    {
        for (int i = 0; i < Items.Count; i++) Items[i].IsSelected = i == value;
    }

    public void Open(string? text = null)
    {
        Query = text ?? string.Empty;
        Rebuild(Query, []);
        IsOpen = true;
        if (!string.IsNullOrWhiteSpace(text)) _ = SearchAsync(text);
    }

    [RelayCommand]
    public void Close()
    {
        _cts?.Cancel();
        IsOpen = false;
    }

    /// <summary>The commands matching the text, best first (pure, unit-tested).</summary>
    public IReadOnlyList<PaletteCommand> MatchCommands(string text) =>
        _commands.Select(c => (c, score: CommandMatcher.Score(text, _loc[c.TitleKey], c.Keywords.Append(c.Key))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => _commands.IndexOf(x.c))
            .Select(x => x.c)
            .ToList();

    private void Rebuild(string text, IReadOnlyList<SearchResult> results, IReadOnlyDictionary<(SearchResultKind, int), string>? descriptions = null)
    {
        Items.Clear();
        if (string.IsNullOrWhiteSpace(text) && Recent != null)
            foreach (var r in Recent()) Items.Add(r);
        foreach (var c in MatchCommands(text).Take(string.IsNullOrWhiteSpace(text) ? 12 : 6))
            Items.Add(new PaletteItem(c.Icon, _loc[c.TitleKey], null, _loc["Palette_Command"], c.Shortcut, c.Run));
        foreach (var r in results)
        {
            var result = r;
            var subtitle = descriptions != null && descriptions.TryGetValue((r.Kind, r.Id), out var d) ? d : r.Snippet;
            Items.Add(new PaletteItem(SearchIcons.For(r.Kind), r.Title, subtitle, _loc[$"SearchKind_{r.Kind}"], null,
                () => OpenResult?.Invoke(result) ?? Task.CompletedTask));
        }
        SelectedIndex = Items.Count > 0 ? 0 : -1;
        Hint = Items.Count == 0 && !IsSearching ? _loc["Palette_NoResults"] : string.Empty;
    }

    private async Task SearchAsync(string text)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        if (text.Trim().Length < 2) { IsSearching = false; return; }
        try
        {
            IsSearching = true;
            await Task.Delay(150, cts.Token);
            var results = await _search.SearchAsync(new SearchRequest(text, null, 12), cts.Token);
            if (cts.IsCancellationRequested || text != Query) return;
            IReadOnlyDictionary<(SearchResultKind, int), string>? descriptions = null;
            if (Describe != null)
            {
                try { descriptions = await Describe(results); }
                catch (Exception ex) { _logger.LogDebug(ex, "Describing palette results failed"); }
            }
            if (cts.IsCancellationRequested || text != Query) return;
            IsSearching = false;
            Rebuild(text, results, descriptions);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            IsSearching = false;
            _logger.LogWarning(ex, "Palette search failed");
        }
    }

    [RelayCommand]
    public void MoveDown()
    {
        if (Items.Count > 0) SelectedIndex = (SelectedIndex + 1) % Items.Count;
    }

    [RelayCommand]
    public void MoveUp()
    {
        if (Items.Count > 0) SelectedIndex = SelectedIndex <= 0 ? Items.Count - 1 : SelectedIndex - 1;
    }

    [RelayCommand]
    public async Task ExecuteAsync(PaletteItem? item)
    {
        item ??= SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;
        if (item == null) return;
        Close();
        try
        {
            await item.Run();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Palette command {Title} failed", item.Title);
        }
    }
}
