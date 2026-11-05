using Verita.Infrastructure.DataHub;

namespace Verita.Engine.Tests;

public class HisImportPackTests
{
    [Fact]
    public void Remap_his_token_and_prefixed_codes_to_verita_hospital()
    {
        const string token = "010266-15";
        Assert.Equal("RYN-UAT-ABC", HisImportPack.RemapValue("010266-15", token, "RYN-UAT-ABC"));
        Assert.Equal("RYN-UAT-ABC.CLN-1", HisImportPack.RemapValue("010266-15.CLN-1", token, "RYN-UAT-ABC"));
        Assert.Equal("RYN-UAT-ABC-OP-9", HisImportPack.RemapValue("010266-15-OP-9", token, "RYN-UAT-ABC"));
        Assert.Equal("SL-11", HisImportPack.RemapValue("SL-11", token, "RYN-UAT-ABC"));
        Assert.Equal("Rayan", HisImportPack.RemapValue("Rayan", token, "RYN-UAT-ABC"));
        Assert.Equal("012010", HisImportPack.RemapValue("012010", token, "RYN-UAT-ABC"));
        Assert.Equal("A-1234", HisImportPack.RemapValue("A-1234", token, "RYN-UAT-ABC"));
        Assert.Equal("CLN-1", HisImportPack.RemapOrgCode("010266-15.CLN-1", token));
        Assert.Equal("FAC-1", HisImportPack.RemapOrgCode("FAC-1", token));
        Assert.Equal("ORA-11106", HisImportPack.RemapOrgCode("010266-15.ORA-11106", token));
        Assert.Equal("CC-UNMAPPED", HisImportPack.RemapOrgCode("CC-UNMAPPED", token));
        Assert.Equal("ORA-11106", HisImportPack.RemapOrgCode("RYN-UAT-ABC.ORA-11106", token, "RYN-UAT-ABC"));
        Assert.Equal("ORA-11106", HisImportPack.RemapOrgCode("ORA-11106", token, "RYN-UAT-ABC"));
        Assert.Equal("ORA-11106", HisImportPack.LocalOrgCode("SWD-AGLSNR.ORA-11106"));
    }

    [Fact]
    public void Doctor_clinic_file_loads_after_its_doctor_and_keeps_a_clinic_missing_from_the_catalogue()
    {
        Assert.Equal("ProviderClinic", HisImportPack.SpecFromFileName("15_ProviderClinic.csv"));
        Assert.True(Array.IndexOf(EntitySpecs.All, EntitySpecs.Find("Provider")) < Array.IndexOf(EntitySpecs.All, EntitySpecs.Find("ProviderClinic")));
        Assert.Equal("UNMAPPED", HisImportPack.UnmappedFallback("ProviderClinic", "departmentCode"));
    }

    [Fact]
    public void Encounters_with_nothing_billed_are_not_loaded()
    {
        static Dictionary<string, string> Enc(string no, string status, string arrived) => new(StringComparer.OrdinalIgnoreCase)
            { ["encounterNo"] = no, ["hisStatus"] = status, ["hisArrivedAt"] = arrived };
        static Dictionary<string, string> Line(string no) => new(StringComparer.OrdinalIgnoreCase) { ["encounterNo"] = no };
        var encounters = new HisImportPack.ParsedFile("33_Encounter.csv", EntitySpecs.Find("Encounter"),
        [
            Enc("cancelled", "4", ""), Enc("arrived-unbilled", "2", "2026-11-01"), Enc("service", "2", "2026-11-01"), Enc("revenue", "6", ""),
            Enc("activity-only", "2", "2026-11-01"), Enc("refund-only", "6", ""), Enc("not-from-his", "", ""),
        ]);
        var services = new HisImportPack.ParsedFile("34_EncounterService.csv", EntitySpecs.Find("EncounterService"), [Line("service")]);
        var revenue = new HisImportPack.ParsedFile("36_RevenueTransaction.csv", EntitySpecs.Find("RevenueTransaction"), [Line("revenue")]);
        var activity = new HisImportPack.ParsedFile("35_ActivityTransaction.csv", EntitySpecs.Find("ActivityTransaction"), [Line("activity-only")]);
        var refunds = new HisImportPack.ParsedFile("37_RefundTransaction.csv", EntitySpecs.Find("RefundTransaction"), [Line("refund-only")]);

        var (files, skipped) = HisImportPack.SkipUnbilledEncounters([encounters, services, activity, revenue, refunds]);
        Assert.Equal(4, skipped);
        Assert.Equal(["service", "revenue", "not-from-his"], files.Single(f => f.Spec!.Name == "Encounter").Rows.Select(r => r["encounterNo"]));

        // Encounters sent alone: the billing is not there to judge by, so nothing is left out.
        var (alone, none) = HisImportPack.SkipUnbilledEncounters([encounters]);
        Assert.Equal(0, none);
        Assert.Equal(7, alone.Single().Rows.Count);

        // Every row skipped: the empty file does not reach the importer.
        var bookings = new HisImportPack.ParsedFile("33_Encounter.csv", EntitySpecs.Find("Encounter"), [Enc("cancelled", "4", "")]);
        Assert.DoesNotContain(HisImportPack.SkipUnbilledEncounters([bookings, services]).Files, f => f.Spec!.Name == "Encounter");
    }

