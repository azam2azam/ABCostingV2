using Verita.Infrastructure.Queries;
using System.IO.Compression;
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
using Verita.Infrastructure.DataHub;
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

    private static Principal Everywhere(User u) => new(u.Id, u.Email, u.Name, u.Role, u.Title, ["*"], ["*"]);

    [Fact]
    public async Task Data_entry_recipe_uses_the_costing_version_of_the_procedures_hospital()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritaDbContext>();
        var hub = scope.ServiceProvider.GetRequiredService<DataHubService>();
        var p = Everywhere(await db.Users.FirstAsync(u => u.Email == "costing.manager@verita.health"));
        var open = await db.CostingVersions.Where(v => v.Status != "PUBLISHED").ToListAsync();
        Assert.True(open.Select(v => v.HospitalId).Distinct().Count() > 1);
        var spec = EntitySpecs.Find("ProcedureActivityLine")!;
        foreach (var v in open)
        {
            var proc = await db.Procedures.Where(x => x.HospitalId == v.HospitalId).OrderBy(x => x.Code).Select(x => x.Code).FirstAsync();
            var act = await db.Activities.Where(x => x.HospitalId == v.HospitalId).OrderBy(x => x.Code).Select(x => x.Code).FirstAsync();
            var ok = await hub.SaveAsync(spec, p, [new() { ["procedureCode"] = proc, ["costingVersionCode"] = v.Code, ["activityCode"] = act, ["quantity"] = "1" }], dryRun: true, "TEST", default);
            Assert.Equal(0, ok.Rejected);
            var missing = await hub.SaveAsync(spec, p, [new() { ["procedureCode"] = proc, ["costingVersionCode"] = "CV-NOT-HERE", ["activityCode"] = act, ["quantity"] = "1" }], dryRun: true, "TEST", default);
            Assert.Equal(1, missing.Rejected);
        }
    }

    [Theory]
    [InlineData("CostingVersion")]
    [InlineData("CostingDefinition")]
    [InlineData("ProcedureActivityLine")]
    [InlineData("ProcedureResourceLine")]
    public async Task Import_refuses_costing_model_tables(string table)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritaDbContext>();
        var his = scope.ServiceProvider.GetRequiredService<HisImportService>();
        var p = Everywhere(await db.Users.FirstAsync(u => u.Email == "costing.manager@verita.health"));
        var before = await db.CostingVersions.CountAsync();
        using var csv = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("code,name\nCV-X,X\n"));
        var error = await Assert.ThrowsAsync<AppError>(() => his.ImportAsync(p, $"{table}.csv", csv, null, dryRun: false, table, default));
        Assert.Contains("costing models", error.Message);
        Assert.Equal(before, await db.CostingVersions.CountAsync());
    }

    [Fact]
    public async Task Import_of_a_zip_skips_costing_model_tables_and_loads_the_rest()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritaDbContext>();
        var his = scope.ServiceProvider.GetRequiredService<HisImportService>();
        var p = Everywhere(await db.Users.FirstAsync(u => u.Email == "costing.manager@verita.health"));
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(dir!.FullName, "samples"))) dir = dir.Parent;
        using var zip = new MemoryStream();
        ZipFile.CreateFromDirectory(Path.Combine(dir.FullName, "samples", "onboarding-example"), zip);
        zip.Position = 0;
        var results = await his.ImportAsync(p, "onboarding-example.zip", zip, null, dryRun: true, null, default);
        Assert.Contains(results, r => r.Entity == "Procedure");
        Assert.DoesNotContain(results, r => HisImportPack.CostingModelTables.Contains(r.Entity));
    }

    [Fact]
    public async Task Seeding_endpoints_are_gone()
    {
        var client = await As("costing.manager@verita.health");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/data/costing-seed/preview?hospital=NSH-RYD")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/data/costing-seed/apply", new { hospital = "NSH-RYD" })).StatusCode);
    }

    [Fact]
    public async Task Encounter_lines_show_refund_table_rows_except_cancelled_ones()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritaDbContext>();
        var q = scope.ServiceProvider.GetRequiredService<ReadQueries>();
        var user = Everywhere(await db.Users.FirstAsync(u => u.Email == "cfo@verita.health"));
        var encounter = await db.Encounters.FirstAsync();
        var code = await db.Hospitals.Where(h => h.Id == encounter.HospitalId).Select(h => h.Code).FirstAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        RefundTransaction Refund(string no, DateTime? cancelled) => new()
        {
            Id = IdGen.Sequential(), HospitalId = encounter.HospitalId, EncounterId = encounter.Id, RefundNo = no, RefundDate = new DateTime(2026, 10, 5),
            OriginalInvoiceNo = "TEST", InvoiceLineNo = 1, RefundAmount = 12.5m, CancelledAt = cancelled, SourceRef = $"REF-{no}-1",
        };
        var live = Refund($"T{tag}A", null);
        var cancelled = Refund($"T{tag}B", new DateTime(2026, 10, 6));
        db.RefundTransactions.AddRange(live, cancelled);
        await db.SaveChangesAsync();
        try
        {
            var scoped = await q.CatalogueScopeAsync(user, null, code, null, default);
            var lines = (await q.PatientEncounterAsync(scoped, user, encounter.Id, default)).Lines;
            Assert.Equal(-12.5m, lines.Single(l => l.SourceRef == live.SourceRef).Gross);
            Assert.DoesNotContain(lines, l => l.SourceRef == cancelled.SourceRef);
        }
        finally
        {
            await db.RefundTransactions.Where(r => r.Id == live.Id || r.Id == cancelled.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Import_stores_his_invoice_fields_and_package_items_without_changing_the_money()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritaDbContext>();
        var hub = scope.ServiceProvider.GetRequiredService<DataHubService>();
        var user = Everywhere(await db.Users.FirstAsync(u => u.Email == "costing.manager@verita.health"));
        var encounter = await db.Encounters.FirstAsync();
        var hospital = await db.Hospitals.FirstAsync(h => h.Id == encounter.HospitalId);
        var payer = await db.Payers.Select(x => x.Code).FirstAsync();
        var tag = Guid.NewGuid().ToString("N")[..10];
        var revenueRef = $"REV-T-{tag}";
        var packageRef = $"PKG-T-{tag}";
        try
        {
            var results = await hub.ImportSetAsync([
                (EntitySpecs.Find("RevenueTransaction")!, [new()
                {
                    ["encounterNo"] = encounter.EncounterNo, ["payerCode"] = payer, ["grossAmount"] = "420.38", ["discount"] = "420.38", ["netAmount"] = "0",
                    ["postedAt"] = "2026-10-28", ["sourceSystem"] = "VIDA-HIS", ["sourceRef"] = revenueRef,
                    ["hisUnitPrice"] = "420.38", ["hisQuantity"] = "1", ["hisDiscountAmount"] = "120.38", ["waivedAmount"] = "300", ["patientShare"] = "0", ["companyShare"] = "0",
                    ["isZeroPrice"] = "true", ["packageProcedureId"] = "PK1", ["isAcceptedWithDrg"] = "true",
                }]),
                (EntitySpecs.Find("InvoicePackageItem")!, [new()
                {
                    ["hospitalCode"] = hospital.Code, ["encounterNo"] = encounter.EncounterNo, ["hisEncounterNo"] = encounter.EncounterNo, ["hisProcedureId"] = "NOT-LOADED",
                    ["packageProcedureId"] = "PK1", ["transactionType"] = "2", ["invoiceNo"] = "1", ["lineItemNo"] = "1", ["invoiceDate"] = "2026-10-28",
                    ["price"] = "100", ["discountedCharge"] = "80", ["patientShare"] = "0", ["patientPaid"] = "0", ["companyShare"] = "80", ["companyPaid"] = "0",
                    ["sourceSystem"] = "VIDA-HIS", ["sourceRef"] = packageRef,
                }]),
            ], user, dryRun: false, "TEST", default, hospital.Code);
            Assert.All(results, r => Assert.Equal(0, r.Rejected));
            var line = await db.RevenueTransactions.AsNoTracking().FirstAsync(r => r.SourceRef == revenueRef);
            Assert.Equal(420.38m, line.GrossAmount);
            Assert.Equal(420.38m, line.Discount);
            Assert.Equal(0m, line.NetAmount);
            Assert.Equal(300m, line.WaivedAmount);
            Assert.Equal(120.38m, line.HisDiscountAmount);
            Assert.True(line.IsZeroPrice);
            Assert.Equal("PK1", line.PackageProcedureId);
            var item = await db.InvoicePackageItems.AsNoTracking().FirstAsync(r => r.SourceRef == packageRef);
            Assert.Equal(encounter.Id, item.EncounterId);
            Assert.Null(item.ProcedureId);
            Assert.Equal(80m, item.CompanyShare);
            Assert.Null((await db.Encounters.AsNoTracking().FirstAsync(e => e.Id == encounter.Id)).PackageId);
        }
        finally
        {
            await db.RevenueTransactions.Where(r => r.SourceRef == revenueRef).ExecuteDeleteAsync();
            await db.InvoicePackageItems.Where(r => r.SourceRef == packageRef).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task CostingVersions_BelongToOneHospital()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VeritaDbContext>();
        var q = scope.ServiceProvider.GetRequiredService<ReadQueries>();
        var admin = await db.Users.FirstAsync(u => u.Email == "cfo@verita.health");
        var p = new Principal(admin.Id, admin.Email, admin.Name, admin.Role, admin.Title, ["*"], ["*"]);
        var codes = await db.CostingVersions.Select(v => v.HospitalId).Distinct().Join(db.Hospitals, id => id, h => h.Id, (_, h) => h.Code).ToListAsync();
        Assert.True(codes.Count > 1);
        var seen = new HashSet<Guid>();
        foreach (var code in codes)
        {
            var models = await q.CostingModelsAsync(p, code, default);
            Assert.NotEmpty(models.Versions);
            Assert.All(models.Versions, v => Assert.Equal(code, v.Hospital));
            Assert.All(models.Versions, v => Assert.True(seen.Add(v.Id)));
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
            Id = IdGen.Sequential(), HospitalId = (await db.Hospitals.FirstAsync()).Id, Code = $"CV-TEST-{Guid.NewGuid():N}"[..20], Name = "Integration test version", Description = "Temporary",
            EffectiveFrom = new DateOnly(2020, 1, 1), EffectiveTo = new DateOnly(2020, 12, 31), Status = "DRAFT", CreatedAt = DateTime.UtcNow,
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
    public async Task Draft_run_can_be_deleted_only_with_run_execute()
    {
        var manager = await As("costing.manager@verita.health");
        var created = await manager.PostAsJsonAsync("/api/v1/runs", new { hospital = "NSH-RYD", period = "2026-08" });
        created.EnsureSuccessStatusCode();
        var code = (await Json(created)).GetProperty("code").GetString()!;
        var deleted = false;
        try
        {
            var admin = await (await As("admin@verita.health")).DeleteAsync($"/api/v1/runs/{code}");
            Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);

            var kept = await manager.DeleteAsync($"/api/v1/runs/{Run}");
            Assert.Equal(HttpStatusCode.Conflict, kept.StatusCode);
            Assert.Equal("RUN_STATE", Code(await Json(kept)));

            var gone = await manager.DeleteAsync($"/api/v1/runs/{code}");
            Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/v1/runs/{code}")).StatusCode);
            deleted = true;
        }
        finally
        {
            if (!deleted) await manager.DeleteAsync($"/api/v1/runs/{code}");
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
