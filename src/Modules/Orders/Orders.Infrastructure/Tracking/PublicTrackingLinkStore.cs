using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Tracking;
using Paqueteria.Application.Auditing;

namespace Orders.Infrastructure.Tracking;

/// <summary>
/// TRK-002-AUTO-LINK statements on <c>orders.public_tracking_tokens</c>, shared by the get-or-create and revoke
/// service and by the order-creation issuer. Every statement runs on the caller's tenant transaction (RLS scoped to
/// the owner organization) and none returns or writes a plaintext token: rows keep only the SHA-256 of the token,
/// its generation and the key version it was derived with.
/// </summary>
internal static class PublicTrackingLinkStore
{
    internal const int AdvisoryLockNamespace = 0x54524B31; // TRK1
    internal const string IssuedAction = "TRACKING_TOKEN_ISSUED";
    internal const string RevokedAction = "TRACKING_TOKEN_REVOKED";
    internal const string AuditEntityType = "PublicTrackingToken";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal sealed record ActiveLink(Guid Id, int Generation, int? KeyVersion, byte[] TokenHash);

    /// <summary>Serializes every link change of one order, whatever path makes it.</summary>
    internal static async Task AcquireOrderLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(orderId.ToByteArray(), digest);
        var orderKey = BinaryPrimitives.ReadInt32BigEndian(digest);
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(@namespace,@order_key);",
            connection,
            transaction);
        command.Parameters.Add(
            new NpgsqlParameter<int>("namespace", NpgsqlDbType.Integer) { TypedValue = AdvisoryLockNamespace });
        command.Parameters.Add(
            new NpgsqlParameter<int>("order_key", NpgsqlDbType.Integer) { TypedValue = orderKey });
        await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>The internal status of an order the organization owns, locked, or null for the uniform 404.</summary>
    internal static async Task<string?> ReadOwnedOrderStatusForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT status
            FROM orders.orders
            WHERE id=@order_id
              AND owner_org_id=@owner_org_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("order_id", NpgsqlDbType.Uuid) { TypedValue = orderId });
        command.Parameters.Add(
            new NpgsqlParameter<Guid>("owner_org_id", NpgsqlDbType.Uuid) { TypedValue = organizationId });
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>
    /// The first event that took the order to a final public status (the same predicate as the SQL lookup), or null.
    /// </summary>
    internal static async Task<DateTimeOffset?> ReadFirstFinalEventAtAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT min(occurred_at)
            FROM orders.order_events
            WHERE order_id=@order_id
              AND public_event_code IN ('DELIVERED','RETURNED','CANCELLED');
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("order_id", NpgsqlDbType.Uuid) { TypedValue = orderId });
        return await command.ExecuteScalarAsync(cancellationToken) switch
        {
            DateTime value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
            DateTimeOffset value => value.ToUniversalTime(),
            _ => null,
        };
    }

    /// <summary>Every link of the order the public lookup could still accept, newest first.</summary>
    internal static async Task<IReadOnlyList<ActiveLink>> ReadActiveLinksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id,generation,key_version,token_hash
            FROM orders.public_tracking_tokens
            WHERE order_id=@order_id
              AND revoked_at IS NULL
              AND expires_at>@now
            ORDER BY created_at DESC,generation DESC,id;
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("order_id", NpgsqlDbType.Uuid) { TypedValue = orderId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", NpgsqlDbType.TimestampTz) { TypedValue = now });
        var links = new List<ActiveLink>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            links.Add(new ActiveLink(
                reader.GetGuid(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetFieldValue<byte[]>(3)));
        }

        return links;
    }

    /// <summary>Retires every link of the order the public lookup could still accept.</summary>
    internal static async Task<int> RetireActiveLinksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE orders.public_tracking_tokens
            SET revoked_at=@now
            WHERE order_id=@order_id
              AND revoked_at IS NULL
              AND expires_at>@now;
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("order_id", NpgsqlDbType.Uuid) { TypedValue = orderId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", NpgsqlDbType.TimestampTz) { TypedValue = now });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// The next generation of the order: one above every generation it ever had, revoked ones included, so a
    /// revoked link is never derived again.
    /// </summary>
    internal static async Task<int> ReadNextGenerationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT COALESCE(max(generation),0)
            FROM orders.public_tracking_tokens
            WHERE order_id=@order_id;
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("order_id", NpgsqlDbType.Uuid) { TypedValue = orderId });
        var current = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        return checked(current + 1);
    }

    /// <summary>
    /// Inserts a derived link. It has no fixed expiry (<c>expires_at</c> is <c>infinity</c>): the SQL lookup ends it
    /// 24 hours after the order finishes. Only the SHA-256 of the token is written.
    /// </summary>
    internal static async Task InsertDerivedLinkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tokenId,
        Guid orderId,
        Guid ownerOrganizationId,
        int generation,
        int keyVersion,
        byte[] tokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO orders.public_tracking_tokens
                (id,order_id,owner_org_id,token_hash,expires_at,revoked_at,created_at,generation,key_version)
            VALUES
                (@id,@order_id,@owner_org_id,@token_hash,'infinity'::timestamptz,NULL,@created_at,@generation,@key_version);
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = tokenId });
        command.Parameters.Add(new NpgsqlParameter<Guid>("order_id", NpgsqlDbType.Uuid) { TypedValue = orderId });
        command.Parameters.Add(
            new NpgsqlParameter<Guid>("owner_org_id", NpgsqlDbType.Uuid) { TypedValue = ownerOrganizationId });
        command.Parameters.Add(
            new NpgsqlParameter<byte[]>("token_hash", NpgsqlDbType.Bytea) { TypedValue = tokenHash });
        command.Parameters.Add(
            new NpgsqlParameter<DateTimeOffset>("created_at", NpgsqlDbType.TimestampTz) { TypedValue = now });
        command.Parameters.Add(
            new NpgsqlParameter<int>("generation", NpgsqlDbType.Integer) { TypedValue = generation });
        command.Parameters.Add(
            new NpgsqlParameter<int>("key_version", NpgsqlDbType.Integer) { TypedValue = keyVersion });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static Task WriteIssuedAuditAsync(
        IAppendOnlyAuditWriter auditWriter,
        IAuditPayloadRedactor auditRedactor,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        string? requestId,
        Guid tokenId,
        int generation,
        int keyVersion,
        int previousTokensRevokedCount,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
        WriteAuditAsync(
            auditWriter,
            auditRedactor,
            connection,
            transaction,
            actorId,
            organizationId,
            orderId,
            requestId,
            IssuedAction,
            JsonSerializer.SerializeToElement(new
            {
                order_id = orderId,
                token_id = tokenId,
                generation,
                key_version = keyVersion,
                previous_tokens_revoked_count = previousTokensRevokedCount,
                request_id = requestId,
            }, JsonOptions),
            occurredAt,
            cancellationToken);

    internal static Task WriteRevokedAuditAsync(
        IAppendOnlyAuditWriter auditWriter,
        IAuditPayloadRedactor auditRedactor,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        string? requestId,
        int previousTokensRevokedCount,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
        WriteAuditAsync(
            auditWriter,
            auditRedactor,
            connection,
            transaction,
            actorId,
            organizationId,
            orderId,
            requestId,
            RevokedAction,
            JsonSerializer.SerializeToElement(new
            {
                order_id = orderId,
                previous_tokens_revoked_count = previousTokensRevokedCount,
                request_id = requestId,
            }, JsonOptions),
            occurredAt,
            cancellationToken);

    private static Task WriteAuditAsync(
        IAppendOnlyAuditWriter auditWriter,
        IAuditPayloadRedactor auditRedactor,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        string? requestId,
        string action,
        JsonElement payload,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
        auditWriter.WriteAsync(
            connection,
            transaction,
            new AuditEntry(
                Guid.NewGuid(),
                organizationId,
                actorId,
                action,
                AuditEntityType,
                orderId,
                requestId,
                auditRedactor.Redact(payload),
                occurredAt),
            cancellationToken);
}
