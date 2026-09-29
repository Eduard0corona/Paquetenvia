using Custody.Application.ProofUploads;
using Custody.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Custody.Infrastructure.Proofs;

/// <summary>
/// API-INC-LIST-PROOFS-2026-09-29: listOrderProofs. One explicit tenant transaction (set_config
/// after BEGIN, FORCE RLS) settles the capability first, then the order's visibility, then reads
/// proof metadata only. <c>custody.proofs</c> is append-only and this service never writes; it never
/// selects the object key, the capture point or the protected recipient name, and it never reaches
/// object storage, so no binary content, signed URL or personal data can reach the response.
/// </summary>
public sealed class PostgreSqlProofReadService(
    TenantTransactionContext<CustodyDbContext> transactionContext) : IProofReadService
{
    public async Task<ProofPage> ListOrderProofsAsync(
        ListOrderProofsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ProofReadPolicy.IsValidShape(query))
        {
            throw new ProofConflictException("INVALID_REQUEST");
        }

        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(query.ActorId, [query.OrganizationId]),
                async (dbContext, token) =>
                {
                    var (connection, transaction) = CustodySql.Database(dbContext);
                    if (!await MayReadAsync(connection, transaction, query, token))
                    {
                        throw new ProofForbiddenException();
                    }

                    // A missing order and another tenant's order are the same uniform not-found.
                    await using (var order = new NpgsqlCommand(
                                     """
                                     SELECT 1 FROM orders.orders o
                                     WHERE o.id=@order
                                       AND (o.owner_org_id=@organization OR o.operator_org_id=@organization)
                                     """,
                                     connection,
                                     transaction))
                    {
                        order.Parameters.Add(CustodySql.P("order", NpgsqlDbType.Uuid, query.OrderId));
                        order.Parameters.Add(CustodySql.P("organization", NpgsqlDbType.Uuid, query.OrganizationId));
                        if (await order.ExecuteScalarAsync(token) is null)
                        {
                            throw new ProofNotFoundException();
                        }
                    }

                    await using var command = new NpgsqlCommand(
                        """
                        SELECT p.id,p.proof_type,p.sha256,p.captured_at,p.created_at
                        FROM custody.proofs p
                        WHERE p.order_id=@order
                          AND (p.owner_org_id=@organization OR p.operator_org_id=@organization)
                          AND (@has_cursor=false OR (p.created_at,p.id) < (@cursor_created_at,@cursor_id))
                        ORDER BY p.created_at DESC,p.id DESC
                        LIMIT @take
                        """,
                        connection,
                        transaction);
                    command.Parameters.Add(CustodySql.P("order", NpgsqlDbType.Uuid, query.OrderId));
                    command.Parameters.Add(CustodySql.P("organization", NpgsqlDbType.Uuid, query.OrganizationId));
                    command.Parameters.Add(CustodySql.P("has_cursor", NpgsqlDbType.Boolean, query.Cursor is not null));
                    command.Parameters.Add(CustodySql.P(
                        "cursor_created_at",
                        NpgsqlDbType.TimestampTz,
                        query.Cursor?.CreatedAt ?? DateTimeOffset.UnixEpoch));
                    command.Parameters.Add(CustodySql.P("cursor_id", NpgsqlDbType.Uuid, query.Cursor?.Id ?? Guid.Empty));
                    command.Parameters.Add(CustodySql.P("take", NpgsqlDbType.Integer, ProofReadPolicy.PageSize + 1));

                    var rows = new List<(ProofSummary Proof, DateTimeOffset CreatedAt)>();
                    await using (var reader = await command.ExecuteReaderAsync(token))
                    {
                        while (await reader.ReadAsync(token))
                        {
                            var proofType = reader.GetString(1);
                            var sha256 = reader.GetFieldValue<byte[]>(2);
                            if (!ProofRequestPolicy.IsSupportedProofType(proofType) || sha256.Length != 32)
                            {
                                throw new ProofStorageUnavailableException();
                            }

                            rows.Add((
                                new ProofSummary(
                                    reader.GetGuid(0),
                                    proofType,
                                    Convert.ToHexStringLower(sha256),
                                    reader.GetFieldValue<DateTimeOffset>(3)),
                                reader.GetFieldValue<DateTimeOffset>(4)));
                        }
                    }

                    var hasMore = rows.Count > ProofReadPolicy.PageSize;
                    if (hasMore)
                    {
                        rows.RemoveAt(rows.Count - 1);
                    }

                    var next = hasMore
                        ? ProofCursorCodec.Encode(new ProofCursor(query.OrderId, rows[^1].CreatedAt, rows[^1].Proof.Id))
                        : null;
                    return new ProofPage(rows.Select(row => row.Proof).ToArray(), next);
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProofUploadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException)
        {
            throw new ProofStorageUnavailableException();
        }
    }

    /// <summary>
    /// The capability from the persisted, active memberships of the active user in the selected
    /// organization, read without touching any order or proof.
    /// </summary>
    private static async Task<bool> MayReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ListOrderProofsQuery query,
        CancellationToken cancellationToken)
    {
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
        command.Parameters.Add(CustodySql.P("actor", NpgsqlDbType.Uuid, query.ActorId));
        command.Parameters.Add(CustodySql.P("organization", NpgsqlDbType.Uuid, query.OrganizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) &&
            ProofReadPolicy.MayRead(reader.GetBoolean(0), reader.GetBoolean(1), query.MfaSatisfied);
    }
}
