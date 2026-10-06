using Verita.Domain;

namespace Verita.Application;

/// <summary>
/// The signed-in user, as the server sees it. Hospital scope comes from the identity, never from
/// a request parameter, and a user with no hospital scope sees nothing, not everything (BRD 22.4).
/// </summary>
public sealed record Principal(Guid Id, string Email, string Name, string Role, string Title, string[] HospitalScope, string[] DeptScope)
{
    public bool Can(string permission) => Security.Can(Role, permission);
    public bool AllHospitals => HospitalScope.Contains("*");
    public bool HasHospital(string code) => AllHospitals || HospitalScope.Contains(code, StringComparer.OrdinalIgnoreCase);
    public bool AllDepartments => DeptScope.Contains("*");
    public bool HasDepartment(string code) => AllDepartments || DeptScope.Contains(code, StringComparer.OrdinalIgnoreCase);

    public static string[] ParseScope(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? [] : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// A typed error with a stable code a client can branch on (ARCH-13). Rendered as
/// { error: { code, message, detail } } — never a redirect, never an HTML page (FR-0.5).
/// </summary>
public sealed class AppError(string code, string message, int status, object? detail = null) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public object? Detail { get; } = detail;

    public static AppError RunNotFound(string run) => new("RUN_NOT_FOUND", $"No calculation run matches '{run}'.", 404);
    public static AppError RunImmutable(string code) => new("RUN_IMMUTABLE", $"Run {code} is published and cannot be modified. Create a new run to correct the period.", 409);
    public static AppError OutOfScope(string what) => new("OUT_OF_SCOPE", $"{what} is outside your hospital scope.", 403);
    public static AppError NotCosted(string message) => new("NOT_COSTED", message, 404);
    public static AppError InvalidDimension(string dim, IEnumerable<string> allowed) =>
        new("INVALID_DIMENSION", $"'{dim}' is not a profitability dimension.", 400, new { allowed });
    public static AppError Forbidden(string permission) => new("FORBIDDEN", $"Your role does not hold {permission}.", 403);
    public static AppError NotFound(string what) => new("NOT_FOUND", $"{what} was not found.", 404);
    public static AppError Invalid(string message, object? detail = null) => new("INVALID_REQUEST", message, 400, detail);
    public static AppError Conflict(string code, string message, object? detail = null) => new(code, message, 409, detail);
}

/// <summary>Writes the audit trail (FR-10.9). A failed write never fails the operation it records (FR-10.12).</summary>
public interface IAuditWriter
{
    Task WriteAsync(Principal? actor, string action, string entityType, string? entityId, string? entityLabel,
        object? before = null, object? after = null, object? metadata = null, CancellationToken ct = default);
}

/// <summary>Request context: the caller's IP for the audit trail.</summary>
public interface IRequestInfo
{
    string? IpAddress { get; }
}
