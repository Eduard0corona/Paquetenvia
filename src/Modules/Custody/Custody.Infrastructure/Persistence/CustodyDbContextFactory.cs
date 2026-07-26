using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Paqueteria.Infrastructure.Tenancy;

namespace Custody.Infrastructure.Persistence;

public sealed class CustodyDbContextFactory : IDesignTimeDbContextFactory<CustodyDbContext>
{
    public CustodyDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PAQUETERIA_DESIGN_CONNECTION")
            ?? "Host=localhost;Database=paqueteria;Username=paqueteria_migrator;Password=design-only";
        var options = new DbContextOptionsBuilder<CustodyDbContext>()
            .UseNpgsql(connectionString, postgres =>
            {
                postgres.MigrationsHistoryTable("__ef_migrations_history_custody", "platform");
                postgres.UseNetTopologySuite();
            })
            .Options;
        return new CustodyDbContext(options, new TenantDatabaseExecutionState());
    }
}
