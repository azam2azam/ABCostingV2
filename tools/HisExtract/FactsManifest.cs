namespace HisExtract;

/// <summary>One encounter of a day's list: its HIS key and the group it must be synced with.</summary>
/// <param name="Type">IP (AdmissionNo), OP (AppointmentNo) or ER (AssessmentNo).</param>
public sealed record ManifestItem(string Type, int No, int Group);

/// <summary>A day's encounters for the sync worker, with the ER ↔ appointment links read once for the whole day.</summary>
public sealed record FactsManifest(IReadOnlyList<ManifestItem> Items, IReadOnlyList<ErLink> ErLinks)
{
    public int Count => Items.Count;

    /// <summary>
    /// Batches of about <paramref name="size"/> encounters, in group order. A group (an ER visit and its appointment)
    /// is never split, so a batch can run over the size by the rest of a group.
    /// </summary>
    public IEnumerable<(ManifestItem Item, int Batch)> Batches(int size)
    {
        int batch = 1, inBatch = 0;
        foreach (var group in Items.GroupBy(i => i.Group).OrderBy(g => g.Key))
        {
            var members = group.ToList();
            if (inBatch > 0 && inBatch + members.Count > size) { batch++; inBatch = 0; }
            foreach (var item in members) yield return (item, batch);
            inBatch += members.Count;
        }
    }

    /// <summary>The keys and ER links of some of the day's encounters, as one batch.</summary>
    public FactBatch BatchOf(IEnumerable<ManifestItem> items)
    {
        var list = items.ToList();
        var er = list.Where(i => i.Type == "ER").Select(i => i.No).ToHashSet();
        return new FactBatch(
            list.Where(i => i.Type == "IP").Select(i => i.No).ToArray(),
            list.Where(i => i.Type == "OP").Select(i => i.No).ToArray(),
            [.. er],
            ErLinks.Where(l => er.Contains(l.Assessment)).ToArray());
    }

