using Microsoft.EntityFrameworkCore;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.Runs;

/// <summary>
/// Per package, what HIS billed, claimed and recorded on its lines in a run: cases, amount billed, doctor share, claims,
/// materials, medicine, staff time and ward days. Packages reads the stored rows instead of adding a month of lines on
/// every page; the package detail page reads the same lines for one package through <see cref="LoadAsync"/>.
/// </summary>
/// <remarks>
/// A package's lines are its own procedure line and (HIS) the child lines billed under it, which name the package in
/// PackageProcedureId ("0" is none). An inpatient package is billed as its children only (doctor fee, OR, consumables, …).
/// For a HIS hospital a line counts when its invoice line counts and was not refunded in full.
/// Consumption, medicine, staff time and ward stays tied to one of a package's lines are that package's. The rest of the
/// encounter's (HIS bills an admission's consumables and medication on general lines such as INPATIENT CONSUMABLE CHARGES)
/// is the episode's, shared evenly among the encounter's packages, so a one-package admission matches the encounter.
/// </remarks>
public static class PackageBillingSummary
{
    /// <summary>INV-2-7-1 and REV-2-7-1 are the same invoice line: the part after the prefix matches.</summary>
    public static string Tail(string sourceRef) => sourceRef.Length > 4 ? sourceRef[4..] : "";

    /// <summary>One of a package's lines. Own is the package procedure's own line; otherwise a child line billed under it.</summary>
    public sealed record Line(Guid Package, Guid Encounter, string Ref, bool Own, Guid? Procedure = null);

    /// <summary>A line's money. Net is billed including VAT (net of discount and refunds, plus tax), as the claim is.</summary>
    /// <param name="ClaimedOnClaimedInvoice">Claimed, counting only invoices HIS put on a claim (HIS_InvoiceMain.IsClaimed).</param>
    public sealed record LineMoney(decimal Net, decimal? Share, decimal Claimed, decimal Settled, decimal Rejected, decimal ClaimedOnClaimedInvoice = 0m);

    /// <summary>What one package's part of an encounter consumed and took. Medicine is billed including VAT; MedicineClaimed is HIS's claim on those lines.</summary>
    public sealed class Episode
    {
        public decimal Materials, MaterialsClaimed, MaterialsCovered, MedicineCovered, MedicineCoveredQty, Medicine, MedicineClaimed, MedicineLines, Staff, Nurse, Ward;
        public int MaterialItems;
        /// <summary>The claims on consumable and medicine lines, counting only invoices HIS put on a claim.</summary>
        public decimal MaterialsClaimedOnClaimed, MedicineClaimedOnClaimed;
    }

    /// <param name="ClaimFlagKnown">Whether the lines carry HIS_InvoiceMain.IsClaimed; facts extracted before it was added do not.</param>
    /// <param name="ByLine">The episodes again, split by the invoice line each figure came from (its <see cref="Tail"/> ref);
    /// "" for figures on no line (ward days, records with no line). Filled only when LoadAsync is asked for one encounter.</param>
    public sealed record Data(List<Line> Lines, Dictionary<(Guid Encounter, string Ref), LineMoney> Money,
        Dictionary<(Guid Encounter, string Ref), decimal> OwnQuantity, Dictionary<(Guid Package, Guid Encounter), Episode> Episodes, bool ClaimFlagKnown = false,
        Dictionary<(Guid Package, Guid Encounter, string Ref), Episode>? ByLine = null);

    /// <summary>The invoice of a line ref (Tail of the SourceRef: "{type}-{invoice}-{line}"): the ref without its line number.</summary>
    public static string Invoice(string? lineRef) =>
        lineRef is { Length: > 0 } r && r.LastIndexOf('-') is var i and > 0 ? r[..i] : "";

