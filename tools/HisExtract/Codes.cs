namespace HisExtract;

/// <summary>Stable HIS codes from SetupID/ProjectID and HIS keys. VERITA Hospital.Code is chosen on import.</summary>
public sealed class Codes(Settings s)
{
    public string Hospital => s.H;
    public string UnmappedDept => $"{s.H}.UNMAPPED";
    public string UnmappedCc => $"{s.H}.CC-UNMAPPED";
    public string UnmappedSl => "SL-UNMAPPED";
    public string SelfPayer => "PAY-SELF";

    public string Facility(object id) => $"{s.H}.FAC-{id}";
    public string ServiceLine(object groupId) => $"SL-{groupId}";
    public string ClinicDept(object id) => $"{s.H}.CLN-{id}";
    public string WardDept(object id) => $"{s.H}.NS-{id}";
    public string OtDept(object id) => $"{s.H}.OT-{id}";
    public string LabDept(object id) => $"{s.H}.LAB-{id}";
    public string ClinicCc(object id) => $"{s.H}.CC-CLN-{id}";
    public string WardCc(object id) => $"{s.H}.CC-NS-{id}";
    public string OtCc(object id) => $"{s.H}.CC-OT-{id}";
    public string LabCc(object id) => $"{s.H}.CC-LAB-{id}";
    public string OracleCc(string costCenterId) => $"{s.H}.ORA-{costCenterId.Trim()}";
    public string Procedure(string procedureId) => $"{s.H}.{procedureId.Trim()}";
    public string Provider(object doctorId) => $"{s.H}.DOC-{doctorId}";
    public string Payer(object companyId) => $"PAY-{companyId}";
    public string OpEncounter(object appointmentNo) => $"{s.H}-OP-{appointmentNo}";
    public string IpEncounter(object admissionNo) => $"{s.H}-IP-{admissionNo}";
    public string ErEncounter(object assessmentNo) => $"{s.H}-ER-{assessmentNo}";

    /// <summary>HIS PatientID as MRN — never PatientUID, never a SetupID/ProjectID token.</summary>
    public string Mrn(object patientId)
    {
        var id = Convert.ToInt64(patientId, System.Globalization.CultureInfo.InvariantCulture);
        return id > 0 ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    }

    public string DeptFromClinic(object? clinicId) =>
        clinicId is null || IsEmpty(clinicId) ? UnmappedDept : ClinicDept(clinicId);

    public string CcFromClinic(object? clinicId) =>
        clinicId is null || IsEmpty(clinicId) ? UnmappedCc : ClinicCc(clinicId);

    public string CcFromInvoice(string? hisCostCenterId, object? clinicId)
    {
        if (!string.IsNullOrWhiteSpace(hisCostCenterId)) return OracleCc(hisCostCenterId);
        return CcFromClinic(clinicId);
    }

    public string PayerOrSelf(object? companyId) =>
        companyId is null || IsEmpty(companyId) ? SelfPayer : Payer(companyId);

    public string ProviderOrEmpty(object? doctorId) =>
        doctorId is null || IsEmpty(doctorId) ? "" : Provider(doctorId);

    private static bool IsEmpty(object v) =>
        v is DBNull || Convert.ToInt64(v) <= 0;
}
