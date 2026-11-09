using Verita.Application;
using Verita.Application.Assistant;
using Verita.Infrastructure.DataHub;
using Verita.Infrastructure.Queries;
using static Verita.Application.Assistant.AssistantText;

namespace Verita.Infrastructure.Assistant;

public sealed record ReplyBlock(string Type, string? Title, string[] Columns, string[][] Rows, string? Note = null);
public sealed record ReplyLink(string Label, string Path, Dictionary<string, string> Query);
public sealed record ReplyFigure(string Key, string Label, decimal Value, string Unit);

/// <summary>What a handler produced for one scope. <see cref="Figures"/> are what a comparison lines up.</summary>
public sealed class Answer
{
    public string Text { get; set; } = "";
    public List<ReplyBlock> Blocks { get; } = [];
    public List<ReplyLink> Links { get; } = [];
    public List<ReplyFigure> Figures { get; } = [];
    public List<string> Terms { get; } = [];
    public List<string> Notes { get; } = [];
    public ReplyLink? Navigate { get; set; }
}

/// <summary>
/// One handler per intent. Every number comes from the same read query the matching screen
/// uses, with the caller's own principal, so masking and scope rules apply unchanged.
/// </summary>
public sealed class AssistantAnswers(ReadQueries q, DataHubService hub)
{
    private const int DefaultLimit = 10, MaxLimit = 50;

    public async Task<Answer> AnswerAsync(Principal p, Interpretation it, ScopedRun? s, CancellationToken ct)
    {
        var a = new Answer();
        switch (it.Intent)
        {
            case "dashboard.summary": await Dashboard(a, p, it, s!, ct); break;
            case "procedure.cost": await ProcedureCost(a, p, it, s!, ct); break;
            case "procedure.top": await ProcedureTop(a, p, it, s!, ct); break;
            case "package.cost": await PackageCost(a, p, it, s!, ct); break;
            case "package.losing": await PackagesLosing(a, p, it, s!, ct); break;
            case "profitability.by": await Profitability(a, p, it, s!, ct); break;
            case "variance.top": await Variances(a, it, s!, ct); break;
            case "billing.nonbillable": await NonBillable(a, p, it, s!, ct); break;
            case "capacity.idle": await Capacity(a, p, it, s!, ct); break;
            case "quality.issues": await Quality(a, s!, ct); break;
            case "reconciliation.status": await Reconciliation(a, it, s!, ct); break;
            case "run.status": await RunStatus(a, p, it, ct); break;
            case "imports.recent": await Imports(a, it, ct); break;
            case "navigate": Navigate(a, p, it); break;
            case "help.term": Term(a, it); break;
            case "help.howto": await HowTo(a, p, it, ct); break;
            default: a.Text = "I can't answer that yet."; break;
        }
        if (it.View == "explain") Explain(a, it, s);
        return a;
    }

    private static int Limit(Interpretation it) => Math.Clamp(int.TryParse(it["limit"]?.Key, out var n) ? n : DefaultLimit, 1, MaxLimit);
    private static bool Bottom(Interpretation it) => it["order"]?.Key == "bottom";
    private static string Cur(ScopedRun s) => s.Hospital.Currency;
    private static string Where(ScopedRun s) => $"{s.Hospital.Name}, {s.Period.Code}";
    private static Dictionary<string, string> Scope(ScopedRun s) => new() { ["hospital"] = s.Hospital.Code, ["period"] = s.Period.Code };
    private static ReplyLink Screen(string label, string path, ScopedRun? s) => new(label, path, s is null ? [] : Scope(s));

    // ── Run summary ──────────────────────────────────────────────────────────────────

    private async Task Dashboard(Answer a, Principal p, Interpretation it, ScopedRun s, CancellationToken ct)
    {
        var d = await q.DashboardAsync(s, p, ct);
        var t = d.Headline.ToDictionary(x => x.Key);
        foreach (var tile in d.Headline) a.Figures.Add(new(tile.Key, tile.Label, tile.Value, tile.Unit == "count" ? "" : tile.Unit));
        var total = t.GetValueOrDefault("total")?.Value ?? 0m;
        var text = $"{Where(s)}: total cost is {Cur(s)} {Money(total)} — direct {Money(t.GetValueOrDefault("direct")?.Value ?? 0)}, " +
                   $"indirect {Money(t.GetValueOrDefault("indirect")?.Value ?? 0)} and overheads {Money(t.GetValueOrDefault("overheads")?.Value ?? 0)}, " +
                   $"over {Number(t.GetValueOrDefault("encounters")?.Value ?? 0)} encounters.";
        if (t.TryGetValue("revenue", out var rev) && t.TryGetValue("margin", out var mar))
            text += $" Revenue is {Cur(s)} {Money(rev.Value)}, leaving a margin of {Cur(s)} {Money(mar.Value)} ({mar.Note}).";
        else text += " Revenue and margin need revenue.view, which your role does not hold.";
        a.Text = text;
        a.Blocks.Add(new("figures", null, ["Measure", "Value"], d.Headline.Select(x => new[] { x.Label, x.Unit == "count" ? Number(x.Value) : $"{x.Unit} {Money(x.Value)}" }).ToArray()));
        if (it.View == "detail")
        {
            a.Blocks.Add(new("table", "Cost by nature", ["Nature", Cur(s), "Share"], d.CostNature.Select(b => new[] { b.Label, Money(b.Value), Pct(b.Share) }).ToArray()));
            if (p.Can("profitability.view"))
            {
                var pr = await q.ProfitabilityAsync(s, p, "serviceLine", ct);
                a.Blocks.Add(new("table", "By service line (highest cost first)", ["Service line", "Encounters", "Total cost", "Margin %"],
                    pr.Rows.OrderByDescending(r => r.Total).Take(8).Select(r => new[] { r.Label, Number(r.Encounters), Money(r.Total), Pct(r.MarginPct) }).ToArray()));
            }
            a.Text += " The breakdown by cost nature is below.";
        }
        a.Terms.AddRange(["direct-cost", "indirect-cost", "overhead"]);
        a.Links.Add(Screen("Open the dashboard", "dashboard", s));
    }

