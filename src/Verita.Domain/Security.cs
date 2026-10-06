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

    /// <summary>FR-11.4: twenty-four modules grouped by what a person is doing.</summary>
    public static readonly NavGroup[] Navigation =
    [
        new("Overview", [
            new("dashboard", "Dashboard", "dashboard.view", "Executive, finance, clinical, revenue-cycle and supply-chain views of one run."),
            new("profitability", "Profitability", "profitability.view", "Contribution and operating margin by hospital, department, service line, physician, payer, procedure, package, DRG, encounter type and segment."),
            new("variance", "Variance", "variance.view", "Actual against standard, decomposed into rate, quantity and mix effects with a written explanation for each."),
            new("capacity", "Capacity", "costing.view", "Time-driven capacity, utilisation and the cost of capacity paid for but not used."),
        ]),
        new("Cost model", [
            new("cost-centres", "Cost Centres", "masterdata.view", "The financial units money lands in, their step-down sequence and their closing balances."),
            new("cost-pools", "Cost Pools", "costing.view", "Homogeneous bundles of cost, their GL mappings, drivers and allocation method."),
            new("resources", "Resources", "masterdata.view", "Everything that costs money and is consumed, with effective-dated rates and capacity models."),
            new("cost-drivers", "Cost Drivers", "costing.view", "The measurable things that cause cost, their source systems, formulas and measured volumes."),
            new("activities", "Activities", "masterdata.view", "Units of clinical and operational work, their standard recipes and unit costs."),
            new("procedures", "Procedures", "costing.view", "Standard and actual cost per procedure, fully decomposed into its cost components."),
            new("packages", "Packages", "costing.view", "Bundled episodes: cost, revenue, variance and margin against the contracted price."),
            new("drgs", "DRGs", "costing.view", "Case-mix cost distribution, length of stay, reimbursement, margin and high-cost outliers."),
        ]),
        new("Configuration", [
            new("costing-models", "Costing Models", "costing.view", "Versioned costing definitions with their effective dates, diffs and approval history."),
            new("allocation-rules", "Allocation Rules", "costing.view", "How each pool is distributed: method, driver, stage, targets and the money each rule moved."),
        ]),
        new("Execution", [
            new("runs", "Calculation Runs", "costing.view", "Controlled costing runs, their eleven stages, money in and out, and lifecycle state."),
            new("reconciliation", "Reconciliation", "costing.view", "The bridge from the general ledger to patient cost, with every residual named and explained."),
            new("patient-costing", "Patient Costing", "patient.view", "Cost, revenue and margin for individual encounters, traceable to source transactions."),
        ]),
        new("Analysis", [
            new("simulation", "Simulation", "simulation.view", "What-if scenarios executed by the same engine as a real run."),
            new("reports", "Reports", "reports.view", "Standing reports for finance, executive, clinical operations, revenue cycle and supply chain."),
        ]),
        new("Assurance", [
            new("data-quality", "Data Quality", "quality.view", "Nineteen automated checks, passes included, gating every run."),
            new("governance", "Governance", "governance.view", "Four-eye approval of costing configuration and runs, with the full lifecycle history."),
            new("audit", "Audit", "audit.view", "Every change, approval, publication and PHI access, with actor, timestamp and before-and-after state."),
        ]),
        new("System", [
            new("administration", "Administration", "admin.view", "Users, roles, hospital and department scopes, and the permission matrix."),
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
