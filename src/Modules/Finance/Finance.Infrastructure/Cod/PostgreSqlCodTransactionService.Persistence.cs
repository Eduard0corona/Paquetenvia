using System.Security.Cryptography;
using System.Text.Json;
using Finance.Application;
using Finance.Application.Cod;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application.Auditing;
using static Finance.Infrastructure.Persistence.FinanceSql;

namespace Finance.Infrastructure.Cod;

public sealed partial class PostgreSqlCodTransactionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private async Task<T?> BeginIdempotencyAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        string scope,
        string key,
        byte[] requestHash,
        int expectedStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        where T : class
    {
        await using (var advisory = Create(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,0));",
            gateway.CommandTimeoutSeconds))
        {
            advisory.Parameters.Add(P("key", NpgsqlDbType.Text, $"{organizationId:D}:{scope}:{key}"));
            await advisory.ExecuteNonQueryAsync(cancellationToken);
        }

        IdempotencyRow? stored;
        await using (var query = Create(
            connection,
            transaction,
            """
            SELECT request_hash,response_status,response_body::text,resource_id
            FROM platform.idempotency_keys
            WHERE owner_org_id=@organization AND scope=@scope AND idempotency_key=@key
            """,
            gateway.CommandTimeoutSeconds))
        {
            query.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
            query.Parameters.Add(P("scope", NpgsqlDbType.Text, scope));
            query.Parameters.Add(P("key", NpgsqlDbType.Text, key));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            stored = await reader.ReadAsync(cancellationToken)
                ? new(
                    reader.GetFieldValue<byte[]>(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3))
                : null;
        }

        if (stored is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(stored.Hash, requestHash))
            {
                throw Conflict(FinanceConflictCode.IdempotencyConflict);
            }

            if (stored.Status != expectedStatus || stored.ResourceId is null ||
                string.IsNullOrWhiteSpace(stored.Body))
            {
                throw Conflict(FinanceConflictCode.InconsistentReplayEvidence);
            }

            try
            {
                return JsonSerializer.Deserialize<T>(stored.Body, JsonOptions)
                    ?? throw Conflict(FinanceConflictCode.InconsistentReplayEvidence);
            }
            catch (JsonException exception)
            {
                throw Conflict(FinanceConflictCode.InconsistentReplayEvidence, exception);
            }
        }

        await using var insert = Create(
            connection,
            transaction,
            """
            INSERT INTO platform.idempotency_keys(
              owner_org_id,scope,idempotency_key,request_hash,response_status,
              response_body,resource_id,created_at,expires_at)
            VALUES (@organization,@scope,@key,@hash,NULL,NULL,NULL,@now,@expires)
            """,
            gateway.CommandTimeoutSeconds);
        insert.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        insert.Parameters.Add(P("scope", NpgsqlDbType.Text, scope));
        insert.Parameters.Add(P("key", NpgsqlDbType.Text, key));
        insert.Parameters.Add(P("hash", NpgsqlDbType.Bytea, requestHash));
        insert.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        insert.Parameters.Add(P(
            "expires",
            NpgsqlDbType.TimestampTz,
            now.AddMinutes(gateway.IdempotencyLifetimeMinutes)));
        RequireOne(await insert.ExecuteNonQueryAsync(cancellationToken), "Idempotency reservation failed.");
        return null;
    }

    private async Task CompleteIdempotencyAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        string scope,
        string key,
        byte[] requestHash,
        int status,
        Guid resourceId,
        T response,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = Create(
            connection,
            transaction,
            """
            UPDATE platform.idempotency_keys
            SET response_status=@status,response_body=@body,resource_id=@resource,expires_at=@expires
            WHERE owner_org_id=@organization AND scope=@scope AND idempotency_key=@key
              AND request_hash=@hash AND response_status IS NULL
              AND response_body IS NULL AND resource_id IS NULL
            """,
            gateway.CommandTimeoutSeconds);
        command.Parameters.Add(P("status", NpgsqlDbType.Integer, status));
        command.Parameters.Add(P("body", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(response, JsonOptions)));
        command.Parameters.Add(P("resource", NpgsqlDbType.Uuid, resourceId));
        command.Parameters.Add(P(
            "expires",
            NpgsqlDbType.TimestampTz,
            now.AddMinutes(gateway.IdempotencyLifetimeMinutes)));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, scope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, key));
        command.Parameters.Add(P("hash", NpgsqlDbType.Bytea, requestHash));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "Idempotency completion failed.");
    }

    private async Task<CodRow?> ReadCodByOrderForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = Create(
            connection,
            transaction,
            """
            SELECT id,order_id,amount_cents,status,recorded_at,reconciled_at
            FROM finance.cod_transactions
            WHERE order_id=@order
              AND (owner_org_id=@organization OR operator_org_id=@organization)
            FOR UPDATE
            """,
            gateway.CommandTimeoutSeconds);
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        return await ReadCodAsync(command, cancellationToken);
    }

    private async Task<CodRow?> ReadCodByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid codTransactionId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = Create(
            connection,
            transaction,
            """
            SELECT id,order_id,amount_cents,status,recorded_at,reconciled_at
            FROM finance.cod_transactions
            WHERE id=@cod
              AND (owner_org_id=@organization OR operator_org_id=@organization)
            """ + (forUpdate ? " FOR UPDATE" : string.Empty),
            gateway.CommandTimeoutSeconds);
        command.Parameters.Add(P("cod", NpgsqlDbType.Uuid, codTransactionId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        return await ReadCodAsync(command, cancellationToken);
    }

    private static async Task<CodRow?> ReadCodAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5))
            : null;
    }

    /// <summary>
    /// The driver credited with the collection is the one holding the order's active assignment, so the
    /// collector is derived from dispatch rather than supplied by the caller.
    /// </summary>
    private async Task<Guid?> ReadCollectingDriverAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = Create(
            connection,
            transaction,
            """
            SELECT driver_id
            FROM dispatch.assignments
            WHERE order_id=@order AND status IN ('ACCEPTED','ACTIVE')
              AND (owner_org_id=@organization OR operator_org_id=@organization)
            ORDER BY created_at DESC,id DESC
            LIMIT 1
            """,
            gateway.CommandTimeoutSeconds);
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        return await command.ExecuteScalarAsync(cancellationToken) is Guid driverId ? driverId : null;
    }

    private async Task InsertCodAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid codId,
        OrderRow order,
        long amountCents,
        string reference,
        Guid? collectedByDriverId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = Create(
            connection,
            transaction,
            """
            INSERT INTO finance.cod_transactions(
              id,order_id,owner_org_id,operator_org_id,amount_cents,status,
              collected_by_driver_id,recorded_at,reconciled_at,reference,created_at)
            VALUES (@id,@order,@owner,@operator,@amount,'RECORDED',@driver,@now,NULL,@reference,@now)
            """,
            gateway.CommandTimeoutSeconds);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, codId));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, order.Id));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
        command.Parameters.Add(P("operator", NpgsqlDbType.Uuid, order.OperatorOrganizationId));
        command.Parameters.Add(P("amount", NpgsqlDbType.Bigint, amountCents));
        command.Parameters.Add(P("driver", NpgsqlDbType.Uuid, collectedByDriverId));
        command.Parameters.Add(P("reference", NpgsqlDbType.Text, reference));
        command.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "COD insert failed.");
    }

    private async Task ReconcileCodAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid codId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = Create(
            connection,
            transaction,
            """
            UPDATE finance.cod_transactions
            SET status='RECONCILED',reconciled_at=@now
            WHERE id=@cod AND status='RECORDED'
            """,
            gateway.CommandTimeoutSeconds);
        command.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        command.Parameters.Add(P("cod", NpgsqlDbType.Uuid, codId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(FinanceConflictCode.ConcurrencyConflict);
        }
    }

    private async Task WriteAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        string action,
        Guid codId,
        string? requestId,
        object payload,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var redacted = auditRedactor.Redact(JsonSerializer.SerializeToElement(payload, JsonOptions));
        await auditWriter.WriteAsync(
            connection,
            transaction,
            new AuditEntry(
                Guid.NewGuid(),
                organizationId,
                actorId,
                action,
                "CodTransaction",
                codId,
                requestId,
                redacted,
                occurredAt),
            cancellationToken);
        await failureInjector.OnStageAsync(FinanceTransactionStage.AuditInserted, cancellationToken);
    }
}
