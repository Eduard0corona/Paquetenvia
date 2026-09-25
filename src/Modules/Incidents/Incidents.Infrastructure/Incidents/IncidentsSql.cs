using System.Data.Common;
using Incidents.Application.Incidents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Incidents.Infrastructure.Incidents;

internal sealed record AuthorizedOrder(
    Guid OwnerOrganizationId,
    Guid? OperatorOrganizationId,
    string Status);

internal sealed record ResolutionCapability(bool IsActiveDispatcher, bool IsActivePlatformAdmin);

/// <summary>An incident as the resolution reads it, locked for the rest of the transaction.</summary>
internal sealed record LockedIncident(
    Guid Id,
    Guid OrderId,
    string Status,
    string Severity,
    string IncidentType,
    string ReasonCode,
    string NextAction,
    bool CustodyAcquired,
    DateTimeOffset OccurredAt,
    DateTimeOffset SlaDueAt,
    IReadOnlyList<Guid> EvidenceProofIds);

internal static class IncidentsSql
{
    internal static (NpgsqlConnection Connection, NpgsqlTransaction Transaction) Database(
        DbContext context) =>
        ((NpgsqlConnection)context.Database.GetDbConnection(),
         (NpgsqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction());

    /// <summary>
    /// Resolves the order inside the active tenant transaction and answers whether the actor may
    /// open an incident on it. A dispatcher or an MFA-satisfied platform admin of the tenant may;
    /// a driver may only for an order they currently hold an active assignment for. The read is
    /// cross-schema and strictly read-only.
    /// </summary>
    internal static async Task<AuthorizedOrder?> ReadAuthorizedOrderAsync(
        DbContext context,
        Guid orderId,
        Guid actorId,
        Guid organizationId,
        bool mfaSatisfied,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = Database(context);
        await using var command = new NpgsqlCommand(
            """
            SELECT o.owner_org_id,o.operator_org_id,o.status,
              (
                EXISTS (
                  SELECT 1
                  FROM identity.users u
                  JOIN organizations.organization_memberships m ON m.user_id=u.id
                  WHERE u.id=@actor AND u.status='ACTIVE'
                    AND m.organization_id=@organization AND m.status='ACTIVE'
                    AND m.role='DISPATCHER')
                OR (
                  @mfa
                  AND EXISTS (
                    SELECT 1
                    FROM identity.users u
                    JOIN organizations.organization_memberships m ON m.user_id=u.id
                    WHERE u.id=@actor AND u.status='ACTIVE'
                      AND m.organization_id=@organization AND m.status='ACTIVE'
                      AND m.role='PLATFORM_ADMIN'))
                OR EXISTS (
                  SELECT 1
                  FROM identity.users u
                  JOIN organizations.organization_memberships m ON m.user_id=u.id
                  JOIN drivers.driver_profiles d
                    ON d.user_id=u.id AND d.org_id=m.organization_id
                  JOIN dispatch.assignments a
                    ON a.driver_id=d.id AND a.order_id=o.id
                  WHERE u.id=@actor AND u.status='ACTIVE'
                    AND m.organization_id=@organization AND m.status='ACTIVE'
                    AND m.role='DRIVER'
                    AND d.status='ACTIVE'
                    AND a.owner_org_id=o.owner_org_id
                    AND a.operator_org_id IS NOT DISTINCT FROM o.operator_org_id
                    AND (a.owner_org_id=@organization OR a.operator_org_id=@organization)
                    AND a.status IN ('ACCEPTED','ACTIVE'))
              ) AS authorized
            FROM orders.orders o
            WHERE o.id=@order
            """,
            connection,
            transaction);
        command.Parameters.Add(P("actor", NpgsqlDbType.Uuid, actorId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(P("mfa", NpgsqlDbType.Boolean, mfaSatisfied));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!reader.GetBoolean(3))
        {
            throw new IncidentForbiddenException();
        }

        return new AuthorizedOrder(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.GetString(2));
    }

    /// <summary>
    /// Counts how many of the requested proofs are POD-001 evidence of this very order inside the
    /// caller's tenant. Anything else — another order's proof, another tenant's proof, an unknown
    /// id — simply does not count, so the caller fails closed without disclosing which it was.
    /// </summary>
    internal static async Task<int> CountOwnedProofsAsync(
        DbContext context,
        Guid orderId,
        Guid organizationId,
        IReadOnlyList<Guid> proofIds,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = Database(context);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM custody.proofs p
            WHERE p.id = ANY(@proofs)
              AND p.order_id=@order
              AND (p.owner_org_id=@organization OR p.operator_org_id=@organization)
            """,
            connection,
            transaction);
        command.Parameters.Add(P("proofs", NpgsqlDbType.Array | NpgsqlDbType.Uuid, proofIds.ToArray()));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>
    /// Reads the actor's resolution capability in the active organization without touching any
    /// incident, so a caller who may not resolve learns nothing about incidents or replay evidence.
    /// </summary>
    internal static async Task<ResolutionCapability> ReadResolutionCapabilityAsync(
        DbContext context,
        Guid actorId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = Database(context);
        await using var command = new NpgsqlCommand(
            """
            SELECT
              COALESCE(bool_or(m.role='DISPATCHER'),false),
              COALESCE(bool_or(m.role='PLATFORM_ADMIN'),false)
            FROM identity.users u
            JOIN organizations.organization_memberships m ON m.user_id=u.id
            WHERE u.id=@actor AND u.status='ACTIVE'
              AND m.organization_id=@organization AND m.status='ACTIVE'
            """,
            connection,
            transaction);
        command.Parameters.Add(P("actor", NpgsqlDbType.Uuid, actorId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ResolutionCapability(reader.GetBoolean(0), reader.GetBoolean(1))
            : new ResolutionCapability(false, false);
    }

    /// <summary>
    /// Locks the incident for the rest of the transaction. FORCE RLS already hides every other
    /// tenant's incident; the explicit tenant predicate keeps a missing and a foreign incident on
    /// the same plan, so both are the same uniform not-found.
    /// </summary>
    internal static async Task<LockedIncident?> ReadIncidentForUpdateAsync(
        DbContext context,
        Guid incidentId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = Database(context);
        LockedIncident incident;
        await using (var command = new NpgsqlCommand(
            """
            SELECT i.order_id,i.status,i.severity,i.incident_type,i.reason_code,i.next_action,
                   i.custody_acquired,i.occurred_at,i.sla_due_at
            FROM incidents.incidents i
            WHERE i.id=@incident AND (i.owner_org_id=@organization OR i.operator_org_id=@organization)
            FOR UPDATE
            """,
            connection,
            transaction))
        {
            command.Parameters.Add(P("incident", NpgsqlDbType.Uuid, incidentId));
            command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            incident = new LockedIncident(
                incidentId,
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetBoolean(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetFieldValue<DateTimeOffset>(8),
                []);
        }

        await using var evidence = new NpgsqlCommand(
            """
            SELECT e.proof_id
            FROM incidents.incident_evidence e
            WHERE e.incident_id=@incident
            ORDER BY e.proof_id
            """,
            connection,
            transaction);
        evidence.Parameters.Add(P("incident", NpgsqlDbType.Uuid, incidentId));
        var proofIds = new List<Guid>();
        await using (var reader = await evidence.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                proofIds.Add(reader.GetGuid(0));
            }
        }

        return incident with { EvidenceProofIds = proofIds };
    }

    internal static async Task AcquireIdempotencyLockAsync(
        DbContext context,
        Guid organizationId,
        string scope,
        string key,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = Database(context);
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@value,0));",
            connection,
            transaction);
        command.Parameters.Add(P(
            "value",
            NpgsqlDbType.Text,
            $"{organizationId:D}:{scope}:{key}"));
        await command.ExecuteScalarAsync(cancellationToken);
    }

    internal static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type)
    {
        Value = value ?? DBNull.Value,
    };
}
