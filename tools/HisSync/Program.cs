using Microsoft.EntityFrameworkCore;
using Verita.HisSync;
using Verita.Infrastructure;

// VERITA HIS sync: one ASP.NET Core site with two background workers, so it deploys to IIS like the API.
// Keep the app pool running (Start mode AlwaysRunning, Idle time-out 0, preload on); see README.md.
var builder = WebApplication.CreateBuilder(args);

var cs = builder.Configuration.GetConnectionString("Verita")
    ?? throw new InvalidOperationException("ConnectionStrings:Verita is not set.");
builder.Services.AddVerita(cs);
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection("HisSync"));
builder.Services.Configure<HisOptions>(builder.Configuration.GetSection("His"));
builder.Services.AddSingleton<SyncStore>();
builder.Services.AddSingleton<PackRunner>();
builder.Services.AddHostedService<CatalogueWorker>();
builder.Services.AddHostedService<FactsWorker>();
// A failing worker logs and carries on; it never stops the site.
builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/health"));

/// Liveness for IIS / monitoring. No data.
app.MapGet("/health", (SyncStore store) => Results.Ok(new { status = "ok", enabled = store.Options.Enabled, at = DateTime.UtcNow }));

/// What each worker is doing per hospital, as on the HIS Sync screen. Needs the X-Sync-Key header when HisSync:StatusKey is set.
app.MapGet("/status", async (HttpRequest req, SyncStore store, IConfiguration config, CancellationToken ct) =>
{
    var key = config["HisSync:StatusKey"];
    if (!string.IsNullOrEmpty(key) && req.Headers["X-Sync-Key"] != key) return Results.Unauthorized();
    await using var db = await store.DbAsync(ct);
    var hospitals = await store.HospitalsAsync(ct);
    var ids = hospitals.Select(h => h.Id).ToList();
    var workers = await db.HisSyncWorkers.AsNoTracking().Where(w => ids.Contains(w.HospitalId)).ToListAsync(ct);
    return Results.Ok(new
    {
        enabled = store.Options.Enabled,
        today = store.Today.ToString("yyyy-MM-dd"),
        hospitals = hospitals.Select(h => new
        {
            hospital = h.Code, tenant = h.Tenant,
            workers = workers.Where(w => w.HospitalId == h.Id).Select(w => new
            {
                w.Kind, w.Status, w.Step, currentDate = w.CurrentDate?.ToString("yyyy-MM-dd"), w.BatchesDone, w.BatchesTotal,
                w.RecordsDone, w.RecordsTotal, w.HeartbeatAt, w.LastError,
            }),
        }),
    });
});

app.Run();
