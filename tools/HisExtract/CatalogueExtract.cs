namespace HisExtract;

public static class CatalogueExtract
{
    public static async Task RunAsync(HisConnection db, Settings s, CsvZipWriter pack)
    {
        var c = new Codes(s);
        var asOf = DateTime.Today;
        pack.WriteManifest("catalogue", null, null);

        await HospitalAsync(db, s, c, pack);
        await FacilityAsync(db, s, c, pack);
        await ServiceLineAsync(db, s, c, pack);
        await DepartmentAsync(db, s, c, pack);
        await CostCenterAsync(db, s, c, pack);
        await ResourceAsync(c, pack);
        await ProviderAsync(db, s, c, pack);
        await ProcedureAsync(db, s, c, pack);
        await ProcedureRateAsync(db, s, c, pack);
        await PackageAsync(db, s, c, pack);
        await DrgAsync(db, s, pack);
        await PayerAsync(db, s, c, pack);
        pack.Note($"Catalogue extract finished {asOf:u}");
    }

    private static async Task HospitalAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("01_Hospital.csv", "code", "name", "city", "currency", "bedCount", "isActive");
        var beds = 0;
        await db.ScalarAsync(
            "SELECT COUNT(1) FROM dbo.INP_Bed WITH (NOLOCK) WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ISNULL(IsActive,1)=1",
            s.BindTenant, v => beds = Convert.ToInt32(v ?? 0));

