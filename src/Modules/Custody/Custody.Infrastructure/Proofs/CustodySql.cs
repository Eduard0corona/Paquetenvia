using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Custody.Infrastructure.Proofs;

internal sealed record AuthorizedOrder(
    Guid OwnerOrganizationId,
    Guid? OperatorOrganizationId,
    string Status);

internal static class CustodySql
{
    internal static (NpgsqlConnection Connection, NpgsqlTransaction Transaction) Database(
        DbContext context) =>
        ((NpgsqlConnection)context.Database.GetDbConnection(),
         (NpgsqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction());

    internal static async Task<AuthorizedOrder?> ReadAuthorizedOrderAsync(
        DbContext context,
        Guid orderId,
        Guid actorId,
        Guid organizationId,
        bool mfaSatisfied,
        CancellationToken cancellationToken,
        bool lockOrderForShare = false)
    {
        var (connection, transaction) = Database(context);
        // POD finalization holds FOR SHARE on the order so a concurrent ORD-002 transition
        // (which takes FOR UPDATE) either commits first and is re-read here, or waits for
        // the proof to commit. Read-only callers keep the non-locking snapshot read.
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
                    AND d.driver_type IN ('OWN','EXTERNAL') AND d.status='ACTIVE'
                    AND a.assignment_type=d.driver_type
                    AND a.owner_org_id=o.owner_org_id
                    AND a.operator_org_id IS NOT DISTINCT FROM o.operator_org_id
                    AND (a.owner_org_id=@organization OR a.operator_org_id=@organization)
                    AND a.status IN ('ACCEPTED','ACTIVE'))
              ) AS authorized
            FROM orders.orders o
            WHERE o.id=@order
            """ + (lockOrderForShare ? "\nFOR SHARE OF o" : string.Empty),
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
            throw new Application.ProofUploads.ProofForbiddenException();
        }

        return new AuthorizedOrder(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.GetString(2));
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
