using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;
using Verita.Infrastructure.Governance;

namespace Verita.Integration.Tests;

/// <summary>
/// Runs against the bootstrapped Verita database on SQL Express (scripts/tools.sh bootstrap).
/// These are the BRD's integration criteria: masking per role (AC-10), hospital scope by direct
/// id (AC-11), immutability (AC-9), the publication gate (AC-12), four-eye approval (AC-18) and
/// the independent derivation of a procedure's cost from the database (AC-5, BRD 23.5).
/// </summary>
public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Run = "RUN-NSH-RYD-2026-08";
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly Dictionary<string, string> Tokens = [];

    public ApiTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private async Task<HttpClient> As(string email)
    {
        var client = _factory.CreateClient();
        if (!Tokens.TryGetValue(email, out var token))
        {
            var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Demo!Pass123" });
            res.EnsureSuccessStatusCode();
            token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            Tokens[email] = token;
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    private static string Code(JsonElement e) => e.GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    public async Task Unauthenticated_requests_get_json_401_not_a_redirect()
    {
        var res = await _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).GetAsync($"/api/v1/dashboard?run={Run}");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("UNAUTHENTICATED", Code(await Json(res)));
    }

    [Fact]
    public async Task Missing_permission_is_json_403()
    {
        var res = await (await As("cfo@verita.health")).GetAsync("/api/v1/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("FORBIDDEN", Code(await Json(res)));
    }

    [Fact]
    public async Task Every_response_names_its_run()
    {
        var body = await Json(await (await As("cfo@verita.health")).GetAsync($"/api/v1/reconciliation?run={Run}"));
        var meta = body.GetProperty("meta");
        Assert.Equal(Run, meta.GetProperty("run").GetString());
        Assert.False(string.IsNullOrEmpty(meta.GetProperty("costingVersion").GetString()));
        // NG-5: money is a string on the wire.
        Assert.Equal(JsonValueKind.String, body.GetProperty("data").GetProperty("glTotal").ValueKind);
    }

    [Fact]
    public async Task Phi_is_masked_without_phi_view_and_unmasked_with_it()
    {
        // AC-10.
        var masked = await Json(await (await As("cfo@verita.health")).GetAsync($"/api/v1/encounters?run={Run}&pageSize=5"));
        foreach (var row in masked.GetProperty("data").GetProperty("rows").EnumerateArray())
            Assert.StartsWith("••••", row.GetProperty("mrn").GetString());
        var open = await Json(await (await As("clinical.director@verita.health")).GetAsync($"/api/v1/encounters?run={Run}&pageSize=5"));
        foreach (var row in open.GetProperty("data").GetProperty("rows").EnumerateArray())
            Assert.DoesNotContain("••••", row.GetProperty("mrn").GetString());
    }

    [Fact]
    public async Task Out_of_scope_hospital_is_refused_by_direct_id()
    {
        // AC-11 / FR-0.6: 403, not an empty result.
        var res = await (await As("jeddah.director@verita.health")).GetAsync($"/api/v1/procedures?run={Run}");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("OUT_OF_SCOPE", Code(await Json(res)));
    }

    [Fact]
    public async Task Department_manager_sees_only_their_departments()
    {
        var res = await Json(await (await As("surgery.manager@verita.health")).GetAsync($"/api/v1/cost-centres?run={Run}"));
        var rows = res.GetProperty("data").EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Contains(r.GetProperty("department").GetString(), new[] { "Surgery", "Critical Care" }));
    }

    [Fact]
    public async Task Forced_run_cannot_be_published()
    {
        // AC-12 / FR-9.8: the fixture carries an unmapped GL account, so its runs are forced.
        var res = await (await As("cfo@verita.health")).GetAsync($"/api/v1/runs/{Run}");
        var blockers = (await Json(res)).GetProperty("data").GetProperty("publishBlockers").EnumerateArray().Select(b => b.GetString()).ToList();
        Assert.Contains(blockers, b => b!.Contains("DQ-GL-UNMAPPED"));
    }

    [Fact]
    public async Task Published_run_refuses_re_execution_with_its_own_code()
    {
        // AC-9 / FR-10.7. A run is marked published for the duration of the test only.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritaDbContext>();
        var run = await db.CalculationRuns.FirstAsync(r => r.Code == "RUN-HMC-JED-2026-07");
        var before = run.Status;
        await db.CalculationRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "PUBLISHED"));
        try
        {
            var res = await (await As("costing.manager@verita.health")).PostAsJsonAsync($"/api/v1/runs/{run.Code}/execute", new { force = false });
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Equal("RUN_IMMUTABLE", Code(await Json(res)));
            var transition = await (await As("cfo@verita.health")).PostAsJsonAsync($"/api/v1/runs/{run.Code}/transition", new { to = "REVIEWED" });
            Assert.Equal("RUN_IMMUTABLE", Code(await Json(transition)));
        }
        finally
        {
            await db.CalculationRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, before));
        }
    }

    [Fact]
    public async Task Four_eye_approval_refuses_self_approval_and_records_both_parties()
    {
        // AC-18 / FR-0.1, on a throwaway costing version.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritaDbContext>();
        var gov = scope.ServiceProvider.GetRequiredService<GovernanceService>();
        Principal P(User u) => new(u.Id, u.Email, u.Name, u.Role, u.Title, ["*"], ["*"]);
        var cfo = P(await db.Users.FirstAsync(u => u.Email == "cfo@verita.health"));
        var controller = P(await db.Users.FirstAsync(u => u.Email == "controller@verita.health"));
        var version = new CostingVersion
        {
            Id = IdGen.Sequential(), Code = $"CV-TEST-{Guid.NewGuid():N}"[..20], Name = "Integration test version", Description = "Temporary",
            EffectiveFrom = new DateOnly(2030, 1, 1), Status = "DRAFT", CreatedAt = DateTime.UtcNow,
        };
        db.CostingVersions.Add(version);
        await db.SaveChangesAsync();
        try
        {
            var req = await gov.RequestAsync("COSTING_VERSION", version.Id, "Test change", cfo);
            var self = await Assert.ThrowsAsync<AppError>(() => gov.DecideAsync(req.Id, true, "self", cfo));
            Assert.Equal("SELF_APPROVAL", self.Code);
            var decided = await gov.DecideAsync(req.Id, true, "checked", controller);
            Assert.Equal("CLINICAL_APPROVAL", decided.Stage);
            Assert.Equal(cfo.Id, decided.RequestedById);
            Assert.Equal(controller.Id, decided.ReviewedById);
        }
        finally
        {
            await db.ApprovalRequests.Where(a => a.EntityId == version.Id).ExecuteDeleteAsync();
            await db.CostingVersions.Where(v => v.Id == version.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public void Administrator_holds_no_approval_and_auditor_writes_nothing()
    {
        // FR-0.2, FR-0.3.
        var admin = Security.Role("ADMIN")!;
        Assert.DoesNotContain(admin.Permissions, p => p.StartsWith("approval.") || p == "run.publish");
        var auditor = Security.Role("AUDITOR")!;
        Assert.DoesNotContain(auditor.Permissions, p => Security.WritePermissions.Contains(p) || Security.GovernancePermissions.Contains(p));
    }

    /// <summary>
    /// BRD 23.5: MRI brain's direct standard cost rebuilt from the recipe and the effective rates in
    /// the database, without calling the engine, and compared with what the engine published.
    /// </summary>
    [Fact]
    public async Task Mri_brain_standard_cost_matches_an_independent_derivation()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritaDbContext>();
        var run = await db.CalculationRuns.FirstAsync(r => r.Code == Run);
        var period = await db.Periods.FirstAsync(p => p.Id == run.PeriodId);
        var proc = await db.Procedures.FirstAsync(p => p.HospitalId == run.HospitalId && p.Name == "MRI brain with contrast");
        var def = await db.CostingDefinitions.FirstAsync(d => d.ProcedureId == proc.Id && d.CostingVersionId == run.CostingVersionId);
        var lines = await db.ProcedureActivityLines.Where(l => l.DefinitionId == def.Id).ToListAsync();
        var asOf = period.EndDate;

        decimal expected = 0m;
        var byResource = new Dictionary<Guid, decimal>();
        foreach (var l in lines)
            foreach (var r in await db.ActivityResources.Where(x => x.ActivityId == l.ActivityId).ToListAsync())
                byResource[r.ResourceId] = byResource.GetValueOrDefault(r.ResourceId) + r.StdQuantity * l.Quantity;
        foreach (var (resourceId, qty) in byResource)
        {
            var rate = (await db.ResourceRates.Where(x => x.ResourceId == resourceId).ToListAsync())
                .Single(x => x.EffectiveFrom <= asOf && (x.EffectiveTo == null || x.EffectiveTo >= asOf)).UnitCost;
            expected += Math.Round(Math.Round(qty, 6) * rate, 4, MidpointRounding.AwayFromZero);
        }

        var published = await db.CostComponents.Where(c => c.RunId == run.Id && c.Scope == "PROCEDURE" && c.ScopeId == proc.Id && c.CostNature != "OVERHEAD")
            .SumAsync(c => c.Amount);
        Assert.Equal(expected, published);
    }
}

public sealed class DataHubTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public DataHubTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private async Task<HttpClient> Manager()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "costing.manager@verita.health", password = "Demo!Pass123" });
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Every_table_has_a_template_with_its_columns()
    {
        var client = await Manager();
        var entities = await client.GetFromJsonAsync<JsonElement>("/api/v1/data/entities");
        Assert.True(entities.GetArrayLength() >= 36);
        var csv = await client.GetStringAsync("/api/v1/data/ResourceRate/template");
        Assert.StartsWith("resourceCode,unitCost", csv.TrimStart('﻿'));
    }

    [Fact]
    public async Task Validation_rejects_bad_rows_one_by_one_and_saves_nothing()
    {
        var client = await Manager();
        var csv = "code,name,city,currency,bedCount\nZZ-OK,Fine,Test,SAR,10\nZZ-BAD,Broken,Test,SAR,abc\nZZ-OK,Duplicate,Test,SAR,10\n";
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "Hospital.csv" },
            { new StringContent("true"), "dryRun" },
        };
        var res = await client.PostAsync("/api/v1/data/import", form);
        res.EnsureSuccessStatusCode();
        var r = (await res.Content.ReadFromJsonAsync<JsonElement>())[0];
        Assert.Equal(1, r.GetProperty("created").GetInt32());
        Assert.Equal(2, r.GetProperty("rejected").GetInt32());
        var hospitals = await client.GetFromJsonAsync<JsonElement>("/api/v1/data/Hospital?search=ZZ-");
        Assert.Equal(0, hospitals.GetProperty("total").GetInt32()); // the dry run rolled back
    }

    [Fact]
    public void Csv_parser_handles_quotes_commas_and_guidance_rows()
    {
        var rows = Verita.Infrastructure.DataHub.Csv.Parse("﻿code,name\r\n# required,required\r\nA,\"Smith, \"\"Jr\"\"\"\r\nB,\"two\nlines\"\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Smith, \"Jr\"", rows[0]["name"]);
        Assert.Equal("two\nlines", rows[1]["name"]);
    }
}
