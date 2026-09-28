using Npgsql;
using Paqueteria.Application.Security;

internal sealed record MasterDataGateOptions(string DeploymentClass, bool Gate007Closed, Guid PlatformOrganizationId);

/// <summary>
/// MDM-001 B1/N2 / GATE-007: writes the single row of <c>platform.master_data_deployment_gate</c> as
/// <c>paqueteria_migrator</c> (its owner and the only role its RLS policy admits), from the deployment
/// (migration) connection only, and records every change as an append-only audit row of the PLATFORM
/// organization in the same transaction. The loader function reads the marker and, whatever the operator's
/// machine says, takes only SYNTHETIC files in a SYNTHETIC database, never takes them in a REAL one, and loads
/// driver profiles only in a SYNTHETIC database or once GATE-007 is recorded as closed. A REAL database is
/// never turned back into a SYNTHETIC one. The operator login cannot write the marker: it is not a member of
/// the migrator.
/// </summary>
internal static class MasterDataGate
{
    internal const int RefusedExitCode = 10;

    internal static async Task SetAsync(
        string connectionString,
        MasterDataGateOptions options,
        string? deploymentClassVariable,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (options.DeploymentClass is not ("SYNTHETIC" or "REAL"))
        {
            throw new MasterDataLoadException("MDM001_GATE_DEPLOYMENT_CLASS: use SYNTHETIC or REAL.", RefusedExitCode);
        }

        if (options.Gate007Closed && options.DeploymentClass != "REAL")
        {
            throw new MasterDataLoadException(
                "MDM001_GATE_007_ONLY_FOR_REAL: GATE-007 is recorded only for a REAL database.", RefusedExitCode);
        }

        // Defence in depth: a pilot deployment never marks its database synthetic.
        if (options.DeploymentClass == "SYNTHETIC" &&
            string.Equals(deploymentClassVariable, "PILOT_REAL_PEOPLE", StringComparison.Ordinal))
        {
            throw new MasterDataLoadException(
                $"MDM001_GATE_SYNTHETIC_REFUSED: {SyntheticEnvironmentPolicy.DeploymentClassVariable}=PILOT_REAL_PEOPLE.",
                RefusedExitCode);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // AI-01 §4.1-§4.2: role and tenant context after BEGIN, scoped to this transaction. The PLATFORM
        // organization is the tenant of the audit row.
        await using (var context = new NpgsqlCommand(
            """
            SET LOCAL ROLE paqueteria_migrator;
            SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true),
                   set_config('app.current_user_id', '', true);
            """, connection, transaction))
        {
            context.Parameters.Add(new NpgsqlParameter<Guid[]>("organization_ids", [options.PlatformOrganizationId]));
            await context.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var platform = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM organizations.organizations WHERE id=@id AND organization_type='PLATFORM' AND status='ACTIVE')",
            connection, transaction))
        {
            platform.Parameters.AddWithValue("id", options.PlatformOrganizationId);
            if (await platform.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new MasterDataLoadException(
                    "MDM001_GATE_PLATFORM_ORGANIZATION_REQUIRED: --platform-organization-id must name the ACTIVE PLATFORM organization.",
                    RefusedExitCode);
            }
        }

        // Serialize gate changes and read the current state under the row lock.
        string? previousClass = null;
        bool? previousGate = null;
        await using (var current = new NpgsqlCommand(
            "SELECT deployment_class, gate_007_closed FROM platform.master_data_deployment_gate FOR UPDATE",
            connection, transaction))
        await using (var reader = await current.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                previousClass = reader.GetString(0);
                previousGate = reader.GetBoolean(1);
            }
        }

        // MDM-001 N2: never backwards. A database marked REAL may hold reviewed, real master data; making it
        // SYNTHETIC again would let synthetic files and driver profiles in without GATE-007.
        if (previousClass == "REAL" && options.DeploymentClass == "SYNTHETIC")
        {
            throw new MasterDataLoadException(
                "MDM001_GATE_REAL_TO_SYNTHETIC_REFUSED: a REAL database is never marked SYNTHETIC again.",
                RefusedExitCode);
        }

        await using (var upsert = new NpgsqlCommand(
            """
            INSERT INTO platform.master_data_deployment_gate(singleton,deployment_class,gate_007_closed,updated_at,updated_by)
            VALUES (true,@class,@closed,now(),session_user)
            ON CONFLICT (singleton) DO UPDATE
              SET deployment_class=EXCLUDED.deployment_class, gate_007_closed=EXCLUDED.gate_007_closed,
                  updated_at=EXCLUDED.updated_at, updated_by=EXCLUDED.updated_by;
            INSERT INTO platform.audit_logs(id,org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted,occurred_at)
            VALUES (
              gen_random_uuid(), @platform, NULL, 'MASTER_DATA_GATE_CHANGED', 'MASTER_DATA_DEPLOYMENT_GATE',
              gen_random_uuid(), 'mdm-001-gate',
              jsonb_build_object(
                'from', CASE WHEN @previous_class::text IS NULL THEN NULL
                        ELSE jsonb_build_object('deployment_class', @previous_class::text, 'gate_007_closed', @previous_gate::boolean) END,
                'to', jsonb_build_object('deployment_class', @class::text, 'gate_007_closed', @closed::boolean),
                'operator_ref', encode(sha256(convert_to(@prefix || session_user, 'UTF8')), 'hex')),
              clock_timestamp());
            """, connection, transaction))
        {
            upsert.Parameters.AddWithValue("class", options.DeploymentClass);
            upsert.Parameters.AddWithValue("closed", options.Gate007Closed);
            upsert.Parameters.AddWithValue("platform", options.PlatformOrganizationId);
            upsert.Parameters.Add(new NpgsqlParameter<string?>("previous_class", previousClass) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            upsert.Parameters.Add(new NpgsqlParameter<bool?>("previous_gate", previousGate) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Boolean });
            upsert.Parameters.AddWithValue("prefix", OperatorReferencePrefix);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        output.WriteLine(
            $"MDM001_GATE_SET deployment_class={options.DeploymentClass} gate_007_closed={(options.Gate007Closed ? "true" : "false")} (audited)");
    }

    /// <summary>The domain-separation prefix of <c>operator_ref</c>; the loader function uses the same one.</summary>
    internal const string OperatorReferencePrefix = "paquetenvia.mdm-001.operator:";
}
