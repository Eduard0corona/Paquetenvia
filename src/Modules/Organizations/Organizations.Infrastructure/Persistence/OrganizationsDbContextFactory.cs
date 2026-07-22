using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Paqueteria.Infrastructure.Tenancy;

namespace Organizations.Infrastructure.Persistence;

public sealed class OrganizationsDbContextFactory : IDesignTimeDbContextFactory<OrganizationsDbContext>
{
    public OrganizationsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PAQUETERIA_EF_DESIGN_DB");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "PAQUETERIA_EF_DESIGN_DB must explicitly identify a disposable or synthetic design database.");
        }

        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql(connectionString, postgres =>
                postgres.MigrationsHistoryTable("__ef_migrations_history_organizations", "platform"))
            .Options;
        return new OrganizationsDbContext(options, new TenantDatabaseExecutionState());
    }
}
