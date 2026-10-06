using System.Globalization;
using Verita.Domain;

namespace Verita.Engine;

/// <summary>Number formatting for calculation expressions and narratives (NFR-20: English, UK).</summary>
internal static class Fmt
{
    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");

    public static string N(decimal v, int decimals = 2) =>
        Math.Round(v, decimals, MidpointRounding.AwayFromZero).ToString("N" + decimals, Uk);

    public static string Sar(decimal v, int decimals = 0) => "SAR " + N(v, decimals);

    /// <summary>A rate shown at six decimal places, trailing zeros trimmed.</summary>
    public static string R(decimal v) => Money.R6(v).ToString("0.######", CultureInfo.InvariantCulture);

    public static string P(decimal v, int decimals = 1) => Math.Round(v, decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals, CultureInfo.InvariantCulture);

    public static string Plural(int n, string word, string? plural = null) => n == 1 ? word : plural ?? word + "s";

    public static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
