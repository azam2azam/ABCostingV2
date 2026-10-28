namespace HisExtract;

/// <summary>
/// Maps HIS clinic → physician role resource. One resource per role, not per doctor.
/// Active doctors should be remapped from majority encounter department after facts load.
/// </summary>
internal static class PhysicianRoles
{
    internal sealed record Role(string Token, string Name, string ClinicCcId, bool ErStaff);

    internal static readonly Role[] All =
    [
        new("MED",    "Internal Medicine Consultant", "1",  false),
        new("ER",     "Emergency Medicine Consultant", "10", true),
        new("PED",    "Paediatrician",                 "2",  false),
        new("OBGYN",  "Obstetrician & Gynaecologist",  "3",  false),
        new("DENT",   "Dentist",                       "17", false),
        new("SURG",   "General Surgeon",               "4",  false),
        new("ANES",   "Consultant Anaesthetist",       "24", false),
        new("CARD",   "Cardiologist",                  "21", false),
        new("RAD",    "Consultant Radiologist",        "19", false),
        new("PHYSIO", "Physiotherapy Physician",       "23", false),
        new("ENT",    "ENT Surgeon",                   "7",  false),
        new("ICU",    "Intensivist",                   "80", false),
        new("ORTHO",  "Orthopaedic Surgeon",           "12", false),
        new("URO",    "Urologist",                     "8",  false),
        new("DERM",   "Dermatologist",                 "5",  false),
        new("OTHER",  "Physician (other specialties)", "",   false),
    ];

    internal static string ResourceCode(Codes c, string token) => $"{c.Hospital}.RES-MD-{token}";

    internal static string CostCentre(Codes c, Role role) =>
        role.Token == "OTHER" || string.IsNullOrEmpty(role.ClinicCcId) ? c.UnmappedCc : c.ClinicCc(role.ClinicCcId);

    internal static string TokenFromClinic(int? clinicId)
    {
        if (clinicId is null or <= 0) return "OTHER";
        return clinicId.Value switch
        {
            10 or 70 => "ER",
            5 or 37 => "DERM",
            24 => "ANES",
            21 or 258 or 605 => "CARD",
            19 or 117 => "RAD",
            23 => "PHYSIO",
            7 or 66 => "ENT",
            80 or 81 or 82 or 250 => "ICU",
            12 or 61 => "ORTHO",
            8 or 46 => "URO",
            17 or 72 or 102 or 131 => "DENT",
            3 or 63 or 71 or 40 or 602 => "OBGYN",
            2 or 48 or 64 or 257 => "PED",
            4 or 65 or 108 or 16 or 18 or 27 or 35 => "SURG",
            1 or 25 or 26 or 14 or 62 or 9 or 11 or 106 or 107 or 30 or 36 or 38 or 49 or 501 => "MED",
            _ => "OTHER",
        };
    }
}
