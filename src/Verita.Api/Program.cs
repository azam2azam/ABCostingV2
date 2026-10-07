using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Verita.Api.Infrastructure;
using Verita.Application;
using Verita.Domain;
using Verita.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var cs = builder.Configuration.GetConnectionString("Verita")
         ?? throw new InvalidOperationException("ConnectionStrings:Verita is not configured.");
var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is not configured.");
var issuer = builder.Configuration["Jwt:Issuer"] ?? "verita";

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IRequestInfo, HttpRequestInfo>();
builder.Services.AddVerita(cs);
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddSingleton<RunQueue>();
builder.Services.AddHostedService<RunWorker>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonAuthorizationResultHandler>();

// NFR-10: production signs in through OIDC (Entra ID) with MFA. Locally issued tokens exist for
// break-glass accounts and for this development build; both carry the same claims.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = issuer,
        ValidateAudience = true, ValidAudience = issuer,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1),
        NameClaimType = "email", RoleClaimType = "role",
    };
});

// ARCH-15: one policy per permission, generated from the single declaration in Domain/Security.
builder.Services.AddAuthorization(o =>
{
    foreach (var permission in Security.AllPermissions)
        o.AddPolicy(permission, p => p.RequireAuthenticatedUser().RequireClaim("perm", permission));
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

builder.Services.AddControllers().AddJsonOptions(o => Json.Configure(o.JsonSerializerOptions));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "VERITA API", Version = "v1", Description = "Hospital activity-based costing. Money is a string on the wire (NG-5)." });
    o.MapType<decimal>(() => new() { Type = "string", Format = "decimal" });
    o.MapType<decimal?>(() => new() { Type = "string", Format = "decimal", Nullable = true });
    o.CustomSchemaIds(t => t.FullName!.Replace("Verita.Infrastructure.Queries.", "").Replace("+", "").Replace("Verita.Api.Controllers.", ""));
    o.SupportNonNullableReferenceTypes();
    o.SchemaFilter<AllPropertiesRequiredFilter>();
    o.AddSecurityDefinition("bearer", new() { Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
});

var app = builder.Build();

app.UseMiddleware<ErrorEnvelopeMiddleware>();
app.UseSwagger();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "VERITA v1"));
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

public partial class Program;