        var n = 0;
        await db.QueryAsync("""
            SELECT p.Name, p.Alias, p.IsActive, c.CityName, cur.ISOCurrencyID3
            FROM dbo.STP_Project p WITH (NOLOCK)
            LEFT JOIN dbo.STP_City c WITH (NOLOCK)
              ON c.SetupID = p.SetupID AND c.ISOCountryID = p.ISOCountryID AND c.ISOCityID = p.ISOCityID
            LEFT JOIN dbo.STP_Currency cur WITH (NOLOCK)
              ON cur.SetupID = p.SetupID AND cur.CurrencyID = p.DefaultCurrencyID
            WHERE p.SetupID = @SetupId AND p.ProjectID = @ProjectId
            """, s.BindTenant, r =>
        {
            var name = HisConnection.S(r, "Name");
            if (name.Length == 0) name = HisConnection.S(r, "Alias");
            if (name.Length == 0) name = s.H;
            var currency = HisConnection.S(r, "ISOCurrencyID3");
            if (currency.Length == 0) currency = "SAR";
            csv.Write(c.Hospital, name, HisConnection.S(r, "CityName"), currency, Maps.Int(beds),
                Maps.Bool(HisConnection.B(r, "IsActive")));
            n++;
        });
        if (n == 0)
            csv.Write(c.Hospital, s.H, "", "SAR", Maps.Int(beds), "true");
    }

    private static async Task FacilityAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("02_Facility.csv", "hospitalCode", "code", "name", "facilityType", "squareMetres");
        await db.QueryAsync("""
            SELECT BuildingID, Description, IsActive
            FROM dbo.INP_Building WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ISNULL(IsActive,1)=1
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "BuildingID");
            var name = HisConnection.S(r, "Description");
            if (name.Length == 0) name = c.Facility(id);
            csv.Write(c.Hospital, c.Facility(id), name, "BUILDING", "0");
        });
    }

    private static async Task ServiceLineAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("03_ServiceLine.csv", "code", "name", "category");
        csv.Write(c.UnmappedSl, "Unmapped", "OTHER");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { c.UnmappedSl };
        await db.QueryAsync("""
            SELECT GroupID, GroupDescription, Alias, IsActive
            FROM dbo.HIS_ClinicGroup WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ISNULL(IsActive,1)=1
            """, s.BindTenant, r =>
        {
            var code = c.ServiceLine(HisConnection.I(r, "GroupID"));
            if (!seen.Add(code)) return;
            var name = HisConnection.S(r, "GroupDescription");
            if (name.Length == 0) name = HisConnection.S(r, "Alias");
            if (name.Length == 0) name = code;
            csv.Write(code, name, "CLINICAL");
        });
    }

    private static async Task DepartmentAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("04_Department.csv",
            "hospitalCode", "serviceLineCode", "code", "catalogCode", "name", "deptType", "specialty");
        csv.Write(c.Hospital, c.UnmappedSl, c.UnmappedDept, "UNMAPPED", "Unmapped", "ADMIN", "");

        await db.QueryAsync("""
            SELECT ClinicID, ClinicDescription, Alias, GroupID, MappingCode, IsActive
            FROM dbo.HIS_Clinic WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ISNULL(IsActive,1)=1
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "ClinicID");
            var name = HisConnection.S(r, "ClinicDescription");
            if (name.Length == 0) name = HisConnection.S(r, "Alias");
            if (name.Length == 0) name = c.ClinicDept(id);
            var map = HisConnection.S(r, "MappingCode");
            var g = HisConnection.Ni(r, "GroupID");
            csv.Write(c.Hospital, g is null ? c.UnmappedSl : c.ServiceLine(g.Value),
                c.ClinicDept(id), map.Length == 0 ? $"CLN-{id}" : map, name, "CLINICAL", name);
        });

        await db.QueryAsync("""
            SELECT NursingStationID, Description, Alias, MappingCode
            FROM dbo.INP_NursingStation WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ISNULL(IsActive,1)=1
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "NursingStationID");
            var name = HisConnection.S(r, "Description");
            if (name.Length == 0) name = HisConnection.S(r, "Alias");
            if (name.Length == 0) name = c.WardDept(id);
            var map = HisConnection.S(r, "MappingCode");
            csv.Write(c.Hospital, c.UnmappedSl, c.WardDept(id), map.Length == 0 ? $"NS-{id}" : map, name, "CLINICAL", "Inpatient");
        });

        await db.QueryAsync("""
            SELECT OTID, Description
            FROM dbo.INP_OTMain WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ISNULL(IsActive,1)=1
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "OTID");
            var name = HisConnection.S(r, "Description");
            if (name.Length == 0) name = c.OtDept(id);
            csv.Write(c.Hospital, c.UnmappedSl, c.OtDept(id), $"OT-{id}", name, "CLINICAL", "Surgery");
        });

        await db.QueryAsync("""
            SELECT DivisionID, DivisionName
            FROM dbo.LAB_Division WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ISNULL(IsActive,1)=1
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "DivisionID");
            var name = HisConnection.S(r, "DivisionName");
            if (name.Length == 0) name = c.LabDept(id);
            csv.Write(c.Hospital, c.UnmappedSl, c.LabDept(id), $"LAB-{id}", name, "ANCILLARY", "Laboratory");
        });
    }

    private static async Task CostCenterAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("05_CostCenter.csv",
            "hospitalCode", "departmentCode", "facilityCode", "code", "name", "ccType", "stepDownSeq", "isActive");
        csv.Write(c.Hospital, c.UnmappedDept, "", c.UnmappedCc, "Unmapped", "OVERHEAD", "10", "true");

        await db.QueryAsync("""
            SELECT ClinicID, ClinicDescription FROM dbo.HIS_Clinic WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ISNULL(IsActive,1)=1
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "ClinicID");
            var name = HisConnection.S(r, "ClinicDescription");
            csv.Write(c.Hospital, c.ClinicDept(id), "", c.ClinicCc(id),
                (name.Length == 0 ? c.ClinicCc(id) : name) + " CC", "PRODUCTION", "100", "true");
        });

        await db.QueryAsync("""
            SELECT NursingStationID, Description FROM dbo.INP_NursingStation WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ISNULL(IsActive,1)=1
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "NursingStationID");
            var name = HisConnection.S(r, "Description");
            csv.Write(c.Hospital, c.WardDept(id), "", c.WardCc(id),
                (name.Length == 0 ? c.WardCc(id) : name) + " CC", "PRODUCTION", "100", "true");
        });

        await db.QueryAsync("""
            SELECT OTID, Description FROM dbo.INP_OTMain WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ISNULL(IsActive,1)=1
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "OTID");
            csv.Write(c.Hospital, c.OtDept(id), "", c.OtCc(id),
                HisConnection.S(r, "Description") + " CC", "PRODUCTION", "80", "true");
        });

        await db.QueryAsync("""
            SELECT DivisionID, DivisionName FROM dbo.LAB_Division WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ISNULL(IsActive,1)=1
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "DivisionID");
            csv.Write(c.Hospital, c.LabDept(id), "", c.LabCc(id),
                HisConnection.S(r, "DivisionName") + " CC", "ANCILLARY", "90", "true");
        });

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await db.QueryAsync("""
            SELECT CostCenterID, CostCenterName, IsActive
            FROM dbo.HIS_CostCenterSetup WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ISNULL(IsActive,1)=1
            """, cmd => cmd.Parameters.AddWithValue("@SetupId", s.SetupId), r =>
        {
            var raw = HisConnection.S(r, "CostCenterID");
            if (raw.Length == 0 || !seen.Add(raw)) return;
            var name = HisConnection.S(r, "CostCenterName");
            if (name.Length == 0) name = raw;
            csv.Write(c.Hospital, c.UnmappedDept, "", c.OracleCc(raw), name, "PRODUCTION", "100",
                Maps.Bool(HisConnection.B(r, "IsActive")));
        });
    }

    private static Task ResourceAsync(Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("11_Resource.csv",
            "hospitalCode", "costCentreCode", "costPoolCode", "code", "name", "resourceType", "uom", "isCapacityDriven", "isActive");
        foreach (var role in PhysicianRoles.All)
            csv.Write(c.Hospital, PhysicianRoles.CostCentre(c, role), "",
                PhysicianRoles.ResourceCode(c, role.Token), role.Name, "LABOR_PHYSICIAN", "MIN", "true", "true");
        return Task.CompletedTask;
    }

    private static async Task ProviderAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("14_Provider.csv", "resourceCode", "departmentCode", "code", "name", "specialty", "grade");
        await db.QueryAsync("""
            SELECT d.DoctorID, d.DoctorName, d.Alias, d.EmploymentType,
                   (SELECT TOP 1 dc.ClinicID FROM dbo.HIS_DoctorClinic dc WITH (NOLOCK)
                    WHERE dc.SetupID=d.SetupID AND dc.ProjectID=d.ProjectID AND dc.DoctorID=d.DoctorID
                      AND ISNULL(dc.IsActive,1)=1
                    ORDER BY ISNULL(dc.IsDefaultClinic,0) DESC, dc.ClinicID) AS ClinicID,
                   (SELECT TOP 1 ds.SpecialityID FROM dbo.HIS_DoctorSpeciality ds WITH (NOLOCK)
                    WHERE ds.SetupID=d.SetupID AND ds.ProjectID=d.ProjectID AND ds.DoctorID=d.DoctorID
                      AND ISNULL(ds.IsActive,1)=1
                    ORDER BY ds.SpecialityID) AS SpecialityID
            FROM dbo.HIS_Doctor d WITH (NOLOCK)
            WHERE d.SetupID=@SetupId AND d.ProjectID=@ProjectId AND ISNULL(d.IsActive,1)=1
              AND ISNULL(d.IsDoctorDummy,0)=0
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "DoctorID");
            var name = HisConnection.S(r, "DoctorName");
            if (name.Length == 0) name = HisConnection.S(r, "Alias");
            if (name.Length == 0) name = c.Provider(id);
            var spec = HisConnection.Ni(r, "SpecialityID");
            var clinicId = HisConnection.Ni(r, "ClinicID");
            csv.Write(PhysicianRoles.ResourceCode(c, PhysicianRoles.TokenFromClinic(clinicId)),
                c.DeptFromClinic(clinicId),
                c.Provider(id), name, spec is null ? "" : $"SPEC-{spec}",
                "ET-" + HisConnection.I(r, "EmploymentType"));
        });
    }

    private static async Task ProcedureAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("18_Procedure.csv",
            "hospitalCode", "departmentCode", "serviceLineCode", "code", "catalogCode", "name", "category", "procType", "complexity", "isActive");
        await db.QueryAsync("""
            SELECT p.ProcedureID, p.ProcedureName, p.Alias, p.OracleItemCode, p.IsSurgeryProcedure, p.IsPackage,
                   p.SurgerySeverity, p.IsActive, p.CategoryID,
                   cat.CategoryDescription,
                   (SELECT TOP 1 m.ClinicID FROM dbo.HIS_ProcedureClinicMapping m WITH (NOLOCK)
                    WHERE m.SetupID=p.SetupID AND m.ProjectID=p.ProjectID AND m.ProcedureID=p.ProcedureID
                      AND ISNULL(m.IsActive,1)=1
                    ORDER BY m.ClinicID) AS ClinicID,
                   (SELECT TOP 1 t.DivisionID FROM dbo.LAB_TestMain t WITH (NOLOCK)
                    WHERE t.SetupID=p.SetupID AND t.ProjectID=p.ProjectID AND t.ProcedureID=p.ProcedureID) AS DivisionID,
                   CAST(CASE WHEN EXISTS (
                        SELECT 1 FROM dbo.LAB_TestMain t WITH (NOLOCK)
                        WHERE t.SetupID=p.SetupID AND t.ProjectID=p.ProjectID AND t.ProcedureID=p.ProcedureID)
                     THEN 1 ELSE 0 END AS bit) AS IsLab
            FROM dbo.HIS_Procedure p WITH (NOLOCK)
            LEFT JOIN dbo.HIS_ProcedureCategory cat WITH (NOLOCK)
              ON cat.SetupID=p.SetupID AND cat.ProjectID=p.ProjectID AND cat.CategoryID=p.CategoryID
            WHERE p.SetupID=@SetupId AND p.ProjectID=@ProjectId AND ISNULL(p.IsMedicine,0)=0
            """, s.BindTenant, r =>
        {
            var pid = HisConnection.S(r, "ProcedureID");
            if (pid.Length == 0) return;
            var name = HisConnection.S(r, "ProcedureName");
            if (name.Length == 0) name = HisConnection.S(r, "Alias");
            if (name.Length == 0) name = pid;
            var cat = HisConnection.S(r, "CategoryDescription");
            var lab = HisConnection.B(r, "IsLab");
            var div = HisConnection.Ni(r, "DivisionID");
            var clinic = HisConnection.Ni(r, "ClinicID");
            string dept;
            string sl = c.UnmappedSl;
            if (lab && div is not null) dept = c.LabDept(div.Value);
            else if (clinic is not null) dept = c.ClinicDept(clinic.Value);
            else dept = c.UnmappedDept;
            var catalog = HisConnection.S(r, "OracleItemCode");
            if (catalog.Length == 0) catalog = pid;
            var sev = HisConnection.I(r, "SurgerySeverity");
            var complexity = sev is >= 1 and <= 5 ? sev : 3;
            csv.Write(c.Hospital, dept, sl, c.Procedure(pid), catalog, name,
                cat.Length == 0 ? "General" : cat,
                Maps.ProcType(HisConnection.B(r, "IsSurgeryProcedure"), lab, cat, name),
                Maps.Int(complexity), Maps.Bool(HisConnection.B(r, "IsActive")));
        });
    }

    private static async Task ProcedureRateAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("19_ProcedureRate.csv", "procedureCode", "hospitalCode", "listPrice", "effectiveFrom");
        await db.QueryAsync("""
            SELECT ProcedureID, PublishedPrice, Price
            FROM dbo.HIS_Procedure WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ISNULL(IsMedicine,0)=0
            """, s.BindTenant, r =>
        {
            var pid = HisConnection.S(r, "ProcedureID");
            if (pid.Length == 0) return;
            var pub = HisConnection.F(r, "PublishedPrice");
            var price = pub != 0 ? pub : HisConnection.F(r, "Price");
            csv.Write(c.Procedure(pid), c.Hospital, Maps.Money(Maps.Round4(price)), "2000-01-01");
        });
    }

    private static async Task PackageAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var pkg = pack.Create("24_Package.csv",
            "hospitalCode", "code", "catalogCode", "name", "description", "price", "coveredLOS", "isActive");
        using var comp = pack.Create("25_PackageComponent.csv", "packageCode", "procedureCode", "quantity");

        await db.QueryAsync("""
            SELECT ProcedureID, ProcedureName, Alias, PublishedPrice, Price, LOS, IsActive, OracleItemCode
            FROM dbo.HIS_Procedure WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ISNULL(IsPackage,0)=1 AND ISNULL(IsMedicine,0)=0
            """, s.BindTenant, r =>
        {
            var pid = HisConnection.S(r, "ProcedureID");
            if (pid.Length == 0) return;
            var name = HisConnection.S(r, "ProcedureName");
            if (name.Length == 0) name = HisConnection.S(r, "Alias");
            var pub = HisConnection.F(r, "PublishedPrice");
            var price = pub != 0 ? pub : HisConnection.F(r, "Price");
            var catalog = HisConnection.S(r, "OracleItemCode");
            pkg.Write(c.Hospital, c.Procedure(pid), catalog.Length == 0 ? pid : catalog, name, "",
                Maps.Money(Maps.Round4(price)), Maps.Int(HisConnection.I(r, "LOS")),
                Maps.Bool(HisConnection.B(r, "IsActive")));
        });

        await db.QueryAsync("""
            SELECT d.ProcedureID, d.ChildProcedureID, d.LineItemNo
            FROM dbo.HIS_ProcedurePackageDetail d WITH (NOLOCK)
            INNER JOIN dbo.HIS_Procedure parent WITH (NOLOCK)
              ON parent.SetupID=d.SetupID AND parent.ProjectID=d.ProjectID AND parent.ProcedureID=d.ProcedureID
             AND ISNULL(parent.IsPackage,0)=1
            INNER JOIN dbo.HIS_Procedure child WITH (NOLOCK)
              ON child.SetupID=d.SetupID AND child.ProjectID=d.ProjectID AND child.ProcedureID=d.ChildProcedureID
             AND ISNULL(child.IsMedicine,0)=0
            WHERE d.SetupID=@SetupId AND d.ProjectID=@ProjectId AND ISNULL(d.IsActive,1)=1
            """, s.BindTenant, r =>
        {
            var parent = HisConnection.S(r, "ProcedureID");
            var child = HisConnection.S(r, "ChildProcedureID");
            if (parent.Length == 0 || child.Length == 0) return;
            comp.Write(c.Procedure(parent), c.Procedure(child), "1");
        });
    }

    private static async Task DrgAsync(HisConnection db, Settings s, CsvZipWriter pack)
    {
        using var csv = pack.Create("26_Drg.csv", "code", "name", "mdc", "relativeWt", "expectedLOS", "baseRate");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await db.QueryAsync("""
            SELECT DRGCode, DRGDescription, DRGWeight, AverageLOS, DRGBaseRate, DRGCodeSpecialty
            FROM dbo.HIS_DRGSpecification WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ISNULL(IsActive,1)=1
            """, s.BindTenant, r =>
        {
            var code = HisConnection.S(r, "DRGCode");
            if (code.Length == 0 || !seen.Add(code)) return;
            var name = HisConnection.S(r, "DRGDescription");
            if (name.Length == 0) name = code;
            csv.Write(code, name, HisConnection.S(r, "DRGCodeSpecialty"),
                Maps.Dec(Maps.Round4(HisConnection.F(r, "DRGWeight"))),
                Maps.Dec(Maps.Round4(HisConnection.F(r, "AverageLOS"))),
                Maps.Money(Maps.Round4(HisConnection.F(r, "DRGBaseRate"))));
        });
    }

    private static async Task PayerAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("27_Payer.csv", "code", "name", "payerType", "contractRate");
        csv.Write(c.SelfPayer, "Self pay / cash", "SELF_PAY", "1");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { c.SelfPayer };
        await db.QueryAsync("""
            SELECT CompanyID, CompanyName, Alias, CompanyType, Status
            FROM dbo.HIS_Company WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "CompanyID");
            var code = c.Payer(id);
            if (!seen.Add(code)) return;
            var name = HisConnection.S(r, "CompanyName");
            if (name.Length == 0) name = HisConnection.S(r, "Alias");
            if (name.Length == 0) name = code;
            csv.Write(code, name, Maps.PayerType(HisConnection.I(r, "CompanyType"), name), "1");
        });
    }
}
