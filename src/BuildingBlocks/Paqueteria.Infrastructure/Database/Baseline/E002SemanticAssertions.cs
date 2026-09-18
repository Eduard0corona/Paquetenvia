using Npgsql;
using Paqueteria.Infrastructure.Database;

namespace Paqueteria.Infrastructure.Database.Baseline;

public sealed record E002SemanticReport(
    E002NotificationState NotificationState,
    string RoutineMap,
    int ControlledIdentities,
    int NormalizedExecuteRows);

public sealed class E002SemanticException(IReadOnlyList<string> violations)
    : InvalidOperationException($"E002_SEMANTIC_PARTIAL; STOP_FOR_CONTRACT_REVIEW: {string.Join(" | ", violations)}")
{
    public IReadOnlyList<string> Violations { get; } = violations;
}

public sealed class E002SemanticAssertions
{
    private sealed record AclEntry(string Grantee, string Grantor, string Privilege, bool Grantable);

    private static readonly (string Name, bool BypassRls)[] Roles =
    [
        ("paqueteria_migrator", false),
        ("paqueteria_app", false),
        ("paqueteria_worker", false),
        ("paqueteria_bootstrap", true),
        ("paqueteria_outbox_executor", true),
        ("paqueteria_maintenance", true),
    ];

    private static readonly string[] SpecializedOwners =
        ["paqueteria_bootstrap", "paqueteria_outbox_executor", "paqueteria_maintenance"];

    public async Task<E002SemanticReport> AssertAsync(
        NpgsqlConnection connection,
        E002NotificationState state,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        if (state == E002NotificationState.Drift)
        {
            throw new InvalidOperationException("E002_ROUTINE_MAP_STATE_UNRESOLVED; STOP_FOR_CONTRACT_REVIEW");
        }

        var persisted = await E002NotificationStateReader.ReadAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (persisted != state)
        {
            throw new InvalidOperationException("E002_ROUTINE_MAP_STATE_UNRESOLVED; STOP_FOR_CONTRACT_REVIEW");
        }

        var violations = new List<string>();
        await AssertRoleAttributesAsync(connection, transaction, violations, cancellationToken).ConfigureAwait(false);
        await AssertEffectiveCapabilitiesAsync(connection, transaction, violations, cancellationToken).ConfigureAwait(false);
        await AssertSchemaOwnersAsync(connection, transaction, violations, cancellationToken).ConfigureAwait(false);
        await AssertExtensionsAsync(connection, transaction, violations, cancellationToken).ConfigureAwait(false);
        await AssertTemporaryCreateResidueAsync(connection, transaction, violations, cancellationToken).ConfigureAwait(false);
        await AssertSchemaAclAsync(connection, transaction, "security", SecurityAcl(), violations, cancellationToken)
            .ConfigureAwait(false);
        await AssertSchemaAclAsync(connection, transaction, "notifications", NotificationsAcl(state), violations,
            cancellationToken).ConfigureAwait(false);
        var mapState = E002NotificationStateReader.SelectMap(state);
        var (identities, aclRows) = await AssertRoutineMapCoreAsync(
            connection, transaction, mapState, violations, cancellationToken).ConfigureAwait(false);
        if (violations.Count != 0)
        {
            throw new E002SemanticException(violations.AsReadOnly());
        }

        return new E002SemanticReport(state, E002RoutineMap.Name(mapState), identities, aclRows);
    }

    public async Task AssertNtf001TargetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        var persisted = await E002NotificationStateReader.ReadAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (persisted != E002NotificationState.Pending)
        {
            throw new InvalidOperationException("E002_ROUTINE_MAP_TRANSITION_CONTEXT_INVALID; STOP_FOR_CONTRACT_REVIEW");
        }

