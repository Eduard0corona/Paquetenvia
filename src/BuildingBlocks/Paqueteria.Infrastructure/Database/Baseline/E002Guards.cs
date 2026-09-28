using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

/// <summary>
/// E-002 v0.8 §36 sanitized guard failure. The message carries only the guard code, the phase and a
/// sanitized state class; it never carries credentials or raw catalog dumps.
/// </summary>
public sealed class E002GuardException(string guardCode, string phase, string detail)
    : InvalidOperationException($"{guardCode}; STOP_FOR_CONTRACT_REVIEW; phase={phase}; {detail}")
{
    public string GuardCode { get; } = guardCode;

    public string Phase { get; } = phase;
}

/// <summary>
/// Canonical E-002 v0.8 §10–§11 NULL-aware ACL entry: raw NULL ACLs expand through acldefault before aclexplode.
/// </summary>
public sealed record E002AclEntry(string Grantee, string Grantor, string Privilege, bool Grantable);

/// <summary>
/// Normalized ACL snapshot compared as a set (E-002 v0.8 §10: array ordering is irrelevant).
/// </summary>
public sealed class E002AclSnapshot(string objectClass, IReadOnlyCollection<E002AclEntry> entries)
{
    public string ObjectClass { get; } = objectClass;

    public IReadOnlyCollection<E002AclEntry> Entries { get; } = entries;

    public bool SetEquals(E002AclSnapshot other) =>
        Entries.ToHashSet().SetEquals(other.Entries);

    /// <summary>E-002 v0.8 §20: normalized post-cleanup database ACL must equal the pre-bridge snapshot.</summary>
    public static void AssertDatabaseRestored(E002AclSnapshot before, E002AclSnapshot after, string phase)
    {
        if (!before.SetEquals(after))
        {
            throw new E002GuardException("E002_DATABASE_ACL_RESTORE_MISMATCH", phase,
                $"object=database expected_entries={before.Entries.Count} observed_entries={after.Entries.Count}");
        }
    }

    public static async Task<E002AclSnapshot> ReadDatabaseAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string phase,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE grantee.rolname END,
                   grantor.rolname, acl.privilege_type, acl.is_grantable
            FROM pg_catalog.pg_database d
            CROSS JOIN LATERAL pg_catalog.aclexplode(
              COALESCE(d.datacl,pg_catalog.acldefault('d',d.datdba))) acl
            LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
            LEFT JOIN pg_catalog.pg_roles grantor ON grantor.oid=acl.grantor
            WHERE d.datname=current_database()
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<E002AclEntry>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(E002Guards.ReadAclEntry(reader, "database", phase));
        }

        return new E002AclSnapshot("database", entries);
    }
}

internal static class E002Guards
{
    internal static readonly (string Name, bool BypassRls)[] CanonicalRoles =
    [
        ("paqueteria_migrator", false),
        ("paqueteria_app", false),
        ("paqueteria_worker", false),
        ("paqueteria_bootstrap", true),
        ("paqueteria_outbox_executor", true),
        ("paqueteria_maintenance", true),
        ("paqueteria_lifecycle_executor", true),
        ("paqueteria_cleanup_executor", true),
        ("paqueteria_registration_executor", true),
        ("paqueteria_session_executor", true),
        ("paqueteria_master_data_executor", true),
        ("paqueteria_master_data_loader", false),
    ];

    /// <summary>
    /// MDM-001-OPERATOR-LOADER: roles an installation that predates the Pricing MDM-001 lane does not have yet.
    /// A missing one is not a mismatch while its function is absent too; once either exists it is held to the
    /// same exact attributes as every canonical role.
    /// </summary>
    internal static readonly string[] LaneIntroducedRoles =
    [
        "paqueteria_master_data_executor", "paqueteria_master_data_loader",
    ];

