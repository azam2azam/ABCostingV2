namespace HisExtract;

/// <summary>Tenant and connection defaults for the UAT HIS box. Every value is overridable.</summary>
public sealed class Settings
{
    public const string DefaultConnection =
        "Server=RAY-VIDA-UAT;Database=HISGENX;User Id=HmgDbUser;Password=HmgDbUser123;TrustServerCertificate=True;Application Name=VeritaHisExtract";

    public string ConnectionString { get; set; } = DefaultConnection;
    public string SetupId { get; set; } = "010266";
    public short ProjectId { get; set; } = 15;
    public string HospitalCode { get; set; } = "RYN-UAT";
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string OutDir { get; set; } = Path.Combine(Environment.CurrentDirectory, "his-extract-out");

    public string H => HospitalCode;

    public void BindTenant(Microsoft.Data.SqlClient.SqlCommand cmd)
    {
        cmd.Parameters.AddWithValue("@SetupId", SetupId);
        cmd.Parameters.AddWithValue("@ProjectId", ProjectId);
    }

    public void BindPeriod(Microsoft.Data.SqlClient.SqlCommand cmd)
    {
        BindTenant(cmd);
        cmd.Parameters.AddWithValue("@From", From ?? throw new InvalidOperationException("Period From is required."));
        cmd.Parameters.AddWithValue("@To", To ?? throw new InvalidOperationException("Period To is required."));
    }
}
