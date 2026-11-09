namespace Verita.Application.Assistant;

/// <summary>A filled parameter, and where its value came from.</summary>
/// <param name="Kind">hospital, period, procedure, package, dimension, nav, term, howto, limit, order, metric.</param>
/// <param name="Source">explicit (said in this message), carried (from the thread), screen (the screen's selection), default (picked by rule).</param>
public sealed record Slot(string Kind, string Key, string Label, string Source, double Score, string? Note = null);

public sealed record Option(string Label, string Text, string? Intent = null, Dictionary<string, string>? Pick = null);

/// <summary>The parts of the confidence score, kept so a low score can be explained.</summary>
public sealed record ScoreParts(double Intent, double Entities, double Coverage, double Penalty, double Confidence, string? RunnerUp, string[] Reasons);

public static class TurnTypes
{
    public const string New = "NEW", Refine = "REFINE", Drilldown = "DRILLDOWN", Correction = "CORRECTION", Rephrase = "REPHRASE",
        Shift = "SHIFT", Clarify = "CLARIFY", Meta = "META";

    /// <summary>Turns that build on the previous answer, so its parameters carry forward.</summary>
    public static bool Continues(string t) => t is Refine or Drilldown or Correction or Rephrase or Shift;
}

public static class Bands
{
    public const string Answer = "answer", Confirm = "confirm", Clarify = "clarify";
    public const double AnswerAt = 0.85, ConfirmAt = 0.6;
    public static string Of(double confidence) => confidence >= AnswerAt ? Answer : confidence >= ConfirmAt ? Confirm : Clarify;
}

/// <summary>What a thread remembers between turns.</summary>
public sealed class ThreadContext
{
    public string? Intent { get; set; }
    public string View { get; set; } = "summary";
    public Dictionary<string, Slot> Params { get; set; } = [];
    public DateTime? At { get; set; }
    /// <summary>The last turn asked for a parameter (e.g. "Which procedure?") and the next message may just be the answer.</summary>
    public string? Pending { get; set; }
    public Guid? LastReplyId { get; set; }

    /// <summary>
    /// What the next message builds on after <paramref name="it"/> was answered. Small talk
    /// keeps the context; "start over" clears it; a clarifying question remembers what it asked for.
    /// </summary>
    public static ThreadContext Next(ThreadContext? prev, Interpretation it, Guid replyId, DateTime now, IntentCatalogue catalogue)
    {
        if (it.TurnType == TurnTypes.Meta)
            return it.Meta == "reset"
                ? new ThreadContext { At = now, LastReplyId = replyId }
                : new ThreadContext { Intent = prev?.Intent, View = prev?.View ?? "summary", Params = prev?.Params ?? [], At = now, LastReplyId = replyId, Pending = prev?.Pending };
        string? pending = null;
        if (it.Band == Bands.Clarify && it.Intent is not null)
            pending = catalogue.Get(it.Intent)?.Required.FirstOrDefault(r => !it.Slots.ContainsKey(r))
                      ?? it.Options.FirstOrDefault()?.Pick?.Keys.FirstOrDefault();
        return new ThreadContext
        {
            Intent = it.Intent ?? prev?.Intent,
            View = it.Intent is null ? prev?.View ?? "summary" : it.View,
            Params = it.Intent is null ? prev?.Params ?? [] : it.Slots,
            At = now,
            Pending = pending,
            LastReplyId = replyId,
        };
    }
}

public sealed record ScreenScope(string? Hospital, string? Period);

/// <summary>Overrides a client sends when a user clicks a suggestion or removes an assumption.</summary>
public sealed record AskOverrides(string? Intent = null, Dictionary<string, string>? Pick = null, string[]? Remove = null);

public sealed record Interpretation(
    string TurnType, string? Intent, string View, Dictionary<string, Slot> Slots, ScoreParts Score, string Band,
    string? Question, Option[] Options, string? Meta, string[] Changed, string[] Notes)
{
    public Slot? this[string kind] => Slots.GetValueOrDefault(kind);
}

/// <summary>The names the interpreter can recognise for one user.</summary>
public interface IAssistantVocabulary
{
    /// <summary>Dimensions, screens, terms and how-tos.</summary>
    Lexicon Static { get; }
    /// <summary>Hospitals in the user's scope.</summary>
    Lexicon Hospitals { get; }
    /// <summary>Procedures and packages of one hospital.</summary>
    Lexicon ForHospital(string hospital);
    IReadOnlyList<PeriodRef> Periods(string hospital);
    /// <summary>The hospital to use when nothing names one: the one with the latest published run.</summary>
    string? DefaultHospital { get; }
}

/// <summary>
/// Turns a message into an intent and parameters, using the thread's context for follow-ups.
/// Pure: no database, no clock (the caller passes <c>now</c>), so the golden tests run it directly.
/// </summary>
public sealed class Interpreter(IntentCatalogue catalogue)
{
    private static readonly HashSet<string> LargeKinds = ["procedure", "package"];
    private static readonly HashSet<string> ScopeKinds = ["hospital", "period"];
    private static readonly HashSet<string> ScalarKinds = ["limit", "order", "metric", "measure"];
    private static readonly HashSet<string> GenericPlaceWords = ["hospital", "medical", "center", "centre", "specialist", "general", "clinic", "complex", "city", "health"];
    private static string? CodeNote(string kind, string? extra) => LargeKinds.Contains(kind) && !string.IsNullOrEmpty(extra) ? "code " + extra : null;
    private static readonly HashSet<string> Pronouns = ["it", "that", "this", "those", "them", "these", "same", "there", "its", "ones", "they", "their"];
    private static readonly HashSet<string> MarkerWords =
    [
        "and", "about", "what", "how", "why", "explain", "compare", "vs", "versus", "against", "no", "not", "nope", "wrong", "actually", "instead",
        "meant", "mean", "break", "down", "breakdown", "detail", "details", "detailed", "component", "components", "composition", "calculated",
        "made", "makes", "same", "also", "for", "simpler", "understand", "dont", "plain", "words", "more", "else", "other", "another",
    ];
    private static readonly Dictionary<string, string> MetaPhrases = new()
    {
        ["thanks"] = "thanks", ["thank you"] = "thanks", ["thx"] = "thanks", ["ok"] = "thanks", ["okay"] = "thanks", ["great"] = "thanks", ["cool"] = "thanks",
        ["got it"] = "thanks", ["perfect"] = "thanks", ["nice"] = "thanks", ["ok thanks"] = "thanks", ["great thanks"] = "thanks", ["thanks a lot"] = "thanks",
        ["hi"] = "greeting", ["hello"] = "greeting", ["hey"] = "greeting", ["good morning"] = "greeting", ["salam"] = "greeting", ["assalamualaikum"] = "greeting",
        ["help"] = "help", ["what can you do"] = "help", ["what can i ask"] = "help", ["what can i ask you"] = "help", ["what do you do"] = "help",
        ["start over"] = "reset", ["reset"] = "reset", ["new question"] = "reset", ["clear"] = "reset", ["new topic"] = "reset",
    };

    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RephraseWindow = TimeSpan.FromSeconds(90);

