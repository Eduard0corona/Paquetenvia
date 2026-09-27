using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Paqueteria.Infrastructure.Database.Evolution;

/// <summary>
/// Migration-only context of the platform evolution lane: additive AI-06/AI-18 deltas that belong
/// to no single module lane — the two <c>paqueteria_bootstrap</c> functions, their column grants
/// and cross-module operational indexes, including the platform outbox tables that have no module
/// lane of their own. It maps no entity; it only owns its migration history.
/// </summary>
public sealed class PlatformEvolutionDbContext(
    DbContextOptions<PlatformEvolutionDbContext> options) : DbContext(options);

public sealed class PlatformEvolutionDbContextFactory
    : IDesignTimeDbContextFactory<PlatformEvolutionDbContext>
{
    public PlatformEvolutionDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PAQUETERIA_DESIGN_CONNECTION")
            ?? "Host=localhost;Database=paqueteria;Username=paqueteria_migrator;Password=design-only";
        var options = new DbContextOptionsBuilder<PlatformEvolutionDbContext>()
            .UseNpgsql(connectionString, postgres =>
                postgres.MigrationsHistoryTable(
                    PlatformEvolutionSchema.MigrationsHistoryTable,
                    PlatformEvolutionSchema.Schema))
            .Options;
        return new PlatformEvolutionDbContext(options);
    }
}

public static class PlatformEvolutionSchema
{
    public const string Schema = "platform";
    public const string MigrationsHistoryTable = "__ef_migrations_history_platform_evolution";
}