    // ── Procedures ───────────────────────────────────────────────────────────────────

    private async Task ProcedureCost(Answer a, Principal p, Interpretation it, ScopedRun s, CancellationToken ct)
    {
        var id = Guid.Parse(it["procedure"]!.Key);
        ReadQueries.ProcedureDetail d;
        try { d = await q.ProcedureAsync(s, p, id, ct); }
        catch (AppError e) when (e.Code == "NOT_FOUND")
        {
            a.Text = $"{it["procedure"]!.Label} has no cost in {Where(s)}: it was not performed or not costed in run {s.Run.Code}.";
            return;
        }
        var r = d.Procedure;
        a.Figures.AddRange([new("unit", "Unit cost", r.UnitCost, Cur(s)), new("price", "List price", r.ListPrice, Cur(s)),
            new("volume", "Volume", r.Volume, ""), new("total", "Total cost", r.TotalCost, Cur(s))]);
        var margin = r.ListPrice == 0 ? (decimal?)null : Math.Round((r.ListPrice - r.UnitCost) / r.ListPrice * 100m, 1);
        a.Text = $"{r.Name} ({r.Code}) costs {Cur(s)} {Money(r.UnitCost)} per unit in {Where(s)}" +
                 (r.ListPrice > 0 ? $", against a list price of {Cur(s)} {Money(r.ListPrice)} ({Pct(margin!.Value)} margin on price)." : ".") +
                 $" It was performed {Number(r.Volume)} times, for a total cost of {Cur(s)} {Money(r.TotalCost)}.";
        a.Blocks.Add(new("figures", null, ["Measure", "Value"],
        [
            ["Unit cost", $"{Cur(s)} {Money(r.UnitCost)}"], ["List price", $"{Cur(s)} {Money(r.ListPrice)}"], ["Volume", Number(r.Volume)],
            ["Total cost", $"{Cur(s)} {Money(r.TotalCost)}"], ["Procedure type", r.ProcType], ["Department", r.Department],
        ]));
        if (it.View is "detail" or "explain")
        {
            var comps = d.StandardComponents.OrderByDescending(c => c.Amount).Take(10).ToList();
            if (comps.Count > 0)
            {
                a.Blocks.Add(new("table", "What makes up the cost", ["Component", "Nature", "Qty", "Unit cost", "Amount", "Share"],
                    comps.Select(c => new[] { c.Label, c.CostNature, Number(c.Quantity) + " " + c.Uom, c.UnitCost ?? "masked", Money(c.Amount), Pct(c.Share) }).ToArray(),
                    d.StandardComponents.Length > 10 ? $"Top 10 of {d.StandardComponents.Length} components." : null));
                var top = comps[0];
                a.Text += $" The largest component is {top.Label} at {Pct(top.Share)} of the standard cost.";
            }
            else if (d.Chain.Length > 0)
            {
                a.Blocks.Add(new("table", "Activities it uses", ["Activity", "Qty", "Cost centre", "Overhead per occurrence"],
                    d.Chain.Select(c => new[] { c.Activity, Number(c.Quantity), c.CostCentre, Money(c.OverheadPerOccurrence) }).ToArray()));
            }
            else a.Text += " This run costs it from the costing model (contracted price × Margin Cost % of its procedure type), so there is no component breakdown.";
            if (it.View == "explain")
            {
                var steps = new List<string[]>();
                foreach (var c in comps.Take(3)) steps.Add([c.Label, c.CalcExpression]);
                if (steps.Count > 0) a.Blocks.Add(new("table", "How the largest components were calculated", ["Component", "Calculation"], steps.ToArray()));
            }
        }
        a.Terms.AddRange(["unit-cost", "direct-cost"]);
        a.Links.Add(new("Open this procedure", $"procedures/{r.Id}", Scope(s)));
    }

    private async Task ProcedureTop(Answer a, Principal p, Interpretation it, ScopedRun s, CancellationToken ct)
    {
        var rows = await q.ProcedureListAsync(s, p, ct);
        var byVolume = it["metric"]?.Key == "volume";
        var n = Limit(it);
        var bottom = Bottom(it);
        var pool = rows.Where(r => r.Volume > 0 && (byVolume || r.UnitCost > 0));
        var picked = (byVolume
            ? bottom ? pool.OrderBy(r => r.Volume) : pool.OrderByDescending(r => r.Volume)
            : bottom ? pool.OrderBy(r => r.UnitCost) : pool.OrderByDescending(r => r.UnitCost)).Take(n).ToList();
        if (picked.Count == 0) { a.Text = $"No procedures were costed in {Where(s)}."; return; }
        var what = byVolume ? (bottom ? "least performed" : "most performed") : (bottom ? "cheapest" : "most expensive");
        var f = picked[0];
        a.Text = $"The {picked.Count} {what} procedures in {Where(s)}. First is {f.Name} ({f.Code}) at " +
                 (byVolume ? $"{Number(f.Volume)} performed" : $"{Cur(s)} {Money(f.UnitCost)} per unit") + ".";
        a.Blocks.Add(new("table", null, ["#", "Procedure", "Code", "Volume", "Unit cost", "Total cost"],
            picked.Select((r, i) => new[] { (i + 1).ToString(), r.Name, r.Code, Number(r.Volume), Money(r.UnitCost), Money(r.TotalCost) }).ToArray(),
            $"{Number(rows.Length)} procedures in this run. Ranked by {(byVolume ? "volume" : "unit cost")}."));
        a.Links.Add(Screen("Open Procedure/Activity", "procedures", s));
    }