    private sealed record IntentScore(IntentDef Def, double Score, HashSet<int> Positions, double Raw);

    public Interpretation Interpret(string text, ThreadContext? ctx, ScreenScope screen, IAssistantVocabulary vocab, DateTime now, AskOverrides? overrides = null)
    {
        overrides ??= new();
        var tokens = AssistantText.Tokens(text);
        var stems = tokens.Select(AssistantText.Stem).ToArray();
        var padded = " " + string.Join(' ', tokens) + " ";
        var notes = new List<string>();
        var hasCtx = ctx?.Intent is not null && catalogue.Get(ctx.Intent) is not null;
        var stale = hasCtx && ctx!.At is { } at && now - at > Stale;

        // ── Removing an assumption: the same question again without one parameter ──
        if (overrides.Remove is { Length: > 0 } && tokens.Length == 0 && hasCtx)
        {
            var kept = ctx!.Params.Where(p => !overrides.Remove.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value with { Source = "carried" });
            var def0 = catalogue.Get(ctx.Intent)!;
            var slots0 = Fill(def0, TurnTypes.Refine, kept, [], screen, vocab, ctx, notes);
            var missing0 = def0.Required.FirstOrDefault(r => !slots0.ContainsKey(r));
            var parts0 = new ScoreParts(1, 1, 1, 0, 1, null, ["Re-asked without " + string.Join(", ", overrides.Remove)]);
            return Build(TurnTypes.Refine, def0, ctx.View == "compare" ? "summary" : ctx.View, slots0, parts0, ctx, missing0, notes, text);
        }

        // ── Small talk ──
        var bare = string.Join(' ', tokens);
        if (MetaPhrases.TryGetValue(bare, out var meta))
            return new(TurnTypes.Meta, ctx?.Intent, ctx?.View ?? "summary", [], new(1, 1, 1, 0, 1, null, []), Bands.Answer, null, [], meta, [], []);
        if (tokens.Length == 0)
            return new(TurnTypes.Clarify, null, "summary", [], new(0, 0, 0, 0, 0, null, ["Empty message"]), Bands.Clarify, "What would you like to know?", Starters(), null, [], []);

        // ── Markers ──
        var first = tokens[0];
        var correction = first is "no" or "nope" or "wrong" or "actually" || padded.Contains(" i meant ") || padded.Contains(" i mean ")
                         || padded.Contains(" not that ") || padded.StartsWith(" not ") || padded.Contains(" instead ");
        var simpler = padded.Contains(" dont understand ") || padded.Contains(" do not understand ") || padded.Contains(" simpler ") || padded.Contains(" simply ")
                      || padded.Contains(" what does that mean ") || padded.Contains(" plain ") || padded.Contains(" explain that ") || padded.Contains(" explain it ")
                      || padded.Contains(" in simple ");
        var drill = simpler || tokens.Any(t => t is "why" or "breakdown" or "details" or "detail" or "detailed" or "components" or "composition" or "calculated")
                    || padded.Contains(" break it down ") || padded.Contains(" break down ") || padded.Contains(" made up ") || padded.Contains(" makes up ")
                    || padded.Contains(" how come ") || padded.Contains(" explain ") && !padded.Contains(" explain what ") || padded.Contains(" tell me more ") || padded.Contains(" more detail ");
        var compare = tokens.Any(t => t is "compare" or "compared" or "vs" or "versus" or "against") || padded.Contains(" difference between ");
        var pronoun = tokens.Any(Pronouns.Contains);
        var content = Enumerable.Range(0, tokens.Length)
            .Where(i => !AssistantText.Stop.Contains(tokens[i]) && !AssistantText.Stop.Contains(stems[i]) && !MarkerWords.Contains(tokens[i])).ToList();
        var refine = first is "and" or "also" or "same" || padded.StartsWith(" what about ") || padded.StartsWith(" how about ") || padded.StartsWith(" now ")
                     || padded.StartsWith(" for ") || padded.StartsWith(" in ") || padded.StartsWith(" by ") || padded.StartsWith(" only ") || compare || content.Count <= 2;

        // ── Recognise names ──
        var used = new bool[tokens.Length];
        var markerPositions = Enumerable.Range(0, tokens.Length).Where(i => MarkerWords.Contains(tokens[i])).ToHashSet();
        // Words inside an intent's phrase ("how are we doing") are never part of a procedure's name.
        var phraseUsed = new bool[tokens.Length];
        foreach (var def in catalogue.Intents)
            foreach (var phrase in def.NormalPhrases.Keys)
                foreach (var p in PhrasePositions(tokens, phrase)) phraseUsed[p] = true;

        var explicitSlots = new Dictionary<string, Slot>();
        var ambiguous = new List<(string Kind, List<EntityMatch> Matches)>();
        var entityPositions = new Dictionary<string, int[]>();

        void Take(string kind, List<EntityMatch> found)
        {
            if (found.Count == 0) return;
            var best = found[0];
            if (found.Count > 1 && !best.ByCode && found[1].Score >= best.Score - 0.05 && found[1].Positions.Length == best.Positions.Length
                && found[1].Key != best.Key && found[1].Label != best.Label)
                ambiguous.Add((kind, found.Where(f => f.Score >= best.Score - 0.05).Take(4).ToList()));
            explicitSlots[kind] = new Slot(kind, best.Key, best.Label, "explicit", best.Score, CodeNote(kind, best.Extra));
            entityPositions[kind] = best.Positions;
            foreach (var p in best.Positions) used[p] = true;
        }

        // Picks from a clicked option win over anything recognised.
        var picks = overrides.Pick ?? [];

        // "hospital" or "medical" alone names no hospital in particular.
        // "top 10", "bottom 5", "first 20": a list size, before anything can read the number as a code.
        for (var i = 1; i < tokens.Length; i++)
            if (tokens[i - 1] is "top" or "bottom" or "first" or "last" or "best" or "worst" or "lowest" or "highest" or "least" or "most" or "cheapest" or "biggest" or "smallest" && int.TryParse(tokens[i], out var early) && early is >= 1 and <= 200)
            {
                explicitSlots["limit"] = new("limit", early.ToString(), early.ToString(), "explicit", 1);
                used[i] = true; entityPositions["limit"] = [i];
                break;
            }

        Take("hospital", vocab.Hospitals.Find(stems, used, "hospital", 0.6).Where(m => m.ByCode || m.Positions.Any(p => !GenericPlaceWords.Contains(stems[p]))).ToList());
        // "compare Northgate with Swedi": a second hospital is what to compare against.
        Slot? secondHospital = null;
        if (compare && explicitSlots.TryGetValue("hospital", out var firstHospital))
        {
            var more = vocab.Hospitals.Find(stems, used, "hospital", 0.6)
                .Where(m => m.Key != firstHospital.Key && (m.ByCode || m.Positions.Any(p => !GenericPlaceWords.Contains(stems[p])))).ToList();
            if (more.Count > 0)
            {
                secondHospital = new("hospital", more[0].Key, more[0].Label, "explicit", more[0].Score);
                foreach (var p in more[0].Positions) used[p] = true;
                if (more[0].Positions.Min() < entityPositions["hospital"].Min())
                {
                    // The one named first is the base.
                    (secondHospital, explicitSlots["hospital"]) = (firstHospital, secondHospital);
                    entityPositions["hospital"] = more[0].Positions;
                }
            }
        }
        if (picks.TryGetValue("hospital", out var pickedHospital))
        {
            var e = vocab.Hospitals.Entries.FirstOrDefault(x => x.Key == pickedHospital);
            if (e is not null) { explicitSlots["hospital"] = new("hospital", e.Key, e.Label, "explicit", 1); ambiguous.RemoveAll(a => a.Kind == "hospital"); }
        }

        // The hospital names are looked up in depends on whether this is a follow-up; decide that loosely here.
        var continuing = hasCtx && (refine || drill || correction || compare);
        var lookupHospital = explicitSlots.GetValueOrDefault("hospital")?.Key
                             ?? (continuing ? ctx!.Params.GetValueOrDefault("hospital")?.Key : null)
                             ?? screen.Hospital ?? ctx?.Params.GetValueOrDefault("hospital")?.Key ?? vocab.DefaultHospital;
        var periods = lookupHospital is null ? [] : vocab.Periods(lookupHospital);
        var anchor = ctx?.Params.GetValueOrDefault("period")?.Key ?? screen.Period;
        var pm = PeriodParser.Find(tokens, periods, anchor);
        if (pm is not null)
        {
            foreach (var p in pm.Positions) used[p] = true;
            entityPositions["period"] = pm.Positions;
            if (pm.Code.Length > 0)
            {
                var label = periods.FirstOrDefault(x => x.Code == pm.Code)?.Code ?? pm.Code;
                explicitSlots["period"] = new("period", pm.Code, label, "explicit", pm.Score, pm.Note);
            }
            // Once an option was picked, the note that led to the question no longer applies.
            else if (pm.Note is not null && picks.Count == 0) notes.Add(pm.Note);
        }
        // "compare this month with August": a second period is what to compare against; the one named first is the base.
        Slot? secondPeriod = null;
        if (compare && pm is { Code.Length: > 0 })
        {
            var masked = tokens.Select((t, i) => pm.Positions.Contains(i) ? "" : t).ToArray();
            var pm2 = PeriodParser.Find(masked, periods, anchor);
            if (pm2 is { Code.Length: > 0 } && pm2.Code != pm.Code)
            {
                foreach (var p in pm2.Positions) used[p] = true;
                var (baseP, otherP) = pm2.Positions.Min() < pm.Positions.Min() ? (pm2, pm) : (pm, pm2);
                explicitSlots["period"] = new("period", baseP.Code, baseP.Code, "explicit", baseP.Score, baseP.Note);
                secondPeriod = new("period", otherP.Code, otherP.Code, "explicit", otherP.Score, otherP.Note);
                entityPositions["period"] = pm.Positions.Concat(pm2.Positions).ToArray();
            }
        }
        if (picks.TryGetValue("period", out var pickedPeriod) && periods.Any(x => x.Code == pickedPeriod))
            explicitSlots["period"] = new("period", pickedPeriod, pickedPeriod, "explicit", 1);

        // What to rank by: "by revenue", "by margin %", "highest cost". "%" is lost in normalising, so read it from the raw text.
        var raw = " " + (text ?? "").ToLowerInvariant() + " ";
        (string Key, string Label, string[] Words)? measure =
            System.Text.RegularExpressions.Regex.IsMatch(raw, @"margin\s*(%|percent|percentage|pct|ratio|rate)") ? ("marginPct", "Margin %", ["margin", "percent", "percentage", "pct", "ratio", "rate"])
            : padded.Contains(" contribution ") ? ("contribution", "Contribution", ["contribution"])
            : padded.Contains(" direct cost ") ? ("direct", "Direct cost", ["direct", "cost"])
            : padded.Contains(" indirect cost ") || padded.Contains(" indirect ") ? ("indirect", "Indirect cost", ["indirect", "cost"])
            : padded.Contains(" overhead") ? ("overhead", "Overheads", ["overhead", "overheads"])
            : tokens.Any(t => t is "revenue" or "revenues" or "income" or "sales") ? ("revenue", "Revenue", ["revenue", "revenues", "income", "sales"])
            : tokens.Any(t => t is "encounters" or "encounter" or "visits" or "cases" or "patients") && (padded.Contains(" by ") || padded.Contains(" most ") || padded.Contains(" top ")) ? ("encounters", "Encounters", ["encounters", "encounter", "visits", "cases", "patients"])
            : padded.Contains(" total cost ") || padded.Contains(" by cost ") || padded.Contains(" highest cost ") || padded.Contains(" most cost") ? ("total", "Total cost", ["total", "cost"])
            : padded.Contains(" by margin ") || padded.Contains(" by profit ") || padded.Contains(" highest margin ") || padded.Contains(" lowest margin ") ? ("operating", "Margin", ["margin", "profit"])
            : null;
        if (measure is { } ms)
        {
            explicitSlots["measure"] = new("measure", ms.Key, ms.Label, "explicit", 1);
            entityPositions["measure"] = Enumerable.Range(0, tokens.Length).Where(i => ms.Words.Contains(tokens[i])).ToArray();
        }
        string[] bottomWords = ["cheapest", "lowest", "least", "worst", "bottom", "losing", "lose", "loss", "unprofitable", "negative", "smallest"];
        string[] topWords = ["top", "highest", "most", "biggest", "largest", "best", "costliest", "expensive"];
        var orderAt = Enumerable.Range(0, tokens.Length).FirstOrDefault(i => bottomWords.Contains(tokens[i]) || topWords.Contains(tokens[i]), -1);
        if (orderAt >= 0)
        {
            var bottom = bottomWords.Contains(tokens[orderAt]) || padded.Contains(" least profitable ");
            explicitSlots["order"] = new("order", bottom ? "bottom" : "top", bottom ? "lowest first" : "highest first", "explicit", 1);
            entityPositions["order"] = [orderAt];
        }
        var metricAt = Enumerable.Range(0, tokens.Length).FirstOrDefault(i => tokens[i] is "volume" or "common" or "busiest" or "performed" or "frequent", -1);
        if (metricAt >= 0) { explicitSlots["metric"] = new("metric", "volume", "by volume", "explicit", 1); entityPositions["metric"] = [metricAt]; }

        // Dimensions, screens, terms and how-tos. Not marked as used yet: they only count for an intent that uses them.
        var staticFound = vocab.Static.Find(stems, used, null, 0.6, 24)
            .Where(m => m.Kind switch { "term" => m.Recall >= 0.99, "howto" => m.Recall >= 0.66, "nav" => m.Recall >= 0.5, _ => true })
            .GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.ToList());

