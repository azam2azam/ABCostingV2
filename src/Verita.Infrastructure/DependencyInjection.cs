using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Verita.Application;
using Verita.Infrastructure.Data;
using Verita.Infrastructure.Governance;
using Verita.Infrastructure.Queries;
using Verita.Infrastructure.Runs;

namespace Verita.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddVerita(this IServiceCollection services, string connectionString)
    {
        void Options(DbContextOptionsBuilder o) =>
            o.UseSqlServer(connectionString, sql => { sql.CommandTimeout(300); sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery); });

        services.AddDbContextFactory<VeritaDbContext>(Options);
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<VeritaDbContext>>().CreateDbContext());
        services.AddSingleton(new VeritaConnection(connectionString));
        services.TryAddSingleton<IRequestInfo, NoRequestInfo>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<RunService>();
        services.AddScoped<ScenarioService>();
        services.AddScoped<GovernanceService>();
        services.AddScoped<ReadQueries>();
        return services;
    }
}
