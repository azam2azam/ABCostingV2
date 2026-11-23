namespace Verita.Domain;

/// <summary>
/// Where an episode is reported: IPD, OR, OPD or ER. Each episode sits in exactly one, so the four add up to the
/// hospital total. OR is an IP episode with a theatre booking that went ahead (<c>Encounter.IsSurgical</c>);
/// every other IP episode is IPD, ER stays ER, and everything else (OP, DAYCASE) is OPD.
/// </summary>
public static class CareArea
{
    public const string Ipd = "IPD";
    public const string Or = "OR";
    public const string Opd = "OPD";
    public const string Er = "ER";

    /// <summary>Report order.</summary>
    public static readonly string[] All = [Ipd, Or, Opd, Er];

    public static string Of(string encType, bool isSurgical) => encType switch
    {
        "IP" => isSurgical ? Or : Ipd,
        "ER" => Er,
        _ => Opd,
    };

    public static bool IsKnown(string? area) => area is Ipd or Or or Opd or Er;

    public static string Label(string area) => area switch
    {
        Ipd => "Inpatient (IPD)",
        Or => "Operating room (OR)",
        Opd => "Outpatient (OPD)",
        Er => "Emergency (ER)",
        _ => area,
    };
}