        // Procedures and packages: must use at least one word that is not a question word ("health check", not "quality check"),
        // and at least one word exactly, so a typo alone never names one of tens of thousands of items. Follow-up words
        // ("break it down", "compare") are never part of a name.
        // A name may include words of a phrase ("OPEN APPROVAL - NON-BILLABLE"), but must also use a word of its own:
        // "non-billable encounters" names no procedure.
        var largeSkip = used.Select((u, i) => u || markerPositions.Contains(i)).ToArray();
        var catalogueLex = lookupHospital is null ? null : vocab.ForHospital(lookupHospital);
        var inHospital = catalogueLex is null ? [] : catalogueLex.Find(stems, largeSkip, null, 0.6, 8)
            .Where(m => m.ByCode || m.Exact >= 1 && m.Positions.Any(p => !catalogue.AllKeywords.Contains(stems[p]) && !phraseUsed[p] && !stems[p].All(char.IsDigit))).ToList();
        // A full code may begin with the hospital's own code ("NSH-RYD.BED-ICU-DAY"): look for codes across those words too.
        if (catalogueLex is not null && entityPositions.TryGetValue("hospital", out var hospitalAt))
        {
            var codeSkip = phraseUsed.Select((u, i) => u || markerPositions.Contains(i)).ToArray();
            var codes = catalogueLex.Find(stems, codeSkip, null, 1.0, 4).Where(m => m.ByCode && hospitalAt.All(m.Positions.Contains)).ToList();
            if (codes.Count > 0) inHospital = codes.Concat(inHospital.Where(m => !m.ByCode)).ToList();
        }
        foreach (var g in inHospital.GroupBy(m => m.Kind)) Take(g.Key, g.ToList());
        foreach (var kind in new[] { "procedure", "package" })
            if (picks.TryGetValue(kind, out var pk) && lookupHospital is not null)
            {
                var e = vocab.ForHospital(lookupHospital).Entries.FirstOrDefault(x => x.Kind == kind && x.Key == pk);
                if (e is not null) { explicitSlots[kind] = new(kind, e.Key, e.Label, "explicit", 1, CodeNote(kind, e.Extra)); ambiguous.RemoveAll(a => a.Kind == kind); }
            }
        // A number on its own is a list size ("top 20"); one inside a code was taken above.
        for (var i = 0; i < tokens.Length && !explicitSlots.ContainsKey("limit"); i++)
            if (!used[i] && tokens[i][0] != '0' && int.TryParse(tokens[i], out var n) && n is >= 1 and <= 200)
            {
                explicitSlots["limit"] = new("limit", n.ToString(), n.ToString(), "explicit", 1);
                used[i] = true; entityPositions["limit"] = [i];
                break;
            }

