using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Verita.Integration.Tests;

/// <summary>
/// The assistant against the bootstrapped database: its numbers must equal the screen's endpoint
/// to the cent (it reads the same query), threads are private to their owner, and an answer is
/// withheld from a role without the permission its data needs.
/// </summary>
public sealed class AssistantApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Hospital = "NSH-RYD", Period = "2026-08", Run = "RUN-NSH-RYD-2026-08";
    private static readonly Dictionary<string, string> Tokens = [];

    private async Task<HttpClient> As(string email)
    {
        var client = factory.CreateClient();
        if (!Tokens.TryGetValue(email, out var token))
        {
            var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Demo!Pass123" });
            res.EnsureSuccessStatusCode();
            token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            Tokens[email] = token;
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Ask(HttpClient c, string text, string? thread = null, string? intent = null)
    {
        var res = await c.PostAsJsonAsync("/api/v1/assistant/ask", new { threadId = thread, text, hospital = Hospital, period = Period, intent, pick = (object?)null, remove = (object?)null });
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement Reply(JsonElement r) => r.GetProperty("answer").GetProperty("reply");
    private static string Money(string s) => decimal.Parse(s, CultureInfo.InvariantCulture).ToString("N2", CultureInfo.InvariantCulture);

    private static string Figure(JsonElement reply, string measure) =>
        reply.GetProperty("blocks").EnumerateArray().Where(b => b.GetProperty("type").GetString() == "figures")
            .SelectMany(b => b.GetProperty("rows").EnumerateArray()).First(r => r[0].GetString() == measure)[1].GetString()!;

    [Fact]
    public async Task Run_summary_matches_the_dashboard_to_the_cent()
    {
        var c = await As("cfo@verita.health");
        var dash = await c.GetFromJsonAsync<JsonElement>($"/api/v1/dashboard?run={Run}");
        var total = dash.GetProperty("data").GetProperty("headline").EnumerateArray().First(t => t.GetProperty("key").GetString() == "total").GetProperty("value").GetString()!;
        var reply = Reply(await Ask(c, "give me the summary for this period"));
        Assert.Equal("dashboard.summary", reply.GetProperty("intent").GetString());
        Assert.Equal("answer", reply.GetProperty("band").GetString());
        Assert.Equal(Run, reply.GetProperty("run").GetString());
        Assert.Equal("SAR " + Money(total), Figure(reply, "Total cost"));
    }

    [Fact]
    public async Task Procedure_unit_cost_matches_the_procedure_screen()
    {
        var c = await As("cfo@verita.health");
        var list = await c.GetFromJsonAsync<JsonElement>($"/api/v1/procedures?run={Run}");
        var proc = list.GetProperty("data").EnumerateArray()
            .First(p => decimal.Parse(p.GetProperty("volume").GetString()!, CultureInfo.InvariantCulture) > 0
                        && decimal.Parse(p.GetProperty("unitCost").GetString()!, CultureInfo.InvariantCulture) > 0);
        var reply = Reply(await Ask(c, $"unit cost of {proc.GetProperty("code").GetString()}"));
        Assert.Equal("procedure.cost", reply.GetProperty("intent").GetString());
        Assert.Equal("SAR " + Money(proc.GetProperty("unitCost").GetString()!), Figure(reply, "Unit cost"));
        Assert.Equal("SAR " + Money(proc.GetProperty("totalCost").GetString()!), Figure(reply, "Total cost"));
    }

    [Fact]
    public async Task A_follow_up_keeps_the_thread_and_changes_one_parameter()
    {
        var c = await As("cfo@verita.health");
        var first = await Ask(c, "give me the summary for this period");
        var thread = first.GetProperty("thread").GetProperty("id").GetString();
        var next = Reply(await Ask(c, "and for september?", thread));
        Assert.Equal("REFINE", next.GetProperty("turnType").GetString());
        var period = next.GetProperty("assumptions").EnumerateArray().First(a => a.GetProperty("kind").GetString() == "period");
        Assert.Equal("2026-09", period.GetProperty("value").GetString());
        Assert.True(period.GetProperty("changed").GetBoolean());
        var detail = await c.GetFromJsonAsync<JsonElement>($"/api/v1/assistant/threads/{thread}");
        Assert.Equal(4, detail.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Threads_are_private_to_their_owner()
    {
        var cfo = await As("cfo@verita.health");
        var thread = (await Ask(cfo, "how are we doing")).GetProperty("thread").GetProperty("id").GetString();
        var other = await As("analyst@verita.health");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/assistant/threads/{thread}")).StatusCode);
        var list = await other.GetFromJsonAsync<JsonElement[]>("/api/v1/assistant/threads");
        Assert.DoesNotContain(list!, t => t.GetProperty("id").GetString() == thread);
    }

    [Fact]
    public async Task An_answer_needs_the_permission_its_data_needs()
    {
        // DEPT_MANAGER does not hold profitability.view.
        var c = await As("surgery.manager@verita.health");
        var reply = Reply(await Ask(c, "profitability by payer"));
        Assert.Contains("profitability.view", reply.GetProperty("text").GetString());
        Assert.Equal(0, reply.GetProperty("blocks").GetArrayLength());
    }

    [Fact]
    public async Task Off_topic_questions_are_not_answered()
    {
        var reply = Reply(await Ask(await As("cfo@verita.health"), "who won the football match yesterday"));
        Assert.Equal("clarify", reply.GetProperty("band").GetString());
        Assert.Null(reply.GetProperty("intent").GetString());
    }
}