    /// <summary>Works the run's package figures out once and stores them: per package (Packages) and per package, encounter and doctor (the package page).</summary>
    public static async Task<List<RunPackageBilling>> StoreAsync(VeritaDbContext db, CalculationRun run, CancellationToken ct)
    {
        var hospital = await db.Hospitals.AsNoTracking().FirstAsync(h => h.Id == run.HospitalId, ct);
        var period = await db.Periods.AsNoTracking().FirstAsync(p => p.Id == run.PeriodId, ct);
        var data = await LoadAsync(db, run.Id, hospital, period, null, ct);
        var rows = Count(run.Id, data);
        var doctors = await DoctorRowsAsync(db, run.Id, data, ct);
        // Claims received per package: what its doctor rows add up to (package lines plus the encounter's shared consumable
        // and medicine claims, on invoices HIS put on a claim), so Packages matches the package page.
        if (data.ClaimFlagKnown)
        {
            var received = doctors.GroupBy(d => d.PackageId).ToDictionary(g => g.Key, g => g.Sum(d => d.ClaimsReceived ?? 0m));
            foreach (var r in rows) r.ClaimsReceived = Money.R4(received.GetValueOrDefault(r.PackageId));
        }
        await db.RunPackageBillings.Where(x => x.RunId == run.Id).ExecuteDeleteAsync(ct);
        await db.RunPackageDoctors.Where(x => x.RunId == run.Id).ExecuteDeleteAsync(ct);
        db.RunPackageBillings.AddRange(rows);
        db.RunPackageDoctors.AddRange(doctors);
        await db.SaveChangesAsync(ct);
        return rows;
    }

    /// <summary>
    /// Per package, encounter and doctor. A doctor is the doctor on the line (HIS PrimaryDoctorID). HIS bills some package
    /// lines with no doctor (meals, nursing, shared charges); on an encounter with a doctor on its other package lines they
    /// belong to that doctor (the one with the most lines), so the encounter is one case, not two. What an encounter consumed
    /// for the package is shared evenly among the doctors on its package lines, so the rows add up to the package's figures.
    /// </summary>
    public static async Task<List<RunPackageDoctor>> DoctorRowsAsync(VeritaDbContext db, Guid runId, Data data, CancellationToken ct)
    {
        if (data.Lines.Count == 0) return [];
        var providerOf = (await (from es in db.EncounterServices.AsNoTracking()
                                 join ec in db.EncounterCosts.AsNoTracking() on es.EncounterId equals ec.EncounterId
                                 where ec.RunId == runId
                                 select new { es.EncounterId, es.SourceRef, es.ProviderId }).ToListAsync(ct))
            .GroupBy(x => (x.EncounterId, Tail(x.SourceRef)))
            .ToDictionary(g => g.Key, g => g.Select(x => x.ProviderId).FirstOrDefault(x => x != null));
        var rows = new List<RunPackageDoctor>();
        foreach (var pkg in data.Lines.GroupBy(l => l.Package))
        {
            var byLine = pkg.Select(l => (Line: l, Doctor: providerOf.GetValueOrDefault((l.Encounter, l.Ref)))).ToList();
            var mainDoctor = byLine.Where(x => x.Doctor != null).GroupBy(x => x.Line.Encounter)
                .ToDictionary(g => g.Key, g => g.GroupBy(x => x.Doctor).OrderByDescending(d => d.Count()).First().Key);
            byLine = byLine.Select(x => x.Doctor == null && mainDoctor.TryGetValue(x.Line.Encounter, out var d) ? (x.Line, d) : x).ToList();
            foreach (var enc in byLine.GroupBy(x => x.Line.Encounter))
            {
                var doctors = enc.GroupBy(x => x.Doctor).ToList();
                var episode = data.Episodes.GetValueOrDefault((pkg.Key, enc.Key));
                var share = 1m / doctors.Count;
                foreach (var g in doctors)
                {
                    var m = g.Select(x => data.Money.GetValueOrDefault((x.Line.Encounter, x.Line.Ref))).OfType<LineMoney>().ToList();
                    var shares = m.Where(x => x.Share is not null).Select(x => x.Share!.Value).ToList();
                    decimal E(Func<Episode, decimal> f) => episode is null ? 0m : Money.R6(f(episode) * share);
                    rows.Add(new RunPackageDoctor
                    {
                        Id = IdGen.Sequential(), RunId = runId, PackageId = pkg.Key, EncounterId = enc.Key, ProviderId = g.Key,
                        Performed = Cases(data, g.Select(x => x.Line)),
                        FeeBilled = Money.R4(m.Sum(x => x.Net)), DoctorShare = shares.Count == 0 ? null : Money.R4(shares.Sum()),
                        Claimed = Money.R4(m.Sum(x => x.Claimed)), Settled = Money.R4(m.Sum(x => x.Settled)), Rejected = Money.R4(m.Sum(x => x.Rejected)),
                        ConsumablesBilled = Money.R4(E(e => e.Materials)), ConsumablesCovered = Money.R4(E(e => e.MaterialsCovered)),
                        ConsumablesClaimed = Money.R4(E(e => e.MaterialsClaimed)), ConsumableItems = E(e => e.MaterialItems),
                        MedicineBilled = Money.R4(E(e => e.Medicine)), MedicineCovered = Money.R4(E(e => e.MedicineCovered)),
                        MedicineCoveredQty = E(e => e.MedicineCoveredQty), MedicineLines = E(e => e.MedicineLines),
                        MedicineClaimed = Money.R4(E(e => e.MedicineClaimed)),
                        ClaimsReceived = data.ClaimFlagKnown
                            ? Money.R4(m.Sum(x => x.ClaimedOnClaimedInvoice) + E(e => e.MaterialsClaimedOnClaimed + e.MedicineClaimedOnClaimed))
                            : null,
                        ClaimsReceivedLines = data.ClaimFlagKnown ? Money.R4(m.Sum(x => x.ClaimedOnClaimedInvoice)) : null,
                        WardDays = E(e => e.Ward), StaffMinutes = E(e => e.Staff),
                    });
                }
            }
        }
        return rows;
    }

