using System.Reflection;

namespace Arcadia.Core.Oracle;

/// <summary>One vocabulary entry: a phrase (folded words) that stands for one or more concepts.</summary>
public sealed record OracleLexEntry(string[] Words, IReadOnlyList<string> Concepts, OracleLanguage Language);

/// <summary>A matched phrase in the input: which tokens, and the concept alternatives it can mean.</summary>
public sealed record OracleLexHit(int Start, int Length, IReadOnlyList<IReadOnlyList<string>> Alternatives, bool Fuzzy)
{
    public int End => Start + Length;
}

/// <summary>
/// Stage 3: the data-driven vocabulary (Lexicon/*.txt, embedded). All languages share the same concepts; a language pack
/// only adds words. Phrases match longest-first so "not completed" or "next week" are one unit, not unrelated words.
/// Loaded once, lazily (a few hundred phrases).
/// </summary>
public sealed class OracleLexicon
{
    private static readonly Lazy<OracleLexicon> Shared = new(() => Load(typeof(OracleLexicon).Assembly));
    public static OracleLexicon Default => Shared.Value;

    private readonly Dictionary<OracleLanguage, Dictionary<string, List<OracleLexEntry>>> _byFirst = [];
    private readonly Dictionary<OracleLanguage, HashSet<string>> _words = [];
    private readonly Dictionary<OracleLanguage, List<string>> _singleWords = [];

    public int EntryCount { get; private set; }

    public static OracleLexicon Load(Assembly assembly)
    {
        var lex = new OracleLexicon();
        foreach (var (lang, code) in new[] { (OracleLanguage.English, "en"), (OracleLanguage.German, "de"), (OracleLanguage.Arabic, "ar") })
        {
            using var stream = assembly.GetManifestResourceStream($"Arcadia.Core.Oracle.Lexicon.{code}.txt")
                               ?? throw new InvalidOperationException($"Oracle vocabulary '{code}' is missing.");
            using var reader = new StreamReader(stream);
            lex.Add(lang, reader.ReadToEnd());
        }
        return lex;
    }

