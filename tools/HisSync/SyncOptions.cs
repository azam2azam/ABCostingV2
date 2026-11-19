namespace Verita.HisSync;

/// <summary>"HisSync" in appsettings.json.</summary>
public sealed class SyncOptions
{
    /// <summary>Turns both workers off without stopping the site (health and status still answer).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Run the catalogue worker (daily check and "Sync now").</summary>
    public bool CatalogueEnabled { get; set; } = true;
    /// <summary>Run the facts worker (the dates added on HIS Sync).</summary>
    public bool FactsEnabled { get; set; } = true;
    /// <summary>VERITA hospital codes to sync; empty syncs every hospital linked to a HIS tenant. For a staged rollout.</summary>
    public string[] Hospitals { get; set; } = [];
    /// <summary>Encounters per facts batch. An ER visit and the appointment it bills through always go together.</summary>
    public int BatchSize { get; set; } = 100;
    /// <summary>Pause after each unit of work (a batch, a day's list), so neither HIS nor VERITA is kept busy.</summary>
    public int PauseBetweenBatchesMs { get; set; } = 2000;
    /// <summary>How often an idle worker looks for work.</summary>
    public int PollSeconds { get; set; } = 30;
    /// <summary>Tries before a batch is split (or a single encounter, a day's list or its refunds is marked failed).</summary>
    public int MaxAttempts { get; set; } = 3;
    /// <summary>One VERITA query's limit for the worker (imports, dry-runs). Read at startup.</summary>
    public int VeritaCommandTimeoutSeconds { get; set; } = 1800;
    /// <summary>Minutes before an interrupted catalogue check is resumed again while the process keeps running. After a restart it resumes at once.</summary>
    public int CatalogueRetryMinutes { get; set; } = 5;
    /// <summary>Where packs are written while they are imported (each deleted after), and where an interrupted catalogue check keeps its finished tables to resume from.</summary>
    public string WorkDir { get; set; } = Path.Combine(Path.GetTempPath(), "verita-his-sync");
    /// <summary>Hours of the hospital's clock ahead of UTC, for "today" and the catalogue check time (Saudi Arabia: 3).</summary>
    public int UtcOffsetHours { get; set; } = 3;
}

/// <summary>"His" in appsettings.json: how to reach each HIS tenant. Read-only access is enough.</summary>
public sealed class HisOptions
{
    /// <summary>Used for every tenant without its own entry under <see cref="Tenants"/>.</summary>
    public string ConnectionString { get; set; } = "";
    /// <summary>Per tenant, keyed "SetupID-ProjectID" (for example "010266-15").</summary>
    public Dictionary<string, HisTenantOptions> Tenants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string PharmacyDb { get; set; } = "PHARMAKON";
    public int QueryTimeoutSeconds { get; set; } = 1800;
    /// <summary>Also extract claim, settlement and NPHIES lines (as HisExtract --claims).</summary>
    public bool Claims { get; set; }
}

public sealed class HisTenantOptions
{
    public string? ConnectionString { get; set; }
    public string? PharmacyDb { get; set; }
}
