using Microsoft.Extensions.Options;

namespace Paqueteria.Infrastructure.Database.Pooling;

/// <summary>
/// SCL-007 connection budget: the maximum size of each runtime pool (<see cref="RuntimeDatabaseRole"/>). These values
/// are the only pool sizes the runtime uses; a <c>Maximum Pool Size</c> in a connection string is replaced by them.
/// </summary>
/// <remarks>
/// Arithmetic (docs/development/scl-007-pool-per-role.md): the pilot PostgreSQL B1ms has <c>max_connections</c> 50, of
/// which Azure reserves 15 for replication and monitoring, so 35 are user connections. The runtime pools of the one
/// API replica and the one Worker replica (GATE-013) may take at most 70 % of them (AZR-001-INV-014),
/// <see cref="RuntimeConnectionBudget"/> = floor(35 × 0.70) = 24. The other 11 stay for the migrator jobs, the
/// restore drill, administration and diagnostics. Every process validates the sum of the three sizes, so a value
/// raised in one process alone also fails that process's start.
/// </remarks>
public sealed class DatabasePoolOptions
{
    public const string SectionName = "DatabasePools";

    /// <summary>floor((50 - 15) × 0.70): the most connections the API and Worker runtime pools may hold together.</summary>
    public const int RuntimeConnectionBudget = 24;

    /// <summary><c>paqueteria_app</c> pool of the API (every API module).</summary>
    public int ApplicationMaximumPoolSize { get; set; } = 10;

    /// <summary><c>paqueteria_realtime</c> pool of the API's in-process realtime outbox dispatchers.</summary>
    public int RealtimeMaximumPoolSize { get; set; } = 4;

    /// <summary><c>paqueteria_worker</c> pool of the Worker (every Worker module and job).</summary>
    public int WorkerMaximumPoolSize { get; set; } = 10;

    public int MaximumPoolSizeFor(RuntimeDatabaseRole role) => role switch
    {
        RuntimeDatabaseRole.Application => ApplicationMaximumPoolSize,
        RuntimeDatabaseRole.Realtime => RealtimeMaximumPoolSize,
        RuntimeDatabaseRole.Worker => WorkerMaximumPoolSize,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown runtime database role."),
    };

    /// <summary>The sum of the three configured maxima, in <c>long</c> so no configured value can overflow it.</summary>
    public long TotalMaximumPoolSize =>
        (long)ApplicationMaximumPoolSize + RealtimeMaximumPoolSize + WorkerMaximumPoolSize;
}

/// <summary>Fails the start on any pool size outside 1..budget or a sum above <see cref="DatabasePoolOptions.RuntimeConnectionBudget"/>.</summary>
public sealed class DatabasePoolOptionsValidator : IValidateOptions<DatabasePoolOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabasePoolOptions options)
    {
        var errors = Errors(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    public static IReadOnlyList<string> Errors(DatabasePoolOptions? options)
    {
        if (options is null)
        {
            return [$"{DatabasePoolOptions.SectionName} is required."];
        }

        var errors = new List<string>();
        foreach (var (key, value) in new[]
        {
            (nameof(DatabasePoolOptions.ApplicationMaximumPoolSize), options.ApplicationMaximumPoolSize),
            (nameof(DatabasePoolOptions.RealtimeMaximumPoolSize), options.RealtimeMaximumPoolSize),
            (nameof(DatabasePoolOptions.WorkerMaximumPoolSize), options.WorkerMaximumPoolSize),
        })
        {
            if (value is < 1 or > DatabasePoolOptions.RuntimeConnectionBudget)
            {
                errors.Add(
                    $"{DatabasePoolOptions.SectionName}:{key} must be between 1 and " +
                    $"{DatabasePoolOptions.RuntimeConnectionBudget}.");
            }
        }

        if (options.TotalMaximumPoolSize > DatabasePoolOptions.RuntimeConnectionBudget)
        {
            errors.Add(
                $"{DatabasePoolOptions.SectionName}: ApplicationMaximumPoolSize + RealtimeMaximumPoolSize + " +
                $"WorkerMaximumPoolSize must not exceed the SCL-007 budget of " +
                $"{DatabasePoolOptions.RuntimeConnectionBudget} connections.");
        }

        return errors;
    }
}
