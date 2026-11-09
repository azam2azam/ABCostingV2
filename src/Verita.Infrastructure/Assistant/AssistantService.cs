using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Application.Assistant;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;
using Verita.Infrastructure.Queries;

namespace Verita.Infrastructure.Assistant;

public sealed record AssumptionDto(string Kind, string Label, string Value, string Source, bool Changed, bool Removable, string? Note);

/// <summary>The assistant's reply as stored and as sent: it renders the same way when the thread is reopened.</summary>
public sealed record AssistantReply(
    string Text, string Band, string? TurnType, string? Intent, string? IntentLabel, double Confidence, ScoreParts Score,
    AssumptionDto[] Assumptions, ReplyBlock[] Blocks, ReplyLink[] Links, Option[] Suggestions, Option[] Options, string[] Notes,
    string? Run, string? RunStatus, string? Hospital, string? Period, ReplyLink? Navigate);

public sealed record AssistantMessageDto(Guid Id, string Role, string Text, DateTime CreatedAt, string? TurnType, AssistantReply? Reply,
    int? Feedback, bool Hidden, bool Stale);
public sealed record AssistantThreadDto(Guid Id, string Title, bool IsPinned, bool IsArchived, DateTime CreatedAt, DateTime UpdatedAt, int Messages, string? LastText);
public sealed record AssistantThreadDetail(AssistantThreadDto Thread, AssistantMessageDto[] Messages);
public sealed record AskBody(Guid? ThreadId, string Text, string? Hospital, string? Period, string? Intent, Dictionary<string, string>? Pick, string[]? Remove);
public sealed record AskResult(AssistantThreadDto Thread, AssistantMessageDto Question, AssistantMessageDto Answer);
public sealed record ThreadPatch(string? Title, bool? Pinned, bool? Archived);
public sealed record FeedbackBody(int Value, string? Reason);

/// <summary>
/// The rule-based assistant: threads, questions and answers. It understands a question with the
/// <see cref="Interpreter"/>, answers it with <see cref="AssistantAnswers"/> through the same read
/// queries as the screens, and stores both turns so a thread can be reopened as it was.
/// </summary>
public sealed class AssistantService(IDbContextFactory<VeritaDbContext> factory, ReadQueries q, AssistantAnswers answers)
{
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
    private static readonly Interpreter Interpreter = new(IntentCatalogue.Default);
    private const int MaxText = 1000;

    private static readonly Dictionary<string, string> KindLabels = new()
    {
        ["hospital"] = "Hospital", ["period"] = "Period", ["procedure"] = "Procedure", ["package"] = "Package", ["dimension"] = "By",
        ["limit"] = "Show", ["order"] = "Order", ["metric"] = "Ranked", ["nav"] = "Screen", ["term"] = "Term", ["howto"] = "Task",
        ["compare"] = "Compared with", ["measure"] = "Ranked by",
    };

    // ── Threads ──────────────────────────────────────────────────────────────────────

    public async Task<AssistantThreadDto[]> ThreadsAsync(Principal p, bool archived, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var threads = await db.AssistantThreads.AsNoTracking()
            .Where(t => t.UserId == p.Id && !t.IsDeleted && t.IsArchived == archived)
            .OrderByDescending(t => t.IsPinned).ThenByDescending(t => t.UpdatedAt).Take(200).ToListAsync(ct);
        var ids = threads.Select(t => t.Id).ToList();
        var stats = await db.AssistantMessages.AsNoTracking().Where(m => ids.Contains(m.ThreadId))
            .GroupBy(m => m.ThreadId).Select(g => new { g.Key, N = g.Count(), Last = g.Max(m => m.CreatedAt) }).ToDictionaryAsync(x => x.Key, ct);
        var lastByThread = await db.AssistantMessages.AsNoTracking().Where(m => ids.Contains(m.ThreadId) && m.Role == "user")
            .GroupBy(m => m.ThreadId).Select(g => new { g.Key, Text = g.OrderByDescending(m => m.CreatedAt).Select(m => m.Text).First() }).ToDictionaryAsync(x => x.Key, x => x.Text, ct);
        return threads.Select(t => Dto(t, stats.GetValueOrDefault(t.Id)?.N ?? 0, lastByThread.GetValueOrDefault(t.Id))).ToArray();
    }

