namespace Verita.Domain;

/// <summary>
/// The permission model, the role catalogue and the navigation map (BRD section 4, FR-0.4,
/// NFR-22). Declared once: the API generates its authorisation policies from it, the Angular
/// route guard reads it from /api/v1/meta, and the help page documents it.
/// </summary>
public static class Security
{
    public static readonly string[] ReadPermissions =
    [
        "dashboard.view", "costing.view", "patient.view", "profitability.view", "variance.view",
        "simulation.view", "reports.view", "quality.view", "governance.view", "audit.view",
        "admin.view", "help.view", "masterdata.view",
    ];
    public static readonly string[] SensitivePermissions = ["phi.view", "revenue.view", "salary.view"];
    public static readonly string[] WritePermissions =
        ["masterdata.edit", "costing.edit", "driver.edit", "allocation.edit", "run.execute", "simulation.run", "quality.resolve"];
    public static readonly string[] GovernancePermissions =
        ["approval.request", "approval.finance", "approval.clinical", "run.publish", "admin.manage"];

    public static readonly string[] AllPermissions =
        [.. ReadPermissions, .. SensitivePermissions, .. WritePermissions, .. GovernancePermissions];

    public sealed record RoleDef(string Code, string Name, string Description, string[] Permissions);

    private static readonly string[] ReadOnly = ["dashboard.view", "masterdata.view", "costing.view", "reports.view", "help.view"];

    public static readonly RoleDef[] Roles =
    [
        new("CFO", "Chief Financial Officer",
            "Full financial visibility across all hospitals, and the finance leg of four-eye approval.",
            [.. ReadOnly, "patient.view", "profitability.view", "variance.view", "simulation.view", "simulation.run",
             "quality.view", "governance.view", "audit.view", "revenue.view", "salary.view",
             "approval.request", "approval.finance", "run.publish"]),
        new("CEO", "Chief Executive Officer",
            "Executive view across hospitals. Reads everything financial, approves nothing.",
            [.. ReadOnly, "profitability.view", "variance.view", "simulation.view", "governance.view", "revenue.view"]),
        new("COSTING_MANAGER", "Costing Manager",
            "Owns pools, drivers, rules, activity and procedure definitions. Requests approval; cannot grant it.",
            [.. ReadOnly, "patient.view", "profitability.view", "variance.view", "simulation.view", "simulation.run",
             "quality.view", "quality.resolve", "governance.view", "audit.view", "revenue.view", "salary.view",
             "masterdata.edit", "costing.edit", "driver.edit", "allocation.edit", "run.execute", "approval.request"]),
        new("COSTING_ANALYST", "Costing Analyst",
            "Builds and runs costing, analyses variance. Edits drafts, approves nothing.",
            [.. ReadOnly, "patient.view", "profitability.view", "variance.view", "simulation.view", "simulation.run",
             "quality.view", "quality.resolve", "costing.edit", "driver.edit", "run.execute", "approval.request", "revenue.view"]),
        new("FINANCE_CONTROLLER", "Finance Controller",
            "GL reconciliation, budget vs actual, and the finance approval leg. Approves what it cannot edit.",
            [.. ReadOnly, "profitability.view", "variance.view", "quality.view", "governance.view", "audit.view",
             "revenue.view", "salary.view", "approval.finance", "run.publish"]),
        new("CLINICAL_DIRECTOR", "Clinical Director",
            "Clinical operations for their departments, and the clinical approval leg.",
            [.. ReadOnly, "patient.view", "variance.view", "simulation.view", "quality.view", "governance.view",
             "phi.view", "approval.clinical"]),
        new("DEPT_MANAGER", "Department Manager",
            "Cost and utilisation for their own department only.",
            [.. ReadOnly, "variance.view", "quality.view"]),
        new("RCM_MANAGER", "RCM Manager",
            "Reimbursement, payer margin and DRG profitability.",
            [.. ReadOnly, "patient.view", "profitability.view", "variance.view", "revenue.view", "quality.view"]),
        new("SUPPLY_CHAIN", "Supply Chain Manager",
            "Drug and consumable cost, price variance, utilisation and waste.",
            [.. ReadOnly, "variance.view", "quality.view"]),
        new("AUDITOR", "Internal Auditor",
            "Read-only everywhere including the audit trail. Writes nothing, by design.",
            [.. ReadOnly, "patient.view", "profitability.view", "variance.view", "simulation.view", "quality.view",
             "governance.view", "audit.view", "revenue.view", "salary.view"]),
        new("ADMIN", "Platform Administrator",
            "Users, roles and configuration. No approval rights: administering the platform is not signing off its numbers.",
            [.. ReadOnly, "quality.view", "governance.view", "audit.view", "admin.view", "admin.manage", "masterdata.edit"]),
    ];