    public static List<RunPackageBilling> Count(Guid runId, Data data)
    {
        return data.Lines.GroupBy(l => l.Package).Select(g =>
        {
            var m = g.Select(l => data.Money.GetValueOrDefault((l.Encounter, l.Ref))).OfType<LineMoney>().ToList();
            var shares = m.Where(x => x.Share is not null).Select(x => x.Share!.Value).ToList();
            var encounters = g.Select(l => l.Encounter).Distinct().ToList();
            var episodes = encounters.Select(e => data.Episodes.GetValueOrDefault((g.Key, e))).OfType<Episode>().ToList();
            return new RunPackageBilling
            {
                Id = IdGen.Sequential(), RunId = runId, PackageId = g.Key, Encounters = encounters.Count, Cases = Cases(data, g),
                DoctorShare = shares.Count == 0 ? null : Money.R4(shares.Sum()),
                FeeBilled = Money.R4(m.Sum(x => x.Net)),
                MaterialsBilled = Money.R4(episodes.Sum(x => x.Materials)), MaterialItems = episodes.Sum(x => x.MaterialItems),
                MedicineBilled = Money.R4(episodes.Sum(x => x.Medicine)), MedicineQuantity = Money.R6(episodes.Sum(x => x.MedicineLines)),
                WardDays = Money.R6(episodes.Sum(x => x.Ward)), StaffMinutes = Money.R6(episodes.Sum(x => x.Staff)), NurseMinutes = Money.R6(episodes.Sum(x => x.Nurse)),
                Claimed = Money.R4(m.Sum(x => x.Claimed)), Settled = Money.R4(m.Sum(x => x.Settled)), Rejected = Money.R4(m.Sum(x => x.Rejected)),
            };
        }).ToList();
    }