    /// <summary>
    /// The day's encounters: the same set a whole-day pack covers — admitted, seen or assessed that day, or invoiced
    /// that day — plus the appointments ER visits bill through, and ER visits dated outside the day whose appointment
    /// is in it. Only keys are read; one batch at a time then reads the rows.
    /// </summary>
    public static async Task<FactsManifest> BuildAsync(HisConnection db, Settings s)
    {
        if (s.Batch is not null) throw new InvalidOperationException("Build the day's list with no batch set.");
        await ConsumptionExtract.EncounterSetsAsync(db, s);

        // Links for ER visits near the day, the same reads as the whole-day Encounter file.
        var byInvoice = new Dictionary<int, (int Appointment, int? Company)>();
        await db.QueryAsync("""
            SELECT e.AssessmentNo, m.AppointmentNo, m.CompanyID
            FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
            INNER JOIN dbo.INP_ERAssessment e WITH (NOLOCK)
              ON e.SetupID=m.SetupID AND e.ProjectID=m.ProjectID AND e.InvoiceNo=m.InvoiceNo AND e.PatientID=m.PatientID
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AppointmentNo > 0
              AND m.InvoiceDate >= DATEADD(month,-3,@From) AND m.InvoiceDate < DATEADD(month,3,@To)
              AND m.CancelledOn IS NULL AND e.InvoiceNo > 0
            OPTION (HASH JOIN)
            """, s.BindPeriod, r => byInvoice[HisConnection.I(r, "AssessmentNo")] = (HisConnection.I(r, "AppointmentNo"), HisConnection.Ni(r, "CompanyID")));
        var byAppointmentId = new Dictionary<int, int>();
        await db.QueryAsync("""
            SELECT e.AssessmentNo, ap.AppointmentNo
            FROM dbo.INP_ERAssessment e WITH (NOLOCK)
            INNER JOIN dbo.HIS_Appointment ap WITH (NOLOCK)
              ON ap.SetupID=e.SetupID AND ap.ProjectID=e.ProjectID AND ap.AppointmentNo=e.AppointmentID AND ap.PatientID=e.PatientID
            WHERE e.SetupID=@SetupId AND e.ProjectID=@ProjectId AND e.PatientID > 0 AND e.AppointmentID > 0
              AND e.AssessmentDate >= DATEADD(month,-3,@From) AND e.AssessmentDate < DATEADD(month,3,@To)
            """, s.BindPeriod, r => byAppointmentId[HisConnection.I(r, "AssessmentNo")] = HisConnection.I(r, "AppointmentNo"));

        var ip = await KeysAsync(db, "#ip");
        var op = await KeysAsync(db, "#op");
        var er = await KeysAsync(db, "#er");

        // ER visits dated outside the day still belong to it when their appointment is the day's (seen or invoiced that day).
        var dayAppointments = new HashSet<int>();
        await db.QueryAsync("""
            SELECT a.AppointmentNo FROM dbo.HIS_Appointment a WITH (NOLOCK)
            WHERE a.SetupID=@SetupId AND a.ProjectID=@ProjectId AND a.PatientID > 0
              AND a.AppointmentDate >= @From AND a.AppointmentDate < @To
            UNION
            SELECT m.AppointmentNo FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AppointmentNo > 0
              AND m.InvoiceDate >= @From AND m.InvoiceDate < @To AND m.CancelledOn IS NULL
            """, s.BindPeriod, r => dayAppointments.Add(HisConnection.I(r, "AppointmentNo")));
        await db.QueryAsync("""
            SELECT e.AssessmentNo
            FROM dbo.INP_ERAssessment e WITH (NOLOCK)
            WHERE e.SetupID=@SetupId AND e.ProjectID=@ProjectId AND e.PatientID > 0
              AND e.AssessmentDate >= DATEADD(month,-3,@From) AND e.AssessmentDate < DATEADD(month,3,@To)
              AND (e.AssessmentDate < @From OR e.AssessmentDate >= @To)
            """, s.BindPeriod, r =>
        {
            var a = HisConnection.I(r, "AssessmentNo");
            if (er.Contains(a)) return;
            int? linked = byAppointmentId.TryGetValue(a, out var x) ? x : byInvoice.TryGetValue(a, out var y) ? y.Appointment : null;
            if (linked is { } appointment && dayAppointments.Contains(appointment))
            {
                er.Add(a);
                op.Add(appointment);
            }
        });

        // Every appointment an ER visit of the day bills through is synced with it, in the same batch.
        foreach (var a in er)
        {
            if (byAppointmentId.TryGetValue(a, out var x)) op.Add(x);
            if (byInvoice.TryGetValue(a, out var y)) op.Add(y.Appointment);
        }

        var links = er.Where(a => byAppointmentId.ContainsKey(a) || byInvoice.ContainsKey(a))
            .Select(a => new ErLink(a,
                byAppointmentId.TryGetValue(a, out var x) ? x : null,
                byInvoice.TryGetValue(a, out var y) ? y.Appointment : null,
                byInvoice.TryGetValue(a, out var z) ? z.Company : null))
            .OrderBy(l => l.Assessment).ToList();

        // Groups: each admission alone; an ER visit with the appointment(s) it bills through. Appointments shared by
        // several ER visits pull those visits into one group.
        var parent = new Dictionary<string, string>();
        string Find(string k)
        {
            if (!parent.TryGetValue(k, out var p)) { parent[k] = k; return k; }
            if (p == k) return k;
            var root = Find(p);
            parent[k] = root;
            return root;
        }
        void Union(string a, string b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }
        foreach (var l in links)
        {
            if (l.AppointmentId is { } x && op.Contains(x)) Union($"ER:{l.Assessment}", $"OP:{x}");
            if (l.InvoiceAppointment is { } y && op.Contains(y)) Union($"ER:{l.Assessment}", $"OP:{y}");
        }

        var items = ip.Order().Select(n => ("IP", n))
            .Concat(op.Order().Select(n => ("OP", n)))
            .Concat(er.Order().Select(n => ("ER", n)))
            .ToList();
        var groupOf = new Dictionary<string, int>();
        var result = new List<ManifestItem>(items.Count);
        foreach (var (type, no) in items)
        {
            var root = Find($"{type}:{no}");
            if (!groupOf.TryGetValue(root, out var g)) groupOf[root] = g = groupOf.Count + 1;
            result.Add(new ManifestItem(type, no, g));
        }
        return new FactsManifest(result, links);
    }

    private static async Task<HashSet<int>> KeysAsync(HisConnection db, string table)
    {
        var keys = new HashSet<int>();
        await db.QueryAsync($"SELECT No FROM {table}", _ => { }, r => keys.Add(HisConnection.I(r, "No")));
        return keys;
    }
}
