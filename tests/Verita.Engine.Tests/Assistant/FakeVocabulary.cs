using Verita.Application.Assistant;

namespace Verita.Engine.Tests.Assistant;

/// <summary>A small, fixed world for the golden tests: two hospitals with their own period codes and catalogues.</summary>
public sealed class FakeVocabulary : IAssistantVocabulary
{
    public Lexicon Static { get; } = new(Knowledge.StaticEntries());

    public Lexicon Hospitals { get; } = new(
    [
        new("hospital", "NSH-RYD", "Northgate Specialist Hospital — Riyadh", ["Northgate Specialist Hospital — Riyadh", "NSH-RYD"]),
        new("hospital", "SWD-AGL", "Swedi Hospital 2", ["Swedi Hospital 2", "SWD-AGL"]),
        new("hospital", "SWD-AGLSNR", "Sehat Al Swedi", ["Sehat Al Swedi", "SWD-AGLSNR"]),
    ]);

    private readonly Dictionary<string, Lexicon> _catalogues = new()
    {
        ["NSH-RYD"] = new(
        [
            new("procedure", "p-appx", "Laparoscopic appendectomy", ["Laparoscopic appendectomy"], "SURG-101"),
            new("procedure", "p-cbc", "Complete blood count", ["Complete blood count", "CBC"], "LAB-001"),
            new("procedure", "p-consult", "Outpatient consultation", ["Outpatient consultation"], "OPD-010"),
            new("procedure", "p-mri", "MRI brain with contrast", ["MRI brain with contrast"], "RAD-220"),
            new("procedure", "p-ct", "CT brain without contrast", ["CT brain without contrast"], "RAD-210"),
            new("procedure", "p-health", "Executive health check", ["Executive health check"], "OPD-900"),
            new("procedure", "p-crown", "Cerec crown", ["Cerec crown"], "DEN-040"),
            new("procedure", "p-implant5", "IMPLANT 5", ["IMPLANT 5"], "DEN-005"),
            new("procedure", "p-openappr", "OPEN APPROVAL - NON-BILLABLE", ["OPEN APPROVAL - NON-BILLABLE"], "NSH-RYD.450103781"),
            new("procedure", "p-icu", "Intensive care patient day", ["Intensive care patient day"], "NSH-RYD.BED-ICU-DAY"),
            new("package", "k-delivery", "Normal delivery package", ["Normal delivery package"], "PKG-NVD"),
            new("package", "k-cataract", "Cataract surgery package", ["Cataract surgery package"], "PKG-CAT"),
        ]),
        ["SWD-AGL"] = new(
        [
            new("procedure", "s-cbc", "COMPLETE BLOOD COUNT", ["COMPLETE BLOOD COUNT"], "LAB-001"),
            new("procedure", "s-brain", "BRAIN BIOPSY", ["BRAIN BIOPSY"], "SWD.020"),
            new("procedure", "s-breast", "BREAST BIOPSY", ["BREAST BIOPSY"], "SWD.052"),
            new("package", "s-wellness", "WOMENS WELLNESS PACKAGE", ["WOMENS WELLNESS PACKAGE"], "SWD.PKG1"),
        ]),
    };

    private readonly Dictionary<string, PeriodRef[]> _periods = new()
    {
        ["NSH-RYD"] =
        [
            new("2026-08", new(2026, 8, 1), new(2026, 8, 31), true),
            new("2026-09", new(2026, 9, 1), new(2026, 9, 30), true),
            new("2026-10", new(2026, 10, 1), new(2026, 10, 31), true),
            new("2026-11", new(2026, 11, 1), new(2026, 11, 30), false),
        ],
        ["SWD-AGL"] =
        [
            new("2026 Aug", new(2026, 8, 1), new(2026, 8, 31), true),
            new("2026 Oct", new(2026, 10, 1), new(2026, 10, 31), false),
            new("2026 Nov", new(2026, 11, 1), new(2026, 11, 30), true),
        ],
        ["SWD-AGLSNR"] = [new("2026 Oct", new(2026, 10, 1), new(2026, 10, 31), false)],
    };

    public Lexicon ForHospital(string hospital) => _catalogues.GetValueOrDefault(hospital) ?? new Lexicon([]);
    public IReadOnlyList<PeriodRef> Periods(string hospital) => _periods.GetValueOrDefault(hospital) ?? [];
    public string? DefaultHospital => "NSH-RYD";
}