    /// <summary>
    /// The billed quantity of the package's own lines, plus, for an encounter billed through the package's child lines only
    /// (an inpatient package carries no line of its own), the number of times the package was billed on it.
    /// </summary>
    /// <remarks>
    /// HIS leaves PackageSequenceNo empty, so a billing is a set of the package's child lines: each component (child
    /// procedure) appears a set number of times per billing, its fewest on any encounter of the package (chemotherapy
    /// administration twice, the doctor fee once). An encounter's billings are the most sets any component makes up, so
    /// a component missing from one set does not hide the session. At least one per encounter.
    /// </remarks>
    public static int Cases(Data data, IEnumerable<Line> lines)
    {
        var all = lines.ToList();
        if (all.Count == 0) return 0;
        var own = all.Where(l => l.Own).ToList();
        var ownEncounters = own.Select(l => l.Encounter).ToHashSet();
        var ownCases = (int)Math.Round(own.Sum(l => data.OwnQuantity.GetValueOrDefault((l.Encounter, l.Ref))), MidpointRounding.AwayFromZero);

        // Per set, from all the package's child lines (not only these, which may be one doctor's).
        var package = all[0].Package;
        var perSet = data.Lines.Where(l => l.Package == package && !l.Own && l.Procedure != null)
            .GroupBy(l => (l.Encounter, l.Procedure)).Select(g => (g.Key.Procedure, Count: g.Count()))
            .GroupBy(x => x.Procedure).ToDictionary(g => g.Key!.Value, g => g.Min(x => x.Count));
        var childCases = all.Where(l => !l.Own && !ownEncounters.Contains(l.Encounter)).GroupBy(l => l.Encounter)
            .Sum(e => Math.Max(1, e.Where(l => l.Procedure is { } p && perSet.ContainsKey(p)).GroupBy(l => l.Procedure!.Value)
                .Select(g => g.Count() / perSet[g.Key]).DefaultIfEmpty(1).Max()));
        return ownCases + childCases;
    }

