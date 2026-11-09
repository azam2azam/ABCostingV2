using Verita.Domain;

namespace Verita.Application.Assistant;

public sealed record Term(string Key, string Label, string[] Aliases, string Definition, string? Path = null);
public sealed record HowTo(string Key, string Title, string[] Aliases, string[] Steps, string? Path = null, string? Permission = null, string[]? Before = null);
public sealed record Dimension(string Key, string Label, string[] Aliases);

/// <summary>
/// What the assistant knows without looking at data: the glossary, how-to steps, the
/// profitability dimensions and the screens. The definitions follow the costing method the
/// Help &amp; Method page documents.
/// </summary>
public static class Knowledge
{
    public static readonly Term[] Terms =
    [
        new("direct-cost", "Direct cost", ["direct costs", "direct"],
            "Cost consumed by the patient's care itself: staff time, drugs, consumables and equipment used on a procedure. In a costing-model run it is the contracted price × the procedure type's Margin Cost %.", "procedure-costing"),
        new("indirect-cost", "Indirect cost", ["indirect costs", "indirect"],
            "Support cost carried onto a procedure as a share of its contracted price: the procedure type's Overhead %.", "indirect-costing"),
        new("overhead", "Overheads", ["overheads", "encounter overhead", "encounter overheads"],
            "Encounter-level overheads: cost-centre costs spread over encounters rather than over individual procedures.", "indirect-costing"),
        new("unit-cost", "Unit cost", ["cost per unit", "cost per case"],
            "The cost of one occurrence of a procedure or activity: its total cost divided by its volume in the run."),
        new("cost-driver", "Cost driver", ["driver", "drivers", "cost drivers", "allocation basis"],
            "The measure used to share a cost pool out to what consumed it, such as square metres, headcount, minutes or tests. A pool's cost × (a target's driver volume ÷ total driver volume) is the target's share."),
        new("cost-pool", "Cost pool", ["pool", "pools", "cost pools"],
            "A bucket of general-ledger cost that is allocated together using one driver, for example Facilities or IT."),
        new("cost-centre", "Cost centre", ["cost center", "cost centres", "cost centers"],
            "A financial unit that money lands in. Support cost centres are stepped down onto clinical ones in their step-down sequence.", "cost-centres"),
        new("step-down", "Step-down allocation", ["step down", "stepdown", "step-down sequence"],
            "Allocating support cost centres one at a time, in sequence, onto the cost centres after them, so that every support cost ends on a clinical cost centre."),
        new("idle-capacity", "Idle capacity", ["unused capacity", "idle", "spare capacity"],
            "Capacity that was paid for but not used: practical minutes available minus minutes used, at the resource's cost per minute. It is reported, not charged to patients."),
        new("practical-capacity", "Practical capacity", ["practical minutes", "theoretical capacity"],
            "The minutes a resource can realistically work: theoretical minutes × the practical ratio (allowing for breaks, maintenance and leave)."),
        new("variance", "Variance", ["variances", "rate variance", "quantity variance", "mix variance"],
            "Actual cost minus the baseline (standard or budget). It splits into rate (paid a different price), quantity (used a different amount) and mix (a different case mix); the three always add up to the total."),
        new("contribution-margin", "Contribution margin", ["contribution"],
            "Revenue minus direct cost: what a service contributes toward indirect cost and overheads."),
        new("operating-margin", "Operating margin", ["margin", "full cost margin"],
            "Revenue minus total cost (direct + indirect + overheads)."),
        new("reconciliation", "Reconciliation", ["reconcile", "gl reconciliation", "unexplained"],
            "Checking that the cost attributed to patients plus named residuals (idle capacity, out-of-scope cost) equals the general ledger. What is left is 'unexplained' and must be under materiality to publish."),
        new("published-run", "Published run", ["publish", "published", "publication"],
            "A run that passed its checks and four-eye approval. It is immutable; to correct a period you create a new run. Integrations and default screens read only published runs."),
        new("costing-model", "Costing model", ["costing version", "costing models", "version"],
            "A versioned set of costing definitions (Margin Cost % and Overhead % by procedure type, encounter overheads) with effective dates and approval history.", "costing-models"),
        new("package", "Package", ["packages", "bundle", "bundled episode"],
            "A bundled episode billed at a contracted price. Its cost is the cost of its component procedures; its margin is the price minus that cost.", "packages"),
        new("drg", "DRG", ["drgs", "diagnosis related group"],
            "Diagnosis-related group: inpatient cases classified by diagnosis and treatment, compared on cost per case, length of stay and margin."),
        new("procedure-type", "Procedure type", ["proc type", "procedure types"],
            "The HIS classification a procedure belongs to. Costing percentages are set per procedure type.", "procedure-costing"),
        new("calculation-run", "Calculation run", ["run", "runs", "costing run"],
            "One controlled execution of the eleven costing stages for a hospital and period, with money-in and money-out checked at each stage.", "runs"),
    ];