    // ── Packages ─────────────────────────────────────────────────────────────────────

    private async Task PackageCost(Answer a, Principal p, Interpretation it, ScopedRun s, CancellationToken ct)
    {
        var id = Guid.Parse(it["package"]!.Key);
        ReadQueries.PackageDetail d;
        try { d = await q.PackageAsync(s, p, id, ct); }
        catch (AppError e) when (e.Code == "NOT_FOUND")
        {
            a.Text = $"{it["package"]!.Label} is not costed in {Where(s)}.";
            return;
        }
        var r = d.Package;
        var perCase = r.ActualPerCase != 0 ? r.ActualPerCase : r.UnitCost;
        a.Figures.AddRange([new("price", "Price", r.Price, Cur(s)), new("perCase", "Cost per case", perCase, Cur(s)), new("cases", "Cases", r.Cases, "")]);
        a.Text = $"{r.Name} ({r.Code}) is priced at {Cur(s)} {Money(r.Price)} and costs {Cur(s)} {Money(perCase)} per case in {Where(s)}, over {Number(r.Cases)} cases.";
        var rows = new List<string[]> { new[] { "Price", $"{Cur(s)} {Money(r.Price)}" }, new[] { "Cost per case", $"{Cur(s)} {Money(perCase)}" }, new[] { "Cases", Number(r.Cases) } };
        if (p.Can("revenue.view"))
        {
            a.Figures.Add(new("margin", "Margin %", r.MarginPct, "%"));
            a.Text += $" Its operating margin is {Cur(s)} {Money(r.OperatingMargin)} ({Pct(r.MarginPct)}).";
            rows.Add(["Operating margin", $"{Cur(s)} {Money(r.OperatingMargin)} ({Pct(r.MarginPct)})"]);
        }
        a.Blocks.Add(new("figures", null, ["Measure", "Value"], rows.ToArray()));
        if (it.View is "detail" or "explain")
        {
            a.Blocks.Add(new("table", "Components", ["Procedure", "Code", "Qty", "Price", "Cost"],
                d.Components.OrderByDescending(c => c.Cost).Take(12).Select(c => new[] { c.Procedure, c.Code, Number(c.Quantity), Money(c.Price), Money(c.Cost) }).ToArray(),
                d.Components.Length > 12 ? $"Top 12 of {d.Components.Length} components by cost." : null));
            if (!string.IsNullOrWhiteSpace(d.Explanation)) a.Text += " " + d.Explanation;
        }
        a.Terms.Add("package");
        a.Links.Add(new("Open this package", $"packages/{r.Id}", Scope(s)));
    }

    private async Task PackagesLosing(Answer a, Principal p, Interpretation it, ScopedRun s, CancellationToken ct)
    {
        if (!p.Can("revenue.view")) { a.Text = "Package margins need revenue.view, which your role does not hold."; return; }
        var rows = await q.PackagesAsync(s, p, ct);
        var losing = rows.Where(r => r.Cases > 0 && r.OperatingMargin < 0).OrderBy(r => r.OperatingMargin).ToList();
        if (losing.Count == 0) { a.Text = $"No package lost money in {Where(s)} ({Number(rows.Count(r => r.Cases > 0))} packages had cases)."; return; }
        var n = Limit(it);
        a.Figures.Add(new("losing", "Packages losing money", losing.Count, ""));
        a.Text = $"{losing.Count} of {rows.Count(r => r.Cases > 0)} packages with cases lost money in {Where(s)}, {Cur(s)} {Money(-losing.Sum(r => r.OperatingMargin))} in total. " +
                 $"The biggest loss is {losing[0].Name} at {Cur(s)} {Money(losing[0].OperatingMargin)} ({Pct(losing[0].MarginPct)}).";
        a.Blocks.Add(new("table", null, ["Package", "Cases", "Price", "Cost per case", "Margin", "Margin %"],
            losing.Take(n).Select(r => new[] { r.Name, Number(r.Cases), Money(r.Price), Money(r.ActualPerCase), Money(r.OperatingMargin), Pct(r.MarginPct) }).ToArray()));
        a.Terms.Add("operating-margin");
        a.Links.Add(Screen("Open Packages", "packages", s));
    }

    // ── Profitability ────────────────────────────────────────────────────────────────

