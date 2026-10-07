using Microsoft.AspNetCore.Mvc;
using Verita.Api.Infrastructure;
using Verita.Application;
using Verita.Infrastructure.Queries;

namespace Verita.Api.Controllers;

/// <summary>Shared plumbing: the caller, run selection (ARCH-14) and the meta block (ARCH-12).</summary>
[ApiController]
[Produces("application/json")]
public abstract class VeritaController : ControllerBase
{
    protected CurrentUser Current => HttpContext.RequestServices.GetRequiredService<CurrentUser>();
    protected ReadQueries Q => HttpContext.RequestServices.GetRequiredService<ReadQueries>();

    protected Task<Principal> Me() => Current.GetAsync(HttpContext.RequestAborted);

    /// <summary>Resolve ?run= or ?hospital=&amp;period= within the caller's scope.</summary>
    protected async Task<(Principal P, ScopedRun S)> Scoped()
    {
        var p = await Me();
        var s = await Q.Scope(p, Request.Query["run"], Request.Query["hospital"], Request.Query["period"], HttpContext.RequestAborted);
        return (p, s);
    }

    protected static Envelope<T> Wrap<T>(ScopedRun s, T data) => new(s.Meta(), data);
    protected static Envelope<T> Wrap<T>(T data) => new(new Meta(null, null, null, null, null, null, null, null, DateTime.UtcNow), data);
}