    public static RoleDef? Role(string code) => Roles.FirstOrDefault(r => r.Code == code);

    public static bool Can(string role, string permission) => Role(role)?.Permissions.Contains(permission) ?? false;

    public sealed record NavItem(string Path, string Label, string Permission, string Purpose);
    public sealed record NavGroup(string Label, NavItem[] Items);

    /// <summary>FR-11.4: the modules, plus data entry and import, grouped by what a person is doing.</summary>
    public static readonly NavGroup[] Navigation =
    [
        new("Dashboard", [
            new("dashboard", "Summary", "dashboard.view", "Direct cost, indirect cost, overheads, revenue and margin of one run, by hospital, service line, department, physician, payer, DRG, encounter type and segment."),
            new("ip-dashboard", "IP Dashboard", "dashboard.view", "The Summary view limited to inpatient (IP) encounters for the selected hospital and period."),
            new("op-dashboard", "OP Dashboard", "dashboard.view", "The Summary view limited to outpatient (OP) encounters for the selected hospital and period — OP only, not ER."),
            new("er-dashboard", "ER Dashboard", "dashboard.view", "The Summary view limited to emergency (ER) encounters for the selected hospital and period."),
        ]),
        new("Catalogue", [
            new("departments", "Departments", "costing.view", "Cost, revenue and margin by department for the selected hospital and period — the same view as the Summary department tab."),
            new("cost-centres", "Cost Centres", "masterdata.view", "The financial units money lands in, their step-down sequence and their closing balances."),
            new("packages", "Packages", "costing.view", "Bundled episodes: cost, revenue, variance and margin against the contracted price."),
            new("procedures", "Procedure/Activity", "costing.view", "Standard and actual cost per procedure/activity, fully decomposed into its cost components."),
            new("resources", "Resources", "masterdata.view", "Everything that costs money and is consumed, with effective-dated rates and capacity models."),
            new("patient-encounters", "Patient Encounters", "patient.view", "Patient encounters and what was invoiced for each, with the source reference of every line."),
            new("his-invoicing", "HIS Invoicing", "revenue.view", "Invoices as held in the HIS."),
        ]),
        new("Configuration", [
            new("periods", "Periods", "costing.view", "The costing periods of the selected hospital, with the run and costing model linked to each."),
            new("costing-models", "Costing Models", "costing.view", "Versioned costing definitions with their effective dates, diffs and approval history."),
            new("indirect-costing", "Indirect Costing", "costing.view", "Indirect overhead cost on each procedure/activity type, as a percentage of contracted price, and the amount in SAR."),
        ]),
        new("Direct Costing", [
            new("procedure-costing", "Procedure Costing", "costing.view", "Procedure/activity types in this hospital, and how many sit under each."),
            new("package-costing", "Package Costing", "costing.view", "Package types in this hospital, and the direct cost of each package as a percentage of its contracted price."),
        ]),
        new("Execution", [
            new("runs", "Calculation Runs", "costing.view", "Controlled costing runs, their eleven stages, money in and out, and lifecycle state."),
        ]),
        new("Data", [
            new("data-entry", "Data Entry", "masterdata.view", "Add and edit hospitals, cost centres, resources, rates, activities, procedures and the rest of the costing model."),
            new("data-import", "Data Import", "masterdata.edit", "Download templates and upload raw data files; every row is validated and bad rows are returned with the reason."),
        ]),
        new("System", [
            new("administration", "Administration", "admin.view", "Users, roles, hospital and department scopes, and the permission matrix."),
            new("audit", "Audit", "audit.view", "Every change, approval, publication and PHI access, with actor, timestamp and before-and-after state."),
            new("help", "Help & Method", "help.view", "How the costing model works, what each number means, and the rules behind it."),
        ]),
    ];

    public static IEnumerable<NavItem> AllNavItems => Navigation.SelectMany(g => g.Items);

    /// <summary>FR-0.7: 10029384 -> ••••9384.</summary>
    public static string MaskMrn(string mrn) => "••••" + (mrn.Length > 4 ? mrn[^4..] : mrn);

    /// <summary>FR-0.7: "Layla Al-Otaibi" -> "L. A.".</summary>
    public static string MaskName(string name) =>
        string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + "."));
}
