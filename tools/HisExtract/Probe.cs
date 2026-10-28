using System.Globalization;

namespace HisExtract;

public static class Probe
{
    public static async Task RunAsync(HisConnection db, Settings s)
    {
        Console.WriteLine();
        Console.WriteLine($"SetupID={s.SetupId}  ProjectID={s.ProjectId}");
        await PrintProjectAsync(db, s);

        Console.WriteLine();
        Console.WriteLine("Masters (this tenant)");
        await Row(db, s, "HIS_Clinic");
        await Row(db, s, "HIS_ClinicGroup");
        await Row(db, s, "INP_NursingStation");
        await Row(db, s, "INP_OTMain");
        await Row(db, s, "INP_Building");
        await Row(db, s, "INP_Bed");
        await Row(db, s, "HIS_Doctor");
        await Row(db, s, "HIS_Company");
        await Row(db, s, "HIS_Procedure");
        await Row(db, s, "HIS_Procedure", extra: "AND ISNULL(IsMedicine,0)=1", label: "HIS_Procedure  IsMedicine=1");
        await Row(db, s, "HIS_Procedure", extra: "AND ISNULL(IsPackage,0)=1", label: "HIS_Procedure  IsPackage=1");
        await Row(db, s, "HIS_ProcedurePrice");
        await Row(db, s, "HIS_DRGSpecification");
        await Row(db, s, "HIS_CostCenterSetup", byProject: false);
        await Row(db, s, "HIS_RuleCostCenterNew");
        await Row(db, s, "LAB_TestMain");
        await Row(db, s, "LAB_Division");

        if (s.From is { } from && s.To is { } to)
        {
            Console.WriteLine();
            Console.WriteLine($"Facts  {from:yyyy-MM-dd} .. {to.AddDays(-1):yyyy-MM-dd} inclusive");
            var period = "AND {0} >= @From AND {0} < @To";
            await PeriodRow(db, s, "HIS_Appointment", string.Format(period, "AppointmentDate"));
            await PeriodRow(db, s, "INP_Admission", string.Format(period, "AdmissionDate"));
            await PeriodRow(db, s, "INP_ERAssessment", string.Format(period, "AssessmentDate"));
            await PeriodRow(db, s, "HIS_InvoiceMain", string.Format(period, "InvoiceDate") + " AND CancelledOn IS NULL");
            try
            {
                await using var cmd = db.Cmd("""
                    SELECT COUNT_BIG(1)
                    FROM dbo.HIS_InvoiceDetail d WITH (NOLOCK)
                    INNER JOIN dbo.HIS_InvoiceMain m WITH (NOLOCK)
                      ON m.SetupID=d.SetupID AND m.ProjectID=d.ProjectID
                     AND m.TransactionType=d.TransactionType AND m.InvoiceNo=d.InvoiceNo
                    WHERE d.SetupID=@SetupId AND d.ProjectID=@ProjectId
                      AND m.InvoiceDate >= @From AND m.InvoiceDate < @To AND m.CancelledOn IS NULL
                    """);
                s.BindPeriod(cmd);
                Console.WriteLine($"  {"HIS_InvoiceDetail (via invoice date)",-44} {Convert.ToInt64(await cmd.ExecuteScalarAsync()),12:N0}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {"HIS_InvoiceDetail",-44}  ERROR: {ex.Message}");
            }
            await PeriodRow(db, s, "INP_OTReservation", string.Format(period, "OperationDate"));
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("Pass --from and --to (or choose facts/all) to see one-month volumes.");
        }

        Console.WriteLine();
        Console.WriteLine("Distributions (this tenant) — use these to finish enum maps");
        await Dist(db, s, "HIS_Company", "CompanyType");
        await Dist(db, s, "HIS_Appointment", "Status");
        await Dist(db, s, "HIS_InvoiceMain", "TransactionType");
        await Dist(db, s, "INP_Admission", "AdmissionType");
        await Dist(db, s, "HIS_Patient", "Gender");
        await Dist(db, s, "HIS_Patient", "PatientType");

        Console.WriteLine();
        Console.WriteLine("STP_Parameter sample (Appointment Status, gender, company type if present)");
        await ParametersAsync(db, s);
    }

