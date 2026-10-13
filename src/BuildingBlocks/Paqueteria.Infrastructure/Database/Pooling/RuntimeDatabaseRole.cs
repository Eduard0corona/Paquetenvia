namespace Paqueteria.Infrastructure.Database.Pooling;

/// <summary>
/// SCL-007: the runtime database roles that own a connection pool. A process builds one
/// <see cref="RuntimeDataSource"/> per role it uses and every module of that process resolves that one
/// (<see cref="RuntimeDataSourceServiceCollectionExtensions.AddRuntimeDataSource"/>). Each pool authenticates
/// with one login, a NOINHERIT NOBYPASSRLS member of exactly its role, which every transaction assumes with
/// <c>SET LOCAL ROLE</c> after <c>BEGIN</c>. The migrator, the Container Apps jobs and the tools keep their own
/// credentials and are not runtime pools.
/// </summary>
public enum RuntimeDatabaseRole
{
    /// <summary><c>paqueteria_app</c>, read from <c>ConnectionStrings:Paqueteria</c> (the API).</summary>
    Application,

    /// <summary><c>paqueteria_worker</c>, read from <c>ConnectionStrings:PaqueteriaWorker</c> (the Worker).</summary>
    Worker,

    /// <summary>
    /// <c>paqueteria_realtime</c>, read from <c>ConnectionStrings:PaqueteriaRealtime</c> (the API's in-process realtime
    /// outbox dispatchers, RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12).
    /// </summary>
    Realtime,
}

/// <summary>The fixed names of each <see cref="RuntimeDatabaseRole"/>.</summary>
public static class RuntimeDatabaseRoles
{
    public static IReadOnlyList<RuntimeDatabaseRole> All { get; } =
    [
        RuntimeDatabaseRole.Application,
        RuntimeDatabaseRole.Worker,
        RuntimeDatabaseRole.Realtime,
    ];

    /// <summary>The <c>ConnectionStrings</c> entry the role's pool is built from.</summary>
    public static string ConnectionStringName(RuntimeDatabaseRole role) => role switch
    {
        RuntimeDatabaseRole.Application => "Paqueteria",
        RuntimeDatabaseRole.Worker => "PaqueteriaWorker",
        RuntimeDatabaseRole.Realtime => "PaqueteriaRealtime",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown runtime database role."),
    };

    /// <summary>The NOLOGIN NOBYPASSRLS role (AI-18) the pool's login assumes in every transaction.</summary>
    public static string PostgreSqlRole(RuntimeDatabaseRole role) => role switch
    {
        RuntimeDatabaseRole.Application => "paqueteria_app",
        RuntimeDatabaseRole.Worker => "paqueteria_worker",
        RuntimeDatabaseRole.Realtime => "paqueteria_realtime",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown runtime database role."),
    };

    /// <summary>The role whose pool is built from <paramref name="connectionStringName"/>, matched exactly.</summary>
    public static bool TryFromConnectionStringName(string? connectionStringName, out RuntimeDatabaseRole role)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(ConnectionStringName(candidate), connectionStringName, StringComparison.Ordinal))
            {
                role = candidate;
                return true;
            }
        }

        role = default;
        return false;
    }
}