    private async Task Profitability(Answer a, Principal p, Interpretation it, ScopedRun s, CancellationToken ct)
    {
        var dim = it["dimension"]?.Key ?? "serviceLine";
        var label = Knowledge.DimensionByKey(dim)?.Label ?? dim;
        var d = await q.ProfitabilityAsync(s, p, dim, ct);
        if (d.Rows.Length == 0) { a.Text = $"There is nothing to show by {label.ToLowerInvariant()} in {Where(s)}."; return; }
        var showRevenue = p.Can("revenue.view");
        var bottom = Bottom(it);

        // What to rank by: the measure asked for ("by revenue", "by margin %"), else margin (or cost without revenue.view).
        var measure = it["measure"]?.Key ?? (showRevenue ? "operating" : "total");
        var needsRevenue = measure is "revenue" or "operating" or "marginPct" or "contribution";
        if (needsRevenue && !showRevenue)
        {
            a.Notes.Add("Revenue and margins need revenue.view, which your role does not hold; ranked by total cost instead.");
            measure = "total";
        }
        Func<ReadQueries.ProfitRow, decimal> key = measure switch
        {
            "revenue" => r => r.Revenue, "total" => r => r.Total, "direct" => r => r.Direct, "indirect" => r => r.Indirect,
            "overhead" => r => r.Overhead, "contribution" => r => r.Contribution, "encounters" => r => r.Encounters,
            "marginPct" => r => r.MarginPct, _ => r => r.Operating,
        };
        var measureLabel = measure switch
        {
            "revenue" => "revenue", "total" => "total cost", "direct" => "direct cost", "indirect" => "indirect cost", "overhead" => "overheads",
            "contribution" => "contribution", "encounters" => "encounters", "marginPct" => "margin %", _ => "margin",
        };
        string Fmt(decimal v) => measure switch { "marginPct" => Pct(v), "encounters" => Number(v), _ => $"{Cur(s)} {Money(v)}" };
        // Margin % means nothing without revenue, so rows with none are left out of that ranking.
        var pool = measure == "marginPct" ? d.Rows.Where(r => r.Revenue != 0).ToList() : d.Rows.ToList();
        var ranked = (bottom ? pool.OrderBy(key) : pool.OrderByDescending(key)).Take(Limit(it)).ToList();
        var noun = label.ToLowerInvariant();

        if (it["measure"] is not null && ranked.Count > 0)
        {
            a.Text = $"{(bottom ? "Lowest" : "Top")} {ranked.Count} by {measureLabel}, {noun} in {Where(s)}: " +
                     string.Join("; ", ranked.Take(3).Select((r, i) => $"{i + 1}. {r.Label} ({Fmt(key(r))})")) + "." +
                     (showRevenue ? $" Overall margin is {Cur(s)} {Money(d.Total.Operating)} ({Pct(d.Total.MarginPct)})." : "") +
                     (measure == "marginPct" && pool.Count < d.Rows.Length ? $" {d.Rows.Length - pool.Count} with no revenue are left out." : "");
        }
        else if (showRevenue)
        {
            var best = d.Rows.MaxBy(r => r.Operating)!;
            var worst = d.Rows.MinBy(r => r.Operating)!;
            var losing = d.Rows.Count(r => r.Operating < 0);
            a.Text = $"By {noun} in {Where(s)}: the overall margin is {Cur(s)} {Money(d.Total.Operating)} ({Pct(d.Total.MarginPct)}). " +
                     $"Most profitable is {best.Label} ({Cur(s)} {Money(best.Operating)}); least profitable is {worst.Label} ({Cur(s)} {Money(worst.Operating)})." +
                     (losing > 0 ? $" {losing} of {d.Rows.Length} lose money." : "");
        }
        else a.Text = $"Cost by {noun} in {Where(s)}, highest first. Margins need revenue.view, which your role does not hold.";

        // The usual columns, plus the ranking measure when it is not one of them.
        var cols = (showRevenue ? new List<string> { label, "Encounters", "Revenue", "Total cost", "Margin", "Margin %" } : [label, "Encounters", "Total cost"]);
        var extra = measure switch { "direct" => "Direct cost", "indirect" => "Indirect cost", "overhead" => "Overheads", "contribution" => "Contribution", _ => null };
        if (extra is not null) cols.Insert(1, extra);
        a.Blocks.Add(new("table", $"{(bottom ? "Lowest" : "Highest")} {measureLabel} first", cols.ToArray(),
            ranked.Select(r =>
            {
                var row = showRevenue
                    ? new List<string> { r.Label, Number(r.Encounters), Money(r.Revenue), Money(r.Total), Money(r.Operating), Pct(r.MarginPct) }
                    : [r.Label, Number(r.Encounters), Money(r.Total)];
                if (extra is not null) row.Insert(1, Money(key(r)));
                return row.ToArray();
            }).ToArray(),
            pool.Count > ranked.Count ? $"{ranked.Count} of {pool.Count} rows." : null));
        if (it.View == "detail" && showRevenue)
            a.Blocks.Add(new("table", "Cost build-up", [label, "Direct", "Indirect", "Overheads", "Contribution"],
                ranked.Select(r => new[] { r.Label, Money(r.Direct), Money(r.Indirect), Money(r.Overhead), Money(r.Contribution) }).ToArray()));
        a.Terms.AddRange(["contribution-margin", "operating-margin"]);
        a.Links.Add(Screen("Open the dashboard", "dashboard", s));
    }

    // ── Non-billable (the dashboard's last tab) ──────────────────────────────────────