    /// <summary>Adds a vocabulary text (lines "concepts = phrase | phrase"). Public so tests and future packs can extend it.</summary>
    public void Add(OracleLanguage language, string text)
    {
        var index = _byFirst.TryGetValue(language, out var i) ? i : _byFirst[language] = [];
        var words = _words.TryGetValue(language, out var w) ? w : _words[language] = [];
        var singles = _singleWords.TryGetValue(language, out var s) ? s : _singleWords[language] = [];
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf(" = ", StringComparison.Ordinal);
            if (eq < 0) continue;
            var concepts = line[..eq].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var phrase in line[(eq + 3)..].Split('|'))
            {
                var folded = OracleText.FoldPhrase(phrase.Trim().TrimEnd(','));
                if (folded.Length == 0) continue;
                var parts = folded.Split(' ');
                var entry = new OracleLexEntry(parts, concepts, language);
                if (!index.TryGetValue(parts[0], out var list)) index[parts[0]] = list = [];
                list.Add(entry);
                foreach (var p in parts) words.Add(p);
                if (parts.Length == 1 && parts[0].Length >= 5) singles.Add(parts[0]);
                EntryCount++;
            }
        }
        foreach (var list in index.Values) list.Sort((a, b) => b.Words.Length.CompareTo(a.Words.Length));
    }

    /// <summary>
    /// Deterministic language detection: Arabic script means Arabic; otherwise German when more words are known only in
    /// German than only in English; otherwise the interface language (or English).
    /// </summary>
    public OracleLanguage Detect(IReadOnlyList<OracleToken> tokens, OracleLanguage fallback)
    {
        if (tokens.Any(t => OracleText.IsArabic(t.Text))) return OracleLanguage.Arabic;
        int de = 0, en = 0;
        var deWords = _words.GetValueOrDefault(OracleLanguage.German) ?? [];
        var enWords = _words.GetValueOrDefault(OracleLanguage.English) ?? [];
        foreach (var t in tokens.Where(t => t.Kind == OracleTokenKind.Word))
        {
            var inDe = deWords.Contains(t.Norm);
            var inEn = enWords.Contains(t.Norm);
            if (inDe && !inEn) de++;
            if (inEn && !inDe) en++;
            if (t.Text.Any(c => c is 'ä' or 'ö' or 'ü' or 'ß' or 'Ä' or 'Ö' or 'Ü')) de++;
        }
        if (de > en) return OracleLanguage.German;
        if (en > 0) return OracleLanguage.English;
        return fallback == OracleLanguage.Arabic ? OracleLanguage.English : fallback;
    }

    /// <summary>Matches phrases left to right, longest first, in the given language and then English.</summary>
    public List<OracleLexHit> Match(IReadOnlyList<OracleToken> tokens, OracleLanguage language)
    {
        var packs = language == OracleLanguage.English ? new[] { OracleLanguage.English } : [language, OracleLanguage.English];
        var hits = new List<OracleLexHit>();
        var i = 0;
        while (i < tokens.Count)
        {
            var hit = Longest(tokens, i, packs);
            if (hit != null) { hits.Add(hit); i = hit.End; continue; }
            i++;
        }
        return hits;
    }

    private OracleLexHit? Longest(IReadOnlyList<OracleToken> tokens, int start, OracleLanguage[] packs)
    {
        var first = tokens[start];
        if (first.Kind is not (OracleTokenKind.Word or OracleTokenKind.Number)) return null;
        foreach (var form in Forms(first.Norm, packs[0]))
        {
            List<OracleLexEntry> candidates = [];
            foreach (var p in packs)
                if (_byFirst.TryGetValue(p, out var idx) && idx.TryGetValue(form, out var list)) candidates.AddRange(list);
            if (candidates.Count == 0) continue;
            var bestLength = 0;
            var alternatives = new List<IReadOnlyList<string>>();
            foreach (var e in candidates.OrderByDescending(e => e.Words.Length))
            {
                if (e.Words.Length < bestLength) break;
                if (!Fits(tokens, start, e.Words, form)) continue;
                bestLength = e.Words.Length;
                if (!alternatives.Any(a => a.SequenceEqual(e.Concepts))) alternatives.Add(e.Concepts);
            }
            if (bestLength > 0) return new OracleLexHit(start, bestLength, alternatives, false);
        }
        // typo tolerance for long single words ("unfinshed", "subscriptons"): one edit, never for short words
        if (first.Kind == OracleTokenKind.Word && first.Norm.Length >= 6)
            foreach (var p in packs)
                foreach (var word in _singleWords.GetValueOrDefault(p) ?? [])
                    if (Math.Abs(word.Length - first.Norm.Length) <= 1 && OracleFuzzy.Levenshtein(word, first.Norm, 1) <= 1)
                        return new OracleLexHit(start, 1, _byFirst[p][word].Where(e => e.Words.Length == 1).Select(e => e.Concepts).Distinct().ToList(), true);
        return null;
    }

    private static bool Fits(IReadOnlyList<OracleToken> tokens, int start, string[] words, string firstForm)
    {
        if (start + words.Length > tokens.Count) return false;
        if (words[0] != firstForm) return false;
        for (var k = 1; k < words.Length; k++)
            if (tokens[start + k].Norm != words[k]) return false;
        return true;
    }

    /// <summary>The word, and for Arabic the forms without a leading article/conjunction or a trailing possessive.</summary>
    private static IEnumerable<string> Forms(string norm, OracleLanguage language)
    {
        yield return norm;
        if (!OracleText.IsArabic(norm)) yield break;
        foreach (var prefix in new[] { "وال", "بال", "فال", "لل", "ال", "و", "ب", "ل" })
            if (norm.StartsWith(prefix, StringComparison.Ordinal) && norm.Length - prefix.Length >= 2)
            {
                var rest = norm[prefix.Length..];
                yield return rest;
                if (prefix is not ("ال" or "وال" or "بال" or "فال" or "لل")) yield return "ال" + rest;
            }
        if (norm.Length >= 4 && norm.EndsWith('ي')) yield return norm[..^1];
    }
}
