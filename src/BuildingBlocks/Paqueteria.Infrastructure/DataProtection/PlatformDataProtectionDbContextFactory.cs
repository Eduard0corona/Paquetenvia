using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Paqueteria.Infrastructure.DataProtection;

public sealed class PlatformDataProtectionDbContextFactory
    : IDesignTimeDbContextFactory<PlatformDataProtectionDbContext>
{
    public PlatformDataProtectionDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PAQUETERIA_DESIGN_CONNECTION")
            ?? "Host=localhost;Database=paqueteria;Username=paqueteria_migrator;Password=design-only";
        var options = new DbContextOptionsBuilder<PlatformDataProtectionDbContext>()
            .UseNpgsql(connectionString, postgres =>
                postgres.MigrationsHistoryTable(
                    PlatformDataProtectionSchema.MigrationsHistoryTable,
                    PlatformDataProtectionSchema.Schema))
            .Options;
        return new PlatformDataProtectionDbContext(options);
    }
}

public static class PlatformDataProtectionSchema
{
    public const string Schema = "platform";
    public const string Table = "data_protection_keys";
    public const string QualifiedTable = $"{Schema}.{Table}";
    public const string MigrationsHistoryTable = "__ef_migrations_history_platform";
}
