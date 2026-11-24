namespace HisExtract;

/// <summary>
/// The parts of a day's facts, each with a version. A synced day records the versions it holds
/// (HisSyncDay.Sections, e.g. "encounters:2,billing:1"); "Sync again" reads from HIS only the parts a day lacks or holds
/// at an older version. Raise a part's version when its extract changes in a way worth reading again, and add a part
/// when the extract gains a new kind of data.
/// </summary>
public static class FactSections
{
    /// <summary>Patients and episodes (32_Patient, 33_Encounter). v2: OR flag ignores cancelled theatre bookings.</summary>
    public const string Encounters = "encounters";
    /// <summary>Services, revenue, the episodes' refunds, package items and (with --claims) claims.</summary>
    public const string Billing = "billing";
    /// <summary>Consumption, staff time and bed transfers.</summary>
    public const string Consumption = "consumption";
    /// <summary>Refunds dated in the day, whatever their episode (the day's last step).</summary>
    public const string Refunds = "refunds";
    /// <summary>INP_AdmissionRequest raised, edited or admitted in the day (46_AdmissionRequest). Added in v1.</summary>
    public const string AdmissionRequests = "admissionRequests";

    /// <summary>What the extract reads today.</summary>
    public static readonly IReadOnlyDictionary<string, int> Current = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [Encounters] = 2, [Billing] = 1, [Consumption] = 1, [Refunds] = 1, [AdmissionRequests] = 1,
    };

    /// <summary>What a day synced before versions were recorded holds.</summary>
    public static readonly IReadOnlyDictionary<string, int> Baseline = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [Encounters] = 1, [Billing] = 1, [Consumption] = 1, [Refunds] = 1,
    };

    /// <summary>Parts read batch by batch over the day's episodes; the others are read once for the whole day.</summary>
    public static readonly IReadOnlySet<string> PerEpisode = new HashSet<string>(StringComparer.Ordinal) { Encounters, Billing, Consumption };

    public static Dictionary<string, int> Parse(string? text)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = item.IndexOf(':');
            if (i > 0 && int.TryParse(item[(i + 1)..], out var v)) map[item[..i]] = v;
            else if (i < 0) map[item] = Current.GetValueOrDefault(item);
        }
        return map;
    }

    public static string Format(IReadOnlyDictionary<string, int> map) =>
        string.Join(",", Current.Keys.Where(map.ContainsKey).Select(k => $"{k}:{map[k]}"));

    /// <summary>The parts to read so a day holding <paramref name="held"/> is up to date.</summary>
    public static Dictionary<string, int> Missing(IReadOnlyDictionary<string, int> held) =>
        Current.Where(c => held.GetValueOrDefault(c.Key) < c.Value).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);

    /// <summary>What a day holds after reading <paramref name="read"/> on top of <paramref name="held"/>.</summary>
    public static Dictionary<string, int> Merge(IReadOnlyDictionary<string, int> held, IReadOnlyDictionary<string, int> read)
    {
        var map = new Dictionary<string, int>(held, StringComparer.Ordinal);
        foreach (var (k, v) in read) map[k] = Math.Max(map.GetValueOrDefault(k), v);
        return map;
    }

    public static string Describe(IEnumerable<string> sections) => string.Join(", ", sections.Select(s => s switch
    {
        Encounters => "episodes", Billing => "billing", Consumption => "consumption", Refunds => "refunds",
        AdmissionRequests => "admission requests", _ => s,
    }));
}
