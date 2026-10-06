using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Verita.Infrastructure.Data;

/// <summary>Lets dotnet-ef build migrations without starting the API host.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<VeritaDbContext>
{
    public VeritaDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Verita")
                 ?? "Server=localhost\\SQLEXPRESS;Database=Verita;Trusted_Connection=True;TrustServerCertificate=True";
        return new VeritaDbContext(new DbContextOptionsBuilder<VeritaDbContext>().UseSqlServer(cs).Options);
    }
}