    internal static readonly string[] SpecializedOwners =
    [
        "paqueteria_bootstrap", "paqueteria_outbox_executor", "paqueteria_maintenance", "paqueteria_lifecycle_executor",
        "paqueteria_cleanup_executor", "paqueteria_registration_executor", "paqueteria_session_executor",
        "paqueteria_master_data_executor",
    ];

    /// <summary>E-002 v0.8 §11: an ACL entry whose grantee/grantor cannot be resolved is a normalization failure.</summary>
    internal static E002AclEntry ReadAclEntry(NpgsqlDataReader reader, string objectClass, string phase)
    {
        if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3))
        {
            throw new E002GuardException("E002_ACL_NORMALIZATION_FAILURE", phase,
                $"object={objectClass} unresolved grantee/grantor OID");
        }

        return new E002AclEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3));
    }

    /// <summary>E-002 v0.8 §15: exact canonical-role attribute map (ADR-034 adds the seventh, the lifecycle executor; OPS-003-CLEANUP-ROLE the eighth, the cleanup executor; REG-001 the ninth, the registration executor; BFF-SESSION-TABLE-SHAPE the tenth, the session executor; MDM-001-OPERATOR-LOADER the eleventh and twelfth, the master data executor and its operator grantee). Returns the names of roles that differ.</summary>
    internal static async Task<IReadOnlyList<string>> RoleAttributeMismatchesAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var mismatches = new List<string>();
        foreach (var (name, bypassRls) in CanonicalRoles)
        {
            await using var command = new NpgsqlCommand("""
                SELECT rolcanlogin,rolsuper,rolcreatedb,rolcreaterole,rolinherit,rolreplication,rolbypassrls
                FROM pg_catalog.pg_roles WHERE rolname=@name
                """, connection, transaction);
            command.Parameters.AddWithValue("name", name);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var found = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!found && LaneIntroducedRoles.Contains(name, StringComparer.Ordinal))
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                if (!await MasterDataFunctionExistsAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                mismatches.Add(name);
                continue;
            }

            if (!found ||
                reader.GetBoolean(0) || reader.GetBoolean(1) || reader.GetBoolean(2) ||
                reader.GetBoolean(3) || !reader.GetBoolean(4) || reader.GetBoolean(5) ||
                reader.GetBoolean(6) != bypassRls)
            {
                mismatches.Add(name);
            }
        }

        return mismatches;
    }

    private static async Task<bool> MasterDataFunctionExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.to_regprocedure('security.load_master_data(uuid,uuid,jsonb,bytea,boolean)') IS NOT NULL",
            connection, transaction);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task<bool> RoleExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string role, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.to_regrole(@role) IS NOT NULL", connection, transaction);
        command.Parameters.AddWithValue("role", role);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>E-002 v0.8 §16: effective USAGE/SET authority evaluated by effect. Returns roles lacking it.</summary>
    internal static async Task<IReadOnlyList<string>> EffectiveCapabilityGapsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var gaps = new List<string>();
        foreach (var role in new[] { "paqueteria_migrator", "paqueteria_bootstrap",
                     "paqueteria_outbox_executor", "paqueteria_maintenance", "paqueteria_lifecycle_executor",
                     "paqueteria_cleanup_executor", "paqueteria_registration_executor",
                     "paqueteria_session_executor", "paqueteria_master_data_executor" })
        {
            if (LaneIntroducedRoles.Contains(role, StringComparer.Ordinal) &&
                !await RoleExistsAsync(connection, transaction, role, cancellationToken).ConfigureAwait(false))
            {
                // Checked by RoleAttributeMismatchesAsync: absent only while its lane is absent too.
                continue;
            }

            await using var command = new NpgsqlCommand("""
                SELECT pg_catalog.pg_has_role(session_user,@role,'SET'),
                       pg_catalog.pg_has_role(session_user,@role,'USAGE')
                """, connection, transaction);
            command.Parameters.AddWithValue("role", role);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!reader.GetBoolean(0) || (role == "paqueteria_migrator" && !reader.GetBoolean(1)))
            {
                gaps.Add(role);
            }
        }

        return gaps;
    }
}