    /// <summary>Lines invoiced at no price, by procedure type: the same query as the dashboard's Non-billable tab.</summary>
    private async Task NonBillable(Answer a, Principal p, Interpretation it, ScopedRun s, CancellationToken ct)
    {
        var d = await q.ProfitabilityAsync(s, p, "noBillable", ct);
        var services = d.Total.Services ?? d.Rows.Sum(r => r.Services ?? 0);
        if (d.Rows.Length == 0 || services == 0)
        {
            a.Text = $"Run {s.Run.Code} holds no non-billable lines for {Where(s)}. If you expected some, run the calculation again to fill this list.";
            a.Links.Add(Screen("Open the dashboard", "dashboard", s));
            return;
        }
        var ranked = (Bottom(it) ? d.Rows.OrderBy(r => r.Total) : d.Rows.OrderByDescending(r => r.Total)).Take(Limit(it)).ToList();
        var top = d.Rows.MaxBy(r => r.Total)!;
        a.Figures.AddRange([new("encounters", "Encounters", d.Total.Volume, ""), new("services", "Services", services, ""), new("total", "Total cost", d.Total.Total, Cur(s))]);
        a.Text = $"Non-billable (invoiced at no price) in {Where(s)}: {Number(services)} services on {Number(d.Total.Volume)} encounters, " +
                 $"costing {Cur(s)} {Money(d.Total.Total)} across {d.Rows.Length} procedure types. " +
                 $"The largest procedure type is {top.Label}: {Number(top.Services ?? 0)} services, {Cur(s)} {Money(top.Total)} ({Pct(d.Total.Total == 0 ? 0 : Math.Round(top.Total / d.Total.Total * 100m, 1))} of the cost).";
        a.Blocks.Add(new("figures", null, ["Measure", "Value"],
            [["Encounters", Number(d.Total.Volume)], ["Services", Number(services)], ["Total cost", $"{Cur(s)} {Money(d.Total.Total)}"], ["Procedure types", Number(d.Rows.Length)]]));
        a.Blocks.Add(new("table", "By procedure type", ["Procedure type", "Encounters", "Services", "Total cost", "Share"],
            ranked.Select(r => new[] { r.Label, Number(r.Volume), Number(r.Services ?? 0), Money(r.Total), Pct(d.Total.Total == 0 ? 0 : Math.Round(r.Total / d.Total.Total * 100m, 1)) }).ToArray(),
            d.Rows.Length > ranked.Count ? $"{ranked.Count} of {d.Rows.Length} procedure types, by cost." : null));
        if (it.View is "detail" or "explain")
        {
            // The tab's second chart: services invoiced at a price vs at no price, by the same procedure types.
            var billed = (await q.ProfitabilityAsync(s, p, "billable", ct)).Rows.ToDictionary(r => r.Key, r => r.Services ?? 0);
            var rows = d.Rows.OrderByDescending(r => r.Services ?? 0).Take(Limit(it)).Select(r =>
            {
                var free = r.Services ?? 0;
                var paid = billed.GetValueOrDefault(r.Key);
                return new[] { r.Label, Number(paid), Number(free), Pct(paid + free == 0 ? 0 : Math.Round((decimal)free / (paid + free) * 100m, 1)) };
            }).ToArray();
            a.Blocks.Add(new("table", "Billable vs non-billable services", ["Procedure type", "Billable", "Non-billable", "% non-billable"], rows));
        }
        a.Links.Add(new("Open the Non-billable tab", "dashboard", new(Scope(s)) { ["tab"] = "noBillable" }));
        a.Links.Add(new("Non-billable encounters", "patient-encounters", new(Scope(s)) { ["noBillableType"] = "ALL" }));
    }

    // ── Variance ─────────────────────────────────────────────────────────────────────

    private async Task Variances(Answer a, Interpretation it, ScopedRun s, CancellationToken ct)
    {
        var rows = await q.VariancesAsync(s, ct);
        if (rows.Length == 0) { a.Text = $"Run {s.Run.Code} recorded no variances for {Where(s)}."; return; }
        var top = rows.Take(Limit(it)).ToList();
        var f = top[0];
        string Driver(ReadQueries.VarianceRow v)
        {
            var parts = new[] { ("rate", v.RateEffect), ("quantity", v.QuantityEffect), ("mix", v.MixEffect) };
            var main = parts.MaxBy(x => Math.Abs(x.Item2));
            return main.Item2 == 0 ? "" : $", mostly a {main.Item1} effect";
        }
        a.Text = $"{rows.Length} variances in {Where(s)}. The largest is {f.Label}: {Cur(s)} {Money(f.Variance)} ({Pct(f.VariancePct)}) against {f.Baseline.ToLowerInvariant()}{Driver(f)}.";
        a.Blocks.Add(new("table", null, ["Item", "Baseline", "Actual", "Variance", "%", "Rate", "Quantity", "Mix"],
            top.Select(v => new[] { v.Label, Money(v.BaselineAmount), Money(v.Actual), Money(v.Variance), Pct(v.VariancePct), Money(v.RateEffect), Money(v.QuantityEffect), Money(v.MixEffect) }).ToArray()));
        if (it.View is "detail" or "explain")
        {
            var d = await q.VarianceAsync(s, f.Id, ct);
            a.Blocks.Add(new("table", $"Why {f.Label} moved", ["Component", "Std qty", "Actual qty", "Std rate", "Actual rate", "Rate", "Quantity", "Mix", "Total"],
                d.Components.Take(10).Select(c => new[] { c.Label, Number(c.StandardQty), Number(c.ActualQty), Money(c.StandardRate), Money(c.ActualRate), Money(c.RateEffect), Money(c.QuantityEffect), Money(c.MixEffect), Money(c.TotalEffect) }).ToArray(),
                d.IdentityHolds ? "Rate + quantity + mix equals the total on every line." : "Warning: rate + quantity + mix does not equal the total on every line."));
            if (!string.IsNullOrWhiteSpace(f.Summary)) a.Text += " " + f.Summary;
        }
        a.Terms.Add("variance");
        a.Links.Add(Screen("Open Procedure/Activity", "procedures", s));
    }

    // ── Capacity, quality, reconciliation ────────────────────────────────────────────

