using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Paqueteria.Infrastructure.Tenancy;

namespace Locations.Infrastructure.Persistence;

public sealed class LocationsDbContextFactory : IDesignTimeDbContextFactory<LocationsDbContext>
{
    public LocationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LocationsDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=paqueteria;Username=paqueteria_migrator;Password=development-only",
                postgres => postgres.UseNetTopologySuite())
            .Options;
        return new LocationsDbContext(options, new TenantDatabaseExecutionState());
    }
}