    public static readonly HowTo[] HowTos =
    [
        new("run-costing", "Create a new calculation run",
            ["create a calculation run", "new calculation run", "create a run", "new run", "start a run", "run costing", "run the calculation",
             "calculate costs", "execute a run", "cost a period", "start a calculation", "create run", "run calculation"],
            ["Pick the hospital in the context bar at the top.",
             "Open Execution → Calculation Runs.",
             "Choose the period in the run controls. The costing model in effect for that period is shown beside it.",
             "Click New run. It is disabled when no costing model is in effect for the period, or when the period already has a run under that model (open that run and use Re-run instead).",
             "The run goes through its eleven stages; open it to watch each stage's money in, money out and any gap.",
             "When it reaches RECONCILED, review its data-quality issues, then Request approval."],
            "runs", "run.execute",
            ["The period exists for the hospital (Configuration → Periods).",
             "A costing model is in effect for the period (Configuration → Costing Models). A published version is used; a draft only if none is published.",
             "The period's data is loaded (Data → Data Import)."]),
        new("rerun", "Re-run a calculation run",
            ["re run a run", "rerun", "re-run", "recalculate", "run again", "recalculate a period", "force a run", "run with force"],
            ["Open Execution → Calculation Runs and click the run.",
             "Click Re-run. It recalculates the same hospital, period and costing model and replaces the run's results.",
             "Re-run with force goes past ERROR data-quality issues, but a forced run can never be published. Use it only to see results while the data is fixed."],
            "runs", "run.execute",
            ["The run is not PUBLISHED (a published run never changes; create a run for a new costing model instead) and not running."]),
        new("publish-run", "Publish a run",
            ["publish a run", "publish", "approve a run", "approval", "sign off", "request approval", "get a run approved"],
            ["Open the run from Execution → Calculation Runs.",
             "When it is RECONCILED with no publish blockers, click Request approval.",
             "The finance and clinical approvers approve it. Four-eye: whoever requested cannot approve.",
             "Once APPROVED, someone with run.publish clicks Publish. A published run is final and is what screens show by default."],
            "runs", "approval.request",
            ["The run is RECONCILED, not forced, and has no blocking data-quality issues."]),
        new("add-period", "Add a costing period",
            ["add a period", "create a period", "new period", "add a month", "create a month", "open a period"],
            ["Pick the hospital in the context bar.",
             "Open Configuration → Periods.",
             "Enter the From and To dates and, optionally, a name such as 2026 Oct, then click Add period.",
             "Importing facts for a month also creates its period."],
            "periods", "masterdata.edit", []),
        new("import-his", "Import HIS data",
            ["import his data", "his import", "import from his", "load his data", "his extract", "his extract utility", "upload his pack", "his data",
             "import hisgenx", "bring data from his", "his facts", "his catalogue", "his pack", "extract data from his", "import his month"],
            ["On a machine that can reach the HIS SQL Server, run the HIS Extract Utility (tools/HisExtract). Confirm the connection, the SetupID and the ProjectID. It only reads from HIS.",
             "Run probe first to see this project's row counts.",
             "Run catalogue once: hospital, departments, cost centres, doctors, procedures and prices, packages, DRGs and payers go into a …-catalogue.zip.",
             "Run facts for each month (it asks From/To): patients, encounters, invoice lines, revenue, refunds and package items go into one …-{yyyy-MM}-facts.zip per month.",
             "Copy the zips to where VERITA runs. Open Data → Data Import and choose the catalogue zip first.",
             "VERITA reads the pack and shows its SetupID, ProjectID, month and tables. Choose the VERITA hospital; the one whose SetupID/ProjectID match is marked (HIS match). The first import binds that hospital to the pack's SetupID/ProjectID, and packs from another HIS project are refused for it afterwards.",
             "Click Validate: it runs the real load and rolls it back, listing every failed row with the reason. Then click Proceed.",
             "Import each month's facts zip the same way. Rejected rows download from Import history, ready to fix.",
             "HIS does not hold the costing model: set up the period and costing version in VERITA, then create a calculation run."],
            "data-import", "masterdata.edit",
            ["The HIS Extract Utility is on a machine with read access to the HIS database (VERITA cannot reach HIS directly).",
             "You know the hospital's HIS SetupID and ProjectID.",
             "Each zip is under 200 MB."]),
        new("import-data", "Add a month of data",
            ["import data", "add a month of data", "upload data", "load data", "add data", "new month of data", "upload csv", "import csv", "add a hospital", "import templates", "csv templates"],
            ["Open Data → Data Import.", "Download the templates (one CSV per table, or all of them in a numbered zip).", "Fill them and upload one CSV or the zip.",
             "Press Validate first: it runs the real load in a transaction and rolls it back, showing each failed row with the reason.",
             "Upload for real. Rejected rows download as a CSV ready to fix."], "data-import", "masterdata.edit", []),
        new("edit-data", "Edit master data",
            ["edit data", "change a rate", "edit a record", "data entry", "change master data", "update a rate", "change a price"],
            ["Open Data → Data Entry and pick the table.", "References are picked by code.",
             "Rates, capacity, GL mappings and list prices are never edited in place: saving creates a new version and closes the previous one the day before."], "data-entry", "masterdata.edit", []),
        new("set-direct-costing", "Set direct costing values",
            ["set direct costing values", "set direct costing", "direct costing", "direct costing values", "set margin cost", "margin cost percentage", "margin cost",
             "procedure costing", "set direct cost", "direct cost percentage", "edit procedure costing", "change direct cost", "zero price procedures", "fixed cost"],
            ["Pick the hospital in the context bar.",
             "Open Direct Costing → Procedure Costing.",
             "In the version picker, choose the draft costing version to edit.",
             "Each procedure type row has a Margin Cost (%). A procedure's direct cost = its contracted price × Margin Cost %. Typing it on the type row applies it to every procedure of that type.",
             "To set one procedure differently, expand its type and click its Margin % or its Cost.",
             "Procedures with no contracted price are listed under Zero price procedures; give them a Fixed Cost (SAR) instead.",
             "Click Save. Runs use the values once that version is in effect for the period, and a re-run picks them up."],
            "procedure-costing", "costing.view",
            ["A draft costing model version exists for the hospital. A published version is read-only: create a new version on Configuration → Costing Models first."]),
        new("set-package-costing", "Set package costing values",
            ["set package costing", "package costing", "package costing values", "package margin cost", "set package cost", "edit package costing", "package direct cost"],
            ["Pick the hospital in the context bar.",
             "Open Direct Costing → Package Costing and choose the draft costing version.",
             "Each package type row has a Margin Cost (%): the package's direct cost = its contracted price × Margin Cost %. Expand a type to set one package differently.",
             "Leave Margin Cost at 0 or blank to cost the package from its composition (the sum of its included procedures' cost).",
             "Click Save."],
            "package-costing", "costing.view",
            ["A draft costing model version exists for the hospital (published versions are read-only)."]),
        new("set-indirect-costing", "Set indirect costing and overheads",
            ["set indirect costing", "indirect costing", "indirect costing values", "set overhead", "overhead percentage", "overhead %", "set overheads",
             "encounter overheads", "indirect cost percentage", "edit indirect costing"],
            ["Pick the hospital in the context bar.",
             "Open Configuration → Indirect Costing and choose the draft costing version.",
             "Under Procedure/activity overheads, set each type's Overhead (%): indirect cost = contracted price × Overhead %. Expand a type to set one procedure differently.",
             "Under encounter overheads, add a line per cost centre with an Overhead % or a fixed Overhead (SAR) spread over encounters.",
             "Click Save."],
            "indirect-costing", "costing.view",
            ["A draft costing model version exists for the hospital (published versions are read-only)."]),
        new("change-costing-model", "Create a costing model version",
            ["change margin", "change overhead", "costing percentage", "edit costing model", "new costing version", "create a costing model", "new version"],
            ["Open Configuration → Costing Models and click New version.",
             "Copy it from the current version and set its effective dates.",
             "Edit Margin Cost % in Procedure Costing and Overhead % in Indirect Costing.",
             "Request approval. Runs for periods from its effective date use it."], "costing-models", "costing.view", []),
        new("use-assistant", "Use the assistant",
            ["use the assistant", "what can you do", "what can i ask", "help me"],
            ["Ask about costs, procedures, packages, profitability, non-billable services, variance, capacity, data quality, reconciliation, runs and imports.",
             "Follow up with 'and for <hospital>?', 'what about September?', 'why?' or 'break it down'.",
             "Compare in one question: 'compare this month with August'.",
             "Each answer lists what it assumed; remove an assumption to re-ask without it."], null, null, []),
    ];