    public async Task<AssistantThreadDetail> ThreadAsync(Principal p, Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var t = await Owned(db, p, id, ct);
        var messages = await db.AssistantMessages.AsNoTracking().Where(m => m.ThreadId == id).OrderBy(m => m.CreatedAt).ToListAsync(ct);
        var latestRun = await LatestRunsAsync(p, messages, ct);
        return new(Dto(t, messages.Count, messages.LastOrDefault(m => m.Role == "user")?.Text), messages.Select(m => Message(p, m, latestRun)).ToArray());
    }

    public async Task<AssistantThreadDto> PatchAsync(Principal p, Guid id, ThreadPatch body, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var t = await Owned(db, p, id, ct, track: true);
        if (body.Title is { } title)
        {
            title = title.Trim();
            if (title.Length is 0 or > 200) throw AppError.Invalid("A title is 1 to 200 characters.");
            t.Title = title;
        }
        if (body.Pinned is { } pin) t.IsPinned = pin;
        if (body.Archived is { } arch) t.IsArchived = arch;
        await db.SaveChangesAsync(ct);
        return Dto(t, await db.AssistantMessages.CountAsync(m => m.ThreadId == id, ct), null);
    }

    public async Task DeleteAsync(Principal p, Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var t = await Owned(db, p, id, ct, track: true);
        t.IsDeleted = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task<AssistantMessageDto> FeedbackAsync(Principal p, Guid messageId, FeedbackBody body, CancellationToken ct)
    {
        if (body.Value is < -1 or > 1) throw AppError.Invalid("Feedback is 1, 0 or -1.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var m = await db.AssistantMessages.FirstOrDefaultAsync(x => x.Id == messageId && x.Role == "assistant", ct) ?? throw AppError.NotFound("Message");
        await Owned(db, p, m.ThreadId, ct);
        m.Feedback = body.Value == 0 ? null : body.Value;
        m.FeedbackReason = body.Value == 0 ? null : body.Reason?.Trim() is { Length: > 0 } r ? r[..Math.Min(200, r.Length)] : null;
        await db.SaveChangesAsync(ct);
        return Message(p, m, []);
    }

    // ── Asking ───────────────────────────────────────────────────────────────────────

    public async Task<AskResult> AskAsync(Principal p, AskBody body, CancellationToken ct, string? display = null)
    {
        var text = (body.Text ?? "").Trim();
        if (text.Length > MaxText) throw AppError.Invalid($"A question is at most {MaxText} characters.");
        if (text.Length == 0 && body.Remove is not { Length: > 0 }) throw AppError.Invalid("Ask a question.");
        var now = DateTime.UtcNow;

        await using var db = await factory.CreateDbContextAsync(ct);
        AssistantThread thread;
        if (body.ThreadId is { } tid) thread = await Owned(db, p, tid, ct, track: true);
        else
        {
            thread = new AssistantThread { Id = Guid.NewGuid(), UserId = p.Id, Title = Title(text), CreatedAt = now, UpdatedAt = now };
            db.AssistantThreads.Add(thread);
        }
        var ctx = thread.ContextJson is null ? null : JsonSerializer.Deserialize<ThreadContext>(thread.ContextJson, J);
        var vocab = await AssistantVocabulary.ForAsync(factory, p, ct);
        var it = Interpreter.Interpret(text, ctx, new ScreenScope(body.Hospital, body.Period), vocab, now,
            new AskOverrides(body.Intent, body.Pick, body.Remove));

        var displayText = display ?? (text.Length > 0 ? text : "Without " + string.Join(", ", (body.Remove ?? []).Select(k => KindLabels.GetValueOrDefault(k, k).ToLowerInvariant())));
        var question = new AssistantMessage
        {
            Id = Guid.NewGuid(), ThreadId = thread.Id, Role = "user", Text = displayText, Intent = it.Intent, TurnType = it.TurnType,
            ParamsJson = JsonSerializer.Serialize(it.Slots, J), Matched = it.Intent is not null || it.TurnType == TurnTypes.Meta,
            Score = (decimal)it.Score.Confidence, ParentMessageId = ctx?.LastReplyId, CreatedAt = now,
        };
        db.AssistantMessages.Add(question);

        // A correction or rephrase says the previous answer missed (unless the user already rated it).
        if (it.TurnType is TurnTypes.Correction or TurnTypes.Rephrase && ctx?.LastReplyId is { } lastId)
        {
            var prev = await db.AssistantMessages.FirstOrDefaultAsync(m => m.Id == lastId, ct);
            if (prev is { Feedback: null }) { prev.Feedback = -1; prev.FeedbackReason = it.TurnType == TurnTypes.Correction ? "corrected" : "rephrased"; }
        }

        var (reply, run, permission) = await ReplyAsync(p, it, text, vocab, ct);
        var answer = new AssistantMessage
        {
            Id = Guid.NewGuid(), ThreadId = thread.Id, ParentMessageId = question.Id, Role = "assistant", Text = reply.Text, Intent = it.Intent,
            TurnType = it.TurnType, DetailJson = JsonSerializer.Serialize(reply, J), Permission = permission, RunId = run?.Run.Id, RunCode = run?.Run.Code,
            Matched = question.Matched, Score = question.Score, CreatedAt = now.AddMilliseconds(1),
        };
        db.AssistantMessages.Add(answer);

        // What the next message builds on.
        var next = ThreadContext.Next(ctx, it, answer.Id, now, IntentCatalogue.Default);
        thread.ContextJson = JsonSerializer.Serialize(next, J);
        thread.UpdatedAt = now;
        if (string.IsNullOrWhiteSpace(thread.Title)) thread.Title = Title(text);
        await db.SaveChangesAsync(ct);

        var count = await db.AssistantMessages.CountAsync(m => m.ThreadId == thread.Id, ct);
        return new(Dto(thread, count, displayText), Message(p, question, []), Message(p, answer, []));
    }

    /// <summary>Re-ask a stored answer against the current run for the same hospital and period.</summary>
    public async Task<AskResult> RefreshAsync(Principal p, Guid messageId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var m = await db.AssistantMessages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == messageId && x.Role == "assistant", ct) ?? throw AppError.NotFound("Message");
        var thread = await Owned(db, p, m.ThreadId, ct);
        var asked = m.ParentMessageId is { } pid ? await db.AssistantMessages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid, ct) : null;
        if (asked?.Intent is null || asked.ParamsJson is null) throw AppError.Invalid("That answer cannot be refreshed.");
        var slots = JsonSerializer.Deserialize<Dictionary<string, Slot>>(asked.ParamsJson, J) ?? [];
        var pick = slots.Where(s => s.Key is "hospital" or "period" or "procedure" or "package" or "dimension").ToDictionary(s => s.Key, s => s.Value.Key);
        // Re-ask the stored intent with the stored parameters pinned.
        return await AskAsync(p, new AskBody(thread.Id, asked.Text, slots.GetValueOrDefault("hospital")?.Key, slots.GetValueOrDefault("period")?.Key,
            asked.Intent, pick, null), ct, display: "Refresh: " + asked.Text);
    }

