using System.Text;

namespace Arcadia.Core.Search;

public enum SearchResultKind { Project, Task, Note, Link, Document, Game, Trip, Place, Journal, Country, Event, Transaction, Account, Budget, Recurring, Goal }

/// <summary>A search hit. <see cref="ProjectId"/> lets the UI open the owning project for tasks, notes and links.</summary>
public sealed record SearchResult(SearchResultKind Kind, int Id, int? ProjectId, string Title, string? Snippet, double Score)
{
    /// <summary>Non-numeric identity for results that are not database rows (a country code).</summary>
    public string? Key { get; init; }
}

public sealed record SearchRequest(string Text, IReadOnlySet<SearchResultKind>? Kinds = null, int Limit = 30);

/// <summary>One source of results (projects index, documents, games…). New modules add a provider.</summary>
public interface ISearchProvider
{
    IReadOnlySet<SearchResultKind> Kinds { get; }
    Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Arcadia-wide search: fans out to the providers and merges results by score.</summary>
public interface IGlobalSearchService
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
}

public sealed class GlobalSearchService(IEnumerable<ISearchProvider> providers) : IGlobalSearchService
{
    private readonly IReadOnlyList<ISearchProvider> _providers = providers.ToList();

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) return [];
        var relevant = _providers.Where(p => request.Kinds == null || p.Kinds.Overlaps(request.Kinds)).ToList();
        // on a worker thread: SQLite completes "async" calls synchronously, so the queries would otherwise run on the
        // caller's (UI) thread. One after another — each is a few milliseconds, and providers may share a connection.
        var batches = await Task.Run(async () =>
        {
            var all = new List<IReadOnlyList<SearchResult>>(relevant.Count);
            foreach (var p in relevant) all.Add(await p.SearchAsync(request, cancellationToken).ConfigureAwait(false));
            return all;
        }, cancellationToken).ConfigureAwait(false);
        return batches.SelectMany(b => b)
            .Where(r => request.Kinds == null || request.Kinds.Contains(r.Kind))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(request.Limit)
            .ToList();
    }
}

/// <summary>Builds safe SQLite FTS5 queries from what the user typed.</summary>
public static class FtsQuery
{
    /// <summary>
    /// Each word becomes a quoted prefix term (<c>"wor"*</c>), so punctuation, operators and quotes in the input
    /// can never form FTS syntax. Returns null when nothing searchable remains.
    /// </summary>
    public static string? Build(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var terms = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' || char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.NonSpacingMark)
                current.Append(ch);
            else if (current.Length > 0) { terms.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) terms.Add(current.ToString());
        terms = terms.Take(8).ToList();
        return terms.Count == 0 ? null : string.Join(' ', terms.Select(t => $"\"{t}\"*"));
    }
}

/// <summary>Ranks command-palette entries against typed text (case-insensitive, word starts preferred).</summary>
public static class CommandMatcher
{
    /// <summary>0 = no match; higher is better.</summary>
    public static int Score(string query, string title, IEnumerable<string>? keywords = null)
    {
        query = query.Trim();
        if (query.Length == 0) return 1;
        var best = ScoreOne(query, title) * 2;
        foreach (var k in keywords ?? []) best = Math.Max(best, ScoreOne(query, k));
        return best;
    }

    private static int ScoreOne(string q, string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var cmp = StringComparison.CurrentCultureIgnoreCase;
        if (text.Equals(q, cmp)) return 100;
        if (text.StartsWith(q, cmp)) return 80;
        // start of a later word ("proj" in "Open Projects")
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (word.StartsWith(q, cmp)) return 60;
        if (text.Contains(q, cmp)) return 40;
        // initials / subsequence ("np" → "New Project")
        int i = 0;
        foreach (var ch in text)
            if (i < q.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(q[i])) i++;
        return i == q.Length ? 10 : 0;
    }
}
