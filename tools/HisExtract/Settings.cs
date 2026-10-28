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
