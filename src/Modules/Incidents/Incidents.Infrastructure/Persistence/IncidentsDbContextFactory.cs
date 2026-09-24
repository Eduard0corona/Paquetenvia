using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Paqueteria.Infrastructure.Tenancy;

namespace Incidents.Infrastructure.Persistence;

public sealed class IncidentsDbContextFactory : IDesignTimeDbContextFactory<IncidentsDbContext>
{
    public IncidentsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PAQUETERIA_DESIGN_CONNECTION")
            ?? "Host=localhost;Database=paqueteria;Username=paqueteria_migrator;Password=design-only";
        var options = new DbContextOptionsBuilder<IncidentsDbContext>()
            .UseNpgsql(connectionString, postgres =>
                postgres.MigrationsHistoryTable("__ef_migrations_history_incidents", "platform"))
            .Options;
        return new IncidentsDbContext(options, new TenantDatabaseExecutionState());
    }
}
