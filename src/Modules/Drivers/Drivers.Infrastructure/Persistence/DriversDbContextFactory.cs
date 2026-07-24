using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Paqueteria.Infrastructure.Tenancy;

namespace Drivers.Infrastructure.Persistence;

public sealed class DriversDbContextFactory : IDesignTimeDbContextFactory<DriversDbContext>
{
    public DriversDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PAQUETERIA_DESIGN_CONNECTION")
            ?? "Host=localhost;Database=paqueteria;Username=paqueteria_migrator;Password=local";
        var options = new DbContextOptionsBuilder<DriversDbContext>()
            .UseNpgsql(connectionString, postgres =>
            {
                postgres.MigrationsAssembly(typeof(DriversDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_drivers", "platform");
            })
            .Options;
        return new DriversDbContext(options, new TenantDatabaseExecutionState());
    }
}