        var violations = new List<string>();
        await AssertRoutineMapCoreAsync(connection, transaction, E002RoutineMapState.Ntf001TargetApplied,
            violations, cancellationToken).ConfigureAwait(false);
        if (violations.Count != 0)
        {
            throw new E002SemanticException(violations.AsReadOnly());
        }
    }

    public static void AssertConnectionReset(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (builder.NoResetOnClose)
        {
            throw new InvalidOperationException("E002_CONNECTION_RESET_DISABLED; STOP_FOR_CONTRACT_REVIEW");
        }
    }

    private static async Task AssertRoleAttributesAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        foreach (var (name, bypassRls) in Roles)
        {
            await using var command = new NpgsqlCommand("""
                SELECT rolcanlogin,rolsuper,rolcreatedb,rolcreaterole,rolinherit,rolreplication,rolbypassrls
                FROM pg_catalog.pg_roles WHERE rolname=@name
                """, connection, transaction);
            command.Parameters.AddWithValue("name", name);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetBoolean(0) || reader.GetBoolean(1) || reader.GetBoolean(2) ||
                reader.GetBoolean(3) || !reader.GetBoolean(4) || reader.GetBoolean(5) ||
                reader.GetBoolean(6) != bypassRls)
            {
                violations.Add($"role attributes differ: {name}");
            }
        }
    }

    private static async Task AssertEffectiveCapabilitiesAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        foreach (var role in new[] { "paqueteria_migrator", "paqueteria_bootstrap",
                     "paqueteria_outbox_executor", "paqueteria_maintenance" })
        {
            await using var command = new NpgsqlCommand("""
                SELECT pg_catalog.pg_has_role(session_user,@role,'SET'),
                       pg_catalog.pg_has_role(session_user,@role,'USAGE')
                """, connection, transaction);
            command.Parameters.AddWithValue("role", role);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!reader.GetBoolean(0) || (role == "paqueteria_migrator" && !reader.GetBoolean(1)))
            {
                violations.Add($"effective deployment capability missing: {role}");
            }
        }
    }

    private static async Task AssertSchemaOwnersAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT expected.name, pg_catalog.pg_get_userbyid(n.nspowner)
            FROM unnest(@schemas::text[]) expected(name)
            LEFT JOIN pg_catalog.pg_namespace n ON n.nspname=expected.name
            """, connection, transaction);
        command.Parameters.AddWithValue("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(1) || reader.GetString(1) != "paqueteria_migrator")
            {
                violations.Add($"schema owner differs: {reader.GetString(0)}");
            }
        }
    }

    private static async Task AssertExtensionsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT e.extname,e.extversion,n.nspname
            FROM pg_catalog.pg_extension e
            JOIN pg_catalog.pg_namespace n ON n.oid=e.extnamespace
            WHERE e.extname IN ('postgis','pgcrypto')
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var installed = new Dictionary<string, (string Version, string Schema)>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            installed.Add(reader.GetString(0), (reader.GetString(1), reader.GetString(2)));
        }

        if (!installed.TryGetValue("postgis", out var postgis) ||
            postgis.Schema != "public" || !postgis.Version.StartsWith("3.6", StringComparison.Ordinal))
        {
            violations.Add("PostGIS 3.6 extension placement differs");
        }

        if (!installed.TryGetValue("pgcrypto", out var pgcrypto) || pgcrypto.Schema != "extensions")
        {
            violations.Add("pgcrypto extension placement differs");
        }
    }

    private static async Task AssertTemporaryCreateResidueAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand("""
            SELECT EXISTS (
              SELECT 1 FROM pg_catalog.pg_database d
              CROSS JOIN LATERAL pg_catalog.aclexplode(
                COALESCE(d.datacl,pg_catalog.acldefault('d',d.datdba))) acl
              JOIN pg_catalog.pg_roles r ON r.oid=acl.grantee
              WHERE d.datname=current_database() AND r.rolname='paqueteria_migrator'
                AND acl.privilege_type='CREATE')
              OR pg_catalog.has_database_privilege('paqueteria_migrator',current_database(),'CREATE')
            """, connection, transaction))
        {
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                violations.Add("temporary database CREATE residue");
            }
        }

        foreach (var schema in new[] { "security", "notifications" })
        foreach (var role in SpecializedOwners)
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_catalog.has_schema_privilege(@role,@schema,'CREATE')", connection, transaction);
            command.Parameters.AddWithValue("role", role);
            command.Parameters.AddWithValue("schema", schema);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                violations.Add($"temporary CREATE residue: {schema}/{role}");
            }
        }
    }

    private static IReadOnlyList<AclEntry> SecurityAcl() =>
    [
        new("paqueteria_migrator", "paqueteria_migrator", "CREATE", false),
        new("paqueteria_migrator", "paqueteria_migrator", "USAGE", false),
        new("paqueteria_app", "paqueteria_migrator", "USAGE", false),
        new("paqueteria_worker", "paqueteria_migrator", "USAGE", false),
        new("paqueteria_bootstrap", "paqueteria_migrator", "USAGE", false),
        new("paqueteria_outbox_executor", "paqueteria_migrator", "USAGE", false),
        new("paqueteria_maintenance", "paqueteria_migrator", "USAGE", false),
    ];

    private static IReadOnlyList<AclEntry> NotificationsAcl(E002NotificationState state)
    {
        var expected = new List<AclEntry>
        {
            new("paqueteria_migrator", "paqueteria_migrator", "CREATE", false),
            new("paqueteria_migrator", "paqueteria_migrator", "USAGE", false),
            new("paqueteria_app", "paqueteria_migrator", "USAGE", false),
            new("paqueteria_worker", "paqueteria_migrator", "USAGE", false),
        };
        if (state == E002NotificationState.Applied)
        {
            expected.Add(new("paqueteria_outbox_executor", "paqueteria_migrator", "USAGE", false));
        }

        return expected;
    }

    private static async Task AssertSchemaAclAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string schema,
        IReadOnlyList<AclEntry> expected, ICollection<string> violations, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE grantee.rolname END,
                   grantor.rolname, acl.privilege_type, acl.is_grantable
            FROM pg_catalog.pg_namespace n
            CROSS JOIN LATERAL pg_catalog.aclexplode(
              COALESCE(n.nspacl,pg_catalog.acldefault('n',n.nspowner))) acl
            LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
            LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
            WHERE n.nspname=@schema
            """, connection, transaction);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var observed = new List<AclEntry>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            observed.Add(new AclEntry(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetBoolean(3)));
        }

        foreach (var missing in expected.Except(observed)) violations.Add($"{schema} ACL missing: {missing}");
        foreach (var extra in observed.Except(expected)) violations.Add($"{schema} ACL unexpected: {extra}");
        if (expected.Count != observed.Count) violations.Add($"{schema} ACL row cardinality differs");
    }

    private static async Task<(int Identities, int ExecuteRows)> AssertRoutineMapCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, E002RoutineMapState mapState,
        ICollection<string> violations, CancellationToken cancellationToken)
    {
        var map = E002RoutineMap.Select(mapState);
        var expectedOids = new HashSet<uint>();
        var totalRows = 0;
        foreach (var routine in map)
        {
            await using var command = new NpgsqlCommand("""
                SELECT p.oid,pg_catalog.pg_get_function_identity_arguments(p.oid),
                       pg_catalog.pg_get_userbyid(p.proowner),
                       CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE grantee.rolname END,
                       grantor.rolname,acl.privilege_type,acl.is_grantable
                FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                LEFT JOIN LATERAL pg_catalog.aclexplode(
                  COALESCE(p.proacl,pg_catalog.acldefault('f',p.proowner))) acl ON true
                LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
                WHERE p.oid=pg_catalog.to_regprocedure(@signature)
                  AND n.nspname IN ('security','notifications')
                """, connection, transaction);
            command.Parameters.AddWithValue("signature", routine.Signature);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var observed = new List<AclEntry>();
            var found = false;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                found = true;
                expectedOids.Add(reader.GetFieldValue<uint>(0));
                if (reader.GetString(2) != routine.Owner)
                {
                    violations.Add($"routine owner differs: {routine.Signature}");
                }

                if (!reader.IsDBNull(5) && reader.GetString(5) == "EXECUTE")
                {
                    observed.Add(new AclEntry(reader.GetString(3), reader.GetString(4),
                        reader.GetString(5), reader.GetBoolean(6)));
                }
            }

            if (!found)
            {
                violations.Add($"routine missing: {routine.Signature}");
                continue;
            }

            var expected = new[] { routine.Owner }.Concat(routine.Grantees)
                .Select(grantee => new AclEntry(grantee, routine.Owner, "EXECUTE", false)).ToArray();
            foreach (var missing in expected.Except(observed))
                violations.Add($"routine EXECUTE missing: {routine.Signature}/{missing}");
            foreach (var extra in observed.Except(expected))
                violations.Add($"routine EXECUTE unexpected: {routine.Signature}/{extra}");
            if (expected.Length != observed.Count)
                violations.Add($"routine EXECUTE row cardinality differs: {routine.Signature}");
            totalRows += observed.Count;
        }

        await using (var extras = new NpgsqlCommand("""
            SELECT p.oid,p.oid::regprocedure::text
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            JOIN pg_catalog.pg_roles r ON r.oid=p.proowner
            WHERE n.nspname IN ('security','notifications')
              AND r.rolname=ANY(@owners::text[])
            """, connection, transaction))
        {
            extras.Parameters.AddWithValue("owners", SpecializedOwners);
            await using var reader = await extras.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!expectedOids.Contains(reader.GetFieldValue<uint>(0)))
                {
                    violations.Add($"unexpected specialized-owner routine: {reader.GetString(1)}");
                }
            }
        }

        var expectedRows = map.Sum(routine => 1 + routine.Grantees.Count);
        if (totalRows != expectedRows)
        {
            violations.Add($"routine EXECUTE total differs: expected {expectedRows}, observed {totalRows}");
        }

        return (expectedOids.Count, totalRows);
    }
}
