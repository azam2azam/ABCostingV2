using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Infrastructure.Data;
using Verita.Infrastructure.Runs;

namespace Verita.Api.Infrastructure;

/// <summary>
/// NG-5 / NFR-8: decimal(19,4) does not survive JavaScript's number, so money crosses the wire as
/// a string. The client formats it; it never does arithmetic on it.
/// </summary>
public sealed class DecimalAsStringConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? decimal.Parse(reader.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture)
            : reader.GetDecimal();

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Resolves the signed-in user from the database on every request, so scope always
/// comes from the identity store and never from a request parameter (BRD 22.4).</summary>
public sealed class CurrentUser(IHttpContextAccessor http, IDbContextFactory<VeritaDbContext> factory)
{
    private Principal? _cached;

    public async Task<Principal> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;
        var sub = http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.HttpContext?.User.FindFirstValue("sub");
        if (!Guid.TryParse(sub, out var id)) throw new AppError("UNAUTHENTICATED", "Sign in to continue.", 401);
        await using var db = await factory.CreateDbContextAsync(ct);
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.IsActive, ct)
                ?? throw new AppError("UNAUTHENTICATED", "This account is not active.", 401);
        return _cached = new Principal(u.Id, u.Email, u.Name, u.Role, u.Title, Principal.ParseScope(u.HospitalScope), Principal.ParseScope(u.DeptScope));
    }
}

public sealed class HttpRequestInfo(IHttpContextAccessor http) : IRequestInfo
{
    public string? IpAddress => http.HttpContext?.Connection.RemoteIpAddress?.ToString();
}

/// <summary>ARCH-13: errors are a typed envelope with stable codes, never an HTML page.</summary>
public sealed class ErrorEnvelopeMiddleware(RequestDelegate next, ILogger<ErrorEnvelopeMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (AppError e)
        {
            await Write(ctx, e.Status, e.Code, e.Message, e.Detail);
        }
        catch (Exception e) when (!ctx.Response.HasStarted)
        {
            log.LogError(e, "Unhandled error on {Path}", ctx.Request.Path);
            await Write(ctx, 500, "INTERNAL", "Something went wrong on the server. The error has been logged.", null);
        }
    }

    public static Task Write(HttpContext ctx, int status, string code, string message, object? detail)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = new { code, message, detail } }, Json.Options));
    }
}

/// <summary>FR-0.5: 401 and 403 answer as JSON, never as a redirect to a sign-in page.</summary>
public sealed class JsonAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Challenged)
        {
            await ErrorEnvelopeMiddleware.Write(context, 401, "UNAUTHENTICATED", "Sign in to continue.", null);
            return;
        }
        if (result.Forbidden)
        {
            var needed = policy.Requirements.OfType<ClaimsAuthorizationRequirement>().SelectMany(r => r.AllowedValues ?? []).FirstOrDefault();
            await ErrorEnvelopeMiddleware.Write(context, 403, "FORBIDDEN", needed is null ? "Your role cannot do this." : $"Your role does not hold {needed}.", null);
            return;
        }
        await _default.HandleAsync(next, context, policy, result);
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions o)
    {
        o.Converters.Add(new DecimalAsStringConverter());
        o.Converters.Add(new JsonStringEnumConverter());
        o.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        return o;
    }
}

/// <summary>
/// ARCH-8: runs execute out of the request. The API queues the job and answers 202; progress is
/// read back from RunStep rows, which the client polls (ARCH-10).
/// </summary>
public sealed record RunJob(Guid RunId, bool Force, string Actor);

public sealed class RunQueue
{
    private readonly Channel<RunJob> _channel = Channel.CreateUnbounded<RunJob>();
    public ValueTask EnqueueAsync(RunJob job) => _channel.Writer.WriteAsync(job);
    public IAsyncEnumerable<RunJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public sealed class RunWorker(RunQueue queue, IServiceScopeFactory scopes, ILogger<RunWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var runs = scope.ServiceProvider.GetRequiredService<RunService>();
                var result = await runs.ExecuteAsync(job.RunId, job.Force, job.Actor, ct: stoppingToken);
                log.LogInformation("Run {Code} finished {Status} in {Ms} ms", result.Code, result.Status, result.DurationMs);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // NFR-15: a failed run does not take the platform down; it is marked FAILED.
                log.LogError(ex, "Run {RunId} could not be executed", job.RunId);
            }
        }
    }
}

/// <summary>
/// Every DTO property is always serialised, so the OpenAPI document marks them all required and
/// the generated TypeScript types (NG-11) are exact rather than all-optional.
/// </summary>
public sealed class AllPropertiesRequiredFilter : Swashbuckle.AspNetCore.SwaggerGen.ISchemaFilter
{
    public void Apply(Microsoft.OpenApi.Models.OpenApiSchema schema, Swashbuckle.AspNetCore.SwaggerGen.SchemaFilterContext context)
    {
        if (schema.Properties is { Count: > 0 })
            foreach (var name in schema.Properties.Keys) schema.Required.Add(name);
    }
}