        // A package found as a name outranks the same words read as a procedure.
        if (explicitSlots.TryGetValue("package", out var pkg) && explicitSlots.TryGetValue("procedure", out var proc) && pkg.Score >= proc.Score)
        { explicitSlots.Remove("procedure"); ambiguous.RemoveAll(a => a.Kind == "procedure"); }

        // ── Score every intent on the words alone ──
        var scopeUsed = Enumerable.Range(0, tokens.Length).Where(i => used[i]).ToHashSet();
        var scores = catalogue.Intents.Select(def => ScoreIntent(def, stems, padded, tokens, staticFound, explicitSlots, entityPositions, content, scopeUsed))
            .OrderByDescending(s => s.Raw).ThenByDescending(s => s.Score).ToList();
        var top = scores[0];
        var second = scores.Count > 1 ? scores[1] : null;
        var strong = top.Score >= 0.6;
        var ctxDef = hasCtx ? catalogue.Get(ctx!.Intent)! : null;

        if (overrides.Intent is not null && catalogue.Get(overrides.Intent) is { } forced)
        {
            top = scores.First(s => s.Def.Id == forced.Id) with { Score = 1, Raw = 9 };
            strong = true;
            second = null;
        }

        // "How do I create X?" with no guide for X asks for guidance, not data: list the guides rather than answer a report.
        var howtoAsk = padded.StartsWith(" how do i ") || padded.StartsWith(" how to ") || padded.StartsWith(" how can i ") || padded.StartsWith(" how do we ")
                       || padded.StartsWith(" where do i ") || padded.StartsWith(" where can i ") || padded.Contains(" steps to ") || padded.StartsWith(" how should i ");
        var actionVerb = tokens.Any(t => t is "create" or "add" or "make" or "start" or "set" or "setup" or "configure" or "publish" or "approve" or "delete" or "remove"
                                         or "change" or "edit" or "update" or "import" or "upload" or "load" or "run" or "rerun" or "export" or "download" or "fix" or "close");
        if (howtoAsk && actionVerb && !staticFound.ContainsKey("howto") && overrides.Intent is null && catalogue.Get("help.howto") is { } guide)
        {
            var guideScore = new ScoreParts(0.5, 0, Coverage(content, Enumerable.Range(0, tokens.Length).Where(i => used[i]).ToHashSet(), markerPositions), 0, 0.5, null,
                ["A how-to question with no matching guide"]);
            // If it names a screen, point there first.
            // Only a screen named in full: "HIS" alone is not the HIS Invoicing screen.
            var screenHint = staticFound.GetValueOrDefault("nav")?.FirstOrDefault(m => m.Recall >= 0.99);
            var opts = Knowledge.HowTos.Select(h => new Option(h.Title, "how do i " + h.Title.ToLowerInvariant())).ToList();
            if (screenHint is not null) opts.Insert(0, new Option($"Open {screenHint.Label}", "open " + screenHint.Label.ToLowerInvariant()));
            return new(TurnTypes.Clarify, guide.Id, "summary", [], guideScore, Bands.Clarify,
                screenHint is null
                    ? "I don't have step-by-step guidance for that yet. These are the guides I have:"
                    : $"I don't have step-by-step guidance for that yet, but it is done on {screenHint.Label}. These are the guides I have:",
                opts.ToArray(), null, [], notes.ToArray());
        }

