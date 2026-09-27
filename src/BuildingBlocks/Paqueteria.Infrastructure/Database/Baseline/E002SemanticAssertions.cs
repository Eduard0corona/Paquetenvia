using Npgsql;
using Paqueteria.Infrastructure.Database;

namespace Paqueteria.Infrastructure.Database.Baseline;

public sealed record E002SemanticReport(
    E002NotificationState NotificationState,
    string RoutineMap,
    int ControlledIdentities,
    int NormalizedExecuteRows);

/// <summary>
/// E-002 v0.10 Amendment 1 SEMANTIC_PARTIAL classification. Each violation is prefixed with the exact
/// E-002 v0.8 §36 guard code that names the failed invariant (e.g. "E002_ROLE_ATTRIBUTE_MISMATCH: ...").
/// </summary>
public sealed class E002SemanticException(IReadOnlyList<string> violations)
    : InvalidOperationException($"E002_SEMANTIC_PARTIAL; STOP_FOR_CONTRACT_REVIEW: {string.Join(" | ", violations)}")
{
    public IReadOnlyList<string> Violations { get; } = violations;

    public IReadOnlyList<string> GuardCodes { get; } = violations
        .Select(violation => violation.Split(':', 2)[0])
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

public sealed class E002SemanticAssertions
{
    private sealed record AclEntry(string Grantee, string Grantor, string Privilege, bool Grantable);

    private static string[] SpecializedOwners => E002Guards.SpecializedOwners;

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
        var lif001Applied = await E002LifecycleStateReader.IsAppliedAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        var ops003Applied = await E002CleanupStateReader.IsAppliedAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        var (identities, aclRows) = await AssertRoutineMapCoreAsync(
            connection, transaction, mapState, lif001Applied, ops003Applied, violations, cancellationToken)
            .ConfigureAwait(false);
        await AssertSecurityDefinerAsync(connection, transaction, violations, cancellationToken).ConfigureAwait(false);
        if (violations.Count != 0)
        {
            throw new E002SemanticException(violations.AsReadOnly());
        }

        return new E002SemanticReport(
            state, E002RoutineMap.Name(mapState, lif001Applied, ops003Applied), identities, aclRows);
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
        var lif001Applied = await E002LifecycleStateReader.IsAppliedAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        var ops003Applied = await E002CleanupStateReader.IsAppliedAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        await AssertRoutineMapCoreAsync(connection, transaction, E002RoutineMapState.Ntf001TargetApplied,
            lif001Applied, ops003Applied, violations, cancellationToken).ConfigureAwait(false);
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
        foreach (var name in await E002Guards.RoleAttributeMismatchesAsync(connection, transaction, cancellationToken)
                     .ConfigureAwait(false))
        {
            violations.Add($"E002_ROLE_ATTRIBUTE_MISMATCH: role attributes differ: {name}");
        }
    }

    private static async Task AssertEffectiveCapabilitiesAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        foreach (var role in await E002Guards.EffectiveCapabilityGapsAsync(connection, transaction, cancellationToken)
                     .ConfigureAwait(false))
        {
            violations.Add($"E002_EFFECTIVE_ROLE_CAPABILITY_MISSING: effective deployment capability missing: {role}");
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
                violations.Add($"E002_SEMANTIC_PARTIAL: schema owner differs: {reader.GetString(0)}");
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
            violations.Add("E002_SEMANTIC_PARTIAL: PostGIS 3.6 extension placement differs");
        }

        if (!installed.TryGetValue("pgcrypto", out var pgcrypto) || pgcrypto.Schema != "extensions")
        {
            violations.Add("E002_SEMANTIC_PARTIAL: pgcrypto extension placement differs");
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
                violations.Add("E002_SEMANTIC_PARTIAL: temporary database CREATE residue");
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
                violations.Add($"E002_SEMANTIC_PARTIAL: temporary CREATE residue: {schema}/{role}");
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
            var entry = E002Guards.ReadAclEntry(reader, $"schema:{schema}", "semantic-assertions");
            observed.Add(new AclEntry(entry.Grantee, entry.Grantor, entry.Privilege, entry.Grantable));
        }

        // E-002 v0.8 §21 (security = canonical post-AI18 map) and §27 (notifications = exact restoration).
        var guard = schema == "security" ? "E002_BASELINE_SECURITY_ACL_MISMATCH" : "E002_NOTIFICATIONS_ACL_RESTORE_MISMATCH";
        foreach (var missing in expected.Except(observed)) violations.Add($"{guard}: {schema} ACL missing: {missing}");
        foreach (var extra in observed.Except(expected)) violations.Add($"{guard}: {schema} ACL unexpected: {extra}");
        if (expected.Count != observed.Count) violations.Add($"{guard}: {schema} ACL row cardinality differs");
    }

    private static async Task<(int Identities, int ExecuteRows)> AssertRoutineMapCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, E002RoutineMapState mapState,
        bool lif001Applied, bool ops003Applied, ICollection<string> violations, CancellationToken cancellationToken)
    {
        var map = E002RoutineMap.Select(mapState, lif001Applied, ops003Applied);
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
                    violations.Add($"E002_OWNER_MAP_MISMATCH: routine owner differs: {routine.Signature}");
                }

                if (!reader.IsDBNull(5) && reader.GetString(5) == "EXECUTE")
                {
                    if (reader.IsDBNull(3) || reader.IsDBNull(4))
                    {
                        throw new E002GuardException("E002_ACL_NORMALIZATION_FAILURE", "semantic-assertions",
                            "object=routine unresolved grantee/grantor OID");
                    }

                    observed.Add(new AclEntry(reader.GetString(3), reader.GetString(4),
                        reader.GetString(5), reader.GetBoolean(6)));
                }
            }

            if (!found)
            {
                violations.Add($"E002_OWNER_MAP_MISMATCH: routine missing: {routine.Signature}");
                continue;
            }

            var expected = new[] { routine.Owner }.Concat(routine.Grantees)
                .Select(grantee => new AclEntry(grantee, routine.Owner, "EXECUTE", false)).ToArray();
            foreach (var missing in expected.Except(observed))
                violations.Add($"E002_ROUTINE_ACL_MISMATCH: routine EXECUTE missing: {routine.Signature}/{missing}");
            foreach (var extra in observed.Except(expected))
                violations.Add($"E002_ROUTINE_ACL_MISMATCH: routine EXECUTE unexpected: {routine.Signature}/{extra}");
            if (expected.Length != observed.Count)
                violations.Add($"E002_ROUTINE_ACL_MISMATCH: routine EXECUTE row cardinality differs: {routine.Signature}");
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
                    violations.Add($"E002_OWNER_MAP_MISMATCH: unexpected specialized-owner routine: {reader.GetString(1)}");
                }
            }
        }

        var expectedRows = map.Sum(routine => 1 + routine.Grantees.Count);
        if (totalRows != expectedRows)
        {
            violations.Add($"E002_ROUTINE_ACL_MISMATCH: routine EXECUTE total differs: expected {expectedRows}, observed {totalRows}");
        }

        return (expectedOids.Count, totalRows);
    }

    /// <summary>
    /// E-002 v0.8 §31: every SECURITY DEFINER routine owned by a specialized privileged role must pin an explicit
    /// search_path that starts with pg_catalog (no writable schema before trusted schemas) and ends with pg_temp.
    /// </summary>
    private static async Task AssertSecurityDefinerAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT p.oid::regprocedure::text, p.proconfig
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            JOIN pg_catalog.pg_roles r ON r.oid=p.proowner
            WHERE n.nspname IN ('security','notifications') AND p.prosecdef
              AND r.rolname=ANY(@owners::text[])
            """, connection, transaction);
        command.Parameters.AddWithValue("owners", SpecializedOwners);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var searchPath = reader.IsDBNull(1)
                ? null
                : reader.GetFieldValue<string[]>(1)
                    .FirstOrDefault(setting => setting.StartsWith("search_path=", StringComparison.Ordinal));
            var schemas = searchPath?["search_path=".Length..]
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
            if (schemas.Length < 2 || schemas[0] != "pg_catalog" || schemas[^1] != "pg_temp" || schemas.Contains("public"))
            {
                violations.Add($"E002_SECURITY_DEFINER_UNSAFE: search_path unsafe or missing: {reader.GetString(0)}");
            }
        }
    }
}
