using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Verita.Application.Assistant;

namespace Verita.Infrastructure.Assistant;

public sealed class IntentRouterOptions
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-haiku-5-5";
    public int DailyCallsPerUser { get; set; } = 30;
    public int DailyCallsTotal { get; set; } = 500;
    public int MaxQuestionChars { get; set; } = 300;
}

/// <summary>
/// Routes a question to an intent with the Claude API. The intent list is the cached prompt prefix,
/// the answer is forced through a tool whose intent is an enum, and a daily budget plus a short
/// circuit breaker keep a bad day from costing anything: every failure just means "no routing".
/// </summary>
public sealed class ClaudeIntentRouter(HttpClient http, IOptions<IntentRouterOptions> options, IMemoryCache memory, ILogger<ClaudeIntentRouter> log) : IIntentRouter
{
    private readonly IntentRouterOptions _o = options.Value;
    private static readonly object Gate = new();
    private string? _system;

    public bool IsEnabled => _o.Enabled && !string.IsNullOrWhiteSpace(_o.ApiKey);

    public async Task<string?> RouteAsync(string question, IReadOnlyList<IntentDef> intents, Guid userId, CancellationToken ct)
    {
        if (!_o.Enabled || string.IsNullOrWhiteSpace(_o.ApiKey)) return null;
        question = question.Trim();
        if (question.Length == 0) return null;
        if (question.Length > _o.MaxQuestionChars) question = question[.._o.MaxQuestionChars];
        if (memory.TryGetValue("router:open", out _)) return null;
        if (!TakeBudget(userId)) return null;

        var ids = intents.Select(i => i.Id).Append("none").ToArray();
        var body = new JsonObject
        {
            ["model"] = _o.Model,
            ["max_tokens"] = 60,
            ["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = System(intents),
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            }),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = "route",
                ["description"] = "Report which intent the question asks for, or none.",
                ["input_schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["intent"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()) } },
                    ["required"] = new JsonArray("intent"),
                },
            }),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "route" },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = question }),
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = JsonContent.Create(body) };
            req.Headers.Add("x-api-key", _o.ApiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var res = await http.SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode)
            {
                var detail = await res.Content.ReadAsStringAsync(cts.Token);
                Trip($"{(int)res.StatusCode} {(detail.Length > 300 ? detail[..300] : detail)}");
                return null;
            }
            var json = await res.Content.ReadFromJsonAsync<JsonNode>(cts.Token);
            var intent = json?["content"]?.AsArray().FirstOrDefault(c => c?["type"]?.GetValue<string>() == "tool_use")?["input"]?["intent"]?.GetValue<string>();
            return intent is not null && intent != "none" && intents.Any(i => i.Id == intent) ? intent : null;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Trip(e.GetType().Name);
            return null;
        }
    }

    private string System(IReadOnlyList<IntentDef> intents) => _system ??=
        "You route questions for VERITA, a hospital costing application, to exactly one intent. Reply only through the route tool. " +
        "Choose 'none' when no intent fits or the message is not a question about the application. Never answer the question. " +
        "Questions may contain typos, be short, or be phrased loosely; read the meaning, not the exact words.\n\n" +
        "Disambiguation rules (they win over the intent list):\n" +
        "- Losing money, a loss, lose/losing, negative or low margin, unprofitable, profit, \"which X lose\" or \"which X are making a loss\" about procedures, departments, payers, insurers, physicians or any breakdown: profitability.by. " +
        "Only when it is about packages, use package.losing.\n" +
        "- Most expensive, cheapest, costliest, unit cost ranking, most performed, busiest, by volume, by encounters, by revenue or by total cost for a list of procedures with no mention of losing or profit: procedure.top.\n" +
        "- The word 'procedure' alone does not mean procedure.top; judge what is being asked about them.\n" +
        "- A question about one named procedure or package's cost: procedure.cost or package.cost.\n\n" +
        "Intents (id: label. What it answers. Typical words and phrases. Examples):\n" +
        string.Join('\n', intents.Select(i =>
        {
            var words = string.Join(", ", i.Keywords.OrderByDescending(k => k.Value).Take(10).Select(k => k.Key));
            var phrases = string.Join(", ", i.Phrases.OrderByDescending(k => k.Value).Take(6).Select(k => $"\"{k.Key}\""));
            return $"- {i.Id}: {i.Label}. Words: {words}." + (phrases.Length > 0 ? $" Phrases: {phrases}." : "") + $" Examples: {string.Join(" | ", i.Examples)}";
        }));

    /// <summary>Counts one call against the user's and the global daily budget; false when either is spent.</summary>
    private bool TakeBudget(Guid userId)
    {
        var day = DateTime.UtcNow.ToString("yyyyMMdd");
        lock (Gate)
        {
            var total = memory.GetOrCreate($"router:total:{day}", e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1); return new int[1]; })!;
            var mine = memory.GetOrCreate($"router:user:{day}:{userId}", e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1); return new int[1]; })!;
            if (total[0] >= _o.DailyCallsTotal || mine[0] >= _o.DailyCallsPerUser) return false;
            total[0]++; mine[0]++;
            return true;
        }
    }

    /// <summary>After an API failure, stop calling for a minute.</summary>
    private void Trip(string why)
    {
        log.LogWarning("Intent router failed ({Why}); pausing for a minute.", why);
        memory.Set("router:open", true, TimeSpan.FromMinutes(1));
    }
}