    public static readonly Dimension[] Dimensions =
    [
        new("serviceLine", "Service line", ["service line", "service lines", "specialty", "specialties", "speciality"]),
        new("department", "Department", ["department", "departments", "dept", "depts", "clinic", "clinics"]),
        new("payer", "Payer", ["payer", "payers", "payor", "payors", "sponsor", "sponsors"]),
        new("insurer", "Insurer", ["insurer", "insurers", "insurance", "insurance company", "insurance companies"]),
        new("physician", "Physician", ["physician", "physicians", "doctor", "doctors", "consultant", "consultants", "surgeon", "surgeons"]),
        new("procedure", "Procedure", ["procedure", "procedures"]),
        new("package", "Package", ["package", "packages"]),
        new("drg", "DRG", ["drg", "drgs"]),
        new("encounterType", "Encounter type", ["encounter type", "visit type", "inpatient", "outpatient", "ip op", "admission type"]),
        new("segment", "Patient segment", ["segment", "segments", "patient segment", "nationality"]),
        new("hospital", "Hospital", ["hospitals", "by hospital", "facility"]),
        new("resource", "Resource", ["resource", "resources"]),
    ];

    /// <summary>The small, fixed vocabulary: dimensions, screens, terms and how-tos.</summary>
    public static IEnumerable<LexEntry> StaticEntries()
    {
        foreach (var d in Dimensions) yield return new("dimension", d.Key, d.Label, d.Aliases);
        foreach (var n in Security.AllNavItems)
            yield return new("nav", n.Path, n.Label, [n.Label, n.Path.Replace('-', ' ')], n.Permission);
        foreach (var t in Terms) yield return new("term", t.Key, t.Label, t.Aliases);
        foreach (var h in HowTos) yield return new("howto", h.Key, h.Title, h.Aliases);
    }

    public static Term? TermByKey(string key) => Terms.FirstOrDefault(t => t.Key == key);
    public static HowTo? HowToByKey(string key) => HowTos.FirstOrDefault(t => t.Key == key);
    public static Dimension? DimensionByKey(string key) => Dimensions.FirstOrDefault(t => t.Key == key);
}
