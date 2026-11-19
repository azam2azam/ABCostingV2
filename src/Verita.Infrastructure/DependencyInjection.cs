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
    /// <param name="commandTimeoutSeconds">One query's limit. The API keeps 300; the HIS sync worker allows more for background imports.</param>
    public static IServiceCollection AddVerita(this IServiceCollection services, string connectionString, int commandTimeoutSeconds = 300)
    {
        void Options(DbContextOptionsBuilder o) =>
            o.UseSqlServer(connectionString, sql => { sql.CommandTimeout(commandTimeoutSeconds); sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery); });

        services.AddDbContextFactory<VeritaDbContext>(Options);
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<VeritaDbContext>>().CreateDbContext());
        services.AddSingleton(new VeritaConnection(connectionString));
        services.TryAddSingleton<IRequestInfo, NoRequestInfo>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<RunService>();
        services.AddScoped<PeriodFactsService>();
        services.AddScoped<HisSync.HisSyncService>();
        services.AddScoped<ScenarioService>();
        services.AddScoped<GovernanceService>();
        services.AddScoped<ReadQueries>();
        services.AddScoped<DataHub.DataHubService>();
        services.AddScoped<DataHub.HisImportService>();
        services.AddScoped<DataHub.ImportFileService>();
        services.AddScoped<CostingSeed.RecipeSuggestionService>();
        services.AddScoped<Assistant.AssistantAnswers>();
        services.AddMemoryCache();
        services.AddOptions<Assistant.IntentRouterOptions>().BindConfiguration("IntentRouter");
        services.AddHttpClient<Application.Assistant.IIntentRouter, Assistant.ClaudeIntentRouter>();
        services.AddScoped<Assistant.AssistantService>();
        return services;
    }
}
