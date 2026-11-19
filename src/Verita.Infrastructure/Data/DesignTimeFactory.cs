using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Verita.Infrastructure.Data;

/// <summary>Lets dotnet-ef read the model without starting the API host (dbcontext script, for reference DDL). There are no migrations.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<VeritaDbContext>
{
    public VeritaDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Verita")
                 ?? "Server=localhost\\SQLEXPRESS;Database=Verita;Trusted_Connection=True;TrustServerCertificate=True";
        return new VeritaDbContext(new DbContextOptionsBuilder<VeritaDbContext>().UseSqlServer(cs).Options);
    }
}