    /// <summary>
    /// Every package's lines in the run, with their money and what each package's part of its encounters consumed.
    /// With <paramref name="onlyPackage"/>, only the encounters that billed that package are read (every package on them,
    /// so an encounter's shared items are split the same way as in the run's figures).
    /// </summary>
    /// <param name="onlyEncounter">Only this encounter's lines, with the figures also split by invoice line (Data.ByLine).</param>
    public static async Task<Data> LoadAsync(VeritaDbContext db, Guid runId, Hospital hospital, Period period, Guid? onlyPackage, CancellationToken ct,
        Guid? onlyEncounter = null)
    {
        var hid = hospital.Id;
        var (start, end) = (period.StartDate.ToDateTime(TimeOnly.MinValue), period.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var fromHis = !string.IsNullOrWhiteSpace(hospital.HisSetupId);
        var empty = new Data([], [], [], []);

        var packages = await (from pkg in db.Packages.AsNoTracking()
                              join pr in db.Procedures.AsNoTracking() on new { pkg.HospitalId, pkg.Code } equals new { pr.HospitalId, pr.Code } into pj
                              from pr in pj.DefaultIfEmpty()
                              where pkg.HospitalId == hid
                              select new { PackageId = pkg.Id, pkg.Code, ProcedureId = pr == null ? (Guid?)null : pr.Id }).ToListAsync(ct);
        if (packages.Count == 0) return empty;
        var pkgByProc = packages.Where(x => x.ProcedureId != null).GroupBy(x => x.ProcedureId!.Value).ToDictionary(g => g.Key, g => g.First().PackageId);
        var pkgByCode = packages.GroupBy(x => x.Code).ToDictionary(g => g.Key, g => g.First().PackageId);
        var procIds = pkgByProc.Keys.ToList();
        var codes = pkgByCode.Keys.ToList();

        var counted = db.RevenueTransactions.AsNoTracking().Where(HisInvoiceRules.Counts).Where(r => r.PostedAt >= start && r.PostedAt < end);
        var source = (fromHis ? counted : db.RevenueTransactions.AsNoTracking())
            .Where(r => db.EncounterCosts.Any(ec => ec.RunId == runId && ec.EncounterId == r.EncounterId));
        var services = from es in db.EncounterServices.AsNoTracking()
                       join ec in db.EncounterCosts.AsNoTracking() on es.EncounterId equals ec.EncounterId
                       where ec.RunId == runId
                       select es;
        if (onlyEncounter is { } oe)
        {
            source = source.Where(r => r.EncounterId == oe);
            services = services.Where(es => es.EncounterId == oe);
        }
        List<Guid>? only = null;
        if (onlyPackage is { } op)
        {
            var code = packages.First(x => x.PackageId == op).Code;
            var ownProc = packages.First(x => x.PackageId == op).ProcedureId;
            only = await source.Where(r => (ownProc != null && r.ProcedureId == ownProc) || r.PackageProcedureId == code).Select(r => r.EncounterId)
                .Union(services.Where(es => ownProc != null && es.ProcedureId == ownProc).Select(es => es.EncounterId))
                .Distinct().ToListAsync(ct);
            if (only.Count == 0) return empty;
            source = source.Where(r => only.Contains(r.EncounterId));
            services = services.Where(es => only.Contains(es.EncounterId));
        }

        var revenue = (await source
                .Where(r => (r.ProcedureId != null && procIds.Contains(r.ProcedureId.Value)) || (r.PackageProcedureId != null && codes.Contains(r.PackageProcedureId)))
                .Select(r => new { r.EncounterId, r.SourceRef, r.ProcedureId, r.PackageProcedureId, r.NetAmount, r.RefundedAmount, r.TaxAmount, r.DoctorShare, r.DoctorShareType, r.ClaimedAmount, r.SettledAmount, r.RejectedAmount, r.HisIsClaimed })
                .ToListAsync(ct))
            .Where(r => !fromHis || r.RefundedAmount == null || r.NetAmount <= 0m || r.RefundedAmount < r.NetAmount)
            .ToList();
        Guid? PackageOf(Guid? procedureId, string? packageCode) =>
            procedureId is { } pid && pkgByProc.TryGetValue(pid, out var own) ? own
            : packageCode is { Length: > 0 } c && c != "0" && pkgByCode.TryGetValue(c, out var parent) ? parent : null;

        var lines = new List<Line>();
        var claimFlagKnown = false;
        var money = new Dictionary<(Guid, string), LineMoney>();
        foreach (var r in revenue)
        {
            if (PackageOf(r.ProcedureId, r.PackageProcedureId) is not { } pkg) continue;
            var key = (r.EncounterId, Tail(r.SourceRef));
            lines.Add(new Line(pkg, r.EncounterId, key.Item2, r.ProcedureId is { } p && pkgByProc.ContainsKey(p), r.ProcedureId));
            // HIS share type: V = a value; P = a percentage of the line amount.
            decimal? share = r.DoctorShare is not { } v ? null
                : string.Equals(r.DoctorShareType, "P", StringComparison.OrdinalIgnoreCase) ? r.NetAmount * v / 100m : v;
            var m = money.GetValueOrDefault(key) ?? new LineMoney(0m, null, 0m, 0m, 0m);
            money[key] = new LineMoney(m.Net + r.NetAmount - (r.RefundedAmount ?? 0m) + (r.TaxAmount ?? 0m), share is null ? m.Share : (m.Share ?? 0m) + share,
                m.Claimed + (r.ClaimedAmount ?? 0m), m.Settled + (r.SettledAmount ?? 0m), m.Rejected + (r.RejectedAmount ?? 0m),
                m.ClaimedOnClaimedInvoice + (r.HisIsClaimed == true ? r.ClaimedAmount ?? 0m : 0m));
            if (r.HisIsClaimed is not null) claimFlagKnown = true;
        }
        var ownServices = await services.Where(es => procIds.Contains(es.ProcedureId)).Select(es => new { es.EncounterId, es.ProcedureId, es.SourceRef, es.Quantity }).ToListAsync(ct);
        var ownQuantity = ownServices.GroupBy(l => (l.EncounterId, Tail(l.SourceRef))).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        // Without HIS revenue lines, the package procedure's service lines are the package's lines.
        if (!fromHis) lines.AddRange(ownServices.Select(l => new Line(pkgByProc[l.ProcedureId], l.EncounterId, Tail(l.SourceRef), true, l.ProcedureId)));
        lines = lines.Distinct().ToList();
        if (lines.Count == 0) return empty;

        var encIds = lines.Select(l => l.Encounter).Distinct().ToList();
        var lineOwner = lines.GroupBy(l => (l.Encounter, l.Ref)).ToDictionary(g => g.Key, g => g.First().Package);
        var packagesOf = lines.GroupBy(l => l.Encounter).ToDictionary(g => g.Key, g => g.Select(l => l.Package).Distinct().ToList());
        var episodes = new Dictionary<(Guid, Guid), Episode>();
        Episode At(Guid pkg, Guid encounter) => episodes.TryGetValue((pkg, encounter), out var e) ? e : episodes[(pkg, encounter)] = new Episode();
        var byLine = onlyEncounter is null ? null : new Dictionary<(Guid, Guid, string), Episode>();
        void AddTo(Guid pkg, Guid encounter, string? serviceRef, Action<Episode, decimal> add, decimal f)
        {
            add(At(pkg, encounter), f);
            if (byLine is null) return;
            var key = (pkg, encounter, serviceRef is null ? "" : Tail(serviceRef));
            add(byLine.TryGetValue(key, out var le) ? le : byLine[key] = new Episode(), f);
        }
        void Add(Guid encounter, string? serviceRef, Action<Episode, decimal> add)
        {
            if (serviceRef is not null && lineOwner.TryGetValue((encounter, Tail(serviceRef)), out var owner)) { AddTo(owner, encounter, serviceRef, add, 1m); return; }
            if (!packagesOf.TryGetValue(encounter, out var pkgs)) return;
            foreach (var pkg in pkgs) AddTo(pkg, encounter, serviceRef, add, 1m / pkgs.Count);
        }

        // Materials: the invoice lines HIS consumption records point to (mostly INPATIENT CONSUMABLE CHARGES), each line once,
        // net of refunds plus VAT, and HIS's claim on them. A record's BilledAmount is its line's amount copied onto every item
        // on the line, so adding records would count a line once per item. Lines of medicine procedures are Medicine's.
        var consumption = await db.ConsumptionItems.AsNoTracking().Where(c => c.EncounterId != null && encIds.Contains(c.EncounterId.Value) && c.Category != "MEDICINE")
            .Select(c => new { Encounter = c.EncounterId!.Value, c.ServiceRef, c.HisUnitPrice }).ToListAsync(ct);
        foreach (var c in consumption) Add(c.Encounter, c.ServiceRef, (e, _) => e.MaterialItems++);
        var consumedLines = consumption.Where(c => c.ServiceRef != null).Select(c => (c.Encounter, Tail(c.ServiceRef!))).ToHashSet();
        var unbilledLines = new HashSet<(Guid, string)>();
        foreach (var r in (await (fromHis ? counted : db.RevenueTransactions.AsNoTracking())
                         .Where(r => encIds.Contains(r.EncounterId))
                         .Where(r => !db.Procedures.Any(pr => pr.Id == r.ProcedureId && pr.Category == "MEDICINE"))
                         .Select(r => new { r.EncounterId, r.SourceRef, r.NetAmount, r.RefundedAmount, r.TaxAmount, r.ClaimedAmount, r.HisIsClaimed }).ToListAsync(ct))
                     .Where(r => consumedLines.Contains((r.EncounterId, Tail(r.SourceRef))))
                     .Where(r => !fromHis || r.RefundedAmount == null || r.NetAmount <= 0m || r.RefundedAmount < r.NetAmount))
        {
            var net = r.NetAmount - (r.RefundedAmount ?? 0m) + (r.TaxAmount ?? 0m);
            if (r.NetAmount == 0m) unbilledLines.Add((r.EncounterId, Tail(r.SourceRef)));
            // A line billed under a package is one of its lines (Invoice Billed) already: not again as consumables.
            if (lineOwner.ContainsKey((r.EncounterId, Tail(r.SourceRef)))) continue;
            var onClaim = r.HisIsClaimed == true ? r.ClaimedAmount ?? 0m : 0m;
            Add(r.EncounterId, r.SourceRef, (e, f) => { e.Materials += net * f; e.MaterialsClaimed += (r.ClaimedAmount ?? 0m) * f; e.MaterialsClaimedOnClaimed += onClaim * f; });
        }
        // Covered: items on a counted line billed at nothing (the package price covers them), valued at HIS floor-consumption
        // UnitPrice, which is quantity × selling price before discount and VAT.
        foreach (var c in consumption.Where(c => c.ServiceRef != null && c.HisUnitPrice is > 0m && unbilledLines.Contains((c.Encounter, Tail(c.ServiceRef!)))))
            Add(c.Encounter, c.ServiceRef, (e, f) => e.MaterialsCovered += c.HisUnitPrice!.Value * f);
        // Medicine covered: medicine given but not billed on an invoice line (no billed amount; MED-BIL records are the billed
        // lines themselves), valued at the pharmacy master's standard selling price × quantity. The quantity counts every such
        // medicine; one with no price adds nothing to the amount.
        var prices = await db.MedicinePrices.AsNoTracking().Where(m => m.HospitalId == hid && m.StdSellingPrice != null)
            .ToDictionaryAsync(m => m.MedicineCode, m => m.StdSellingPrice!.Value, ct);
        foreach (var c in await db.ConsumptionItems.AsNoTracking()
                     .Where(c => c.EncounterId != null && encIds.Contains(c.EncounterId.Value) && c.Category == "MEDICINE" && c.ItemKeySystem == "HIS_MED"
                                 && !c.SourceRef.StartsWith("MED-BIL-") && (c.BilledAmount == null || c.BilledAmount == 0m) && c.Quantity > 0m)
                     .Select(c => new { Encounter = c.EncounterId!.Value, c.ServiceRef, c.ItemKey, c.Quantity }).ToListAsync(ct))
        {
            var price = prices.GetValueOrDefault(c.ItemKey);
            Add(c.Encounter, c.ServiceRef, (e, f) => { e.MedicineCovered += c.Quantity * price * f; e.MedicineCoveredQty += c.Quantity * f; });
        }
        // Medicine: invoice lines of medicine procedures (INPATIENT MEDICATION, vials, gels, …), net of refunds plus VAT, and HIS's claim on them.
        foreach (var r in await (fromHis ? counted : db.RevenueTransactions.AsNoTracking())
                     .Where(r => encIds.Contains(r.EncounterId))
                     .Join(db.Procedures.AsNoTracking().Where(pr => pr.HospitalId == hid && pr.Category == "MEDICINE"), r => r.ProcedureId, pr => pr.Id, (r, pr) => r)
                     .Where(r => !fromHis || r.RefundedAmount == null || r.NetAmount <= 0m || r.RefundedAmount < r.NetAmount)
                     .Select(r => new { r.EncounterId, r.SourceRef, Net = r.NetAmount - (r.RefundedAmount ?? 0m) + (r.TaxAmount ?? 0m), Claimed = r.ClaimedAmount ?? 0m, OnClaim = r.HisIsClaimed == true ? r.ClaimedAmount ?? 0m : 0m }).ToListAsync(ct))
        {
            // A medicine line billed under a package is one of its lines (Invoice Billed) already: not again as medicine.
            if (lineOwner.ContainsKey((r.EncounterId, Tail(r.SourceRef)))) continue;
            Add(r.EncounterId, r.SourceRef, (e, f) => { e.Medicine += r.Net * f; e.MedicineClaimed += r.Claimed * f; e.MedicineClaimedOnClaimed += r.OnClaim * f; e.MedicineLines += f; });
        }
        foreach (var st in await db.StaffTimes.AsNoTracking().Where(t => t.EncounterId != null && encIds.Contains(t.EncounterId.Value))
                     .Select(t => new { Encounter = t.EncounterId!.Value, t.ServiceRef, t.StaffRole, t.Minutes }).ToListAsync(ct))
        {
            var nurse = st.StaffRole.EndsWith("NURSE", StringComparison.OrdinalIgnoreCase);
            Add(st.Encounter, st.ServiceRef, (e, f) => { e.Staff += st.Minutes * f; if (nurse) e.Nurse += st.Minutes * f; });
        }
        // HIS INP_Transfer Duration is days on the ward; a stay belongs to the whole encounter.
        foreach (var w in await db.EncounterTransfers.AsNoTracking().Where(t => t.EncounterId != null && encIds.Contains(t.EncounterId.Value))
                     .GroupBy(t => t.EncounterId!.Value).Select(g => new { Encounter = g.Key, Days = g.Sum(t => t.Duration ?? 0) }).ToListAsync(ct))
            Add(w.Encounter, null, (e, f) => e.Ward += w.Days * f);

        return new Data(lines, money, ownQuantity, episodes, claimFlagKnown, byLine);
    }
}
