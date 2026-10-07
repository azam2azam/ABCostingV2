using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Verita.Engine;
using Verita.Infrastructure.Queries;
using Verita.Infrastructure.Runs;
using static Verita.Infrastructure.Queries.ReadQueries;

namespace Verita.Api.Controllers;

/// <summary>
/// The read surface behind the twenty-four modules (ARCH-11). Every response is enveloped with
/// the run it came from (ARCH-12); every endpoint carries the permission its module needs, from
/// the same declaration the interface's route guard reads (FR-0.4).
/// </summary>
[Route("api/v1")]
public sealed class CostingController : VeritaController
{
    [HttpGet("dashboard")]
    [Authorize(Policy = "dashboard.view")]
    public async Task<Envelope<DashboardDto>> Dashboard(CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.DashboardAsync(s, p, ct)); }

    [HttpGet("profitability")]
    [Authorize(Policy = "profitability.view")]
    public async Task<Envelope<ProfitDto>> Profitability([FromQuery] string dimension = "serviceLine", CancellationToken ct = default)
    { var (p, s) = await Scoped(); return Wrap(s, await Q.ProfitabilityAsync(s, p, dimension, ct)); }

    [HttpGet("variance")]
    [Authorize(Policy = "variance.view")]
    public async Task<Envelope<VarianceRow[]>> Variances(CancellationToken ct) { var (_, s) = await Scoped(); return Wrap(s, await Q.VariancesAsync(s, ct)); }

    [HttpGet("variance/{id:guid}")]
    [Authorize(Policy = "variance.view")]
    public async Task<Envelope<VarianceDetail>> Variance(Guid id, CancellationToken ct) { var (_, s) = await Scoped(); return Wrap(s, await Q.VarianceAsync(s, id, ct)); }

    [HttpGet("capacity")]
    [Authorize(Policy = "costing.view")]
    public async Task<Envelope<CapacityDto>> Capacity(CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.CapacityAsync(s, p, ct)); }

    [HttpGet("cost-centres")]
    [Authorize(Policy = "masterdata.view")]
    public async Task<Envelope<CostCentreRow[]>> CostCentres(CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.CostCentresAsync(s, p, ct)); }

    [HttpGet("cost-centres/{id:guid}")]
    [Authorize(Policy = "masterdata.view")]
    public async Task<Envelope<CostCentreDetail>> CostCentre(Guid id, CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.CostCentreAsync(s, p, id, ct)); }

    [HttpGet("cost-pools")]
    [Authorize(Policy = "costing.view")]
    public async Task<Envelope<PoolRow[]>> Pools(CancellationToken ct) { var (_, s) = await Scoped(); return Wrap(s, await Q.CostPoolsAsync(s, ct)); }

    [HttpGet("cost-pools/{id:guid}")]
    [Authorize(Policy = "costing.view")]
    public async Task<Envelope<PoolDetail>> Pool(Guid id, CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.CostPoolAsync(s, p, id, ct)); }

    [HttpGet("resources")]
    [Authorize(Policy = "masterdata.view")]
    public async Task<Envelope<ResourceRow[]>> Resources(CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.ResourcesAsync(s, p, ct)); }

    [HttpGet("resources/{id:guid}")]
    [Authorize(Policy = "masterdata.view")]
    public async Task<Envelope<ResourceDetail>> Resource(Guid id, CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.ResourceAsync(s, p, id, ct)); }

    [HttpGet("cost-drivers")]
    [Authorize(Policy = "costing.view")]
    public async Task<Envelope<DriverRow[]>> Drivers(CancellationToken ct) { var (_, s) = await Scoped(); return Wrap(s, await Q.DriversAsync(s, ct)); }

    [HttpGet("cost-drivers/{id:guid}")]
    [Authorize(Policy = "costing.view")]
    public async Task<Envelope<DriverDetail>> Driver(Guid id, CancellationToken ct) { var (_, s) = await Scoped(); return Wrap(s, await Q.DriverAsync(s, id, ct)); }

    [HttpGet("activities")]
    [Authorize(Policy = "masterdata.view")]
    public async Task<Envelope<ActivityRow[]>> Activities(CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.ActivitiesAsync(s, p, ct)); }

    [HttpGet("activities/{id:guid}")]
    [Authorize(Policy = "masterdata.view")]
    public async Task<Envelope<ActivityDetail>> Activity(Guid id, CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.ActivityAsync(s, p, id, ct)); }

    [HttpGet("procedures")]
    [Authorize(Policy = "costing.view")]
    public async Task<Envelope<ProcedureRow[]>> Procedures(CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.ProceduresAsync(s, p, ct)); }

    /// <summary>The cost explainer — the primary acceptance test rendered (BRD 23.1).</summary>
    [HttpGet("procedures/{id:guid}")]
    [Authorize(Policy = "costing.view")]
    public async Task<Envelope<ProcedureDetail>> Procedure(Guid id, CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.ProcedureAsync(s, p, id, ct)); }

    [HttpGet("packages")]
    [Authorize(Policy = "costing.view")]
    public async Task<Envelope<PackageRow[]>> Packages(CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.PackagesAsync(s, p, ct)); }

    [HttpGet("packages/{id:guid}")]
    [Authorize(Policy = "costing.view")]
    public async Task<Envelope<PackageDetail>> Package(Guid id, CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.PackageAsync(s, p, id, ct)); }

    [HttpGet("drgs")]
    [Authorize(Policy = "costing.view")]
    public async Task<Envelope<DrgRow[]>> Drgs(CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.DrgsAsync(s, p, ct)); }

    [HttpGet("drgs/{id:guid}")]
    [Authorize(Policy = "costing.view")]
    public async Task<Envelope<DrgDetail>> Drg(Guid id, CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.DrgAsync(s, p, id, ct)); }

    [HttpGet("costing-models")]
    [Authorize(Policy = "costing.view")]
    public async Task<CostingModelsDto> CostingModels(CancellationToken ct) => await Q.CostingModelsAsync(await Me(), ct);

    [HttpGet("allocation-rules")]
    [Authorize(Policy = "costing.view")]
    public async Task<Envelope<RuleRow[]>> Rules(CancellationToken ct) { var (_, s) = await Scoped(); return Wrap(s, await Q.AllocationRulesAsync(s, ct)); }

    [HttpGet("reconciliation")]
    [Authorize(Policy = "costing.view")]
    public async Task<Envelope<ReconDto>> Reconciliation(CancellationToken ct) { var (_, s) = await Scoped(); return Wrap(s, await Q.ReconciliationAsync(s, ct)); }

    [HttpGet("encounters")]
    [Authorize(Policy = "patient.view")]
    public async Task<Envelope<EncounterPage>> Encounters([FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] string sort = "total",
        [FromQuery] bool desc = true, [FromQuery] string? search = null, [FromQuery] string? encType = null, [FromQuery] Guid? departmentId = null,
        [FromQuery] Guid? drgId = null, [FromQuery] Guid? packageId = null, [FromQuery] Guid? payerId = null, CancellationToken ct = default)
    {
        var (p, s) = await Scoped();
        return Wrap(s, await Q.EncountersAsync(s, p, new EncounterQuery
        {
            Page = page, PageSize = pageSize, Sort = sort, Desc = desc, Search = search, EncType = encType, DepartmentId = departmentId,
            DrgId = drgId, PackageId = packageId, PayerId = payerId,
        }, ct));
    }

    [HttpGet("encounters/{id:guid}")]
    [Authorize(Policy = "patient.view")]
    public async Task<Envelope<EncounterDetail>> Encounter(Guid id, CancellationToken ct) { var (p, s) = await Scoped(); return Wrap(s, await Q.EncounterAsync(s, p, id, ct)); }

    [HttpGet("data-quality")]
    [Authorize(Policy = "quality.view")]
    public async Task<Envelope<QualityDto>> Quality(CancellationToken ct) { var (_, s) = await Scoped(); return Wrap(s, await Q.QualityAsync(s, ct)); }
}