    private static async Task PrintProjectAsync(HisConnection db, Settings s)
    {
        await db.QueryAsync("""
            SELECT p.Name, p.Alias, p.OraganisationCode, p.IsActive, p.ISOCityID, p.DefaultCurrencyID,
                   c.CityName, cur.ISOCurrencyID3
            FROM dbo.STP_Project p WITH (NOLOCK)
            LEFT JOIN dbo.STP_City c WITH (NOLOCK)
              ON c.SetupID = p.SetupID AND c.ISOCountryID = p.ISOCountryID AND c.ISOCityID = p.ISOCityID
            LEFT JOIN dbo.STP_Currency cur WITH (NOLOCK)
              ON cur.SetupID = p.SetupID AND cur.CurrencyID = p.DefaultCurrencyID
            WHERE p.SetupID = @SetupId AND p.ProjectID = @ProjectId
            """, s.BindTenant, r =>
        {
            Console.WriteLine($"  STP_Project.Name     = {HisConnection.S(r, "Name")}");
            Console.WriteLine($"  Alias                = {HisConnection.S(r, "Alias")}");
            Console.WriteLine($"  OraganisationCode    = {HisConnection.S(r, "OraganisationCode")}");
            Console.WriteLine($"  City                 = {HisConnection.S(r, "CityName")}");
            Console.WriteLine($"  Currency             = {HisConnection.S(r, "ISOCurrencyID3")}");
            Console.WriteLine($"  IsActive             = {HisConnection.B(r, "IsActive")}");
        });
    }

    private static async Task Row(HisConnection db, Settings s, string table, bool byProject = true, string extra = "", string? label = null)
    {
        try
        {
            var n = await db.CountAsync(s, table, byProject, extra);
            Console.WriteLine($"  {(label ?? table),-44} {n,12:N0}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {(label ?? table),-44}  ERROR: {ex.Message}");
        }
    }

    private static async Task PeriodRow(HisConnection db, Settings s, string from, string extra, string? label = null)
    {
        try
        {
            var sql = $"SELECT COUNT_BIG(1) FROM dbo.{from} WITH (NOLOCK) WHERE SetupID = @SetupId AND ProjectID = @ProjectId {extra}";
            if (from.Contains("INNER JOIN", StringComparison.OrdinalIgnoreCase))
                sql = $"SELECT COUNT_BIG(1) FROM dbo.{from} WHERE d.SetupID = @SetupId AND d.ProjectID = @ProjectId {extra}";
            await using var cmd = db.Cmd(sql);
            s.BindPeriod(cmd);
            var n = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            Console.WriteLine($"  {(label ?? from),-44} {n,12:N0}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {(label ?? from),-44}  ERROR: {ex.Message}");
        }
    }

    private static async Task Dist(HisConnection db, Settings s, string table, string col)
    {
        Console.WriteLine($"  {table}.{col}");
        try
        {
            await db.QueryAsync($"""
                SELECT TOP 20 {col} AS v, COUNT_BIG(1) AS n
                FROM dbo.{table} WITH (NOLOCK)
                WHERE SetupID = @SetupId AND ProjectID = @ProjectId
                GROUP BY {col}
                ORDER BY COUNT_BIG(1) DESC
                """, s.BindTenant, r =>
            {
                Console.WriteLine($"      {Convert.ToString(r.GetValue(0)),-12} {Convert.ToInt64(r.GetValue(1)),12:N0}");
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      ERROR: {ex.Message}");
        }
    }

    private static async Task ParametersAsync(HisConnection db, Settings s)
    {
        try
        {
            await db.QueryAsync("""
                SELECT TOP 80 ParameterGroup, ParameterType, ParameterCode, Description, Alias
                FROM dbo.STP_Parameter WITH (NOLOCK)
                WHERE SetupID = @SetupId AND ISNULL(IsActive,1)=1
                ORDER BY ParameterGroup, ParameterType, ParameterCode
                """, cmd => cmd.Parameters.AddWithValue("@SetupId", s.SetupId), r =>
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"      g={HisConnection.I(r, "ParameterGroup"),-4} t={HisConnection.I(r, "ParameterType"),-5} c={HisConnection.I(r, "ParameterCode"),-5} {HisConnection.S(r, "Description")}"));
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("      " + ex.Message);
        }
    }
}