        // ── Turn type ──
        string turn;
        IntentDef? def1;
        var view = "summary";
        var explicitKinds = explicitSlots.Keys.Where(k => k != "order" || !drill).ToHashSet();

        bool Relevant(IntentDef d, string k) => ScopeKinds.Contains(k) || d.Uses.Contains(k) || d.Required.Contains(k);

        if (overrides.Intent is not null)
        {
            turn = hasCtx && top.Def.Id == ctxDef!.Id ? TurnTypes.Refine : TurnTypes.New;
            def1 = top.Def;
            if (compare && def1.Compare) view = "compare";
            else if (drill && turn == TurnTypes.Refine) view = simpler || !def1.Drilldown ? "explain" : "detail";
            else if (turn == TurnTypes.Refine && ctx!.View == "compare" && def1.Compare) view = "compare";
        }
        else if (hasCtx && correction)
        {
            turn = TurnTypes.Correction;
            def1 = strong && top.Def.Id != ctxDef!.Id ? top.Def : ctxDef!;
        }
        else if (hasCtx && drill && (!strong || top.Def.Id == ctxDef!.Id || top.Def.Id == "help.term" && !explicitSlots.ContainsKey("term") && !staticFound.ContainsKey("term")))
        {
            turn = TurnTypes.Drilldown;
            def1 = ctxDef!;
            view = simpler || !def1.Drilldown ? "explain" : "detail";
        }
        else if (hasCtx && ctx!.Pending is { } pending && (explicitSlots.ContainsKey(pending) || staticFound.ContainsKey(pending)) && (!strong || top.Def.Id == ctxDef!.Id))
        {
            turn = TurnTypes.Refine;
            def1 = ctxDef!;
        }
        else if (hasCtx && refine && (!strong || top.Def.Id == ctxDef!.Id) && explicitKinds.Concat(staticFound.Keys).Any(k => Relevant(ctxDef!, k)))
        {
            turn = TurnTypes.Refine;
            def1 = ctxDef!;
            if (compare && def1.Compare) view = "compare";
        }
        else if (hasCtx && refine && compare && ctxDef!.Compare && !strong)
        {
            // "compare with another hospital": the target is still to be named.
            turn = TurnTypes.Refine;
            def1 = ctxDef;
            view = "compare";
        }
        else if (hasCtx && refine && !strong && (explicitKinds.Count > 0 || staticFound.Count > 0) && top.Score >= 0.3)
        {
            // A follow-up naming something the last answer does not use ("by payer" after a summary): move to the intent that does.
            turn = TurnTypes.Shift;
            def1 = top.Def;
        }
        else if (hasCtx && pronoun && top.Def.Id != ctxDef!.Id && scores.First(s => s.Def.Id == ctxDef.Id).Score >= 0.35
                 && !explicitKinds.Any(LargeKinds.Contains) && !staticFound.ContainsKey("term") && !staticFound.ContainsKey("nav"))
        {
            // "which ones lose money?" after a profitability answer: "ones" are the rows just shown.
            turn = TurnTypes.Refine;
            def1 = ctxDef;
        }
        else if (hasCtx && strong)
        {
            // Same intent with new parameters ("top 5 by volume") refines; the same question reworded soon after rephrases.
            var newParams = explicitKinds.Any(k => Relevant(ctxDef!, k) && ctx!.Params.GetValueOrDefault(k)?.Key != explicitSlots[k].Key);
            if (top.Def.Id == ctxDef!.Id && newParams) turn = TurnTypes.Refine;
            else if (top.Def.Id == ctxDef.Id && ctx!.At is { } last && now - last < RephraseWindow) turn = TurnTypes.Rephrase;
            else if (top.Def.Id != ctxDef.Id && pronoun) turn = TurnTypes.Shift;
            else turn = TurnTypes.New;
            def1 = top.Def;
        }
        else if (top.Score >= 0.3)
        {
            turn = TurnTypes.New;
            def1 = top.Def;
        }
        else if (compare && (secondPeriod is not null || secondHospital is not null))
        {
            // "compare August and September" names no topic: compare the run summaries.
            turn = TurnTypes.New;
            def1 = catalogue.Get("dashboard.summary")!;
            top = scores.First(s => s.Def.Id == def1.Id) with { Score = 0.8 };
        }
        else
        {
            var q = hasCtx
                ? $"I didn't understand that. You can follow up on the {ctxDef!.Label.ToLowerInvariant()} above, or ask something new:"
                : "I didn't understand that. Here are some things I can answer:";
            var opts = hasCtx
                ? ctxDef!.Suggestions.Take(3).Select(s => new Option(s, s)).Concat(Starters().Take(2)).ToArray()
                : Starters();
            return new(TurnTypes.Clarify, null, "summary", [], new(top.Score, 0, Coverage(content, Enumerable.Range(0, tokens.Length).Where(i => used[i]).ToHashSet(), markerPositions), 0, Math.Round(top.Score * 0.45, 4), top.Def.Id, ["No intent matched"]),
                Bands.Clarify, q, opts, null, [], notes.ToArray());
        }