    private async Task Capacity(Answer a, Principal p, Interpretation it, ScopedRun s, CancellationToken ct)
    {
        var d = await q.CapacityAsync(s, p, ct);
        var used = d.CapacityPaid == 0 ? 0 : Math.Round(d.CapacityUsed / d.CapacityPaid * 100m, 1);
        a.Figures.AddRange([new("idle", "Idle capacity", d.Idle, Cur(s)), new("paid", "Capacity paid", d.CapacityPaid, Cur(s)), new("usedPct", "Used", used, "%")]);
        var worst = d.Rows.Where(r => r.IsCapacityDriven && r.CostOfUnused > 0).OrderByDescending(r => r.CostOfUnused).Take(Limit(it)).ToList();
        a.Text = $"Idle capacity in {Where(s)} is {Cur(s)} {Money(d.Idle)}: {Pct(used)} of the {Cur(s)} {Money(d.CapacityPaid)} of capacity paid for was used." +
                 (worst.Count > 0 ? $" The most idle resource is {worst[0].Name} ({Pct(worst[0].UtilisationPct)} used, {Cur(s)} {Money(worst[0].CostOfUnused)} unused)." : "") +
                 (d.OverCapacity > 0 ? $" Over-capacity of {Cur(s)} {Money(d.OverCapacity)} was also recorded." : "");
        if (worst.Count > 0)
            a.Blocks.Add(new("table", "Most idle resources", ["Resource", "Cost centre", "Used %", "Unused cost"],
                worst.Select(r => new[] { r.Name, r.CostCentre, Pct(r.UtilisationPct), Money(r.CostOfUnused) }).ToArray()));
        a.Terms.AddRange(["idle-capacity", "practical-capacity"]);
        a.Links.Add(Screen("Open Resources", "resources", s));
    }

    private async Task Quality(Answer a, ScopedRun s, CancellationToken ct)
    {
        var d = await q.QualityAsync(s, ct);
        var failing = d.Register.Where(c => c.Issues > 0).OrderBy(c => Array.IndexOf(Verita.Domain.Catalogue.Severities, c.Severity)).ThenByDescending(c => c.Issues).ToList();
        var open = d.Issues.Count(i => i.Status != "RESOLVED");
        a.Text = failing.Count == 0
            ? $"Run {s.Run.Code} passed all {d.Register.Length} data-quality checks."
            : $"Run {s.Run.Code} passed {d.Passed} of {d.Register.Length} checks. {failing.Count} checks raised {Number(d.Issues.Length)} issues ({Number(open)} still open).";
        if (failing.Count > 0)
            a.Blocks.Add(new("table", null, ["Check", "Severity", "Issues", "Question"],
                failing.Take(12).Select(c => new[] { c.Name, c.Severity, Number(c.Issues), c.Question }).ToArray()));
        a.Links.Add(new("Open the run", $"runs/{s.Run.Id}", Scope(s)));
    }

    private async Task Reconciliation(Answer a, Interpretation it, ScopedRun s, CancellationToken ct)
    {
        var d = await q.ReconciliationAsync(s, ct);
        a.Figures.AddRange([new("gl", "General ledger", d.GlTotal, Cur(s)), new("patient", "Patient cost", d.PatientCost, Cur(s)), new("unexplained", "Unexplained", d.Unexplained, Cur(s))]);
        a.Text = $"Reconciliation for {Where(s)}: {d.Status}. Of {Cur(s)} {Money(d.GlTotal)} in the general ledger, {Pct(d.AttributionPct)} " +
                 $"({Cur(s)} {Money(d.PatientCost)}) is attributed to patients and {Cur(s)} {Money(d.ResidualTotal)} is in named residuals. " +
                 $"Unexplained is {Cur(s)} {Money(d.Unexplained)} against a materiality of {Cur(s)} {Money(d.Materiality)}.";
        a.Blocks.Add(new("figures", null, ["Measure", "Value"],
        [
            ["General ledger", Money(d.GlTotal)], ["Patient cost", Money(d.PatientCost)], ["Residuals", Money(d.ResidualTotal)],
            ["Unexplained", Money(d.Unexplained)], ["Materiality", Money(d.Materiality)], ["Status", d.Status],
        ]));
        if (it.View is "detail" or "explain")
        {
            a.Blocks.Add(new("table", "Residuals", ["Bucket", "Amount", "Owner", "Why"], d.Residuals.Select(r => new[] { r.Label, Money(r.Amount), r.Owner, r.Explanation }).ToArray()));
            if (!string.IsNullOrWhiteSpace(d.Narrative)) a.Text += " " + d.Narrative;
        }
        a.Terms.Add("reconciliation");
        a.Links.Add(new("Open the run", $"runs/{s.Run.Id}", Scope(s)));
    }

    // ── Runs and imports ─────────────────────────────────────────────────────────────

