using Npgsql;

namespace Paqueteria.Infrastructure.Database.Pooling;

/// <summary>
/// SCL-007: the one connection pool of a <see cref="RuntimeDatabaseRole"/> in a process. It is built on first use, so a
/// host whose provider is disabled never needs the connection string; then it fails closed, naming the missing
/// <c>ConnectionStrings</c> entry and nothing else.
/// </summary>
/// <remarks>
/// The pool keeps no session state for its users: every tenant unit of work runs in an explicit transaction with
/// <c>set_config(..., true)</c> and <c>SET LOCAL ROLE</c> after <c>BEGIN</c>, so the login, the role and the tenant
/// context are the same for every lease (AI-15 <c>pooling_contract</c>, transaction pooling compatible). Hosts get it
/// from <see cref="RuntimeDataSourceServiceCollectionExtensions.AddRuntimeDataSource"/>; only tests and tools call
/// <see cref="Create"/> themselves.
/// </remarks>
public sealed class RuntimeDataSource : IAsyncDisposable, IDisposable
{
    private readonly Lazy<NpgsqlDataSource> _dataSource;

    private RuntimeDataSource(
        RuntimeDatabaseRole role,
        string? connectionString,
        int maximumPoolSize,
        IReadOnlyList<Action<NpgsqlDataSourceBuilder>> plugins)
    {
        Role = role;
        MaximumPoolSize = maximumPoolSize;
        _dataSource = new(
            () => Build(role, connectionString, maximumPoolSize, plugins),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public RuntimeDatabaseRole Role { get; }

    /// <summary>
    /// The <c>Maximum Pool Size</c> the pool is built with, whatever the connection string says. The connection string
    /// cannot turn pooling off, multiplex, include server error detail or auto-prepare either.
    /// </summary>
    public int MaximumPoolSize { get; }

    public bool IsCreated => _dataSource.IsValueCreated;

    /// <summary>The pool itself, for EF Core (<c>UseNpgsql(dataSource)</c>) and ADO.NET readers.</summary>
    public NpgsqlDataSource DataSource => _dataSource.Value;

    /// <summary>
    /// Builds a standalone pool. Hosts never call this: they share the pool registered by
    /// <see cref="RuntimeDataSourceServiceCollectionExtensions.AddRuntimeDataSource"/>.
    /// </summary>
    public static RuntimeDataSource Create(
        RuntimeDatabaseRole role,
        string? connectionString,
        int maximumPoolSize,
        params Action<NpgsqlDataSourceBuilder>[] plugins)
    {
        if (!RuntimeDatabaseRoles.All.Contains(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown runtime database role.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPoolSize, 1);
        ArgumentNullException.ThrowIfNull(plugins);
        return new RuntimeDataSource(role, connectionString, maximumPoolSize, plugins.ToArray());
    }

    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        DataSource.OpenConnectionAsync(cancellationToken);

    public ValueTask DisposeAsync() =>
        _dataSource.IsValueCreated ? _dataSource.Value.DisposeAsync() : ValueTask.CompletedTask;

    public void Dispose()
    {
        if (_dataSource.IsValueCreated)
        {
            _dataSource.Value.Dispose();
        }
    }

    private static NpgsqlDataSource Build(
        RuntimeDatabaseRole role,
        string? connectionString,
        int maximumPoolSize,
        IReadOnlyList<Action<NpgsqlDataSourceBuilder>> plugins)
    {
        var name = RuntimeDatabaseRoles.ConnectionStringName(role);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{name} is required by the {RuntimeDatabaseRoles.PostgreSqlRole(role)} connection pool.");
        }

        NpgsqlConnectionStringBuilder settings;
        try
        {
            settings = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidCastException)
        {
            // The parser's message may quote a value of the string; only the entry is named.
            throw new InvalidOperationException($"ConnectionStrings:{name} is not a valid connection string.");
        }

        if (settings.MinPoolSize > maximumPoolSize)
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{name} sets a Minimum Pool Size above the {DatabasePoolOptions.SectionName} maximum of its pool.");
        }

        Enforce(settings, maximumPoolSize);
        var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
        foreach (var plugin in plugins)
        {
            plugin(builder);
        }

        // A plugin only maps types; whatever it touched, the pool keeps the enforced settings.
        Enforce(builder.ConnectionStringBuilder, maximumPoolSize);
        return builder.Build();
    }

    /// <summary>
    /// The settings every runtime pool has whatever its connection string says: a real pool capped at the budgeted
    /// size (<c>Pooling=false</c> would open unbounded physical connections), no multiplexing (one command per
    /// connection, so a transaction's <c>set_config</c> and <c>SET LOCAL ROLE</c> stay on its own connection), no
    /// server error detail (it carries row values, which can be personal data, into exceptions and logs) and no
    /// automatic preparation (a prepared statement is session state that outlives the lease).
    /// </summary>
    private static void Enforce(NpgsqlConnectionStringBuilder settings, int maximumPoolSize)
    {
        settings.Pooling = true;
        settings.MaxPoolSize = maximumPoolSize;
        settings.Multiplexing = false;
        settings.IncludeErrorDetail = false;
        settings.MaxAutoPrepare = 0;
    }
}

/// <summary>
/// A type plugin a module adds to a role's shared pool (for example NetTopologySuite for PostGIS). A plugin only maps
/// types; it never creates a pool, opens a connection or sets session state.
/// </summary>
public sealed class RuntimeDataSourcePlugin
{
    /// <summary>
    /// PostGIS geometry through NetTopologySuite on the <c>paqueteria_app</c> pool (Locations and Drivers). The building
    /// block does not reference the plugin package; the modules that do register it under this one name.
    /// </summary>
    public const string NetTopologySuite = "NetTopologySuite";

    public RuntimeDataSourcePlugin(RuntimeDatabaseRole role, string name, Action<NpgsqlDataSourceBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        Role = role;
        Name = name;
        Configure = configure;
    }

    public RuntimeDatabaseRole Role { get; }

    public string Name { get; }

    public Action<NpgsqlDataSourceBuilder> Configure { get; }
}