    private async Task<(AssistantReply Reply, ScopedRun? Run, string? Permission)> ReplyAsync(Principal p, Interpretation it, string question,
        IAssistantVocabulary vocab, CancellationToken ct)
    {
        var def = IntentCatalogue.Default.Get(it.Intent);
        var assumptions = Assumptions(it, def);
        AssistantReply Plain(string text, Option[]? options = null, Option[]? suggestions = null, string[]? notes = null, ScopedRun? s = null) =>
            new(text, it.Band, it.TurnType, it.Intent, def?.Label, it.Score.Confidence, it.Score, assumptions, [], [], suggestions ?? [], options ?? [],
                notes ?? it.Notes, s?.Run.Code, s?.Run.Status, s?.Hospital.Code, s?.Period.Code, null);

        if (it.TurnType == TurnTypes.Meta)
        {
            var reply = it.Meta switch
            {
                "thanks" => Plain("You're welcome. Ask me anything else about costs, margins or runs.", suggestions: Suggestions(def)),
                "greeting" => Plain($"Hello {p.Name.Split(' ')[0]}. I answer questions from VERITA's own data. Try one of these:", suggestions: Interpreter.Starters()),
                "reset" => Plain("Starting fresh. What would you like to know?", suggestions: Interpreter.Starters()),
                _ => Plain("I answer questions about costs, procedures, packages, profitability, variance, capacity, data quality, reconciliation, runs and imports, " +
                           "and I can open screens and explain terms. Follow up with 'and for <hospital>?', 'what about September?', 'why?' or 'compare with …'. " +
                           "Every answer lists what it assumed.", suggestions: Interpreter.Starters()),
            };
            return (reply, null, null);
        }

        if (it.Band == Bands.Clarify && (it.Question is not null || it.Intent is null))
            return (Plain(it.Question ?? "I didn't understand that.", it.Options, it.Intent is null ? Interpreter.Starters() : []), null, null);

        if (def is null) return (Plain("I didn't understand that.", null, Interpreter.Starters()), null, null);
        if (def.Permission.Length > 0 && !p.Can(def.Permission))
            // The refusal holds no data, so it is not itself withheld later.
            return (Plain($"Answering that needs {def.Permission}, which your role ({p.Role}) does not hold.", suggestions: Interpreter.Starters()), null, null);

        var needsRun = def.Id is not ("navigate" or "help.term" or "help.howto" or "imports.recent" or "run.status");
        ScopedRun? s = null;
        if (needsRun)
        {
            try { s = await q.Scope(p, null, it["hospital"]?.Key, it["period"]?.Key, ct); }
            catch (AppError e) { return (Plain(e.Message, suggestions: Suggestions(def)), null, def.Permission); }
        }

        Answer a;
        try { a = await answers.AnswerAsync(p, it, s, ct); }
        catch (AppError e) { return (Plain(e.Message, suggestions: Suggestions(def), s: s), s, def.Permission); }

        var blocks = a.Blocks;
        var text = a.Text;
        var notes = it.Notes.Concat(a.Notes).ToList();
        if (it.View == "compare" && it["compare"] is { } cmp && s is not null)
        {
            var (ctext, cblock, cnotes) = await CompareAsync(p, it, s, a, cmp, vocab, ct);
            text = ctext;
            blocks = [cblock];
            notes.AddRange(cnotes);
        }

        var options = Array.Empty<Option>();
        if (it.Band == Bands.Confirm)
        {
            text = $"I think you're asking about {def.Label.ToLowerInvariant()}. " + text;
            if (it.Score.RunnerUp is { } ru && IntentCatalogue.Default.Get(ru) is { } alt)
                options = [new Option($"No, {alt.Label.ToLowerInvariant()}", question, alt.Id)];
        }

        var reply2 = new AssistantReply(text, it.Band, it.TurnType, it.Intent, def.Label, it.Score.Confidence, it.Score, assumptions, blocks.ToArray(),
            a.Links.ToArray(), Suggestions(def), options, notes.Distinct().ToArray(), s?.Run.Code, s?.Run.Status, s?.Hospital.Code, s?.Period.Code,
            it.Band == Bands.Answer ? a.Navigate : null);
        return (reply2, s, def.Permission.Length > 0 ? def.Permission : null);
    }