[Route("api/v1")]
public sealed class AnalysisController(ScenarioService scenarios) : VeritaController
{
    [HttpGet("scenarios")]
    [Authorize(Policy = "simulation.view")]
    public async Task<ScenarioDto[]> Scenarios(CancellationToken ct) => await Q.ScenariosAsync(await Me(), null, ct);

    [HttpGet("scenarios/{id:guid}")]
    [Authorize(Policy = "simulation.view")]
    public async Task<ScenarioDto> Scenario(Guid id, CancellationToken ct) =>
        (await Q.ScenariosAsync(await Me(), id, ct)).FirstOrDefault() ?? throw Verita.Application.AppError.NotFound("Scenario");

    public sealed record LeverBody(string LeverType, string TargetType, string? TargetId, string TargetLabel, string ChangeType, decimal Value);
    public sealed record ScenarioBody(string BaseRun, string Name, string Description, LeverBody[] Levers);

    /// <summary>FR-8.1: the scenario runs through the same pipeline as a real run, then its shadow run is deleted.</summary>
    [HttpPost("scenarios")]
    [Authorize(Policy = "simulation.run")]
    public async Task<ScenarioDto> Create(ScenarioBody body, CancellationToken ct)
    {
        var p = await Me();
        var s = await Q.Scope(p, body.BaseRun, null, null, ct);
        var code = $"SCN-{DateTime.UtcNow:yyMMddHHmmss}";
        var scenario = await scenarios.CreateAsync(s.Run.Id, code, body.Name, body.Description,
            body.Levers.Select(l => new LeverInput(l.LeverType, l.TargetType, l.TargetId, l.TargetLabel, l.ChangeType, l.Value)).ToList(), p.Email, ct: ct);
        await scenarios.RunAsync(scenario.Id, p.Email, ct);
        return (await Q.ScenariosAsync(p, scenario.Id, ct)).First();
    }

    [HttpPost("scenarios/{id:guid}/run")]
    [Authorize(Policy = "simulation.run")]
    public async Task<ScenarioDto> Rerun(Guid id, CancellationToken ct)
    {
        var p = await Me();
        await scenarios.RunAsync(id, p.Email, ct);
        return (await Q.ScenariosAsync(p, id, ct)).First();
    }

    [HttpGet("reports/{report}")]
    [Authorize(Policy = "reports.view")]
    public async Task<Envelope<object>> Report(string report, CancellationToken ct)
    {
        var (p, s) = await Scoped();
        return Wrap(s, await Q.ReportAsync(s, p, report, ct));
    }

    [HttpGet("audit")]
    [Authorize(Policy = "audit.view")]
    public async Task<AuditPage> Audit([FromQuery] string? action, [FromQuery] string? entityType, [FromQuery] string? actor,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        await Q.AuditAsync(action, entityType, actor, page, pageSize, ct);

    [HttpGet("admin/users")]
    [Authorize(Policy = "admin.view")]
    public async Task<UserDto[]> Users(CancellationToken ct) => await Q.UsersAsync(ct);
}
