using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Infrastructure.CostingSeed;
using Verita.Infrastructure.Data;

namespace Verita.Engine.Tests;

public class CostingSeedTests
{
    private static HubMatcher.Centre C(string code, string name, bool active = true) =>
        new(Guid.NewGuid(), "H." + code, name, active);

    [Fact]
    public void HubMatcher_picks_exact_clinical_hubs_and_shares_centres()
    {
        var centres = new[]
        {
            C("CC-CLN-1", "INTERNAL MEDICINE"),
            C("CC-CLN-9", "INTERNAL MEDICINE EXT"),
            C("CC-NS-1", "ER"),
            C("CC-NS-2", "MATERNITY ER"),
            C("CC-NS-3", "Medical Ward"),
            C("CC-NS-4", "Medical Ward - A1"),
            C("CC-NS-5", "ICU"),
            C("CC-NS-6", "ICU-A"),
            C("CC-NS-7", "OR"),
            C("CC-OT-1", "LDR 1"),
            C("CC-LAB-1", "Chemistry"),
            C("CC-LAB-2", "All Divisions"),
            C("CC-CLN-10", "Blood Bank"),
            C("CC-CLN-19", "Radiology"),
            C("CC-NS-46", "CATH LAB"),
            C("ORA-1", "Rayan Pharmacy 1"),
            C("ORA-2", "Housekeeping"),
            C("ORA-3", "Information Technology"),
            C("ORA-4", "IT Back End"),
            C("ORA-5", "Bio Medical - Hospital"),
            C("ORA-6", "Bio Medical - HO"),
            C("ORA-7", "Laundry"),
            C("ORA-8", "Finance Department"),
            C("ORA-9", "Human Resources Department"),
            C("CC-NS-99", "test dummy ward"),
        };

        var hits = HubMatcher.Match(centres).ToDictionary(h => h.Token);

        Assert.Equal("H.CC-CLN-1", hits["PHYS-COMP"].CostCenterCode);
        Assert.Equal(hits["PHYS-COMP"].CostCenterId, hits["OPD-OPS"].CostCenterId);
        Assert.Equal("H.CC-NS-1", hits["ER-STAFF"].CostCenterCode);
        Assert.Equal("H.CC-NS-3", hits["WARD-NURSE"].CostCenterCode);
        Assert.Equal("H.CC-NS-5", hits["ICU-NURSE"].CostCenterCode);
        Assert.Equal(hits["ICU-NURSE"].CostCenterId, hits["ICU-BED"].CostCenterId);
        Assert.Equal("H.CC-NS-7", hits["OR-STAFF"].CostCenterCode);
        Assert.Equal(hits["OR-STAFF"].CostCenterId, hits["OR-FAC"].CostCenterId);
        Assert.Equal("H.CC-NS-46", hits["CATH-OPS"].CostCenterCode);
        Assert.Equal("H.CC-OT-1", hits["LD-OPS"].CostCenterCode);
        Assert.Equal("H.CC-LAB-1", hits["LAB-STAFF"].CostCenterCode);
        Assert.Equal("H.CC-CLN-10", hits["BLOOD-OPS"].CostCenterCode);
        Assert.Equal("H.CC-CLN-19", hits["RAD-STAFF"].CostCenterCode);
        Assert.Equal("H.ORA-1", hits["PHARM-DRUG"].CostCenterCode);
        Assert.Equal("H.ORA-3", hits["IT"].CostCenterCode);
        Assert.Equal("H.ORA-5", hits["BIOMED"].CostCenterCode);
        Assert.False(hits["PHYS-COMP"].Fallback);
        Assert.DoesNotContain(hits.Values, h => h.CostCenterName?.Contains("test", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void HubMatcher_matches_oracle_centres_when_dummy_prefixes_absent()
    {
        var centres = new[]
        {
            C("ORA-11106", "ICU-Intensive Care Unit"),
            C("ORA-11109", "Operation Room (O.R)"),
            C("ORA-11107", "Medical Surgical Ward"),
            C("ORA-11102", "Cath Lab"),
            C("ORA-11104", "DELIVERY ROOM - L.D.R. 11"),
            C("ORA-11201", "Radiology"),
            C("ORA-11120", "Blood Bank"),
            C("ORA-11200", "Out-Patient"),
            C("ORA-1", "HMC Main Pharmacy"),
            C("CC-UNMAPPED", "Unmapped"),
        };

        var hits = HubMatcher.Match(centres).ToDictionary(h => h.Token);

        Assert.Equal("H.ORA-11200", hits["PHYS-COMP"].CostCenterCode);
        Assert.True(hits["PHYS-COMP"].Fallback);
        Assert.Equal("H.ORA-11107", hits["WARD-NURSE"].CostCenterCode);
        Assert.Equal("H.ORA-11106", hits["ICU-NURSE"].CostCenterCode);
        Assert.Equal("H.ORA-11109", hits["OR-STAFF"].CostCenterCode);
        Assert.Equal("H.ORA-11102", hits["CATH-OPS"].CostCenterCode);
        Assert.Equal("H.ORA-11104", hits["LD-OPS"].CostCenterCode);
        Assert.Equal("H.ORA-11120", hits["BLOOD-OPS"].CostCenterCode);
        Assert.Equal("H.ORA-11201", hits["RAD-STAFF"].CostCenterCode);
        Assert.Equal("H.ORA-1", hits["PHARM-DRUG"].CostCenterCode);
    }

    [Fact]
    public void HubMatcher_override_and_pharmacy_fallback()
    {
        var im = C("CC-CLN-1", "DERMATOLOGY");
        var pharm = C("ORA-9", "Main Pharmacy");
        var hits = HubMatcher.Match(
            [im, pharm],
            new Dictionary<string, string> { ["PHYS-COMP"] = "H.CC-CLN-1" });
        var map = hits.ToDictionary(h => h.Token);
        Assert.True(map["PHYS-COMP"].Overridden);
        Assert.Equal("H.CC-CLN-1", map["PHYS-COMP"].CostCenterCode);
        Assert.True(map["PHARM-DRUG"].Fallback);
        Assert.Equal("H.ORA-9", map["PHARM-DRUG"].CostCenterCode);
    }

    [Fact]
    public void ProcedureFamily_exclusive_case()
    {
        Assert.Equal("SKIP", ProcedureFamily.Of("Meal tray", "FOOD & BEVERAGE", "PROC"));
        Assert.Equal("TRANSFUSE", ProcedureFamily.Of("Packed cell transfusion", "NURSING SERVICES", "PROC"));
        Assert.Equal("DENTIMP", ProcedureFamily.Of("Dental implant fixture", "DENTAL", "PROC"));
        Assert.Equal("DENTAL", ProcedureFamily.Of("Scaling", "DENTAL", "PROC"));
        Assert.Equal("CATH", ProcedureFamily.Of("PCI with stent", "PROCEDURE", "PROC"));
        Assert.Equal("LAB", ProcedureFamily.Of("CBC", "LABORATORY", "LAB"));
        Assert.Equal("CONSULT", ProcedureFamily.Of("Clinic visit", "CONSULTATION", "CONSULT"));
        Assert.Equal("MRI-C", ProcedureFamily.Of("MRI brain with contrast", "RADIOLOGY", "IMAGING"));
        Assert.Equal("MRI", ProcedureFamily.Of("MRI lumbar", "RADIOLOGY", "IMAGING"));
        Assert.Equal("USG", ProcedureFamily.Of("ECHO heart", "RADIOLOGY", "IMAGING"));
        Assert.Equal("SURGERY", ProcedureFamily.Of("Appendectomy", "SURGERY", "SURGERY"));
        Assert.Equal("ICU", ProcedureFamily.Of("ICU (INTENSIVE CARE UNIT)", "OTHER SERVICES", "PROCEDURE"));
        Assert.Equal("ICU", ProcedureFamily.Of("ICU - NURSING CARE", "PROCEDURES", "PROCEDURE"));
        Assert.Equal("SURGERY", ProcedureFamily.Of("OR CHARGES PER HOUR", "INPATIENT SERVICE", "PROCEDURE"));
        Assert.Equal("ANES", ProcedureFamily.Of("ANAESTHESIOLOGIST FEES", "INPATIENT SERVICE", "PROCEDURE"));
        Assert.Equal("MED", ProcedureFamily.Of("ANESTHESIA MEDICATION", "INPATIENT SERVICE", "PROCEDURE"));
        Assert.Equal("USG", ProcedureFamily.Of("ECHO (NEW)- (COLOR DOPPLER ECHOCARDIOGRAPHY)", "PROCEDURES", "PROCEDURE"));
        Assert.Equal("TRANSFUSE", ProcedureFamily.Of("PACKED RED BLOOD CELLS (TESTING AND SUPERVISION)", "LABORATORY", "LAB"));
        Assert.Equal("DISP", ProcedureFamily.Of("INPATIENT CONSUMABLE CHARGES", "MISCELLANEOUS SERVICES", "PROCEDURE"));
        Assert.Equal("PROC", ProcedureFamily.Of("VENTILATOR 24 HRS WITH SUCTION", "PROCEDURES", "PROCEDURE"));
        Assert.Equal("WARD", ProcedureFamily.Of("Private room day", "NURSING SERVICES", "BED_DAY"));
        Assert.Equal("MED", ProcedureFamily.Of("Paracetamol tab", "MEDICINE", "PROC"));
        Assert.Equal("PROC", ProcedureFamily.Of("Wound stitch", "PROCEDURE", "PROC"));
        Assert.Equal(["TRIAGE", "ER-ASSESS", "NURSE-ER"], ProcedureFamily.Activities("ER"));
        Assert.Equal(["NURSE-ICU"], ProcedureFamily.Activities("ICU"));
        Assert.Empty(ProcedureFamily.Activities("SKIP"));
        Assert.True(Catalogue.SkipTdabcRecipe("FOOD & BEVERAGE"));
        Assert.True(Catalogue.SkipTdabcRecipe("LAUNDRY"));
        Assert.False(Catalogue.SkipTdabcRecipe("CONSULTATION"));
    }

    [Fact]
    public void VersionsCoveringFacts_picks_published_H_year_of_imported_months()
    {
        var h1 = new CostingVersion { Code = "CV-2026-H1", Status = "PUBLISHED", EffectiveFrom = new DateOnly(2026, 1, 1), EffectiveTo = new DateOnly(2026, 6, 30) };
        var h2 = new CostingVersion { Code = "CV-2026-H2", Status = "PUBLISHED", EffectiveFrom = new DateOnly(2026, 7, 1) };
        var published = new[] { h1, h2 };
        Assert.Equal(["CV-2026-H2"], RecipeSuggestionService.VersionsCoveringFacts(published, [new DateOnly(2026, 10, 1)]).Select(v => v.Code));
        Assert.Equal(["CV-2026-H1", "CV-2026-H2"], RecipeSuggestionService.VersionsCoveringFacts(published, [new DateOnly(2026, 2, 1), new DateOnly(2026, 9, 1)]).Select(v => v.Code));
        Assert.Empty(RecipeSuggestionService.VersionsCoveringFacts(published, [new DateOnly(2025, 12, 1)]));
    }

    [Fact]
    public void TemplateFrom_is_costing_year_start_not_facts_month()
    {
        Assert.Equal(new DateOnly(2026, 1, 1), RecipeSuggestionService.TemplateFrom(new DateOnly(2026, 8, 1)));
        Assert.Equal(new DateOnly(2026, 1, 1), RecipeSuggestionService.TemplateFrom(new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void OpenPeriods_covers_each_calendar_month()
    {
        var months = OpenPeriods.Months(new DateOnly(2026, 10, 15), new DateOnly(2026, 12, 2)).ToArray();
        Assert.Equal([new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1)], months);
        Assert.Equal("2026-10", months[0].ToString("yyyy-MM"));
    }

    [Fact]
    public void Hospital_range_code_is_from_month_and_to_month()
    {
        var from = new DateOnly(2026, 9, 1);
        var to = new DateOnly(2026, 10, 28);
        Assert.Equal("2026-09-10", $"{from:yyyy-MM}-{to:MM}");
        Assert.Equal(58, to.DayNumber - from.DayNumber + 1);
    }

    [Fact]
    public void HisExtractResource_is_physician_role_only()
    {
        Assert.True(SeedTemplates.IsHisExtractResource("SWD-AGLSNR.RES-MD-CARD"));
        Assert.False(SeedTemplates.IsHisExtractResource("SWD-AGLSNR.RES-BLD-PRBC"));
        Assert.False(SeedTemplates.IsHisExtractResource("SWD-AGLSNR.RES-RN-WARD"));
    }

    [Fact]
    public void NshUnitCost_covers_every_seeded_resource_token()
    {
        var missing = SeedTemplates.Resources.Select(r => r.Token).Where(t => !SeedTemplates.NshUnitCost.ContainsKey(t)).ToArray();
        Assert.True(missing.Length == 0, "NshUnitCost missing: " + string.Join(", ", missing));
        Assert.Equal(25m, SeedTemplates.NshUnitCost["DRG-IP"]);
    }

    [Fact]
    public void RecipeSuggestions_fill_forms_and_do_not_mark_unbound_as_saved()
    {
        var h = "SWD-AGLSNR";
        var act = RecipeSuggestions.ActCode(h, "CONSULT");
        var res = RecipeSuggestions.ResCode(h, "MD-MED");
        var input = new RecipeSuggestions.Input(
            h, new DateOnly(2026, 1, 1), "CV-2026-H2",
            [new RecipeSuggestions.HubCodes("PHYS-COMP", "ORA-11200", "CLN-1")],
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { act },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { res, RecipeSuggestions.ResCode(h, "MD-CARD") },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            [new RecipeSuggestions.BilledProc("PRC-1", "Clinic visit", "CONSULTATION", "CONSULT", 12)],
            [new RecipeSuggestions.InvoiceRole("CONSULT", RecipeSuggestions.ResCode(h, "MD-CARD"), "Cardiology", 9)]);

        var pal = RecipeSuggestions.Build(input, "ProcedureActivityLine");
        var line = pal.Items.Single(x => x.Key == "PAL-FAMILY:CONSULT/CONSULT");
        Assert.False(line.AlreadyExists);
        Assert.Equal("PRC-1", line.Fields["procedureCode"]);
        Assert.Equal(act, line.Fields["activityCode"]);
        Assert.Equal("CV-2026-H2", line.Fields["costingVersionCode"]);
        Assert.Equal("1", line.Fields["quantity"]);
        Assert.Empty(line.Missing);

        var ar = RecipeSuggestions.Build(input, "ActivityResource");
        var template = ar.Items.First(x => x.Key == "AR:CONSULT/MD-MED");
        Assert.False(template.AlreadyExists);
        Assert.Equal(act, template.Fields["activityCode"]);
        Assert.Equal(res, template.Fields["resourceCode"]);
        var invoice = ar.Items.First(x => x.Key.StartsWith("AR-INV:", StringComparison.Ordinal));
        Assert.Equal(RecipeSuggestions.ResCode(h, "MD-CARD"), invoice.Fields["resourceCode"]);
        Assert.Contains("invoice", invoice.Reason, StringComparison.OrdinalIgnoreCase);

        var saved = input with { BoundKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { RecipeSuggestions.BoundKey("PRC-1", act) } };
        var after = RecipeSuggestions.Build(saved, "ProcedureActivityLine");
        Assert.DoesNotContain(after.Items, x => !x.AlreadyExists);
        Assert.True(after.Items.Single(x => x.Key == "PAL-FAMILY:CONSULT/CONSULT").AlreadyExists);
    }
}
