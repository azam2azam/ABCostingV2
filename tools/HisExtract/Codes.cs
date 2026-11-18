namespace HisExtract;

/// <summary>Stable HIS codes from SetupID/ProjectID and HIS keys. VERITA Hospital.Code is chosen on import.</summary>
public sealed class Codes(Settings s)
{
    public string Hospital => s.H;
    public string UnmappedDept => "UNMAPPED";
    public string UnmappedCc => "CC-UNMAPPED";
    public string UnmappedSl => "SL-UNMAPPED";
    public string SelfPayer => "PAY-SELF";

    public string Facility(object id) => $"FAC-{id}";
    public string ServiceLine(object groupId) => $"SL-{groupId}";
    public string ClinicDept(object id) => $"CLN-{id}";
    public string WardDept(object id) => $"NS-{id}";
    public string OtDept(object id) => $"OT-{id}";
    public string LabDept(object id) => $"LAB-{id}";
    public string ClinicCc(object id) => $"CC-CLN-{id}";
    public string WardCc(object id) => $"CC-NS-{id}";
    public string OtCc(object id) => $"CC-OT-{id}";
    public string LabCc(object id) => $"CC-LAB-{id}";
    public string OracleCc(string costCenterId) => $"ORA-{costCenterId.Trim()}";
    public string Procedure(string procedureId) => $"{s.H}.{procedureId.Trim()}";
    public string Provider(object doctorId) => $"{s.H}.DOC-{doctorId}";
    public string Payer(object companyId) => $"PAY-{companyId}";
    /// <summary>The HIS number alone: the hospital and encounter type are columns of their own.</summary>
    public string OpEncounter(object appointmentNo) => $"{appointmentNo}";
    public string IpEncounter(object admissionNo) => $"{admissionNo}";
    /// <summary>An ER visit billed through an appointment is one encounter, under the appointment's number.</summary>
    public string ErEncounter(object assessmentNo) =>
        _erAppointment.TryGetValue(Convert.ToInt32(assessmentNo, System.Globalization.CultureInfo.InvariantCulture), out var appointment) ? $"{appointment}" : $"{assessmentNo}";
    public void LinkErToAppointment(int assessmentNo, int appointmentNo)
    {
        _erAppointment[assessmentNo] = appointmentNo;
        _erAppointments.Add(appointmentNo);
    }
    public bool IsErAppointment(int appointmentNo) => _erAppointments.Contains(appointmentNo);
    private readonly Dictionary<int, int> _erAppointment = [];
    private readonly HashSet<int> _erAppointments = [];

    /// <summary>MRNs written to 32_Patient.csv. An encounter whose patient is not among them is skipped.</summary>
    public void AddPatient(string mrn) => _patients.Add(mrn);
    public bool HasPatient(string mrn) => _patients.Contains(mrn);
    /// <summary>
    /// Encounters left out of 33_Encounter.csv because their patient is not in the pack (e.g. an in-window
    /// invoice that points at an old appointment of another patient). Their services, revenue, refunds,
    /// package items, consumption, staff time and transfers are left out too, so the import has no orphans.
    /// </summary>
    public void SkipEncounter(string encounterNo) { if (!_written.Contains(encounterNo)) _skipped.Add(encounterNo); }
    public void WroteEncounter(string encounterNo) { _written.Add(encounterNo); _skipped.Remove(encounterNo); }
    /// <summary>True when this row's encounter was skipped, so the row must not be written. Counted once per file and ref.</summary>
    public bool Skip(string? encounterNo, string file, string sourceRef)
    {
        if (encounterNo is null || !_skipped.Contains(encounterNo)) return false;
        _skippedRefs.Add($"{file}|{sourceRef}");
        return true;
    }
    public IReadOnlyCollection<string> SkippedEncounters => _skipped;
    /// <summary>Rows left out because their encounter was skipped, per file.</summary>
    public IEnumerable<(string File, int Rows)> SkippedRows =>
        _skippedRefs.GroupBy(k => k[..k.IndexOf('|')]).Select(g => (g.Key, g.Count())).OrderBy(x => x.Key, StringComparer.Ordinal);
    private readonly HashSet<string> _skippedRefs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _patients = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _skipped = new(StringComparer.OrdinalIgnoreCase);
    public string Activity(string token) => $"{s.H}.ACT-{token}";
    public string Resource(string token) => $"{s.H}.RES-{token}";

    /// <summary>HIS PatientID as MRN — never PatientUID, never a SetupID/ProjectID token.</summary>
    public string Mrn(object patientId)
    {
        var id = Convert.ToInt64(patientId, System.Globalization.CultureInfo.InvariantCulture);
        return id > 0 ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    }

    public string DeptFromClinic(object? clinicId) =>
        clinicId is null || IsEmpty(clinicId) ? UnmappedDept : ClinicDept(clinicId);

    public string CcFromClinic(object? clinicId) => UnmappedCc;

    public string CcFromInvoice(string? hisCostCenterId, object? clinicId)
    {
        if (!string.IsNullOrWhiteSpace(hisCostCenterId)) return OracleCc(hisCostCenterId);
        return UnmappedCc;
    }

    public string PayerOrSelf(object? companyId) =>
        companyId is null || IsEmpty(companyId) ? SelfPayer : Payer(companyId);

    public string ProviderOrEmpty(object? doctorId) =>
        doctorId is null || IsEmpty(doctorId) ? "" : Provider(doctorId);

    private static bool IsEmpty(object v) =>
        v is DBNull || Convert.ToInt64(v) <= 0;
}