    /// <summary>The same question for another hospital or period, lined up figure by figure.</summary>
    private async Task<(string Text, ReplyBlock Block, List<string> Notes)> CompareAsync(Principal p, Interpretation it, ScopedRun s, Answer baseAnswer, Slot cmp,
        IAssistantVocabulary vocab, CancellationToken ct)
    {
        var notes = new List<string>();
        var kind = cmp.Kind.Replace("compare:", "");
        var hospital = kind == "hospital" ? cmp.Key : s.Hospital.Code;
        string? period = kind == "period" ? cmp.Key : null;
        if (kind == "hospital")
        {
            // The same month in the other hospital's own period codes, or its latest period with a run.
            var theirs = vocab.Periods(hospital);
            var same = theirs.FirstOrDefault(x => x.Start == s.Period.StartDate && x.HasRun);
            var fallback = theirs.Where(x => x.HasRun).OrderByDescending(x => x.Start).FirstOrDefault();
            period = (same ?? fallback)?.Code;
            if (same is null && fallback is not null) notes.Add($"{cmp.Label} has no run for {s.Period.Code}; compared with its latest, {fallback.Code}.");
        }
        var slots = new Dictionary<string, Slot>(it.Slots) { ["hospital"] = new("hospital", hospital, hospital, "explicit", 1) };
        if (period is not null) slots["period"] = new("period", period, period, "explicit", 1); else slots.Remove("period");
        foreach (var k in new[] { "procedure", "package" })
        {
            if (!slots.TryGetValue(k, out var item) || kind != "hospital") continue;
            var code = item.Note?.StartsWith("code ") == true ? item.Note[5..] : null;
            var there = vocab.ForHospital(hospital).Entries.FirstOrDefault(e => e.Kind == k && code is not null && e.Extra == code)
                        ?? vocab.ForHospital(hospital).Entries.FirstOrDefault(e => e.Kind == k && string.Equals(e.Label, item.Label, StringComparison.OrdinalIgnoreCase));
            if (there is null)
                return ($"{item.Label} is not in {cmp.Label}'s catalogue, so there is nothing to compare.", new("figures", null, ["Measure", "Value"], []), notes);
            slots[k] = item with { Key = there.Key };
        }
        ScopedRun other;
        try { other = await q.Scope(p, null, hospital, period, ct); }
        catch (AppError e) { return (e.Message, new("figures", null, ["Measure", "Value"], []), notes); }
        var otherAnswer = await answers.AnswerAsync(p, it with { Slots = slots, View = "summary" }, other, ct);

        // Two periods read oldest to newest (Aug | Nov); two hospitals keep the one asked about first.
        var swap = kind == "period" && other.Period.StartDate < s.Period.StartDate;
        var (a, b) = swap ? (other, s) : (s, other);
        var (fa, fb) = swap ? (otherAnswer, baseAnswer) : (baseAnswer, otherAnswer);
        var left = $"{a.Hospital.Code} {a.Period.Code}";
        var right = $"{b.Hospital.Code} {b.Period.Code}";
        var rows = new List<string[]>();
        var lines = new List<string>();
        foreach (var f in fa.Figures)
        {
            var g = fb.Figures.FirstOrDefault(x => x.Key == f.Key);
            if (g is null) continue;
            string Fmt(decimal v) => f.Unit switch { "" => AssistantText.Number(v), "%" => AssistantText.Pct(v), _ => $"{f.Unit} {AssistantText.Money(v)}" };
            // The change from one column to the other, signed, with its percentage of the starting value.
            string Change(decimal from, decimal to)
            {
                var d = to - from;
                var sign = d > 0 ? "+" : d < 0 ? "−" : "";
                var pct = from == 0 ? "" : $" ({sign}{AssistantText.Pct(Math.Round(Math.Abs(d) / Math.Abs(from) * 100m, 1))})";
                return sign + Fmt(Math.Abs(d)) + pct;
            }
            // Columns: measure, left, right, change left→right, change right→left (for the switch button).
            rows.Add([f.Label, Fmt(f.Value), Fmt(g.Value), Change(f.Value, g.Value), Change(g.Value, f.Value)]);
            if (lines.Count < 2) lines.Add($"{f.Label.ToLowerInvariant()} {Fmt(f.Value)} → {Fmt(g.Value)}, {Change(f.Value, g.Value)}");
        }
        if (rows.Count == 0) return ("Those two cannot be compared figure by figure.", new("figures", null, ["Measure", "Value"], []), notes);
        var text = $"{left} → {right}: " + string.Join("; ", lines) + ".";
        return (text, new("compare", "Comparison", ["Measure", left, right, "Change"], rows.ToArray()), notes);
    }

