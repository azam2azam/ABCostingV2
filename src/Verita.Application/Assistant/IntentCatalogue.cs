using System.Text.Json;

namespace Verita.Application.Assistant;

public sealed record IntentDef(
    string Id, string Label, string Permission, string Path,
    Dictionary<string, double> Keywords, Dictionary<string, double> Phrases, Dictionary<string, double> Entities,
    string[] Required, string[] Uses, bool Drilldown, bool Compare, string[] Examples, string[] Suggestions, string[]? RequiresAny = null, string? Dominant = null)
{
    /// <summary>
    /// An entity kind that, named in full alongside this intent's own wording, settles the
    /// question: "what is a variance" is a definition even though "variance" is also a report.
    /// </summary>
    public string? DominantKind => Dominant;
    /// <summary>Words (or phrases) at least one of which must be present, or the intent scores at most 0.45.</summary>
    public string[] Requires => RequiresAny ?? [];

    /// <summary>Keywords after stemming, so "variances" and "variance" are one keyword.</summary>
    public Dictionary<string, double> Stems { get; init; } = [];
    /// <summary>Phrases after normalising, padded with spaces so they match whole words only.</summary>
    public Dictionary<string, double> NormalPhrases { get; init; } = [];
}

/// <summary>The intents, read once from the embedded intents.json.</summary>
public sealed class IntentCatalogue
{
    private sealed record File(IntentDef[] Intents);

    public IReadOnlyList<IntentDef> Intents { get; }
    private readonly Dictionary<string, IntentDef> _byId;
    /// <summary>Every stemmed keyword of every intent: words that say what is asked, not what it is about.</summary>
    public HashSet<string> AllKeywords { get; }

    public IntentCatalogue(IEnumerable<IntentDef> intents)
    {
        Intents = intents.Select(i => i with
        {
            Stems = i.Keywords.GroupBy(k => AssistantText.Stem(AssistantText.Normalise(k.Key))).ToDictionary(g => g.Key, g => g.Max(x => x.Value)),
            NormalPhrases = i.Phrases.ToDictionary(p => " " + AssistantText.Normalise(p.Key) + " ", p => p.Value),
        }).ToList();
        _byId = Intents.ToDictionary(i => i.Id);
        AllKeywords = Intents.SelectMany(i => i.Stems.Keys).ToHashSet();
    }

    public IntentDef? Get(string? id) => id is not null && _byId.TryGetValue(id, out var d) ? d : null;

    private static readonly Lazy<IntentCatalogue> _default = new(() =>
    {
        using var s = typeof(IntentCatalogue).Assembly.GetManifestResourceStream("Verita.Application.Assistant.intents.json")
                      ?? throw new InvalidOperationException("The assistant's intents.json is not embedded.");
        var file = JsonSerializer.Deserialize<File>(s, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                   ?? throw new InvalidOperationException("intents.json is empty.");
        return new IntentCatalogue(file.Intents);
    });

    public static IntentCatalogue Default => _default.Value;
}