        // ── Parameters ──
        var explicitForIntent = explicitSlots.Where(kv => Relevant(def1, kv.Key) || ScalarKinds.Contains(kv.Key) && def1.Uses.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (kind, list) in staticFound)
            if ((def1.Uses.Contains(kind) || def1.Required.Contains(kind)) && !explicitForIntent.ContainsKey(kind))
            {
                Take(kind, list);
                explicitForIntent[kind] = explicitSlots[kind];
            }

        // "Compare" in a new question asks for a comparison too, not only as a follow-up. A bare "vs"
        // ("billable vs non-billable") counts only when there is a hospital or period to compare against.
        if (compare && def1.Compare && view == "summary")
        {
            var hasTarget = secondPeriod is not null || secondHospital is not null
                || new[] { "hospital", "period" }.Any(k => explicitForIntent.TryGetValue(k, out var c)
                       && c.Key != (ctx?.Params.GetValueOrDefault(k)?.Key ?? (k == "hospital" ? screen.Hospital : screen.Period)));
            if (hasTarget || tokens.Any(t => t is "compare" or "compared")) view = "compare";
        }

        // In a comparison the named hospital or period is what to compare against, not a replacement,
        // unless the question named both sides ("this month with August").
        Slot? compareSlot = null;
        if (view == "compare")
        {
            if (secondPeriod is not null) compareSlot = secondPeriod;
            else if (secondHospital is not null) compareSlot = secondHospital;
            else
                foreach (var k in new[] { "hospital", "period" })
                {
                    var current = ctx?.Params.GetValueOrDefault(k)?.Key ?? (k == "hospital" ? screen.Hospital : screen.Period);
                    if (explicitForIntent.TryGetValue(k, out var c) && c.Key != current)
                    {
                        compareSlot = c with { Kind = k };
                        explicitForIntent.Remove(k);
                        break;
                    }
                }
        }

        var baseSlots = TurnTypes.Continues(turn) && ctx is not null ? ctx.Params : [];
        var slots = Fill(def1, turn, baseSlots, explicitForIntent, screen, vocab, ctx, notes);
        if (compareSlot is not null) slots["compare"] = compareSlot with { Kind = "compare:" + compareSlot.Kind };
        else if (view == "compare" && ctx?.Params.GetValueOrDefault("compare") is { } prevCompare && turn == TurnTypes.Refine && !compare) slots["compare"] = prevCompare;

        // ── Score ──
        var reasons = new List<string>();
        double intentScore;
        if (TurnTypes.Continues(turn) && def1.Id == ctxDef?.Id && turn != TurnTypes.Rephrase) intentScore = 1.0;
        else intentScore = top.Def.Id == def1.Id ? top.Score : scores.First(s => s.Def.Id == def1.Id).Score;
        if (turn == TurnTypes.Shift && intentScore < 0.6) intentScore = Math.Max(intentScore, 0.6);

        var entityScores = slots.Values.Where(s => s.Source == "explicit" && !ScalarKinds.Contains(s.Kind)).Select(s => s.Score).ToList();
        var entityScore = entityScores.Count == 0 ? 1.0 : entityScores.Average();
        var intentPositions = scores.First(s => s.Def.Id == def1.Id).Positions;
        var usedForCoverage = new HashSet<int>(intentPositions);
        foreach (var (k, pos) in entityPositions) if (slots.ContainsKey(k) || k == compareSlot?.Kind) foreach (var p in pos) usedForCoverage.Add(p);
        for (var i = 0; i < tokens.Length; i++) if (phraseUsed[i]) usedForCoverage.Add(i);
        var coverage = Coverage(content, usedForCoverage.Select(p => p).ToHashSet(), markerPositions);

        var penalty = 0.0;
        foreach (var s in slots.Values.Where(s => s.Source is "screen" or "default" && ScopeKinds.Contains(s.Kind)))
        { penalty += s.Source == "default" ? 0.03 : 0.01; }
        if (slots.Values.Any(s => s.Source == "default")) reasons.Add("Used a default for " + string.Join(", ", slots.Values.Where(s => s.Source == "default").Select(s => s.Kind)));
        if (turn is TurnTypes.New && second is not null && second.Score >= 0.5 && (top.Raw - second.Raw) / 1.2 < 0.12 && second.Def.Id != def1.Id)
        {
            penalty += 0.12;
            reasons.Add($"Close to '{second.Def.Label}'");
        }
        if (stale && TurnTypes.Continues(turn)) { penalty += 0.1; reasons.Add("Carried from a conversation more than 30 minutes old"); }
        if (coverage < 0.75) reasons.Add($"Only {coverage:P0} of the words were used");

        var confidence = Math.Clamp(Math.Round(0.45 * intentScore + 0.35 * entityScore + 0.2 * coverage - penalty, 4), 0, 1);
        var parts = new ScoreParts(Math.Round(intentScore, 4), Math.Round(entityScore, 4), Math.Round(coverage, 4), Math.Round(penalty, 4), confidence,
            second?.Def.Id is { } r && r != def1.Id ? r : null, reasons.ToArray());

        // ── Ambiguity and missing parameters ──
        var amb = ambiguous.FirstOrDefault(a => slots.TryGetValue(a.Kind, out var s) && s.Source == "explicit");
        if (amb.Matches is not null)
        {
            var label = amb.Kind switch { "procedure" => "procedure", "package" => "package", "hospital" => "hospital", _ => amb.Kind };
            var options = amb.Matches.Select(m => new Option(m.Extra is { Length: > 0 } code && amb.Kind != "hospital" ? $"{m.Label} ({code})" : m.Label, text, def1.Id,
                new Dictionary<string, string> { [amb.Kind] = m.Key })).ToArray();
            return new(TurnTypes.Clarify, def1.Id, view, slots, parts with { Confidence = Math.Min(confidence, 0.59) }, Bands.Clarify,
                $"Which {label} do you mean?", options, null, Changed(slots, ctx), notes.ToArray());
        }

