using System.Globalization;

namespace HisExtract;

public static class Maps
{
    public static string Sex(object? gender) => Convert.ToInt32(gender ?? 0) switch
    {
        2 => "F",
        0 => "M",
        1 => "M",
        _ => "M",
    };

    public static string Segment(int birthYear, bool pregnant, DateTime asOf)
    {
        if (pregnant) return "MATERNITY";
        var age = asOf.Year - birthYear;
        if (age < 18) return "PEDIATRIC";
        if (age >= 65) return "GERIATRIC";
        return "ADULT";
    }

    public static string PayerType(int companyType, string name)
    {
        var n = name.ToUpperInvariant();
        if (n.Contains("CASH") || n.Contains("SELF PAY") || n.Contains("SELFPAY") || n.Contains("CASH PATIENT"))
            return "SELF_PAY";
        if (n.Contains("MOH") || n.Contains("MINISTRY") || n.Contains("GOSI") || n.Contains("CHI ") || n.Contains("NPHIES"))
            return "GOVERNMENT";
        return companyType switch
        {
            2 => "GOVERNMENT",
            3 => "CORPORATE",
            4 => "SELF_PAY",
            _ => "INSURANCE",
        };
    }

    public static string ProcType(bool surgery, bool lab, string category, string name)
    {
        if (surgery) return "SURGERY";
        var t = (category + " " + name).ToLowerInvariant();
        if (lab || t.Contains("lab") || t.Contains("patholog") || t.Contains("phlebot")) return "LAB";
        if (t.Contains("radio") || t.Contains("imag") || t.Contains("x-ray") || t.Contains("xray")
            || t.Contains("mri") || t.Contains(" ct") || t.Contains("ct ") || t.Contains("ultrasound")
            || t.Contains("ultraso") || t.Contains("mammog") || t.Contains("fluoros")) return "IMAGING";
        if (t.Contains("consult") || t.Contains("clinic visit") || t.Contains("follow up") || t.Contains("follow-up")
            || t.Contains("followup") || t.Contains("new visit")) return "CONSULT";
        if (t.Contains("bed day") || t.Contains("room and board") || t.Contains("room charge")
            || t.Contains("ward day") || t.Contains("icu day") || t.Contains("accommodation")) return "BED_DAY";
        if (t.Contains("emerg") || t.Contains("er visit") || t.Contains("casualty") || t.Contains("triage")) return "ER_VISIT";
        if (t.Contains("physio") || t.Contains("therap") || t.Contains("rehab") || t.Contains("occupational")) return "THERAPY";
        return "PROCEDURE";
    }

    public static string DeptType(string kind) => kind switch
    {
        "LAB" => "ANCILLARY",
        "ADMIN" => "ADMIN",
        _ => "CLINICAL",
    };

    public static string CcType(string kind) => kind switch
    {
        "LAB" => "ANCILLARY",
        "ADMIN" => "OVERHEAD",
        _ => "PRODUCTION",
    };

    public static string Money(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    public static string Dec(decimal v) => v.ToString(CultureInfo.InvariantCulture);
    public static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
    public static string Bool(bool v) => v ? "true" : "false";
    public static string Date(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string DateTime(DateTime d) => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static decimal Round4(double v) =>
        decimal.Round(double.IsNaN(v) || double.IsInfinity(v) ? 0m : (decimal)v, 4, MidpointRounding.AwayFromZero);

    public static string MaskId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var t = raw.Trim();
        if (t.Length < 4) return "••••";
        return t[0] + new string('•', Math.Min(8, t.Length - 2)) + t[^1];
    }

    public static string FullName(string? first, string? middle, string? last) =>
        string.Join(" ", new[] { first, middle, last }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();

    public static DateTime Combine(DateTime date, string? time)
    {
        if (string.IsNullOrWhiteSpace(time)) return date;
        var t = time.Trim();
        if (TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var ts)) return date.Date.Add(ts);
        if (System.DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return date.Date.Add(dt.TimeOfDay);
        return date;
    }
}