    private static AssumptionDto[] Assumptions(Interpretation it, IntentDef? def)
    {
        var order = new[] { "hospital", "period", "procedure", "package", "dimension", "measure", "compare", "metric", "order", "limit", "nav", "term", "howto" };
        return it.Slots.Values.OrderBy(s => Array.IndexOf(order, s.Kind.StartsWith("compare") ? "compare" : s.Kind)).Select(s =>
        {
            var kind = s.Kind.StartsWith("compare") ? "compare" : s.Kind;
            var removable = kind is "period" or "dimension" or "limit" or "order" or "metric" or "measure" or "compare" && def?.Required.Contains(kind) != true;
            return new AssumptionDto(kind, KindLabels.GetValueOrDefault(kind, kind), s.Label, s.Source, it.Changed.Contains(kind), removable, s.Note);
        }).ToArray();
    }

    private static Option[] Suggestions(IntentDef? def) => def is null ? [] : def.Suggestions.Select(x => new Option(x, x)).ToArray();

    // ── Plumbing ─────────────────────────────────────────────────────────────────────

    private static async Task<AssistantThread> Owned(VeritaDbContext db, Principal p, Guid id, CancellationToken ct, bool track = false)
    {
        var set = track ? db.AssistantThreads : db.AssistantThreads.AsNoTracking();
        // Another user's thread answers 404, not 403: its existence is not disclosed.
        return await set.FirstOrDefaultAsync(t => t.Id == id && t.UserId == p.Id && !t.IsDeleted, ct) ?? throw AppError.NotFound("Thread");
    }

