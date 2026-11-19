using Microsoft.Data.SqlClient;

namespace HisExtract;

public static class CatalogueExtract
{
    /// <summary>The catalogue's steps, in order, with the files each writes (by number prefix).</summary>
    public static readonly (string Step, string[] Files)[] Steps =
    [
        ("Hospital", ["01_"]), ("Facility", ["02_"]), ("Service line", ["03_"]), ("Department and cost centre", ["04_", "05_"]),
        ("Resource", ["11_"]), ("Provider", ["14_", "15_"]), ("Procedure", ["18_"]), ("Procedure rate", ["19_"]),
        ("Medicine price", ["28_"]), ("Package", ["24_", "25_"]), ("DRG", ["26_"]), ("Payer", ["27_"]),
    ];

    public static Task RunAsync(HisConnection db, Settings s, CsvZipWriter pack) => RunAsync(db, s, pack, null, null);

    /// <summary>
    /// Extract the catalogue. With <paramref name="done"/>, steps an interrupted run finished (their CSVs are still in
    /// the pack's folder) are kept; every other step clears its files and runs. <paramref name="stepDone"/> is told
    /// each finished step, so a run can record its checkpoint.
    /// </summary>
    public static async Task RunAsync(HisConnection db, Settings s, CsvZipWriter pack, IReadOnlySet<string>? done, Func<string, Task>? stepDone)
    {
        var c = new Codes(s);
        var asOf = DateTime.Today;
        pack.WriteManifest("catalogue", null, null);

        async Task Step(string name, Func<Task> body)
        {
            if (done?.Contains(name) == true)
            {
                pack.Note($"{name}: kept from the interrupted run.");
                return;
            }
            pack.ClearTables(Steps.First(x => x.Step == name).Files);
            pack.Step($"Catalogue: {name}");
            await body();
            if (stepDone is not null) await stepDone(name);
        }

        await Step("Hospital", () => HospitalAsync(db, s, c, pack));
        await Step("Facility", () => FacilityAsync(db, s, c, pack));
        await Step("Service line", () => ServiceLineAsync(db, s, c, pack));
        await Step("Department and cost centre", async () =>
        {
            var departments = await DepartmentAsync(db, s, c, pack);
            await CostCenterAsync(db, s, c, pack, departments);
        });
        await Step("Resource", () => ResourceAsync(c, pack));
        await Step("Provider", () => ProviderAsync(db, s, c, pack));
        await Step("Procedure", () => ProcedureAsync(db, s, c, pack));
        await Step("Procedure rate", () => ProcedureRateAsync(db, s, c, pack));
        await Step("Medicine price", () => MedicinePriceAsync(db, s, c, pack));
        await Step("Package", () => PackageAsync(db, s, c, pack));
        await Step("DRG", () => DrgExtract.CatalogueAsync(db, s, pack));
        await Step("Payer", () => PayerAsync(db, s, c, pack));
        pack.Note($"Catalogue extract finished {asOf:u}");
    }

