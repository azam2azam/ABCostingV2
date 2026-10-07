using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;
using Verita.Infrastructure.Queries;

namespace Verita.Api.Controllers;

[Route("api/v1/auth")]
public sealed class AuthController(VeritaDbContext db, IConfiguration config, IAuditWriter audit) : VeritaController
{
    public sealed record LoginRequest(string Email, string Password);
    public sealed record SessionDto(Guid Id, string Email, string Name, string Role, string RoleName, string Title, string[] HospitalScope,
        string[] DeptScope, string[] Permissions);
    public sealed record LoginResponse(string Token, DateTime ExpiresAt, SessionDto User);

    private static SessionDto Session(Principal p) => new(p.Id, p.Email, p.Name, p.Role, Security.Role(p.Role)?.Name ?? p.Role, p.Title,
        p.HospitalScope, p.DeptScope, Security.Role(p.Role)?.Permissions ?? []);

    /// <summary>Break-glass local sign-in (NFR-10). Production uses OIDC against Entra ID.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest req, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == req.Email.Trim().ToLower() && u.IsActive, ct);
        var ok = user is not null &&
                 new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, req.Password) != PasswordVerificationResult.Failed;
        if (!ok) throw new AppError("INVALID_CREDENTIALS", "Email or password is incorrect.", 401);

        user!.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        var p = new Principal(user.Id, user.Email, user.Name, user.Role, user.Title, Principal.ParseScope(user.HospitalScope), Principal.ParseScope(user.DeptScope));
        await audit.WriteAsync(p, "LOGIN", "User", user.Id.ToString(), user.Email, ct: ct);

        var issuer = config["Jwt:Issuer"] ?? "verita";
        var expires = DateTime.UtcNow.AddHours(10);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new("email", user.Email), new("role", user.Role),
        };
        claims.AddRange((Security.Role(user.Role)?.Permissions ?? []).Select(x => new Claim("perm", x)));
        var token = new JwtSecurityToken(issuer, issuer, claims, expires: expires,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!)), SecurityAlgorithms.HmacSha256));
        return new LoginResponse(new JwtSecurityTokenHandler().WriteToken(token), expires, Session(p));
    }

    [HttpGet("me")]
    public async Task<SessionDto> MeAsync() => Session(await Me());

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var p = await Me();
        await audit.WriteAsync(p, "LOGOUT", "User", p.Id.ToString(), p.Email, ct: ct);
        return NoContent();
    }
}

/// <summary>
/// The single declarations every consumer reads (NFR-22): enumerations, roles and permissions,
/// navigation, the data-quality register, run stages and residual buckets.
/// </summary>
[Route("api/v1/meta")]
public sealed class MetaController : VeritaController
{
    [HttpGet]
    public object Get() => new
    {
        navigation = Security.Navigation,
        roles = Security.Roles,
        permissions = new { read = Security.ReadPermissions, sensitive = Security.SensitivePermissions, write = Security.WritePermissions, governance = Security.GovernancePermissions },
        qualityChecks = QualityChecks.All,
        runSteps = Catalogue.RunSteps,
        runStatuses = Catalogue.RunStatuses,
        runTransitions = Catalogue.RunTransitions,
        residuals = Catalogue.ResidualBuckets.Select(b => new { bucket = b, label = Catalogue.ResidualLabel[b], owner = Catalogue.ResidualOwner[b] }),
        componentTypes = Catalogue.ComponentDisplayOrder.Select(t => new { type = t, label = Catalogue.ComponentLabel(t), nature = t == "OVERHEAD" ? "OVERHEAD" : Catalogue.NatureOf(t) }),
        levers = Catalogue.LeverLabel.Select(kv => new { type = kv.Key, label = kv.Value }),
        leverTargets = Catalogue.LeverTargetTypes,
        changeTypes = Catalogue.ChangeTypes,
        resourceTypes = Catalogue.ResourceTypes.Select(t => new { type = t, label = Catalogue.ResourceTypeLabel[t] }),
        approvalFlow = Catalogue.ApprovalFlow,
        profitabilityDimensions = ReadQueries.Dimensions,
        severities = Catalogue.Severities,
    };
}
