namespace Verita.Application.Assistant;

/// <summary>Something the assistant can recognise by name: a hospital, a procedure, a term.</summary>
/// <param name="Kind">hospital, procedure, package, dimension, nav, term, howto.</param>
/// <param name="Key">What the answer looks it up by: a code, an id or a path.</param>
/// <param name="Label">What the user is shown.</param>
/// <param name="Aliases">Other names it is known by. The label and key are always aliases too.</param>
public sealed record LexEntry(string Kind, string Key, string Label, string[] Aliases, string? Extra = null);

/// <summary>A recognised name. <see cref="Positions"/> are the token positions it used.</summary>
public sealed record EntityMatch(string Kind, string Key, string Label, double Score, int[] Positions, string? Extra = null, bool ByCode = false, int Exact = 0, double Recall = 1.0);

/// <summary>
/// An inverted index over names. Matching scores how much of the user's words a name explains
/// (precision, weighted 0.7) and how much of the name the user said (recall, 0.3), with typo-
/// tolerant token comparison. Codes match exactly, including codes split by punctuation.
/// </summary>
public sealed class Lexicon
{
    private sealed record Alias(int Entry, string[] Tokens);

    private readonly List<LexEntry> _entries = [];
    private readonly List<Alias> _aliases = [];
    private readonly Dictionary<string, List<int>> _byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<char, List<string>> _keysByFirst = [];
    private readonly Dictionary<string, List<int>> _byCode = new(StringComparer.Ordinal);

    public Lexicon(IEnumerable<LexEntry> entries)
    {
        foreach (var e in entries) Add(e);
    }

    public int Count => _entries.Count;
    public IReadOnlyList<LexEntry> Entries => _entries;

    private void Add(LexEntry e)
    {
        var idx = _entries.Count;
        _entries.Add(e);
        foreach (var code in new[] { e.Key, e.Extra }.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            if (Guid.TryParse(code, out _)) continue;
            var compact = AssistantText.Compact(code);
            if (compact.Length >= 2) Push(_byCode, compact, idx);
        }
        foreach (var text in e.Aliases.Prepend(e.Label).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var toks = AssistantText.Stems(text).Where(t => !AssistantText.Stop.Contains(t)).ToArray();
            if (toks.Length == 0) continue;
            var ai = _aliases.Count;
            _aliases.Add(new Alias(idx, toks));
            foreach (var t in toks.Distinct())
            {
                if (!_byToken.TryGetValue(t, out var list))
                {
                    _byToken[t] = list = [];
                    if (!_keysByFirst.TryGetValue(t[0], out var keys)) _keysByFirst[t[0]] = keys = [];
                    keys.Add(t);
                }
                list.Add(ai);
            }
        }
    }

    private static void Push(Dictionary<string, List<int>> d, string k, int v)
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = [];
        if (!l.Contains(v)) l.Add(v);
    }

    /// <summary>
    /// Every entry of <paramref name="kind"/> (or any kind, when null) that the stemmed tokens
    /// name, best first. Positions marked in <paramref name="skip"/> are not used.
    /// </summary>
    public List<EntityMatch> Find(string[] stems, bool[]? skip = null, string? kind = null, double minScore = 0.6, int take = 5)
    {
        var results = new Dictionary<int, EntityMatch>();
        skip ??= new bool[stems.Length];

        // 1. Codes, which may be split across up to six tokens ("NSH-RYD.BED-ICU-DAY" -> nsh ryd bed icu day).
        for (var i = 0; i < stems.Length; i++)
        {
            if (skip[i]) continue;
            var joined = "";
            for (var n = 0; n < 6 && i + n < stems.Length && !skip[i + n]; n++)
            {
                joined += stems[i + n];
                if (joined.Length < 2) continue;
                if (!_byCode.TryGetValue(joined, out var hits)) continue;
                // A one-word code shorter than four characters ("10", "the") is a number or a word, not a code.
                if (n == 0 && joined.Length < 4) continue;
                foreach (var h in hits)
                {
                    var e = _entries[h];
                    if (kind is not null && e.Kind != kind) continue;
                    results[h] = new EntityMatch(e.Kind, e.Key, e.Label, 1.0, Enumerable.Range(i, n + 1).ToArray(), e.Extra, ByCode: true, Exact: n + 1);
                }
            }
        }

        // 2. Names, token by token, typo-tolerant.
        var candidates = new Dictionary<int, double[]>(); // alias -> best similarity per alias token
        var positions = new Dictionary<int, int[]>();
        for (var qi = 0; qi < stems.Length; qi++)
        {
            if (skip[qi] || AssistantText.Stop.Contains(stems[qi])) continue;
            foreach (var (key, sim) in Keys(stems[qi]))
            {
                foreach (var ai in _byToken[key])
                {
                    var alias = _aliases[ai];
                    if (kind is not null && _entries[alias.Entry].Kind != kind) continue;
                    if (!candidates.TryGetValue(ai, out var sims))
                    {
                        candidates[ai] = sims = new double[alias.Tokens.Length];
                        positions[ai] = Enumerable.Repeat(-1, alias.Tokens.Length).ToArray();
                    }
                    for (var t = 0; t < alias.Tokens.Length; t++)
                        if (alias.Tokens[t] == key && sim > sims[t]) { sims[t] = sim; positions[ai][t] = qi; }
                }
            }
        }

        foreach (var (ai, sims) in candidates)
        {
            var alias = _aliases[ai];
            var used = positions[ai].Where(p => p >= 0).Distinct().OrderBy(p => p).ToArray();
            if (used.Length == 0) continue;
            var sum = sims.Sum();
            // The span the name covers in the question; words inside it that the name does not explain count against it.
            var span = Enumerable.Range(used[0], used[^1] - used[0] + 1).Count(p => used.Contains(p) || !skip[p] && !AssistantText.Stop.Contains(stems[p]));
            var precision = sum / Math.Max(span, 1);
            var recall = sum / alias.Tokens.Length;
            var score = Math.Round(0.7 * Math.Min(1, precision) + 0.3 * recall, 4);
            if (score < minScore) continue;
            // Of one entry's aliases keep the best, and on a tie the one that explains more of the question.
            if (results.TryGetValue(alias.Entry, out var prev) && (prev.Score > score || prev.Score == score && prev.Positions.Length >= used.Length)) continue;
            var e = _entries[alias.Entry];
            results[alias.Entry] = new EntityMatch(e.Kind, e.Key, e.Label, score, used, e.Extra, Exact: sims.Count(x => x >= 1.0), Recall: Math.Round(recall, 4));
        }

        return results.Values
            .OrderByDescending(m => m.Score)
            .ThenByDescending(m => m.Positions.Length)
            .ThenBy(m => m.Label.Length)
            .Take(take)
            .ToList();
    }

    /// <summary>Index keys alike enough to a query token, with how alike they are.</summary>
    private IEnumerable<(string Key, double Sim)> Keys(string q)
    {
        if (_byToken.ContainsKey(q)) yield return (q, 1.0);
        if (q.Length < 4 || !_keysByFirst.TryGetValue(q[0], out var keys)) yield break;
        foreach (var k in keys)
        {
            if (k == q) continue;
            var s = AssistantText.Similarity(q, k);
            if (s > 0) yield return (k, s);
        }
    }
}