    private static async Task HospitalAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("01_Hospital.csv", "code", "name", "city", "currency", "bedCount", "isActive");
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
            csv.Write(c.Hospital, name, HisConnection.S(r, "CityName"), currency, "",
                Maps.Bool(HisConnection.B(r, "IsActive")));
            n++;
        });
        if (n == 0)
            csv.Write(c.Hospital, s.H, "", "SAR", "", "true");
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

    private static async Task<Dictionary<string, string>> DepartmentAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var csv = pack.Create("04_Department.csv",
            "hospitalCode", "serviceLineCode", "code", "catalogCode", "name", "deptType", "specialty");
        void Add(params string?[] cells)
        {
            csv.Write(cells);
            names[cells[2] ?? ""] = cells[4] ?? "";
        }
        Add(c.Hospital, c.UnmappedSl, c.UnmappedDept, "UNMAPPED", "Unmapped", "ADMIN", "");

        var clinics = new HashSet<int>();
        await db.QueryAsync("""
            SELECT ClinicID, ClinicDescription, Alias, GroupID, MappingCode, IsActive
            FROM dbo.HIS_Clinic WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "ClinicID");
            if (!clinics.Add(id)) return;
            var name = HisConnection.S(r, "ClinicDescription");
            if (name.Length == 0) name = HisConnection.S(r, "Alias");
            if (name.Length == 0) name = c.ClinicDept(id);
            if (!HisConnection.B(r, "IsActive")) name += " (inactive)";
            var map = HisConnection.S(r, "MappingCode");
            var g = HisConnection.Ni(r, "GroupID");
            Add(c.Hospital, g is null ? c.UnmappedSl : c.ServiceLine(g.Value),
                c.ClinicDept(id), map.Length == 0 ? $"CLN-{id}" : map, name, "CLINICAL", name);
        });

        var extra = 0;
        await db.QueryAsync("""
            SELECT ClinicID FROM dbo.HIS_Appointment WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ClinicID > 0
            UNION
            SELECT ClinicID FROM dbo.INP_Admission WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ClinicID > 0
            UNION
            SELECT AdmittingClinicID FROM dbo.INP_Admission WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND AdmittingClinicID > 0
            UNION
            SELECT ClinicID FROM dbo.INP_ERAssessment WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND ClinicID > 0
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "ClinicID");
            if (!clinics.Add(id)) return;
            extra++;
            Add(c.Hospital, c.UnmappedSl, c.ClinicDept(id), $"CLN-{id}", $"Clinic {id}", "CLINICAL", "");
        });
        if (extra > 0) pack.Note($"Department: {extra} clinics referenced by encounters were missing from HIS_Clinic and were added.");

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
            Add(c.Hospital, c.UnmappedSl, c.WardDept(id), map.Length == 0 ? $"NS-{id}" : map, name, "CLINICAL", "Inpatient");
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
            Add(c.Hospital, c.UnmappedSl, c.OtDept(id), $"OT-{id}", name, "CLINICAL", "Surgery");
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
            Add(c.Hospital, c.UnmappedSl, c.LabDept(id), $"LAB-{id}", name, "ANCILLARY", "Laboratory");
        });
        return names;
    }

    private static async Task CostCenterAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack, IReadOnlyDictionary<string, string> departments)
    {
        using var csv = pack.Create("05_CostCenter.csv",
            "hospitalCode", "departmentCode", "facilityCode", "code", "name", "ccType", "stepDownSeq", "isActive");
        csv.Write(c.Hospital, c.UnmappedDept, "", c.UnmappedCc, "Unmapped", "OVERHEAD", "10", "true");

        // HIS cost centre id → (name, active), in HIS order.
        var centres = new Dictionary<string, (string Name, bool Active)>(StringComparer.OrdinalIgnoreCase);
        await db.QueryAsync("""
            SELECT CostCenterID, CostCenterName, IsActive
            FROM dbo.HIS_CostCenterSetup WITH (NOLOCK)
            WHERE SetupID=@SetupId
            """, cmd => cmd.Parameters.AddWithValue("@SetupId", s.SetupId), r =>
        {
            var raw = HisConnection.S(r, "CostCenterID");
            if (raw.Length == 0 || centres.ContainsKey(raw)) return;
            var name = HisConnection.S(r, "CostCenterName");
            centres[raw] = (name.Length == 0 ? raw : name, HisConnection.B(r, "IsActive"));
        });

        var billed = 0;
        await db.QueryAsync("""
            SELECT DISTINCT LTRIM(RTRIM(d.CostCenterID)) AS CostCenterID
            FROM dbo.HIS_InvoiceDetail d WITH (NOLOCK)
            INNER JOIN dbo.HIS_InvoiceMain m WITH (NOLOCK)
              ON m.SetupID=d.SetupID AND m.ProjectID=d.ProjectID
             AND m.TransactionType=d.TransactionType AND m.InvoiceNo=d.InvoiceNo
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId
              AND NULLIF(LTRIM(RTRIM(d.CostCenterID)), '') IS NOT NULL
            """, s.BindTenant, r =>
        {
            var raw = HisConnection.S(r, "CostCenterID");
            if (raw.Length == 0 || centres.ContainsKey(raw)) return;
            billed++;
            centres[raw] = (raw, true);
        });
        if (billed > 0) pack.Note($"CostCenter: {billed} Oracle codes appear on invoices but not in HIS_CostCenterSetup and were added.");

        var picks = await CostCentreDepartments.ResolveAsync(db, s, c,
            centres.ToDictionary(kv => kv.Key, kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase), departments, pack);
        foreach (var (raw, (name, active)) in centres)
        {
            var dept = picks.TryGetValue(raw, out var p) ? p.Department : c.UnmappedDept;
            csv.Write(c.Hospital, dept, "", c.OracleCc(raw), name, "PRODUCTION", "100", Maps.Bool(active));
        }
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
        using var csv = pack.Create("14_Provider.csv", "resourceCode", "departmentCode", "code", "name", "specialty", "grade", "isDummy");
        await db.QueryAsync("""
            SELECT d.DoctorID, d.DoctorName, d.Alias, d.EmploymentType, d.IsDoctorDummy,
                   (SELECT TOP 1 dc.ClinicID FROM dbo.HIS_DoctorClinic dc WITH (NOLOCK)
                    WHERE dc.SetupID=d.SetupID AND dc.ProjectID=d.ProjectID AND dc.DoctorID=d.DoctorID
                      AND ISNULL(dc.IsActive,1)=1
                    ORDER BY ISNULL(dc.IsDefaultClinic,0) DESC, dc.ClinicID) AS ClinicID,
                   sp.SpecialityID, sp.SpecialityName
            FROM dbo.HIS_Doctor d WITH (NOLOCK)
            -- Specialty names live in STP_Parameter group 1 type 10 (as Epharmacy_fn_HIS_GetDoctorSpecialityDescription reads them).
            OUTER APPLY (SELECT TOP 1 ds.SpecialityID, LTRIM(RTRIM(pm.Description)) AS SpecialityName
                         FROM dbo.HIS_DoctorSpeciality ds WITH (NOLOCK)
                         LEFT JOIN dbo.STP_Parameter pm WITH (NOLOCK)
                           ON pm.SetupID=ds.SetupID AND pm.ParameterGroup=1 AND pm.ParameterType=10
                          AND pm.ParameterCode=ds.SpecialityID
                         WHERE ds.SetupID=d.SetupID AND ds.ProjectID=d.ProjectID AND ds.DoctorID=d.DoctorID
                           AND ISNULL(ds.IsActive,1)=1
                         ORDER BY ds.SpecialityID) sp
            WHERE d.SetupID=@SetupId AND d.ProjectID=@ProjectId
              AND (ISNULL(d.IsActive,1)=1 OR ISNULL(d.IsDoctorDummy,0)=1)
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "DoctorID");
            var name = HisConnection.S(r, "DoctorName");
            if (name.Length == 0) name = HisConnection.S(r, "Alias");
            if (name.Length == 0) name = c.Provider(id);
            var spec = HisConnection.Ni(r, "SpecialityID");
            var specName = HisConnection.S(r, "SpecialityName");
            if (specName.Length > 200) specName = specName[..200];
            var clinicId = HisConnection.Ni(r, "ClinicID");
            // A dummy is a placeholder across clinics, not a person in one role: it stays Not assigned.
            var dummy = HisConnection.B(r, "IsDoctorDummy");
            csv.Write(dummy ? "" : PhysicianRoles.ResourceCode(c, PhysicianRoles.TokenFromClinic(clinicId)),
                c.DeptFromClinic(clinicId),
                c.Provider(id), name, specName.Length > 0 ? specName : spec is null ? "" : $"SPEC-{spec}",
                "ET-" + HisConnection.I(r, "EmploymentType"), Maps.Bool(dummy));
        });

        using var clinics = pack.Create("15_ProviderClinic.csv", "providerCode", "departmentCode", "hisClinicId", "isDefault", "isActive");
        await db.QueryAsync("""
            SELECT dc.DoctorID, dc.ClinicID, dc.IsDefaultClinic, ISNULL(dc.IsActive,1) AS IsActive
            FROM dbo.HIS_DoctorClinic dc WITH (NOLOCK)
            INNER JOIN dbo.HIS_Doctor d WITH (NOLOCK)
              ON d.SetupID=dc.SetupID AND d.ProjectID=dc.ProjectID AND d.DoctorID=dc.DoctorID
            WHERE dc.SetupID=@SetupId AND dc.ProjectID=@ProjectId
              AND (ISNULL(d.IsActive,1)=1 OR ISNULL(d.IsDoctorDummy,0)=1)
            """, s.BindTenant, r =>
        {
            var clinicId = HisConnection.I(r, "ClinicID");
            clinics.Write(c.Provider(HisConnection.I(r, "DoctorID")), c.ClinicDept(clinicId), $"{clinicId}",
                Maps.Bool(HisConnection.B(r, "IsDefaultClinic")), Maps.Bool(HisConnection.B(r, "IsActive")));
        });
    }

    private static async Task ProcedureAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("18_Procedure.csv",
            "hospitalCode", "departmentCode", "serviceLineCode", "code", "catalogCode", "name", "category", "procType", "complexity", "isActive",
            "hisCategoryId", "hisGroupId", "groupName", "hisSubGroupId", "subGroupName", "isPackage");
        await db.QueryAsync("""
            SELECT p.ProcedureID, p.ProcedureName, p.Alias, p.OracleItemCode, p.IsSurgeryProcedure, p.IsPackage,
                   p.SurgerySeverity, p.IsActive, p.CategoryID, p.GroupID, p.SubGroupID,
                   cat.CategoryDescription, grp.GroupDescription, sub.SubGroupDescription,
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
            LEFT JOIN dbo.HIS_ProcedureGroup grp WITH (NOLOCK)
              ON grp.SetupID=p.SetupID AND grp.ProjectID=p.ProjectID AND grp.GroupID=p.GroupID
            LEFT JOIN dbo.HIS_ProcedureSubGroup sub WITH (NOLOCK)
              ON sub.SetupID=p.SetupID AND sub.ProjectID=p.ProjectID AND sub.SubGroupID=p.SubGroupID
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
                Maps.Int(complexity), Maps.Bool(HisConnection.B(r, "IsActive")),
                HisConnection.S(r, "CategoryID"), HisConnection.S(r, "GroupID"), HisConnection.S(r, "GroupDescription"),
                HisConnection.S(r, "SubGroupID"), HisConnection.S(r, "SubGroupDescription"), Maps.Bool(HisConnection.B(r, "IsPackage")));
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

    /// <summary>
    /// Standard medicine prices from the pharmacy master, by HIS medicine code (the code medicine consumption carries).
    /// Read from {PharmacyDb}.dbo.PHR_Medicine under the pharmacy project, as HIS's own procedures do; without access to
    /// the pharmacy database, from the HIS database's own PHR_Medicine. Prices are as stored (per dispensing unit, before VAT).
    /// </summary>
    private static async Task MedicinePriceAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("28_MedicinePrice.csv", "hospitalCode", "medicineCode", "name", "genericName", "strength",
            "packQuantity", "quantityUnit", "stdSellingPrice", "stdPurchasePrice", "vat", "hisStatus", "source");
        static string Num(SqlDataReader r, string col) =>
            r.IsDBNull(r.GetOrdinal(col)) ? "" : Maps.Money(Maps.Round4(HisConnection.F(r, col)));
        var n = 0;
        void Row(SqlDataReader r)
        {
            var code = HisConnection.S(r, "MedicineCode").Trim();
            if (code.Length == 0) return;
            var name = HisConnection.S(r, "Description");
            if (name.Length == 0) name = HisConnection.S(r, "Alias");
            csv.Write(c.Hospital, code, name, HisConnection.S(r, "GenericName"), HisConnection.S(r, "Strength"),
                Num(r, "Quantity"), HisConnection.S(r, "QuantityUnit"), Num(r, "StdSellingPrice"), Num(r, "StdPurchasePrice"),
                Num(r, "VAT"), HisConnection.S(r, "Status"), "PHR_Medicine");
            n++;
        }
        const string columns = "m.MedicineCode, m.Description, m.Alias, m.GenericName, m.Strength, m.Quantity, m.QuantityUnit, m.StdSellingPrice, m.StdPurchasePrice, m.VAT, m.Status";
        var phr = s.PharmacyDb;
        if (!string.IsNullOrWhiteSpace(phr))
        {
            try
            {
                await db.QueryAsync($"""
                    WITH p AS (SELECT dbo.fn_Get_PharmaKonProjectID(@SetupId, @ProjectId) AS ProjectID)
                    SELECT {columns}
                    FROM [{phr}].dbo.PHR_Medicine m WITH (NOLOCK)
                    CROSS JOIN p
                    WHERE m.SetupID = @SetupId AND m.ProjectID = p.ProjectID
                    """, s.BindTenant, Row);
                pack.Note($"MedicinePrice: {n} medicines from {phr}.dbo.PHR_Medicine");
                return;
            }
            catch (SqlException ex)
            {
                pack.Warn($"Medicine prices from {phr}.dbo.PHR_Medicine; trying the HIS database's PHR_Medicine", ex);
                n = 0;
            }
        }
        try
        {
            // The HIS copy may be keyed on the hospital's project or on the pharmacy's (fn_Get_PharmaKonProjectID).
            await db.QueryAsync($"""
                WITH p AS (SELECT dbo.fn_Get_PharmaKonProjectID(@SetupId, @ProjectId) AS ProjectID),
                m AS (
                    SELECT m.*, ROW_NUMBER() OVER (PARTITION BY m.MedicineCode ORDER BY CASE WHEN m.ProjectID = p.ProjectID THEN 0 ELSE 1 END) AS rn
                    FROM dbo.PHR_Medicine m WITH (NOLOCK)
                    CROSS JOIN p
                    WHERE m.SetupID = @SetupId AND m.ProjectID IN (@ProjectId, p.ProjectID)
                )
                SELECT {columns} FROM m WHERE m.rn = 1
                """, s.BindTenant, Row);
            pack.Note($"MedicinePrice: {n} medicines from the HIS database's PHR_Medicine");
        }
        catch (SqlException ex)
        {
            // Optional: without prices, medicine that was not billed is left unvalued.
            pack.Warn("Medicine prices from PHR_Medicine", ex);
        }
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
            SELECT d.ProcedureID, d.ChildProcedureID, COUNT(*) AS Quantity
            FROM dbo.HIS_ProcedurePackageDetail d WITH (NOLOCK)
            INNER JOIN dbo.HIS_Procedure parent WITH (NOLOCK)
              ON parent.SetupID=d.SetupID AND parent.ProjectID=d.ProjectID AND parent.ProcedureID=d.ProcedureID
             AND ISNULL(parent.IsPackage,0)=1
            INNER JOIN dbo.HIS_Procedure child WITH (NOLOCK)
              ON child.SetupID=d.SetupID AND child.ProjectID=d.ProjectID AND child.ProcedureID=d.ChildProcedureID
             AND ISNULL(child.IsMedicine,0)=0
            WHERE d.SetupID=@SetupId AND d.ProjectID=@ProjectId AND ISNULL(d.IsActive,1)=1
            GROUP BY d.ProcedureID, d.ChildProcedureID
            """, s.BindTenant, r =>
        {
            // HIS repeats a procedure as one line per unit, so its quantity is the number of lines.
            var parent = HisConnection.S(r, "ProcedureID");
            var child = HisConnection.S(r, "ChildProcedureID");
            if (parent.Length == 0 || child.Length == 0) return;
            comp.Write(c.Procedure(parent), c.Procedure(child), Maps.Int(HisConnection.I(r, "Quantity")));
        });
    }

    private static async Task PayerAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("27_Payer.csv", "code", "name", "payerType", "contractRate",
            "hisGroupId", "hisGroupName", "hisGroupIsTpa", "hisGroupManagedByTpa");
        csv.Write(c.SelfPayer, "Self pay / cash", "SELF_PAY", "1", "", "", "", "");
        static string Flag(Microsoft.Data.SqlClient.SqlDataReader r, string col) =>
            HisConnection.S(r, col).Length == 0 ? "" : Maps.Bool(HisConnection.B(r, col));
        int grouped = 0, ungrouped = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { c.SelfPayer };
        await db.QueryAsync("""
            SELECT c.CompanyID, c.CompanyName, c.Alias, c.CompanyType, c.Status, c.GroupID,
                   g.GroupName, g.Alias AS GroupAlias, g.IsTPA, g.IsManagedByTPA
            FROM dbo.HIS_Company c WITH (NOLOCK)
            LEFT JOIN dbo.HIS_CompanyGroup g WITH (NOLOCK)
              ON g.SetupID=c.SetupID AND g.ProjectID=c.ProjectID AND g.GroupID=c.GroupID
            WHERE c.SetupID=@SetupId AND c.ProjectID=@ProjectId
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "CompanyID");
            var code = c.Payer(id);
            if (!seen.Add(code)) return;
            var name = HisConnection.S(r, "CompanyName");
            if (name.Length == 0) name = HisConnection.S(r, "Alias");
            if (name.Length == 0) name = code;
            var groupName = HisConnection.S(r, "GroupName");
            if (groupName.Length == 0) groupName = HisConnection.S(r, "GroupAlias");
            if (groupName.Length > 0) grouped++; else ungrouped++;
            csv.Write(code, name, Maps.PayerType(HisConnection.I(r, "CompanyType"), name), "1",
                groupName.Length > 0 ? HisConnection.S(r, "GroupID") : "", groupName, Flag(r, "IsTPA"), Flag(r, "IsManagedByTPA"));
        });
        pack.Note($"Payer: {grouped} companies under a HIS_CompanyGroup, {ungrouped} with none.");
    }
}
