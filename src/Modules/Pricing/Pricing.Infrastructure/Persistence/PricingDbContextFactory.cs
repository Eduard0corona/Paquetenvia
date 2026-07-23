using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Paqueteria.Infrastructure.Tenancy;

namespace Pricing.Infrastructure.Persistence;

public sealed class PricingDbContextFactory : IDesignTimeDbContextFactory<PricingDbContext>
{
    public PricingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PricingDbContext>()
            .UseNpgsql("Host=localhost;Database=paqueteria;Username=paqueteria_migrator;Password=development-only")
            .Options;
        return new PricingDbContext(options, new TenantDatabaseExecutionState());
    }
}
