namespace HisExtract;

/// <summary>
/// Camera of HIS-assigned DRGs. Catalogue from <c>HIS_DRGSpecification</c> (this project, else
/// SetupID-wide). Encounter codes from admission list, grouper log, day-care, then approval — never
/// inferred from procedure mappings.
/// </summary>
public static class DrgExtract
{
    public static string Lookup(IReadOnlyDictionary<int, string> map, int key) =>
        key > 0 && map.TryGetValue(key, out var code) ? code : "";

    public static async Task CatalogueAsync(HisConnection db, Settings s, CsvZipWriter pack)
    {
        using var csv = pack.Create("26_Drg.csv", "code", "name", "mdc", "relativeWt", "expectedLOS", "baseRate");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Emit(string code, string name, string mdc, decimal wt, decimal los, decimal rate)
        {
            if (code.Length == 0 || !seen.Add(code)) return;
            if (name.Length == 0) name = code;
            csv.Write(code, name, mdc, Maps.Dec(wt), Maps.Dec(los), Maps.Money(rate));
        }

        await SpecAsync(db, s, true, Emit);
        var fromProject = seen.Count;
        if (fromProject == 0)
        {
            await SpecAsync(db, s, false, Emit);
            if (seen.Count > 0)
                pack.Note($"Drg: HIS_DRGSpecification empty for ProjectID={s.ProjectId}; used SetupID-wide catalogue ({seen.Count} codes).");
        }
        var fromSpec = seen.Count;

        await Safe(pack, "INP_AdmissionDRGList", () => db.QueryAsync("""
            SELECT DRGCode, MAX(DRGName) DRGName, MAX(DRGCategoryCode) DRGCategoryCode,
                   MAX(DRGWeight) DRGWeight, MAX(ALOS) ALOS
            FROM dbo.INP_AdmissionDRGList WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId
            GROUP BY DRGCode
            """, s.BindTenant, r => Emit(
            HisConnection.S(r, "DRGCode"), HisConnection.S(r, "DRGName"), HisConnection.S(r, "DRGCategoryCode"),
            Num(r, "DRGWeight"), Num(r, "ALOS"), 0m)));

        await Safe(pack, "HIS_DRGLog", () => db.QueryAsync("""
            SELECT DRGCode, MAX(DRGName) DRGName, MAX(DRGCategoryCode) DRGCategoryCode,
                   MAX(DRGWeight) DRGWeight, MAX(ALOS) ALOS
            FROM dbo.HIS_DRGLog WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId
            GROUP BY DRGCode
            """, s.BindTenant, r => Emit(
            HisConnection.S(r, "DRGCode"), HisConnection.S(r, "DRGName"), HisConnection.S(r, "DRGCategoryCode"),
            Num(r, "DRGWeight"), Num(r, "ALOS"), 0m)));

        await Safe(pack, "HIS_DRGDayCare", () => db.QueryAsync("""
            SELECT DRGCode, MAX(DRGWeight) DRGWeight, MAX(DRGBaseRate) DRGBaseRate
            FROM dbo.HIS_DRGDayCare WITH (NOLOCK)
            WHERE SetupId=@SetupId AND ProjectId=@ProjectId AND ISNULL(IsActive,1)=1
            GROUP BY DRGCode
            """, s.BindTenant, r => Emit(
            HisConnection.S(r, "DRGCode"), "", "", Num(r, "DRGWeight"), 0m, Num(r, "DRGBaseRate"))));

        await Safe(pack, "HIS_ApprovalDRG", () => db.QueryAsync("""
            SELECT DRGCode, MAX(DRGWeight) DRGWeight, MAX(DRGBaseRate) DRGBaseRate
            FROM dbo.HIS_ApprovalDRG WITH (NOLOCK)
            WHERE SetupId=@SetupId AND ProjectId=@ProjectId AND ISNULL(IsActive,1)=1
            GROUP BY DRGCode
            """, s.BindTenant, r => Emit(
            HisConnection.S(r, "DRGCode"), "", "", Num(r, "DRGWeight"), 0m, Num(r, "DRGBaseRate"))));

        pack.Note($"Drg: {seen.Count} codes ({fromSpec} from HIS_DRGSpecification).");
    }