        if (view == "compare" && !slots.ContainsKey("compare"))
        {
            var wantsPeriod = padded.Contains(" period ") || padded.Contains(" month ");
            var options = wantsPeriod
                ? (slots.GetValueOrDefault("hospital") is { } h ? vocab.Periods(h.Key) : []).Where(p => p.HasRun && p.Code != slots.GetValueOrDefault("period")?.Key)
                    .OrderByDescending(p => p.Start).Take(4).Select(p => new Option(p.Code, text, def1.Id, new() { ["compare.period"] = p.Code })).ToArray()
                : vocab.Hospitals.Entries.Where(e => e.Key != slots.GetValueOrDefault("hospital")?.Key).Take(5)
                    .Select(e => new Option(e.Label, text, def1.Id, new() { ["compare.hospital"] = e.Key })).ToArray();
            if (picks.Keys.FirstOrDefault(k => k.StartsWith("compare.")) is { } ck)
            {
                var kind = ck["compare.".Length..];
                var key = picks[ck];
                var label = kind == "hospital" ? vocab.Hospitals.Entries.FirstOrDefault(e => e.Key == key)?.Label ?? key : key;
                slots["compare"] = new Slot("compare:" + kind, key, label, "explicit", 1);
            }
            else
                return new(TurnTypes.Clarify, def1.Id, view, slots, parts with { Confidence = Math.Min(confidence, 0.59) }, Bands.Clarify,
                    wantsPeriod ? "Which period should I compare with?" : "Which hospital should I compare with?", options, null, Changed(slots, ctx), notes.ToArray());
        }

