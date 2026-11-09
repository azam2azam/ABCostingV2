using System.Text.Json;
using Verita.Application.Assistant;
using Xunit.Abstractions;

namespace Verita.Engine.Tests.Assistant;

/// <summary>
/// The assistant's golden questions (golden.json): one-off questions and multi-turn
/// conversations, each with the intent, turn type, parameters and confidence band it must
/// produce. A case that answers confidently with the wrong intent is a false answer, the worst
/// failure for a costing platform, so the summary test fails the build above 1%.
/// </summary>
public sealed class AssistantGoldenTests(ITestOutputHelper output)
{
    private static readonly DateTime T0 = new(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Interpreter Interpreter = new(IntentCatalogue.Default);
    private static readonly FakeVocabulary Vocab = new();
    private static readonly JsonElement Golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assistant", "golden.json"))).RootElement;
    private static readonly ScreenScope Screen = new(Golden.GetProperty("screen").GetProperty("hospital").GetString(), Golden.GetProperty("screen").GetProperty("period").GetString());

    public static IEnumerable<object[]> Singles() =>
        Golden.GetProperty("single").EnumerateArray().Select(c => new object[] { c.GetProperty("q").GetString()! });

    public static IEnumerable<object[]> Conversations() =>
        Golden.GetProperty("conversations").EnumerateArray().Select(c => new object[] { c.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(Singles))]
    public void Single_question(string q)
    {
        var c = Golden.GetProperty("single").EnumerateArray().First(x => x.GetProperty("q").GetString() == q);
        var it = Interpreter.Interpret(q, null, Screen, Vocab, T0);
        Log(q, it);
        var errors = Check(c, it);
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Theory]
    [MemberData(nameof(Conversations))]
    public void Conversation(string name)
    {
        var conv = Golden.GetProperty("conversations").EnumerateArray().First(x => x.GetProperty("name").GetString() == name);
        ThreadContext? ctx = null;
        var now = T0;
        var errors = new List<string>();
        var n = 0;
        foreach (var turn in conv.GetProperty("turns").EnumerateArray())
        {
            n++;
            var q = turn.GetProperty("q").GetString()!;
            var overrides = new AskOverrides(
                turn.TryGetProperty("pickIntent", out var pi) ? pi.GetString() : null,
                turn.TryGetProperty("pick", out var pk) ? pk.EnumerateObject().ToDictionary(o => o.Name, o => o.Value.GetString()!) : null,
                turn.TryGetProperty("remove", out var rm) ? rm.EnumerateArray().Select(x => x.GetString()!).ToArray() : null);
            var it = Interpreter.Interpret(q, ctx, Screen, Vocab, now, overrides);
            Log($"{n}. {q}", it);
            errors.AddRange(Check(turn, it).Select(e => $"turn {n} '{q}': {e}"));
            ctx = ThreadContext.Next(ctx, it, Guid.NewGuid(), now, IntentCatalogue.Default);
            now = now.AddSeconds(turn.TryGetProperty("after", out var after) ? after.GetInt32() : 30);
        }
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    /// <summary>Accuracy over the single questions, reported, with the false-answer rate gated.</summary>
    [Fact]
    public void Scorecard()
    {
        int total = 0, intentOk = 0, slotsOk = 0, confident = 0, falseAnswers = 0, declinedOk = 0, declines = 0;
        foreach (var c in Golden.GetProperty("single").EnumerateArray())
        {
            total++;
            var q = c.GetProperty("q").GetString()!;
            var it = Interpreter.Interpret(q, null, Screen, Vocab, T0);
            var expected = c.GetProperty("intent").ValueKind == JsonValueKind.Null ? null : c.GetProperty("intent").GetString();
            var answered = it.Band == Bands.Answer;
            if (expected is null)
            {
                declines++;
                if (!answered) declinedOk++; else falseAnswers++;
                if (it.Intent is null) intentOk++;
                continue;
            }
            if (it.Intent == expected) intentOk++;
            if (Check(c, it).Count == 0) slotsOk++;
            if (answered) { confident++; if (it.Intent != expected) falseAnswers++; }
        }
        var falseRate = (double)falseAnswers / total;
        output.WriteLine($"questions {total} | intent accuracy {(double)intentOk / total:P1} | all checks {(double)slotsOk / (total - declines):P1} | " +
                         $"answered confidently {confident} | declined correctly {declinedOk}/{declines} | false answers {falseAnswers} ({falseRate:P1})");
        Assert.True(falseRate <= 0.01, $"False-answer rate {falseRate:P1} is above 1%.");
    }

    [Fact]
    public void Typos_and_codes_are_matched_but_short_words_are_not_guessed()
    {
        Assert.Equal(1, AssistantText.Distance("teh", "the", 2));
        Assert.Equal(3, AssistantText.Distance("centre", "cerec", 5));
        Assert.True(AssistantText.Similarity("consultaton", "consultation") >= 0.85);
        Assert.Equal(0, AssistantText.Similarity("icu", "icd"));
        Assert.Equal(0, AssistantText.Similarity("weather", "wellness"));
        var lex = Vocab.ForHospital("NSH-RYD");
        Assert.Equal("p-cbc", lex.Find(AssistantText.Stems("lab 001"))[0].Key);
    }

    [Fact]
    public void A_period_is_read_in_the_hospitals_own_codes()
    {
        var nsh = Vocab.Periods("NSH-RYD");
        var swd = Vocab.Periods("SWD-AGL");
        Assert.Equal("2026-08", PeriodParser.Find(AssistantText.Tokens("in august"), nsh, null)!.Code);
        Assert.Equal("2026 Aug", PeriodParser.Find(AssistantText.Tokens("in august"), swd, null)!.Code);
        Assert.Equal("2026-09", PeriodParser.Find(AssistantText.Tokens("last month"), nsh, "2026-10")!.Code);
        Assert.Equal("2026-10", PeriodParser.Find(AssistantText.Tokens("latest"), nsh, "2026-09")!.Code);
        Assert.Equal("2026-09", PeriodParser.Find(AssistantText.Tokens("summary for this period"), nsh, "2026-09")!.Code);
        Assert.Equal("2026 Nov", PeriodParser.Find(AssistantText.Tokens("2026 nov"), swd, null)!.Code);
        Assert.Null(PeriodParser.Find(AssistantText.Tokens("may I see costs"), nsh, null));
    }

    private void Log(string q, Interpretation it) =>
        output.WriteLine($"{q} → {it.TurnType} {it.Intent} {it.View} {it.Band} {it.Score.Confidence:0.00} " +
                         $"[{string.Join(", ", it.Slots.Select(s => $"{s.Key}={s.Value.Key}/{s.Value.Source}"))}] {string.Join("; ", it.Score.Reasons)}");

    private static List<string> Check(JsonElement c, Interpretation it)
    {
        var errors = new List<string>();
        if (c.TryGetProperty("intent", out var intent))
        {
            var expected = intent.ValueKind == JsonValueKind.Null ? null : intent.GetString();
            if (expected is null && it.Band == Bands.Answer) errors.Add($"answered as {it.Intent} ({it.Score.Confidence:0.00}) but should not answer");
            else if (expected is not null && it.Intent != expected) errors.Add($"intent {it.Intent}, expected {expected}");
        }
        if (c.TryGetProperty("turn", out var turn) && it.TurnType != turn.GetString()) errors.Add($"turn {it.TurnType}, expected {turn.GetString()}");
        if (c.TryGetProperty("view", out var view) && it.View != view.GetString()) errors.Add($"view {it.View}, expected {view.GetString()}");
        if (c.TryGetProperty("band", out var band))
        {
            var b = band.GetString();
            var ok = b == "answerable" ? it.Band is Bands.Answer or Bands.Confirm : it.Band == b;
            if (!ok) errors.Add($"band {it.Band} ({it.Score.Confidence:0.00}: {string.Join("; ", it.Score.Reasons)}), expected {b}");
        }
        if (c.TryGetProperty("slots", out var slots))
            foreach (var s in slots.EnumerateObject())
            {
                var want = s.Value.GetString();
                var got = it.Slots.GetValueOrDefault(s.Name)?.Key;
                if (want == "-") { if (got is not null) errors.Add($"{s.Name} should be absent, was {got}"); }
                else if (want == "*") { if (got is null) errors.Add($"{s.Name} missing"); }
                else if (got != want) errors.Add($"{s.Name} {got ?? "(none)"}, expected {want}");
            }
        if (c.TryGetProperty("sources", out var sources))
            foreach (var s in sources.EnumerateObject())
                if (it.Slots.GetValueOrDefault(s.Name)?.Source != s.Value.GetString())
                    errors.Add($"{s.Name} came from {it.Slots.GetValueOrDefault(s.Name)?.Source ?? "(none)"}, expected {s.Value.GetString()}");
        if (c.TryGetProperty("options", out var options) && it.Options.Length < options.GetInt32())
            errors.Add($"{it.Options.Length} options, expected at least {options.GetInt32()}");
        return errors;
    }
}
