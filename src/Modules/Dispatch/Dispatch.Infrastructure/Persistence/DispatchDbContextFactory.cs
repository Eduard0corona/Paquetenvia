using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Paqueteria.Infrastructure.Tenancy;

namespace Dispatch.Infrastructure.Persistence;

public sealed class DispatchDbContextFactory : IDesignTimeDbContextFactory<DispatchDbContext>
{
    public DispatchDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("PAQUETERIA_DESIGN_TIME_DB")
            ?? "Host=localhost;Port=5432;Database=paqueteria;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql(
                connectionString,
                postgres => postgres.MigrationsHistoryTable(
                    "__ef_migrations_history_dispatch",
                    "platform"))
            .Options;
        return new DispatchDbContext(options, new TenantDatabaseExecutionState());
    }
}
