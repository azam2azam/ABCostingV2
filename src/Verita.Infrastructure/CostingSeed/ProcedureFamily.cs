namespace Verita.Infrastructure.CostingSeed;

/// <summary>
/// Charge-master → activity family (docs/seed/HIS-ACTIVITY-RECIPE-SEED.md §4.3). Exclusive CASE, first match.
/// </summary>
public static class ProcedureFamily
{
    public const string Skip = "SKIP";

    public static string Of(string name, string category, string procType)
    {
        var n = name ?? "";
        var cat = category ?? "";
        var pt = procType ?? "";

        if (Contains(cat, "FOOD & BEVERAGE") || Contains(cat, "LAUNDRY")) return Skip;
        if (Contains(n, "TRANSFUS") || Contains(n, "PACKED RED") || Contains(n, "PRBC")) return "TRANSFUSE";
        if (IsDental(cat, pt) && Contains(n, "IMPLANT")) return "DENTIMP";
        if (IsDental(cat, pt)) return "DENTAL";
        if (Contains(n, "CATH") || Contains(n, "ANGIO") || Contains(n, "PCI")) return "CATH";
        if (Contains(n, "DELIVER") || Contains(n, "C-SECTION") || Contains(n, "CESAREAN") || Contains(n, "LABOUR") || Contains(n, "LABOR"))
            return "DELIVERY";
        if (Eq(pt, "LAB") || Contains(cat, "LABORATORY")) return "LAB";
        if (Eq(pt, "CONSULT") || Contains(cat, "CONSULTATION")) return "CONSULT";
        if (Eq(pt, "THERAPY") || Contains(cat, "PHYSIOTHERAPY")) return "PHYSIO";
        if (IsImaging(cat, pt))
        {
            var mri = Contains(n, "MRI") || Contains(cat, "MRI");
            if (mri && Contains(n, "CONTRAST")) return "MRI-C";
            if (mri) return "MRI";
            if (Contains(n, "CT") || Contains(cat, "CT")) return "CT";
            if (Contains(n, "ULTRASOUND") || Contains(n, "USG") || Contains(n, "ECHO")) return "USG";
            return "XRAY";
        }
        if (Eq(pt, "SURGERY")) return "SURGERY";
        if (Contains(n, "ECHO") || Contains(n, "ULTRASOUND") || Contains(n, "USG")) return "USG";
        if (Contains(n, "NICU") || Contains(n, "NEONATAL INTENSIVE")) return "NICU";
        if (Contains(n, "ICU") || Contains(n, "INTENSIVE CARE")) return "ICU";
        if (Contains(n, "ANESTHESIA MEDICATION") || Contains(n, "ANAESTHESIA MEDICATION")) return "MED";
        if (Contains(n, "ANAESTHESIOLOGIST") || Contains(n, "ANESTHESIOLOGIST")) return "ANES";
        if (Contains(n, "OR CHARGE") || Contains(n, "OPERATING ROOM") || Contains(n, "THEATRE")
            || Contains(n, "RECOVERY ROOM"))
            return "SURGERY";
        if (Eq(pt, "ER_VISIT") || Eq(pt, "ER")) return "ER";
        if (Eq(pt, "BED_DAY") || Contains(n, "PRIVATE ROOM") || Contains(n, "BED DAY") || Contains(n, "BED-DAY"))
            return "WARD";
        if (Contains(cat, "NURSING SERVICES")) return "WARD";
        if (Contains(cat, "MEDICINE")) return "MED";
        if (Contains(cat, "DISPOSABLE") || Contains(n, "CONSUMABLE")) return "DISP";
        return "PROC";
    }

    public static IReadOnlyList<string> Activities(string family) =>
        family switch
        {
            "CONSULT" => ["CONSULT"],
            "ER" => ["TRIAGE", "ER-ASSESS", "NURSE-ER"],
            "WARD" => ["NURSE-WARD"],
            "LAB" => ["LAB", "PHLEB"],
            "MRI" => ["MRI-SCAN", "RAD-REPORT"],
            "MRI-C" => ["MRI-SCAN", "MRI-CONTRAST", "RAD-REPORT"],
            "CT" => ["CT-SCAN", "RAD-REPORT"],
            "USG" => ["USG-SCAN", "RAD-REPORT"],
            "XRAY" => ["XRAY", "RAD-REPORT"],
            "PHYSIO" => ["PHYSIO"],
            "SURGERY" => ["SURGERY", "ANESTHESIA", "OR-SETUP", "RECOVERY"],
            "ICU" => ["NURSE-ICU"],
            "NICU" => ["NURSE-NICU"],
            "ANES" => ["ANESTHESIA"],
            "CATH" => ["CATH-PROC"],
            "MED" => ["PHARM-DISPENSE"],
            "DISP" => ["WOUND-CARE"],
            "DENTAL" => ["DENT-VISIT"],
            "DENTIMP" => ["DENT-IMPLANT"],
            "TRANSFUSE" => ["TRANSFUSE"],
            "DELIVERY" => ["DELIVERY"],
            "PROC" => ["PROC"],
            _ => [],
        };

    private static bool IsDental(string cat, string pt) =>
        Contains(cat, "DENT") || Eq(pt, "DENTAL") || Eq(pt, "DENT");

    private static bool IsImaging(string cat, string pt) =>
        Eq(pt, "IMAGING") || Eq(pt, "RADIOLOGY")
        || Contains(cat, "IMAGING") || Contains(cat, "RADIOLOGY") || Contains(cat, "RADIOLOGY SERVICES");

    private static bool Contains(string s, string needle) =>
        s.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool Eq(string s, string value) =>
        s.Equals(value, StringComparison.OrdinalIgnoreCase);
}