    private async Task RunStatus(Answer a, Principal p, Interpretation it, CancellationToken ct)
    {
        var hospital = it["hospital"]?.Key;
        var period = it["period"];
        var runs = (await q.RunsAsync(p, ct)).Where(r => hospital is null || r.Hospital == hospital).ToList();
        if (runs.Count == 0) { a.Text = "There are no calculation runs in your scope for that hospital."; return; }
        if (period is { Source: "explicit" or "carried" })
        {
            var forPeriod = runs.Where(r => r.Period == period.Key).ToList();
            if (forPeriod.Count == 0) a.Text = $"There is no run for {runs[0].HospitalName} {period.Key}.";
            else
            {
                var r = forPeriod.OrderByDescending(x => x.Status == "PUBLISHED").ThenByDescending(x => x.StartedAt).First();
                a.Text = $"Run {r.Code} for {r.HospitalName} {r.Period} is {r.Status}" +
                         (r.PublishedAt is { } pub ? $", published {pub:yyyy-MM-dd}" : "") + (r.Forced ? " (forced — it cannot be published)" : "") +
                         (r.ErrorMessage is { Length: > 0 } err ? $". Error: {err}" : ".") + (forPeriod.Count > 1 ? $" There are {forPeriod.Count} runs for this period." : "");
                a.Links.Add(new("Open the run", $"runs/{r.Id}", new() { ["hospital"] = r.Hospital, ["period"] = r.Period }));
            }
        }
        else
        {
            var latest = runs[0];
            a.Text = $"The latest run for {latest.HospitalName} is {latest.Code} ({latest.Period}), {latest.Status}.";
        }
        a.Blocks.Add(new("table", "Recent runs", ["Run", "Period", "Status", "Version", "Finished"],
            runs.Take(6).Select(r => new[] { r.Code, r.Period, r.Status, r.CostingVersion, r.FinishedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—" }).ToArray()));
        a.Terms.AddRange(["calculation-run", "published-run"]);
        if (a.Links.Count == 0) a.Links.Add(new("Open Calculation Runs", "runs", hospital is null ? [] : new() { ["hospital"] = hospital }));
    }

    private async Task Imports(Answer a, Interpretation it, CancellationToken ct)
    {
        var rows = await hub.HistoryAsync(ct);
        if (rows.Length == 0) { a.Text = "Nothing has been imported yet."; return; }
        var n = Math.Clamp(int.TryParse(it["limit"]?.Key, out var k) ? k : 5, 1, 50);
        var last = rows[0];
        a.Text = $"The last import was {last.Entity} by {last.LoadedBy} on {last.LoadedAt:yyyy-MM-dd HH:mm}: {Number(last.RowsAccepted)} rows accepted, {Number(last.RowsRejected)} rejected." +
                 (last.HasRejects ? " The rejected rows can be downloaded from Data Import." : "");
        var rejects = rows.Take(50).Sum(r => r.RowsRejected);
        if (rejects > 0 && n > 1) a.Text += $" Across the last {Math.Min(50, rows.Length)} imports, {Number(rejects)} rows were rejected.";
        a.Blocks.Add(new("table", null, ["Table", "Accepted", "Rejected", "Loaded", "By"],
            rows.Take(n).Select(r => new[] { r.Entity, Number(r.RowsAccepted), Number(r.RowsRejected), r.LoadedAt.ToString("yyyy-MM-dd HH:mm"), r.LoadedBy }).ToArray()));
        a.Links.Add(new("Open Data Import", "data-import", []));
    }

    // ── Navigation and help ──────────────────────────────────────────────────────────

    private static void Navigate(Answer a, Principal p, Interpretation it)
    {
        var nav = Verita.Domain.Security.AllNavItems.FirstOrDefault(n => n.Path == it["nav"]?.Key);
        if (nav is null) { a.Text = "I couldn't find that screen."; return; }
        if (!p.Can(nav.Permission)) { a.Text = $"{nav.Label} needs {nav.Permission}, which your role does not hold."; return; }
        a.Text = $"Opening {nav.Label}: {nav.Purpose}";
        var query = new Dictionary<string, string>();
        if (it["hospital"] is { } h) query["hospital"] = h.Key;
        a.Navigate = new(nav.Label, nav.Path, query);
        a.Links.Add(a.Navigate);
    }

    private static void Term(Answer a, Interpretation it)
    {
        var t = Knowledge.TermByKey(it["term"]?.Key ?? "");
        if (t is null) { a.Text = "I don't have a definition for that."; return; }
        a.Text = $"{t.Label}: {t.Definition}";
        if (t.Path is not null) a.Links.Add(new($"Open {Verita.Domain.Security.AllNavItems.FirstOrDefault(n => n.Path == t.Path)?.Label ?? t.Path}", t.Path, []));
        a.Links.Add(new("Help & Method", "help", []));
    }

    /// <summary>
    /// A guide: what must be true first, the steps on the real screens, whether the user's role can
    /// do it (and which roles can), and, for runs, what is already there for the hospital and period in view.
    /// </summary>
    private async Task HowTo(Answer a, Principal p, Interpretation it, CancellationToken ct)
    {
        var h = Knowledge.HowToByKey(it["howto"]?.Key ?? "");
        if (h is null) { a.Text = "I don't have steps for that yet."; return; }
        var screen = h.Path is null ? null : Verita.Domain.Security.AllNavItems.FirstOrDefault(n => n.Path == h.Path);
        a.Text = $"How to {char.ToLowerInvariant(h.Title[0])}{h.Title[1..]}" + (screen is null ? "." : $": it is done on {screen.Label}.");
        if (h.Permission is not null && !p.Can(h.Permission))
        {
            var roles = Verita.Domain.Security.Roles.Where(r => r.Permissions.Contains(h.Permission)).Select(r => r.Name).ToList();
            a.Notes.Add($"Your role ({p.Role}) does not hold {h.Permission}, so you will not see these actions. " +
                        (roles.Count > 0 ? $"Ask a {string.Join(" or ", roles.Take(3))}." : "Ask an administrator."));
        }
        if (h.Before is { Length: > 0 }) a.Blocks.Add(new("list", "Before you start", [], h.Before.Select(x => new[] { x }).ToArray()));
        a.Blocks.Add(new("steps", "Steps", [], h.Steps.Select(x => new[] { x }).ToArray()));

        // For runs, say what already exists for the hospital and period being looked at.
        var hospital = it["hospital"]?.Key;
        var period = it["period"]?.Key;
        var query = new Dictionary<string, string>();
        if (hospital is not null) query["hospital"] = hospital;
        if (period is not null) query["period"] = period;
        if (h.Key is "run-costing" or "rerun" or "publish-run" && hospital is not null && period is not null)
        {
            var runs = (await q.RunsAsync(p, ct)).Where(r => r.Hospital == hospital && r.Period == period).OrderByDescending(r => r.StartedAt).ToList();
            var where = $"{it["hospital"]!.Label}, {period}";
            if (runs.Count == 0)
                a.Notes.Add(h.Key == "run-costing"
                    ? $"{where} has no run yet, so New run is available once a costing model is in effect."
                    : $"{where} has no run yet. Create one first.");
            else
            {
                var r = runs[0];
                a.Notes.Add(h.Key switch
                {
                    "run-costing" => $"{where} already has {r.Code} ({r.Status}) under costing model {r.CostingVersion}. New run stays disabled for that model; open the run and use Re-run, or create a new costing model version first.",
                    "rerun" when r.Status == "PUBLISHED" => $"{r.Code} is PUBLISHED and cannot be re-run.",
                    "publish-run" when r.Forced => $"{r.Code} was forced, so it can never be published. Re-run it without force.",
                    _ => $"{where}: {r.Code} is {r.Status}.",
                });
                a.Links.Add(new($"Open {r.Code}", $"runs/{r.Id}", new() { ["hospital"] = r.Hospital, ["period"] = r.Period }));
            }
        }
        // For HIS imports, say which HIS project the hospital is bound to.
        if (h.Key == "import-his" && hospital is not null)
        {
            var hd = (await q.ContextAsync(p, ct)).Hospitals.FirstOrDefault(x => x.Code == hospital);
            if (hd is not null)
                a.Notes.Add(hd.HisSetupId is { Length: > 0 } && hd.HisProjectId is not null
                    ? $"{hd.Name} is bound to HIS SetupID {hd.HisSetupId}, ProjectID {hd.HisProjectId}: run the utility with those values. Packs from another project are refused for this hospital."
                    : $"{hd.Name} is not bound to a HIS project yet; its first HIS import binds it to that pack's SetupID and ProjectID.");
        }

        // For costing values, say which version can be edited: published ones are read-only.
        if (h.Key is "set-direct-costing" or "set-package-costing" or "set-indirect-costing" && hospital is not null)
        {
            var versions = (await q.CostingModelsAsync(p, hospital, ct)).Versions;
            // Same rule as the screens: only PUBLISHED and RETIRED versions are frozen.
            var drafts = versions.Where(v => v.Status is not ("PUBLISHED" or "RETIRED")).OrderByDescending(v => v.EffectiveFrom).Select(v => $"{v.Code} ({v.Status})").ToList();
            var firstEditable = versions.Where(v => v.Status is not ("PUBLISHED" or "RETIRED")).OrderByDescending(v => v.EffectiveFrom).FirstOrDefault()?.Code;
            var name = it["hospital"]!.Label;
            if (drafts.Count > 0)
            {
                a.Notes.Add($"{name}: you can edit {string.Join(" or ", drafts)}; pick it in the version picker. Published versions are read-only.");
                query["version"] = firstEditable!;
            }
            else
            {
                a.Notes.Add(versions.Length == 0
                    ? $"{name} has no costing model version yet. Create one on Costing Models first."
                    : $"{name} has no editable costing version (all are published), so these values are read-only. Create a new version on Costing Models first (it copies the current values).");
                a.Links.Add(new("Open Costing Models", "costing-models", hospital is null ? [] : new() { ["hospital"] = hospital }));
            }
        }
        if (screen is not null) a.Links.Add(new($"Open {screen.Label}", screen.Path, query));
    }

    /// <summary>"Explain that": what the numbers mean and how they were reached, in plain words.</summary>
    private static void Explain(Answer a, Interpretation it, ScopedRun? s)
    {
        var how = it.Intent switch
        {
            "dashboard.summary" => "Total cost is direct cost + indirect cost + overheads for every encounter in the run. Margin is revenue minus total cost.",
            "procedure.cost" => "Unit cost is the procedure's total cost in the run divided by how many times it was performed. The margin shown compares that unit cost with the list price.",
            "procedure.top" => "Procedures are ranked by unit cost (total cost ÷ volume) or by volume; procedures not performed in the run are left out.",
            "package.cost" => "A package's cost per case is the cost of its component procedures for one case. Its margin is the contracted price minus that cost.",
            "package.losing" => "A package loses money when the cost of its cases is more than what was billed for them.",
            "profitability.by" => "Each encounter's revenue and cost are grouped by the chosen dimension. Margin = revenue − (direct + indirect + overheads).",
            "billing.nonbillable" => "A non-billable line is a service invoiced at no price. It still costs money, so its cost is shown by procedure type; an encounter is counted once per type it has such a line in.",
            "variance.top" => "Variance = actual − baseline. It is split into rate (a different price), quantity (a different amount used) and mix (a different case mix), which add up exactly to the total.",
            "capacity.idle" => "Idle capacity = (practical minutes available − minutes used) × cost per minute. It is paid for but not charged to patients.",
            "quality.issues" => "Each check in the register asks one question of the run's data. A check fails when it finds at least one issue.",
            "reconciliation.status" => "Patient cost + named residuals should equal the general ledger. What is left is unexplained and must be under materiality to publish.",
            "run.status" => "A run goes through validation, calculation and reconciliation, then approval and publication. A published run cannot change.",
            _ => null,
        };
        if (how is null) return;
        a.Text = how + " " + a.Text;
        var terms = a.Terms.Distinct().Select(Knowledge.TermByKey).Where(t => t is not null).ToList();
        if (terms.Count > 0) a.Blocks.Add(new("table", "Terms used", ["Term", "Meaning"], terms.Select(t => new[] { t!.Label, t.Definition }).ToArray()));
    }
}