    private static string Title(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return "New conversation";
        t = char.ToUpperInvariant(t[0]) + t[1..];
        return t.Length <= 60 ? t : t[..57].TrimEnd() + "…";
    }

    private static AssistantThreadDto Dto(AssistantThread t, int messages, string? last) =>
        new(t.Id, t.Title, t.IsPinned, t.IsArchived, Utc(t.CreatedAt), Utc(t.UpdatedAt), messages, last);

    /// <summary>SQL Server returns DateTime without a kind; these are stored in UTC and must say so on the wire.</summary>
    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    /// <summary>
    /// An answer is shown only while the user still holds the permission it needed and the
    /// hospital it was about; otherwise its body is withheld. It is marked stale when a newer
    /// run now serves its hospital and period.
    /// </summary>
    private static AssistantMessageDto Message(Principal p, AssistantMessage m, Dictionary<(string, string), Guid> latestRun)
    {
        if (m.Role == "user") return new(m.Id, m.Role, m.Text, Utc(m.CreatedAt), m.TurnType, null, null, false, false);
        var reply = m.DetailJson is null ? null : JsonSerializer.Deserialize<AssistantReply>(m.DetailJson, J);
        var hidden = m.Permission is { Length: > 0 } perm && !p.Can(perm) || reply?.Hospital is { } h && !p.HasHospital(h);
        var stale = !hidden && reply?.Hospital is { } rh && reply.Period is { } rp && m.RunId is { } rid
                    && latestRun.TryGetValue((rh, rp), out var current) && current != rid;
        if (hidden)
            return new(m.Id, m.Role, "This answer is no longer available to your role.", Utc(m.CreatedAt), m.TurnType, null, m.Feedback, true, false);
        return new(m.Id, m.Role, m.Text, Utc(m.CreatedAt), m.TurnType, reply, m.Feedback, false, stale);
    }

    /// <summary>The run each (hospital, period) in the thread resolves to now.</summary>
    private async Task<Dictionary<(string, string), Guid>> LatestRunsAsync(Principal p, List<AssistantMessage> messages, CancellationToken ct)
    {
        var pairs = messages.Where(m => m.Role == "assistant" && m.RunId is not null && m.DetailJson is not null)
            .Select(m => JsonSerializer.Deserialize<AssistantReply>(m.DetailJson!, J))
            .Where(r => r?.Hospital is not null && r.Period is not null && p.HasHospital(r.Hospital))
            .Select(r => (r!.Hospital!, r.Period!)).Distinct().Take(12).ToList();
        var result = new Dictionary<(string, string), Guid>();
        foreach (var (h, pe) in pairs)
        {
            try { result[(h, pe)] = (await q.Scope(p, null, h, pe, ct)).Run.Id; }
            catch (AppError) { }
        }
        return result;
    }
}