    [Fact]
    public void BindHospital_rewrites_hospital_row_and_codes()
    {
        var hospital = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["setupId"] = "010266", ["projectId"] = "15", ["code"] = "010266-15", ["name"] = "Rayan", ["bedCount"] = "1421",
        };
        HisImportPack.BindHospital(hospital, "Hospital", "RYN-UAT-ABC", "010266", "15",
            "Rayyan UAT ABC", "Riyadh", "SAR", true);
        Assert.Equal("RYN-UAT-ABC", hospital["code"]);
        Assert.Equal("Rayyan UAT ABC", hospital["name"]);
        Assert.Equal("Riyadh", hospital["city"]);
        Assert.Equal("010266", hospital["hisSetupId"]);
        Assert.Equal("15", hospital["hisProjectId"]);
        Assert.Equal("", hospital["bedCount"]);

        var facility = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["setupId"] = "010266", ["projectId"] = "15", ["hospitalCode"] = "010266-15", ["code"] = "FAC-1", ["name"] = "Main",
        };
        HisImportPack.BindHospital(facility, "Facility", "RYN-UAT-ABC", "010266", "15");
        Assert.Equal("RYN-UAT-ABC", facility["hospitalCode"]);
        Assert.Equal("FAC-1", facility["code"]);
        Assert.Equal("Main", facility["name"]);

        var legacy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hospitalCode"] = "010266-15", ["code"] = "010266-15.ORA-11106",
            ["departmentCode"] = "010266-15.UNMAPPED", ["facilityCode"] = "010266-15.FAC-2",
        };
        HisImportPack.BindHospital(legacy, "CostCenter", "RYN-UAT-ABC", "010266", "15");
        Assert.Equal("ORA-11106", legacy["code"]);
        Assert.Equal("UNMAPPED", legacy["departmentCode"]);
        Assert.Equal("FAC-2", legacy["facilityCode"]);

        var alreadyBound = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hospitalCode"] = "RYN-UAT-ABC", ["code"] = "RYN-UAT-ABC.ORA-11106",
            ["departmentCode"] = "RYN-UAT-ABC.UNMAPPED", ["costCentreCode"] = "RYN-UAT-ABC.ORA-11106",
        };
        HisImportPack.BindHospital(alreadyBound, "CostCenter", "RYN-UAT-ABC", "010266", "15");
        Assert.Equal("ORA-11106", alreadyBound["code"]);
        Assert.Equal("UNMAPPED", alreadyBound["departmentCode"]);
        Assert.Equal("ORA-11106", alreadyBound["costCentreCode"]);
    }

    [Fact]
    public void BindHospital_does_not_rewrite_his_mrn()
    {
        var patient = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hospitalCode"] = "010266-15", ["mrn"] = "1111",
        };
        HisImportPack.BindHospital(patient, "Patient", "RYN-UAT-V2", "010266", "15");
        Assert.Equal("RYN-UAT-V2", patient["hospitalCode"]);
        Assert.Equal("1111", patient["mrn"]);

        var encounter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hospitalCode"] = "010266-15", ["patientMrn"] = "1111",
            ["encounterNo"] = "010266-15-OP-9", ["providerCode"] = "010266-15.DOC-918",
        };
        HisImportPack.BindHospital(encounter, "Encounter", "RYN-UAT-V2", "010266", "15");
        Assert.Equal("RYN-UAT-V2", encounter["hospitalCode"]);
        Assert.Equal("1111", encounter["patientMrn"]);
        Assert.Equal("RYN-UAT-V2-OP-9", encounter["encounterNo"]);
        Assert.Equal("RYN-UAT-V2.DOC-918", encounter["providerCode"]);

        var txn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["encounterNo"] = "010266-15-OP-9",
            ["activityCode"] = "010266-15.ACT-CONSULT",
            ["resourceCode"] = "010266-15.RES-MD-MED",
            ["costCentreCode"] = "ORA-11106",
            ["sourceRef"] = "APPT-9-CONSULT-MD-MED",
        };
        HisImportPack.BindHospital(txn, "ActivityTransaction", "RYN-UAT-V2", "010266", "15");
        Assert.Equal("RYN-UAT-V2-OP-9", txn["encounterNo"]);
        Assert.Equal("RYN-UAT-V2.ACT-CONSULT", txn["activityCode"]);
        Assert.Equal("RYN-UAT-V2.RES-MD-MED", txn["resourceCode"]);
        Assert.Equal("ORA-11106", txn["costCentreCode"]);
        Assert.Equal("APPT-9-CONSULT-MD-MED", txn["sourceRef"]);

        var leftover = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hospitalCode"] = "010266-15", ["patientMrn"] = "010266-15-1-1111",
        };
        HisImportPack.BindHospital(leftover, "Encounter", "RYN-UAT-V2", "010266", "15");
        Assert.Equal("010266-15-1-1111", leftover["patientMrn"]);
    }

    [Fact]
    public void StripExtra_drops_his_identity_columns()
    {
        var row = HisImportPack.StripExtra(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["setupId"] = "010266", ["projectId"] = "15", ["code"] = "X", ["__row"] = "2",
        });
        Assert.False(row.ContainsKey("setupId"));
        Assert.False(row.ContainsKey("projectId"));
        Assert.Equal("X", row["code"]);
        Assert.Equal("2", row["__row"]);
    }

    [Fact]
    public void Ignored_files_are_manifest_readme_and_log()
    {
        Assert.True(HisImportPack.IsIgnoredFile("00_manifest.csv"));
        Assert.True(HisImportPack.IsIgnoredFile("README.txt"));
        Assert.True(HisImportPack.IsIgnoredFile("extract-log.txt"));
        Assert.False(HisImportPack.IsIgnoredFile("01_Hospital.csv"));
        Assert.Equal("Hospital", HisImportPack.SpecFromFileName("01_Hospital.csv"));
        Assert.Null(HisImportPack.SpecFromFileName("00_manifest.csv"));
        Assert.Equal("EncounterService", HisImportPack.SpecFromFileName("34_EncounterService.csv"));
        Assert.Equal("EncounterService", HisImportPack.SpecFromFileName("34_EncounterService_p2.csv"));
        Assert.Equal("EncounterService", HisImportPack.SpecFromFileName("34_EncounterService_p10.csv"));
        Assert.Equal(1, HisImportPack.PageOf("34_EncounterService.csv"));
        Assert.Equal(2, HisImportPack.PageOf("34_EncounterService_p2.csv"));
        Assert.Equal(10, HisImportPack.PageOf("34_EncounterService_p10.csv"));
    }

    [Fact]
    public void CoalescePages_concatenates_in_page_order_not_last_file_only()
    {
        var p1 = new HisImportPack.ParsedFile("34_EncounterService.csv", EntitySpecs.Find("EncounterService"),
        [
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["sourceRef"] = "a" },
        ]);
        var p10 = new HisImportPack.ParsedFile("34_EncounterService_p10.csv", EntitySpecs.Find("EncounterService"),
        [
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["sourceRef"] = "c" },
        ]);
        var p2 = new HisImportPack.ParsedFile("34_EncounterService_p2.csv", EntitySpecs.Find("EncounterService"),
        [
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["sourceRef"] = "b" },
        ]);
        var merged = HisImportPack.CoalescePages([p10, p1, p2]);
        Assert.Single(merged);
        Assert.Equal(3, merged[0].Rows.Count);
        Assert.Equal(["a", "b", "c"], merged[0].Rows.Select(r => r["sourceRef"]).ToArray());
        Assert.Equal("EncounterService", merged[0].Spec!.Name);
    }

    [Fact]
    public void Identity_from_manifest_row()
    {
        var id = HisImportPack.IdentityFrom([],
        [
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["setupId"] = "010266", ["projectId"] = "15", ["pack"] = "facts",
                ["from"] = "2026-07-01", ["toInclusive"] = "2026-07-31",
            },
        ]);
        Assert.NotNull(id);
        Assert.Equal("010266", id!.SetupId);
        Assert.Equal("15", id.ProjectId);
        Assert.Equal("facts", id.Pack);
        Assert.Equal("2026-07-01", id.From);
        Assert.True(HisImportPack.TryFactRange(id, "HIS-010266-15-2026-07-facts.zip", out var from, out var to));
        Assert.Equal(new DateOnly(2026, 7, 1), from);
        Assert.Equal(new DateOnly(2026, 7, 31), to);
    }

    [Fact]
    public void TryFactRange_reads_month_from_facts_zip_name()
    {
        Assert.True(HisImportPack.TryFactRange(null, "HIS-010266-15-2026-10-facts.zip", out var from, out var to));
        Assert.Equal(new DateOnly(2026, 10, 1), from);
        Assert.Equal(new DateOnly(2026, 10, 31), to);
        Assert.False(HisImportPack.TryFactRange(null, "HIS-010266-15-catalogue.zip", out _, out _));
    }
}
