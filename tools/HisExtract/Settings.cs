namespace HisExtract;

/// <summary>Tenant and connection defaults for the UAT HIS box. Every value is overridable.</summary>
public sealed class Settings
{
    public const string DefaultConnection =
        "Server=RAY-VIDA-UAT;Database=HISGENX;User Id=HmgDbUser;Password=HmgDbUser123;TrustServerCertificate=True;Application Name=VeritaHisExtract;Pooling=true";

    public string ConnectionString { get; set; } = DefaultConnection;
    public string SetupId { get; set; } = "010266";
    public short ProjectId { get; set; } = 15;
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string OutDir { get; set; } = Path.Combine(Environment.CurrentDirectory, "his-extract-out");
    /// <summary>Optional reviewed CSV (costCentreCode, departmentCode) that wins over the derived cost centre → department link.</summary>
    public string? CcMapPath { get; set; }
    /// <summary>
    /// Legacy CLI flag only. Ward nursing is no longer invented from INP_Transfer in extract;
    /// raw transfer timing lands in EncounterTransfer for VERITA to apply standards later.
    /// </summary>
    public decimal WardNurseMinutesPerDay { get; set; } = 260m;
    /// <summary>Seconds one HIS query may run (0 = no limit). A step that times out is skipped and noted in extract-log.txt.</summary>
    public int QueryTimeoutSeconds { get; set; } = 1800;
    /// <summary>Pharmacy / inventory database holding the item master (PHR_Item) for item names; empty to skip. Same server as HIS.</summary>
    public string PharmacyDb { get; set; } = "PHARMAKON";
    /// <summary>
    /// Extract claim, settlement and NPHIES lines (42–45). Off by default: claimed is the invoice's IsClaimed flag on each
    /// revenue line (hisIsClaimed); received and rejected are to be read from their own database later.
    /// </summary>
    public bool Claims { get; set; }

    /// <summary>
    /// Set by the sync worker: extract only these encounters (one batch of a day) instead of the whole day.
    /// Null for the CLI, which extracts whole days. <see cref="From"/>/<see cref="To"/> stay the day either way.
    /// </summary>
    public FactBatch? Batch { get; set; }
    /// <summary>The parts of the facts to read (<see cref="FactSections"/>); null reads everything.</summary>
    public IReadOnlySet<string>? Sections { get; set; }
    public bool Wants(string section) => Sections is null || Sections.Contains(section);
    /// <summary>What a facts run writes: the whole day, one batch of its encounters, or the day's own refunds.</summary>
    public FactsPart Part { get; set; } = FactsPart.Day;
    /// <summary>Lookups that do not change between batches of one day (item names), kept by the sync worker.</summary>
    public Dictionary<string, object> Cache { get; } = new(StringComparer.Ordinal);

    /// <summary>Sync worker hook: the current phase, so a long HIS read is not a single frozen step name.</summary>
    public Func<string, Task>? OnProgress { get; set; }

    /// <summary>HIS statements that took at least 3 seconds during the extract just run. Not copied between batches.</summary>
    public List<string> SlowSteps { get; } = new();

    public async Task ReportAsync(string step)
    {
        if (OnProgress is null) return;
        try { await OnProgress(step); }
        catch (OperationCanceledException) { throw; }
        catch { /* a progress write must not fail the extract */ }
    }

    /// <summary>HIS tenant token used in row codes. Not a VERITA Hospital.Code — import maps that.</summary>
    public string H => $"{SetupId}-{ProjectId}";

    /// <summary>Filename token for HIS identity. SetupID/ProjectID are the extract identity, not VERITA Hospital.Code.</summary>
    public string PackPrefix
    {
        get
        {
            var setup = SetupId;
            foreach (var c in Path.GetInvalidFileNameChars())
                setup = setup.Replace(c, '_');
            return $"HIS-{setup}-{ProjectId}";
        }
    }

    public void BindTenant(Microsoft.Data.SqlClient.SqlCommand cmd)
    {
        cmd.Parameters.AddWithValue("@SetupId", SetupId);
        cmd.Parameters.AddWithValue("@ProjectId", ProjectId);
    }

    public void BindPeriod(Microsoft.Data.SqlClient.SqlCommand cmd)
    {
        BindTenant(cmd);
        var from = From ?? throw new InvalidOperationException("Period From is required.");
        var to = To ?? throw new InvalidOperationException("Period To is required.");
        cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@From", System.Data.SqlDbType.DateTime2) { Value = from.Date });
        cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@To", System.Data.SqlDbType.DateTime2) { Value = to.Date });
    }
}
