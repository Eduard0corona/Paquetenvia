using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

public sealed class DatabaseBaselineStateDetector
{
    private static readonly string[] Tables =
    [
        "platform.outbox_events",
        "platform.location_outbox_events",
        "orders.order_events",
        "orders.order_acceptances",
        "custody.proofs",
        "platform.audit_logs",
    ];

    private static readonly string[] Functions =
    [
        "security.resolve_identity_context(text)",
        "security.get_public_tracking_projection(text)",
        "security.claim_outbox(text,integer,interval)",
        "security.settle_outbox(uuid,uuid,text,text,timestamp with time zone)",
        "security.requeue_stale_outbox(interval,integer,integer)",
        "security.purge_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)",
        "security.claim_location_outbox(text,integer,interval)",
        "security.settle_location_outbox(uuid,uuid,text,text,timestamp with time zone)",
        "security.requeue_stale_location_outbox(interval,integer,integer)",
        "security.purge_location_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)",
    ];

    private static readonly string[] Roles =
    [
        "paqueteria_migrator",
        "paqueteria_app",
        "paqueteria_worker",
        "paqueteria_bootstrap",
        "paqueteria_outbox_executor",
        "paqueteria_maintenance",
    ];

    public async Task<DatabaseBaselineState> DetectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var schemas = DatabaseSchemaCatalog.ApplicationSchemas.ToArray();
        var presentSchemas = await QueryExistingAsync(
            connection,
            transaction,
            "SELECT name FROM unnest(@names::text[]) name WHERE pg_catalog.to_regnamespace(name) IS NOT NULL ORDER BY name",
            schemas,
            cancellationToken).ConfigureAwait(false);
        var presentTables = await QueryExistingAsync(
            connection,
            transaction,
            "SELECT name FROM unnest(@names::text[]) name WHERE pg_catalog.to_regclass(name) IS NOT NULL ORDER BY name",
            Tables,
            cancellationToken).ConfigureAwait(false);
        var presentFunctions = await QueryExistingAsync(
            connection,
            transaction,
            "SELECT name FROM unnest(@names::text[]) name WHERE pg_catalog.to_regprocedure(name) IS NOT NULL ORDER BY name",
            Functions,
            cancellationToken).ConfigureAwait(false);
        var presentRoles = await QueryExistingAsync(
            connection,
            transaction,
            "SELECT name FROM unnest(@names::text[]) name WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_roles r WHERE r.rolname=name) ORDER BY name",
            Roles,
            cancellationToken).ConfigureAwait(false);

        var present = presentSchemas.Select(value => $"schema:{value}")
            .Concat(presentTables.Select(value => $"table:{value}"))
            .Concat(presentFunctions.Select(value => $"function:{value}"))
            .Concat(presentRoles.Select(value => $"role:{value}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = schemas.Select(value => $"schema:{value}")
            .Concat(Tables.Select(value => $"table:{value}"))
            .Concat(Functions.Select(value => $"function:{value}"))
            .Concat(Roles.Select(value => $"role:{value}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missing = expected.Except(present, StringComparer.Ordinal).ToArray();

        var databaseObjectsPresent = presentSchemas.Count + presentTables.Count + presentFunctions.Count;
        var status = missing.Length == 0
            ? DatabaseBaselineStatus.Applied
            : databaseObjectsPresent == 0
                ? DatabaseBaselineStatus.Clean
                : DatabaseBaselineStatus.Partial;
        return new DatabaseBaselineState(status, Array.AsReadOnly(present), Array.AsReadOnly(missing));
    }

    private static async Task<IReadOnlyList<string>> QueryExistingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        string[] names,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("names", names);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetString(0));
        }

        return values.AsReadOnly();
    }
}
