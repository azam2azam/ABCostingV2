using System.Globalization;
using System.Text;

namespace Verita.Application.Assistant;

/// <summary>
/// Text handling for the rule-based assistant: normalising, tokenising, a light English stemmer
/// and fuzzy token comparison. Deterministic and allocation-light; no model, no inference.
/// </summary>
public static class AssistantText
{
    /// <summary>Words that carry no meaning for matching. Kept short on purpose: "by", "for" and
    /// "and" are not here because the interpreter reads them as markers.</summary>
    public static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "am", "do", "does", "did", "of", "in", "on", "at", "to",
        "me", "my", "we", "our", "us", "you", "your", "i", "it's", "its", "please", "pls", "can", "could", "would", "will",
        "show", "tell", "give", "list", "get", "find", "see", "view", "display", "with", "from", "which", "what", "whats",
        "there", "any", "some", "all", "this", "that", "these", "those", "it", "them", "about", "s", "hi", "hello",
        "want", "need", "know", "like", "let", "lets", "now", "currently", "current", "one", "ones", "much", "many", "how",
        "for", "and", "by", "per", "or", "vs", "versus", "than", "then", "so", "just", "also", "out", "up", "into", "have", "has",
    };

    public static string Normalise(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var b = new StringBuilder(d.Length);
        var space = true;
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch))
            {
                b.Append(char.ToLowerInvariant(ch));
                space = false;
            }
            else if (ch is '\'' or '’')
            {
                // "what's" -> "whats", "don't" -> "dont"
            }
            else if (!space)
            {
                b.Append(' ');
                space = true;
            }
        }
        return b.ToString().TrimEnd();
    }

    public static string[] Tokens(string? s) => Normalise(s).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Letters and digits only: "NSH-RYD" and "nsh ryd" both become "nshryd".</summary>
    public static string Compact(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var b = new StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) b.Append(char.ToLowerInvariant(ch));
        return b.ToString();
    }

    /// <summary>A deliberately small stemmer: plurals and a few suffixes that matter for this domain.</summary>
    public static string Stem(string t)
    {
        if (t.Length <= 3 || t.Any(char.IsDigit)) return t;
        if (t.EndsWith("ies") && t.Length > 4) return t[..^3] + "y";
        if (t.EndsWith("sses")) return t[..^2];
        if (t.EndsWith("ches") || t.EndsWith("shes") || t.EndsWith("xes")) return t[..^2];
        if (t.EndsWith("ss") || t.Length > 5 && (t.EndsWith("us") || t.EndsWith("is"))) return t;
        if (t.EndsWith('s')) return t[..^1];
        return t;
    }

    public static string[] Stems(string? s) => Tokens(s).Select(Stem).ToArray();

    /// <summary>
    /// Edit distance with adjacent transpositions (optimal string alignment), giving up
    /// (returning max + 1) once every path exceeds <paramref name="max"/>.
    /// </summary>
    public static int Distance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        if (a == b) return 0;
        var prev2 = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) cur[j] = Math.Min(cur[j], prev2[j - 2] + 1);
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin > max) return max + 1;
            (prev2, prev, cur) = (prev, cur, prev2);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// How alike two stemmed tokens are, 0 to 1. Exact is 1; a long prefix ("cardio" for
    /// "cardiology") 0.9; one typo in a word of five letters or more 0.85; two typos in a word of
    /// eight or more 0.75. Short words must match exactly, or "ICU" would match "ICD".
    /// </summary>
    public static double Similarity(string query, string candidate)
    {
        if (query == candidate) return 1.0;
        if (query.Length >= 4 && candidate.StartsWith(query, StringComparison.Ordinal) && query.Length >= candidate.Length * 0.6) return 0.9;
        if (query.Length < 5 || candidate.Length < 5 || query.Any(char.IsDigit) || candidate.Any(char.IsDigit)) return 0;
        var max = query.Length >= 8 && candidate.Length >= 8 ? 2 : 1;
        var d = Distance(query, candidate, max);
        if (d > max) return 0;
        return d switch { 1 => 0.85, 2 => 0.75, _ => 0 };
    }

    public static string Money(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);
    public static string Number(decimal v) => v.ToString(v == Math.Round(v) ? "N0" : "N2", CultureInfo.InvariantCulture);
    public static string Pct(decimal v) => v.ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