        var missing = def1.Required.FirstOrDefault(r => !slots.ContainsKey(r));
        return Build(turn, def1, view, slots, parts, ctx, missing, notes, text);
    }

    private Interpretation Build(string turn, IntentDef def, string view, Dictionary<string, Slot> slots, ScoreParts parts, ThreadContext? ctx, string? missing, List<string> notes, string text)
    {
        if (missing is not null)
        {
            var q = missing switch
            {
                "procedure" => "Which procedure? Give its name or code.",
                "package" => "Which package? Give its name or code.",
                "nav" => "Which screen should I open?",
                "term" => "Which term should I explain?",
                "howto" => "What would you like to do?",
                _ => $"Which {missing}?",
            };
            var options = missing switch
            {
                "nav" => Knowledge.StaticEntries().Where(e => e.Kind == "nav").Take(6).Select(e => new Option(e.Label, "open " + e.Label)).ToArray(),
                "term" => Knowledge.Terms.Take(6).Select(t => new Option(t.Label, "what is " + t.Label)).ToArray(),
                "howto" => Knowledge.HowTos.Select(h => new Option(h.Title, "how do i " + h.Title.ToLowerInvariant())).ToArray(),
                _ => [],
            };
            return new(TurnTypes.Clarify, def.Id, view, slots, parts with { Confidence = Math.Min(parts.Confidence, 0.59) }, Bands.Clarify, q, options, null,
                Changed(slots, ctx), notes.ToArray());
        }
        return new(turn, def.Id, view, slots, parts, Bands.Of(parts.Confidence), null, [], null, Changed(slots, ctx), notes.ToArray());
    }

    /// <summary>
    /// Fill the intent's parameters. Precedence: said now, then (for a follow-up) the thread, then
    /// the screen, then the thread, then a default. A procedure carried to another hospital is
    /// looked up there again by code, because procedures belong to a hospital.
    /// </summary>
    private static Dictionary<string, Slot> Fill(IntentDef def, string turn, Dictionary<string, Slot> carried, Dictionary<string, Slot> explicitSlots,
        ScreenScope screen, IAssistantVocabulary vocab, ThreadContext? ctx, List<string> notes)
    {
        var kinds = def.Uses.Concat(def.Required).Concat(["hospital", "period"]).Distinct().ToList();
        if (def.Id is "navigate" or "help.term" or "imports.recent") kinds.RemoveAll(k => k is "hospital" or "period");
        var slots = new Dictionary<string, Slot>();
        var continues = TurnTypes.Continues(turn);

        foreach (var k in kinds)
        {
            if (explicitSlots.TryGetValue(k, out var e)) { slots[k] = e; continue; }
            if (continues && carried.TryGetValue(k, out var c)) { slots[k] = c with { Source = "carried" }; continue; }
            if (k == "hospital")
            {
                if (screen.Hospital is { } sh && vocab.Hospitals.Entries.FirstOrDefault(x => x.Key == sh) is { } he) slots[k] = new(k, he.Key, he.Label, "screen", 1);
                else if (ctx?.Params.GetValueOrDefault("hospital") is { } ch) slots[k] = ch with { Source = "carried" };
                else if (vocab.DefaultHospital is { } dh && vocab.Hospitals.Entries.FirstOrDefault(x => x.Key == dh) is { } de) slots[k] = new(k, de.Key, de.Label, "default", 1, "the hospital with the latest published run");
            }
        }

        // The period belongs to the hospital chosen above.
        if (kinds.Contains("period") && !slots.ContainsKey("period") && slots.GetValueOrDefault("hospital") is { } hosp)
        {
            var periods = vocab.Periods(hosp.Key);
            var screenOk = screen.Period is { } sp && screen.Hospital == hosp.Key && periods.Any(p => p.Code == sp);
            // A follow-up carries only what is in its base (so a removed period stays removed); a new question may reuse the thread's.
            var carriedPeriod = carried.GetValueOrDefault("period") ?? (continues ? null : ctx?.Params.GetValueOrDefault("period"));
            if (continues && carriedPeriod is not null && periods.Any(p => p.Code == carriedPeriod.Key)) slots["period"] = carriedPeriod with { Source = "carried" };
            else if (carriedPeriod is not null && continues)
            {
                // The hospital changed: the same month, in the new hospital's own codes.
                var oldHospital = carried.GetValueOrDefault("hospital")?.Key ?? ctx?.Params.GetValueOrDefault("hospital")?.Key;
                var old = oldHospital is null ? null : vocab.Periods(oldHospital).FirstOrDefault(p => p.Code == carriedPeriod.Key);
                var same = old is null ? null : periods.FirstOrDefault(p => p.Start == old.Start);
                if (same is not null) slots["period"] = new("period", same.Code, same.Code, "carried", 1, $"same month as {carriedPeriod.Key}");
                else if (Latest(periods) is { } lp) { slots["period"] = new("period", lp.Code, lp.Code, "default", 1, "latest with a run"); notes.Add($"{hosp.Label} has no {carriedPeriod.Key}; used {lp.Code}."); }
            }
            else if (screenOk) slots["period"] = new("period", screen.Period!, screen.Period!, "screen", 1);
            else if (!continues && carriedPeriod is not null && periods.Any(p => p.Code == carriedPeriod.Key)) slots["period"] = carriedPeriod with { Source = "carried" };
            else if (Latest(periods) is { } lp) slots["period"] = new("period", lp.Code, lp.Code, "default", 1, "latest with a run");
        }

        // Procedures and packages carried into another hospital are found there again.
        foreach (var k in new[] { "procedure", "package" })
        {
            if (!slots.TryGetValue(k, out var s) || s.Source != "carried" || slots.GetValueOrDefault("hospital") is not { } h) continue;
            var lex = vocab.ForHospital(h.Key);
            if (lex.Entries.Any(x => x.Kind == k && x.Key == s.Key)) continue;
            var code = s.Note?.StartsWith("code ") == true ? s.Note[5..] : null;
            var again = lex.Entries.FirstOrDefault(x => x.Kind == k && code is not null && x.Extra == code)
                        ?? lex.Entries.FirstOrDefault(x => x.Kind == k && string.Equals(x.Label, s.Label, StringComparison.OrdinalIgnoreCase));
            if (again is not null) slots[k] = new(k, again.Key, again.Label, "carried", 1, again.Extra is null ? null : "code " + again.Extra);
            else { slots.Remove(k); notes.Add($"{s.Label} is not in {h.Label}'s catalogue."); }
        }

        foreach (var (k, v) in explicitSlots) if (!slots.ContainsKey(k) && (ScalarKinds.Contains(k) || def.Uses.Contains(k))) slots[k] = v;
        if (continues) foreach (var k in ScalarKinds) if (!slots.ContainsKey(k) && carried.TryGetValue(k, out var c) && def.Uses.Contains(k)) slots[k] = c with { Source = "carried" };
        return slots;
    }

    private static PeriodRef? Latest(IReadOnlyList<PeriodRef> periods) =>
        periods.Where(p => p.HasRun).OrderByDescending(p => p.Start).FirstOrDefault() ?? periods.OrderByDescending(p => p.Start).FirstOrDefault();

    private static string[] Changed(Dictionary<string, Slot> slots, ThreadContext? ctx) =>
        ctx is null || ctx.Intent is null ? [] : slots.Where(s => ctx.Params.GetValueOrDefault(s.Key)?.Key is { } old && old != s.Value.Key || !ctx.Params.ContainsKey(s.Key) && s.Value.Source == "explicit")
            .Select(s => s.Key).ToArray();

    private static double Coverage(List<int> content, HashSet<int> used, HashSet<int> markers)
    {
        var relevant = content.Where(i => !markers.Contains(i)).ToList();
        return relevant.Count == 0 ? 1.0 : (double)relevant.Count(used.Contains) / relevant.Count;
    }

    private IntentScore ScoreIntent(IntentDef def, string[] stems, string padded, string[] tokens, Dictionary<string, List<EntityMatch>> staticFound,
        Dictionary<string, Slot> explicitSlots, Dictionary<string, int[]> entityPositions, List<int> content, HashSet<int> scopeUsed)
    {
        var raw = 0.0;
        var pos = new HashSet<int>();
        foreach (var (kw, w) in def.Stems)
        {
            var best = 0.0; var at = -1;
            for (var i = 0; i < stems.Length; i++)
            {
                var s = stems[i] == kw ? 1.0 : stems[i].Length >= 5 && kw.Length >= 5 ? AssistantText.Similarity(stems[i], kw) : 0;
                if (s > best) { best = s; at = i; }
            }
            if (best >= 0.85) { raw += w * best; pos.Add(at); }
        }
        foreach (var (phrase, w) in def.NormalPhrases)
            if (padded.Contains(phrase)) { raw += w; foreach (var p in PhrasePositions(tokens, phrase)) pos.Add(p); }
        foreach (var (kind, boost) in def.Entities)
        {
            if (explicitSlots.TryGetValue(kind, out var s)) { raw += boost * s.Score; if (entityPositions.TryGetValue(kind, out var ep)) foreach (var p in ep) pos.Add(p); }
            else if (staticFound.TryGetValue(kind, out var l)) { raw += boost * l[0].Score; foreach (var p in l[0].Positions) pos.Add(p); }
        }
        // Some intents need a word of their own ("package", "what is"), or another intent's words would trigger them.
        var satisfied = def.Requires.Length == 0 || def.Requires.Any(r => r.Contains(' ') ? padded.Contains(" " + r + " ") : stems.Contains(AssistantText.Stem(r)));
        // Dominant only when the name is the whole subject: nothing else is left in the question
        // ("what is a variance", but not "what does the delivery package cost").
        if (satisfied && def.DominantKind is { } dk && def.Requires.Length > 0
            && (explicitSlots.ContainsKey(dk) || staticFound.TryGetValue(dk, out var dl) && dl[0].Recall >= 0.99))
        {
            var named = explicitSlots.ContainsKey(dk) ? entityPositions.GetValueOrDefault(dk) ?? [] : staticFound[dk][0].Positions;
            var requireWords = def.Requires.SelectMany(r => r.Split(' ')).Select(AssistantText.Stem).ToHashSet();
            var leftover = content.Count(i => !named.Contains(i) && !pos.Contains(i) && !scopeUsed.Contains(i) && !requireWords.Contains(stems[i]));
            // A how-to question says what it is outright ("how do I …"), so a matching guide wins even with extra words.
            if (leftover == 0 || dk == "howto") raw += 2.0;
        }
        if (!satisfied) raw = Math.Min(raw, 0.54);
        return new(def, Math.Min(1.0, Math.Round(raw / 1.2, 4)), pos, raw);
    }

    private static IEnumerable<int> PhrasePositions(string[] tokens, string paddedPhrase)
    {
        var words = paddedPhrase.Trim().Split(' ');
        for (var i = 0; i + words.Length <= tokens.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < words.Length && ok; j++) ok = tokens[i + j] == words[j];
            if (ok) return Enumerable.Range(i, words.Length);
        }
        return [];
    }

    public static Option[] Starters() =>
    [
        new("Run summary", "Give me the summary for this period"),
        new("Most expensive procedures", "Most expensive procedures"),
        new("Profitability by payer", "Profitability by payer"),
        new("Biggest variances", "Biggest variances"),
        new("Idle capacity", "How much idle capacity do we have?"),
        new("Data quality", "Any data quality issues?"),
    ];
}
