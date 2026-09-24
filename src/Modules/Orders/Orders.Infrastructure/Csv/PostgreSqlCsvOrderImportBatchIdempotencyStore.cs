using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Csv;
using Orders.Application.Orders;
using Orders.Infrastructure.Persistence;
using Paqueteria.Application;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Orders.Infrastructure.Csv;

/// <summary>
/// Batch-level CSV-001 idempotency over <c>platform.idempotency_keys</c>, under the same
/// conventions ORD-001 uses for a single order: an advisory transaction lock on the key, a
/// reservation row carrying the canonical request hash, and a completion that stores the replayed
/// response. The record is tenant-scoped by the table's primary key and by row level security, so
/// two organizations that choose the same batch key never see each other's batches.
/// </summary>
/// <remarks>
/// Reserving and completing are two short transactions rather than one long one, because the rows
/// in between are created by <see cref="IOrderService"/> on this same scoped connection. A
/// reservation that is never completed — a crashed commit, or the loser of a race — is therefore
/// possible, and is deliberately treated as "run it again": every row carries an ORD-001 key
/// derived from the same batch, so the second run replays the first run's orders and completes
/// with the same response instead of creating anything new.
/// </remarks>
public sealed class PostgreSqlCsvOrderImportBatchIdempotencyStore(
    TenantTransactionContext<OrdersDbContext> transactionContext,
    IOptions<OrdersOptions> options,
    IClock clock) : ICsvOrderImportBatchIdempotencyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<CsvOrderImportCommitResult?> ReserveOrReplayAsync(
        CsvOrderImportBatchIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return ExecuteAsync(
            identity,
            (connection, transaction, token) =>
                ReserveWithinTransactionAsync(connection, transaction, identity, token),
            cancellationToken);
    }

    public Task<CsvOrderImportCommitResult> CompleteAsync(
        CsvOrderImportBatchIdentity identity,
        CsvOrderImportCommitResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(result);
        return ExecuteAsync(
            identity,
            (connection, transaction, token) =>
                CompleteWithinTransactionAsync(connection, transaction, identity, result, token),
            cancellationToken);
    }

    private async Task<CsvOrderImportCommitResult?> ReserveWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CsvOrderImportBatchIdentity identity,
        CancellationToken cancellationToken)
    {
        await AcquireLockAsync(connection, transaction, identity, cancellationToken);
        var requestHash = CsvOrderImportIdempotency.ComputeBatchRequestHash(identity);
        var stored = await FindAsync(connection, transaction, identity, cancellationToken);
        if (stored is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(stored.RequestHash, requestHash))
            {
                throw new CsvOrderImportBatchConflictException();
            }

            return ReadCompletedResponse(stored, identity);
        }

        var reservedAt = clock.UtcNow;
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO platform.idempotency_keys(
              owner_org_id,scope,idempotency_key,request_hash,response_status,response_body,resource_id,created_at,expires_at)
            VALUES (@owner,@scope,@key,@hash,NULL,NULL,NULL,@created,@expires)
            """);
        AddIdentity(command, identity);
        command.Parameters.Add(P("hash", NpgsqlDbType.Bytea, requestHash));
        command.Parameters.Add(P("created", NpgsqlDbType.TimestampTz, reservedAt));
        command.Parameters.Add(P(
            "expires",
            NpgsqlDbType.TimestampTz,
            reservedAt.AddMinutes(options.Value.IdempotencyLifetimeMinutes)));
        RequireOne(
            await command.ExecuteNonQueryAsync(cancellationToken),
            "The CSV batch idempotency reservation was not inserted.");
        return null;
    }

    private async Task<CsvOrderImportCommitResult> CompleteWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CsvOrderImportBatchIdentity identity,
        CsvOrderImportCommitResult result,
        CancellationToken cancellationToken)
    {
        await AcquireLockAsync(connection, transaction, identity, cancellationToken);
        var requestHash = CsvOrderImportIdempotency.ComputeBatchRequestHash(identity);
        var completedAt = clock.UtcNow;
        await using (var command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE platform.idempotency_keys
            SET response_status=@status,response_body=@response,resource_id=@resource,expires_at=@expires
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key AND request_hash=@hash
              AND response_status IS NULL AND response_body IS NULL AND resource_id IS NULL
            """))
        {
            AddIdentity(command, identity);
            command.Parameters.Add(P("hash", NpgsqlDbType.Bytea, requestHash));
            command.Parameters.Add(P(
                "status",
                NpgsqlDbType.Integer,
                CsvOrderImportIdempotency.BatchResponseStatus));
            command.Parameters.Add(P("response", NpgsqlDbType.Jsonb, Serialize(result)));
            command.Parameters.Add(P(
                "resource",
                NpgsqlDbType.Uuid,
                CsvOrderImportIdempotency.DeriveBatchId(identity)));
            command.Parameters.Add(P(
                "expires",
                NpgsqlDbType.TimestampTz,
                completedAt.AddMinutes(options.Value.IdempotencyLifetimeMinutes)));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                return result;
            }
        }

        // The reservation was completed by a concurrent commit of the same batch. Its response is
        // the batch, and every row in it is the row this call just replayed, so answer with it.
        var stored = await FindAsync(connection, transaction, identity, cancellationToken)
            ?? throw new OrderServiceUnavailableException("The CSV batch idempotency record disappeared.");
        if (!CryptographicOperations.FixedTimeEquals(stored.RequestHash, requestHash))
        {
            throw new CsvOrderImportBatchConflictException();
        }

        return ReadCompletedResponse(stored, identity)
            ?? throw new OrderServiceUnavailableException("The CSV batch idempotency record is inconsistent.");
    }

    private async Task<TResult> ExecuteAsync<TResult>(
        CsvOrderImportBatchIdentity identity,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(identity.ActorId, [identity.OrganizationId]),
                (dbContext, token) => operation(
                    (NpgsqlConnection)dbContext.Database.GetDbConnection(),
                    (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction(),
                    token),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or DbUpdateException)
        {
            throw new OrderServiceUnavailableException("The CSV batch idempotency record failed safely.", exception);
        }
    }

    private async Task AcquireLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CsvOrderImportBatchIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key,0));");
        command.Parameters.Add(P(
            "lock_key",
            NpgsqlDbType.Text,
            $"{identity.OrganizationId:D}:{CsvOrderImportIdempotency.BatchScope}:{identity.IdempotencyKey}"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<BatchRecord?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CsvOrderImportBatchIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT request_hash,response_status,response_body::text,resource_id
            FROM platform.idempotency_keys
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key
            """);
        AddIdentity(command, identity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BatchRecord(
            reader.GetFieldValue<byte[]>(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3));
    }

    /// <summary>
    /// Reads the stored response of a reservation, or null when the batch was reserved but never
    /// completed and therefore has to run again.
    /// </summary>
    private static CsvOrderImportCommitResult? ReadCompletedResponse(
        BatchRecord record,
        CsvOrderImportBatchIdentity identity)
    {
        if (record.ResponseStatus is null && record.ResponseBody is null && record.ResourceId is null)
        {
            return null;
        }

        if (record.ResponseStatus != CsvOrderImportIdempotency.BatchResponseStatus ||
            string.IsNullOrWhiteSpace(record.ResponseBody) ||
            record.ResourceId != CsvOrderImportIdempotency.DeriveBatchId(identity))
        {
            throw new OrderServiceUnavailableException("The CSV batch idempotency record is inconsistent.");
        }

        var stored = Deserialize(record.ResponseBody);
        if (!string.Equals(stored.ContentDigest, identity.ContentDigest, StringComparison.Ordinal) ||
            stored.Rows.Count != identity.RowCount)
        {
            throw new OrderServiceUnavailableException("The CSV batch idempotency response is inconsistent.");
        }

        return stored;
    }

    private static string Serialize(CsvOrderImportCommitResult result) =>
        JsonSerializer.Serialize(
            new StoredBatch(
                result.ContentDigest,
                [.. result.Rows.Select(row => new StoredRow(
                    row.RowNumber,
                    row.QuoteId,
                    row.Status,
                    row.OrderId,
                    row.PublicId,
                    row.ErrorCode))]),
            JsonOptions);

    private static CsvOrderImportCommitResult Deserialize(string body)
    {
        StoredBatch? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredBatch>(body, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new OrderServiceUnavailableException("The CSV batch idempotency response is unreadable.", exception);
        }

        return stored is null
            ? throw new OrderServiceUnavailableException("The CSV batch idempotency response is empty.")
            : new CsvOrderImportCommitResult(
                stored.ContentDigest,
                [.. stored.Rows.Select(row => new CsvOrderImportRowOutcome(
                    row.RowNumber,
                    row.QuoteId,
                    row.Status,
                    row.OrderId,
                    row.PublicId,
                    row.ErrorCode))]);
    }

    private static void AddIdentity(NpgsqlCommand command, CsvOrderImportBatchIdentity identity)
    {
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, identity.OrganizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, CsvOrderImportIdempotency.BatchScope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, identity.IdempotencyKey));
    }

    private NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql) => new(sql, connection, transaction)
        {
            CommandTimeout = options.Value.CommandTimeoutSeconds,
        };

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type)
    {
        Value = value ?? DBNull.Value,
    };

    private static void RequireOne(int affected, string message)
    {
        if (affected != 1)
        {
            throw new OrderServiceUnavailableException(message);
        }
    }

    private sealed record BatchRecord(
        byte[] RequestHash,
        int? ResponseStatus,
        string? ResponseBody,
        Guid? ResourceId);

    private sealed record StoredBatch(string ContentDigest, IReadOnlyList<StoredRow> Rows);

    private sealed record StoredRow(
        int RowNumber,
        string QuoteId,
        string Status,
        Guid? OrderId,
        string? PublicId,
        string? ErrorCode);
}

/// <summary>
/// The batch guard of a module whose order provider is switched off. Nothing can be created, so
/// there is no batch to protect and no record to keep; every commit simply reports what the
/// disabled <see cref="IOrderService"/> made of each row.
/// </summary>
public sealed class DisabledCsvOrderImportBatchIdempotencyStore : ICsvOrderImportBatchIdempotencyStore
{
    public Task<CsvOrderImportCommitResult?> ReserveOrReplayAsync(
        CsvOrderImportBatchIdentity identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<CsvOrderImportCommitResult?>(null);
    }

    public Task<CsvOrderImportCommitResult> CompleteAsync(
        CsvOrderImportBatchIdentity identity,
        CsvOrderImportCommitResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }
}
