using Npgsql;
using Paqueteria.Application.Security;

internal sealed record MasterDataGateOptions(string DeploymentClass, bool Gate007Closed);

/// <summary>
/// MDM-001 B1 / GATE-007: writes the single row of <c>platform.master_data_deployment_gate</c> as
/// <c>paqueteria_migrator</c>, from the deployment (migration) connection only. The loader function reads it
/// and, whatever the operator's machine says, takes only SYNTHETIC files in a SYNTHETIC database, never takes
/// them in a REAL one, and loads driver profiles only in a SYNTHETIC database or once GATE-007 is recorded as
/// closed. The operator login cannot write it: it is not a member of the migrator.
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
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_migrator", connection, transaction))
        {
            await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var upsert = new NpgsqlCommand(
            """
            INSERT INTO platform.master_data_deployment_gate(singleton,deployment_class,gate_007_closed,updated_at,updated_by)
            VALUES (true,@class,@closed,now(),session_user)
            ON CONFLICT (singleton) DO UPDATE
              SET deployment_class=EXCLUDED.deployment_class, gate_007_closed=EXCLUDED.gate_007_closed,
                  updated_at=EXCLUDED.updated_at, updated_by=EXCLUDED.updated_by
            """, connection, transaction))
        {
            upsert.Parameters.AddWithValue("class", options.DeploymentClass);
            upsert.Parameters.AddWithValue("closed", options.Gate007Closed);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        output.WriteLine(
            $"MDM001_GATE_SET deployment_class={options.DeploymentClass} gate_007_closed={(options.Gate007Closed ? "true" : "false")}");
    }
}