    public static async Task<Dictionary<int, string>> ByAdmissionAsync(HisConnection db, Settings s, CsvZipWriter pack)
    {
        var map = new Dictionary<int, string>();
        await Safe(pack, "admission list", () => db.QueryAsync("""
            SELECT AdmissionNo, DRGCode
            FROM dbo.INP_AdmissionDRGList WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId
              AND NULLIF(LTRIM(RTRIM(DRGCode)),'') IS NOT NULL
            ORDER BY CASE WHEN Status=3 THEN 0 ELSE 1 END, LineItemNo DESC
            """, s.BindTenant, r => Add(map, HisConnection.I(r, "AdmissionNo"), HisConnection.S(r, "DRGCode"))));
        var fromList = map.Count;

        await Safe(pack, "grouper log (discharge)", () => db.QueryAsync("""
            SELECT ds.AdmissionNo, g.DRGCode
            FROM dbo.HIS_DRGLog g WITH (NOLOCK)
            INNER JOIN dbo.INP_DischargeSummary ds WITH (NOLOCK)
              ON ds.SetupID=g.SetupID AND ds.ProjectID=g.ProjectID AND ds.DischargeNo=g.TransactionRefNo
            WHERE g.SetupID=@SetupId AND g.ProjectID=@ProjectId AND g.TransactionType=3
              AND NULLIF(LTRIM(RTRIM(g.DRGCode)),'') IS NOT NULL
            ORDER BY g.LineItemNo DESC
            """, s.BindTenant, r => Add(map, HisConnection.I(r, "AdmissionNo"), HisConnection.S(r, "DRGCode"))));

        await Safe(pack, "grouper log (admission)", () => db.QueryAsync("""
            SELECT TransactionRefNo AS AdmissionNo, DRGCode
            FROM dbo.HIS_DRGLog WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND TransactionType=2
              AND NULLIF(LTRIM(RTRIM(DRGCode)),'') IS NOT NULL
            ORDER BY LineItemNo DESC
            """, s.BindTenant, r => Add(map, HisConnection.I(r, "AdmissionNo"), HisConnection.S(r, "DRGCode"))));

        await Safe(pack, "day-care", () => db.QueryAsync("""
            SELECT AdmissionNo, DRGCode
            FROM dbo.HIS_DRGDayCare WITH (NOLOCK)
            WHERE SetupId=@SetupId AND ProjectId=@ProjectId AND ISNULL(IsActive,1)=1 AND AdmissionNo > 0
              AND NULLIF(LTRIM(RTRIM(DRGCode)),'') IS NOT NULL
            ORDER BY LineItemNo DESC
            """, s.BindTenant, r => Add(map, HisConnection.I(r, "AdmissionNo"), HisConnection.S(r, "DRGCode"))));

        await Safe(pack, "approval (IP)", () => db.QueryAsync("""
            SELECT m.AdmissionNo, d.DRGCode
            FROM dbo.HIS_ApprovalDRG d WITH (NOLOCK)
            INNER JOIN dbo.HIS_ApprovalMain m WITH (NOLOCK)
              ON m.SetupID=d.SetupId AND m.ProjectID=d.ProjectId AND m.ApprovalNo=d.ApprovalNo
            WHERE d.SetupId=@SetupId AND d.ProjectId=@ProjectId AND ISNULL(d.IsActive,1)=1
              AND m.AdmissionNo > 0 AND NULLIF(LTRIM(RTRIM(d.DRGCode)),'') IS NOT NULL
            ORDER BY d.LineItemNo DESC
            """, s.BindTenant, r => Add(map, HisConnection.I(r, "AdmissionNo"), HisConnection.S(r, "DRGCode"))));

        pack.Note($"Drg on IP: {map.Count} admissions ({fromList} from INP_AdmissionDRGList).");
        return map;
    }

    public static async Task<Dictionary<int, string>> ByAppointmentAsync(HisConnection db, Settings s, CsvZipWriter pack)
    {
        var map = new Dictionary<int, string>();
        await Safe(pack, "approval (OP)", () => db.QueryAsync("""
            SELECT m.AppointmentNo, d.DRGCode
            FROM dbo.HIS_ApprovalDRG d WITH (NOLOCK)
            INNER JOIN dbo.HIS_ApprovalMain m WITH (NOLOCK)
              ON m.SetupID=d.SetupId AND m.ProjectID=d.ProjectId AND m.ApprovalNo=d.ApprovalNo
            WHERE d.SetupId=@SetupId AND d.ProjectId=@ProjectId AND ISNULL(d.IsActive,1)=1
              AND m.AppointmentNo > 0 AND NULLIF(LTRIM(RTRIM(d.DRGCode)),'') IS NOT NULL
            ORDER BY d.LineItemNo DESC
            """, s.BindTenant, r => Add(map, HisConnection.I(r, "AppointmentNo"), HisConnection.S(r, "DRGCode"))));
        pack.Note($"Drg on OP: {map.Count} appointments from HIS_ApprovalDRG.");
        return map;
    }

    private static async Task SpecAsync(HisConnection db, Settings s, bool byProject,
        Action<string, string, string, decimal, decimal, decimal> emit)
    {
        var where = byProject
            ? "WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ISNULL(IsActive,1)=1"
            : "WHERE SetupID=@SetupId AND ISNULL(IsActive,1)=1";
        await db.QueryAsync($"""
            SELECT DRGCode, DRGDescription, DRGWeight, AverageLOS, DRGBaseRate, DRGCodeSpecialty
            FROM dbo.HIS_DRGSpecification WITH (NOLOCK)
            {where}
            """, s.BindTenant, r => emit(
            HisConnection.S(r, "DRGCode"), HisConnection.S(r, "DRGDescription"), HisConnection.S(r, "DRGCodeSpecialty"),
            Num(r, "DRGWeight"), Num(r, "AverageLOS"), Num(r, "DRGBaseRate")));
    }

    private static void Add(Dictionary<int, string> map, int key, string code)
    {
        if (key <= 0 || code.Length == 0) return;
        map.TryAdd(key, code);
    }

    private static decimal Num(Microsoft.Data.SqlClient.SqlDataReader r, string col)
    {
        try { return Maps.Round4(HisConnection.F(r, col)); }
        catch { return 0m; }
    }

    private static async Task Safe(CsvZipWriter pack, string label, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex) { pack.Note($"Drg {label} skipped: {ex.Message}"); }
    }
}
