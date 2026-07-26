using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Custody.Application.ProofUploads;
using Custody.Domain;
using Custody.Infrastructure.Persistence;
using Custody.Infrastructure.ProofStorage;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Custody.Infrastructure.Proofs;

public sealed class PostgreSqlProofFinalizationService(
    TenantTransactionContext<CustodyDbContext> transactionContext,
    IProofObjectStorage storage,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor redactor,
    IClock clock,
    IProofTelemetry telemetry) : IProofFinalizationService
{
    internal const string IdempotencyScope = "POD-001:FINALIZE_PROOF";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ProofResult> FinalizeAsync(
        FinalizeProofCommand command,
        CancellationToken cancellationToken)
    {
        if (!ProofCapturedAtPolicy.TryNormalizeUtc(command.CapturedAt, out var capturedAt))
        {
            throw new ProofConflictException("INVALID_REQUEST");
        }

        command = command with { CapturedAt = capturedAt };
        Validate(command);
        _ = ProofContract.TryParse(command.ProofType, out var proofType);
        var requestHash = ComputeHash(command);
        var earlyReplay = await ReadAuthorizedReplayAsync(
            command,
            requestHash,
            cancellationToken);
        if (earlyReplay is not null)
        {
            return earlyReplay;
        }

        if (!storage.IsEnabled)
        {
            throw new ProofStorageUnavailableException();
        }

        var session = await ReadSessionAsync(command, cancellationToken)
            ?? throw new ProofConflictException("UPLOAD_SESSION_NOT_READY");
        var objectKey = ProofObjectKeys.Final(
            session.OwnerOrganizationId,
            command.OrderId,
            command.UploadSessionId);
        var storedObject = await storage.HeadAsync(objectKey, cancellationToken)
            ?? throw new ProofConflictException("PROOF_OBJECT_NOT_READY");
        ValidateTrustedObject(command, session, storedObject);

        var result = await transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
            async (dbContext, token) =>
            {
                await CustodySql.AcquireIdempotencyLockAsync(
                    dbContext,
                    command.OrganizationId,
                    IdempotencyScope,
                    command.IdempotencyKey,
                    token);
                var replay = await ReadReplayAsync(dbContext, command, requestHash, token);
                if (replay is not null)
                {
                    return replay;
                }

                var order = await CustodySql.ReadAuthorizedOrderAsync(
                    dbContext,
                    command.OrderId,
                    command.ActorId,
                    command.OrganizationId,
                    command.MfaSatisfied,
                    token) ?? throw new ProofNotFoundException();
                if (!ProofContract.IsAllowedOrderState(proofType, order.Status))
                {
                    throw new ProofConflictException("ORDER_STATE_NOT_ALLOWED");
                }

                var lockedSession = await ReadLockedReadySessionAsync(
                    dbContext,
                    command,
                    token);
                ValidateTrustedObject(command, lockedSession, storedObject);
                var now = clock.UtcNow;
                var proofId = Guid.NewGuid();
                var result = new ProofResult(
                    proofId,
                    command.OrderId,
                    command.UploadSessionId,
                    command.ProofType,
                    storedObject.ContentType,
                    storedObject.SizeBytes,
                    storedObject.Metadata[S3CompatibleProofObjectStorage.Sha256Metadata],
                    command.CapturedAt,
                    command.Latitude,
                    command.Longitude,
                    now);
                await InsertReservationAsync(dbContext, command, requestHash, now, token);
                await InsertProofAsync(dbContext, command, order, objectKey, storedObject, result, token);
                await ConsumeSessionAsync(dbContext, command.UploadSessionId, now, token);
                await WriteAuditAsync(dbContext, command, result, token);
                await CompleteReservationAsync(dbContext, command, requestHash, result, token);
                return result;
            },
            cancellationToken);
        telemetry.FinalizationCompleted(command.ProofType);
        return result;
    }

    private Task<ProofResult?> ReadAuthorizedReplayAsync(
        FinalizeProofCommand command,
        byte[] requestHash,
        CancellationToken cancellationToken) =>
        transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
            async (dbContext, token) =>
            {
                _ = await CustodySql.ReadAuthorizedOrderAsync(
                    dbContext,
                    command.OrderId,
                    command.ActorId,
                    command.OrganizationId,
                    command.MfaSatisfied,
                    token) ?? throw new ProofNotFoundException();
                await CustodySql.AcquireIdempotencyLockAsync(
                    dbContext,
                    command.OrganizationId,
                    IdempotencyScope,
                    command.IdempotencyKey,
                    token);
                return await ReadReplayAsync(dbContext, command, requestHash, token);
            },
            cancellationToken);

    private Task<SessionEvidence?> ReadSessionAsync(
        FinalizeProofCommand command,
        CancellationToken cancellationToken) =>
        transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
            async (dbContext, token) =>
            {
                _ = await CustodySql.ReadAuthorizedOrderAsync(
                    dbContext,
                    command.OrderId,
                    command.ActorId,
                    command.OrganizationId,
                    command.MfaSatisfied,
                    token) ?? throw new ProofNotFoundException();
                var (connection, transaction) = CustodySql.Database(dbContext);
                await using var sql = new NpgsqlCommand(
                    """
                    SELECT owner_org_id,operator_org_id,expected_content_type,maximum_bytes,status,expires_at
                    FROM custody.proof_upload_sessions
                    WHERE id=@session AND order_id=@order
                    """,
                    connection,
                    transaction);
                sql.Parameters.Add(CustodySql.P("session", NpgsqlDbType.Uuid, command.UploadSessionId));
                sql.Parameters.Add(CustodySql.P("order", NpgsqlDbType.Uuid, command.OrderId));
                await using var reader = await sql.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    throw new ProofNotFoundException();
                }

                var status = reader.GetString(4);
                var expiresAt = reader.GetFieldValue<DateTimeOffset>(5);
                if (expiresAt <= clock.UtcNow &&
                    status is "CREATED" or "UPLOADED" or "VALIDATING" or "READY")
                {
                    await reader.DisposeAsync();
                    await using var expire = new NpgsqlCommand(
                        """
                        UPDATE custody.proof_upload_sessions
                        SET status='EXPIRED',updated_at=@now
                        WHERE id=@session AND status=@status AND expires_at<=@now
                        """,
                        connection,
                        transaction);
                    expire.Parameters.Add(CustodySql.P("now", NpgsqlDbType.TimestampTz, clock.UtcNow));
                    expire.Parameters.Add(CustodySql.P("session", NpgsqlDbType.Uuid, command.UploadSessionId));
                    expire.Parameters.Add(CustodySql.P("status", NpgsqlDbType.Text, status));
                    await expire.ExecuteNonQueryAsync(token);
                    return null;
                }

                if (status != "READY")
                {
                    return null;
                }

                return new SessionEvidence(
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    expiresAt);
            },
            cancellationToken);

    private static async Task<SessionEvidence> ReadLockedReadySessionAsync(
        CustodyDbContext context,
        FinalizeProofCommand command,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = CustodySql.Database(context);
        await using var sql = new NpgsqlCommand(
            """
            SELECT owner_org_id,operator_org_id,expected_content_type,maximum_bytes,expires_at
            FROM custody.proof_upload_sessions
            WHERE id=@session AND order_id=@order AND status='READY' AND expires_at>now()
            FOR UPDATE
            """,
            connection,
            transaction);
        sql.Parameters.Add(CustodySql.P("session", NpgsqlDbType.Uuid, command.UploadSessionId));
        sql.Parameters.Add(CustodySql.P("order", NpgsqlDbType.Uuid, command.OrderId));
        await using var reader = await sql.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ProofConflictException("UPLOAD_SESSION_NOT_READY");
        }

        return new SessionEvidence(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    private void Validate(FinalizeProofCommand command)
    {
        if (command.ActorId == Guid.Empty ||
            command.OrganizationId == Guid.Empty ||
            command.OrderId == Guid.Empty ||
            command.UploadSessionId == Guid.Empty ||
            !ProofContract.TryParse(command.ProofType, out _) ||
            !ProofSha256.FixedTimeEquals(command.Sha256, command.Sha256) ||
            command.CapturedAt > clock.UtcNow.AddMinutes(5) ||
            !ProofLocationPolicy.IsValid(command.Latitude, command.Longitude))
        {
            throw new ProofConflictException("INVALID_REQUEST");
        }

        if (command.RecipientName is not null)
        {
            throw new ProofConflictException("PII_PROTECTION_UNAVAILABLE");
        }
    }

    private static void ValidateTrustedObject(
        FinalizeProofCommand command,
        SessionEvidence session,
        ProofObjectDescriptor storedObject)
    {
        if (session.ExpiresAt <= DateTimeOffset.UtcNow ||
            storedObject.ContentType != session.ExpectedContentType ||
            storedObject.SizeBytes != session.MaximumBytes ||
            !MetadataEquals(storedObject, S3CompatibleProofObjectStorage.SessionIdMetadata, command.UploadSessionId.ToString("D")) ||
            !MetadataEquals(storedObject, S3CompatibleProofObjectStorage.OrderIdMetadata, command.OrderId.ToString("D")) ||
            !MetadataEquals(storedObject, S3CompatibleProofObjectStorage.OwnerOrganizationIdMetadata, session.OwnerOrganizationId.ToString("D")) ||
            !MetadataEquals(storedObject, S3CompatibleProofObjectStorage.ProofTypeMetadata, command.ProofType) ||
            !storedObject.Metadata.TryGetValue(S3CompatibleProofObjectStorage.Sha256Metadata, out var sha) ||
            !ProofSha256.TryParseHex(sha, out var storedSha) ||
            !ProofSha256.FixedTimeEquals(storedSha, command.Sha256) ||
            !MetadataEquals(
                storedObject,
                S3CompatibleProofObjectStorage.SizeBytesMetadata,
                storedObject.SizeBytes.ToString(CultureInfo.InvariantCulture)))
        {
            throw new ProofConflictException("PROOF_OBJECT_MISMATCH");
        }
    }

    private static bool MetadataEquals(ProofObjectDescriptor descriptor, string key, string expected) =>
        descriptor.Metadata.TryGetValue(key, out var actual) &&
        string.Equals(actual, expected, StringComparison.Ordinal);

    private static byte[] ComputeHash(FinalizeProofCommand command) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '\n',
            command.OrderId,
            command.UploadSessionId,
            command.ProofType,
            Convert.ToHexString(command.Sha256),
            command.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
            command.Latitude?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
            command.Longitude?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
            command.RecipientName ?? string.Empty)));

    private static async Task<ProofResult?> ReadReplayAsync(
        CustodyDbContext context,
        FinalizeProofCommand command,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = CustodySql.Database(context);
        await using var sql = new NpgsqlCommand(
            """
            SELECT request_hash,response_status,response_body::text,resource_id
            FROM platform.idempotency_keys
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key
            """,
            connection,
            transaction);
        sql.Parameters.Add(CustodySql.P("owner", NpgsqlDbType.Uuid, command.OrganizationId));
        sql.Parameters.Add(CustodySql.P("scope", NpgsqlDbType.Text, IdempotencyScope));
        sql.Parameters.Add(CustodySql.P("key", NpgsqlDbType.Text, command.IdempotencyKey));
        await using var reader = await sql.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte[]>(0), requestHash))
        {
            throw new ProofConflictException("IDEMPOTENCY_CONFLICT");
        }

        if (reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3))
        {
            throw new ProofConflictException("IDEMPOTENCY_IN_PROGRESS");
        }

        var result = JsonSerializer.Deserialize<ProofResult>(reader.GetString(2), JsonOptions)
            ?? throw new ProofConflictException("IDEMPOTENCY_CORRUPT");
        return result.Id == reader.GetGuid(3) ? result : throw new ProofConflictException("IDEMPOTENCY_CORRUPT");
    }

    private static async Task InsertReservationAsync(
        CustodyDbContext context,
        FinalizeProofCommand command,
        byte[] requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = CustodySql.Database(context);
        await using var sql = new NpgsqlCommand(
            """
            INSERT INTO platform.idempotency_keys(
              owner_org_id,scope,idempotency_key,request_hash,response_status,response_body,
              resource_id,created_at,expires_at)
            VALUES (@owner,@scope,@key,@hash,NULL,NULL,NULL,@now,@expires)
            """,
            connection,
            transaction);
        sql.Parameters.Add(CustodySql.P("owner", NpgsqlDbType.Uuid, command.OrganizationId));
        sql.Parameters.Add(CustodySql.P("scope", NpgsqlDbType.Text, IdempotencyScope));
        sql.Parameters.Add(CustodySql.P("key", NpgsqlDbType.Text, command.IdempotencyKey));
        sql.Parameters.Add(CustodySql.P("hash", NpgsqlDbType.Bytea, requestHash));
        sql.Parameters.Add(CustodySql.P("now", NpgsqlDbType.TimestampTz, now));
        sql.Parameters.Add(CustodySql.P("expires", NpgsqlDbType.TimestampTz, now.AddDays(1)));
        await sql.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertProofAsync(
        CustodyDbContext context,
        FinalizeProofCommand command,
        AuthorizedOrder order,
        string objectKey,
        ProofObjectDescriptor storedObject,
        ProofResult result,
        CancellationToken cancellationToken)
    {
        _ = ProofSha256.TryParseHex(result.Sha256, out var sha256);
        var (connection, transaction) = CustodySql.Database(context);
        await using var sql = new NpgsqlCommand(
            """
            INSERT INTO custody.proofs(
              id,order_id,owner_org_id,operator_org_id,upload_session_id,proof_type,object_key,
              sha256,content_type,size_bytes,recipient_name_ciphertext,pii_key_version,
              captured_at,captured_point,created_by,created_at)
            VALUES (
              @id,@order,@owner,@operator,@session,@proof_type,@object_key,@sha256,@content_type,
              @size,NULL,NULL,@captured_at,
              CASE WHEN @longitude IS NULL THEN NULL
                   ELSE ST_SetSRID(ST_MakePoint(@longitude,@latitude),4326) END,
              @actor,@created_at)
            """,
            connection,
            transaction);
        sql.Parameters.Add(CustodySql.P("id", NpgsqlDbType.Uuid, result.Id));
        sql.Parameters.Add(CustodySql.P("order", NpgsqlDbType.Uuid, command.OrderId));
        sql.Parameters.Add(CustodySql.P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
        sql.Parameters.Add(CustodySql.P("operator", NpgsqlDbType.Uuid, order.OperatorOrganizationId));
        sql.Parameters.Add(CustodySql.P("session", NpgsqlDbType.Uuid, command.UploadSessionId));
        sql.Parameters.Add(CustodySql.P("proof_type", NpgsqlDbType.Text, result.ProofType));
        sql.Parameters.Add(CustodySql.P("object_key", NpgsqlDbType.Text, objectKey));
        sql.Parameters.Add(CustodySql.P("sha256", NpgsqlDbType.Bytea, sha256));
        sql.Parameters.Add(CustodySql.P("content_type", NpgsqlDbType.Text, storedObject.ContentType));
        sql.Parameters.Add(CustodySql.P("size", NpgsqlDbType.Bigint, storedObject.SizeBytes));
        sql.Parameters.Add(CustodySql.P("captured_at", NpgsqlDbType.TimestampTz, command.CapturedAt));
        sql.Parameters.Add(CustodySql.P("longitude", NpgsqlDbType.Double, command.Longitude));
        sql.Parameters.Add(CustodySql.P("latitude", NpgsqlDbType.Double, command.Latitude));
        sql.Parameters.Add(CustodySql.P("actor", NpgsqlDbType.Uuid, command.ActorId));
        sql.Parameters.Add(CustodySql.P("created_at", NpgsqlDbType.TimestampTz, result.CreatedAt));
        await sql.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ConsumeSessionAsync(
        CustodyDbContext context,
        Guid sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = CustodySql.Database(context);
        await using var sql = new NpgsqlCommand(
            """
            UPDATE custody.proof_upload_sessions
            SET status='CONSUMED',updated_at=@now
            WHERE id=@session AND status='READY'
            """,
            connection,
            transaction);
        sql.Parameters.Add(CustodySql.P("session", NpgsqlDbType.Uuid, sessionId));
        sql.Parameters.Add(CustodySql.P("now", NpgsqlDbType.TimestampTz, now));
        if (await sql.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new ProofConflictException("UPLOAD_SESSION_NOT_READY");
        }
    }

    private async Task WriteAuditAsync(
        CustodyDbContext context,
        FinalizeProofCommand command,
        ProofResult result,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            order_id = result.OrderId,
            upload_session_id = result.UploadSessionId,
            proof_type = result.ProofType,
            size_bytes = result.SizeBytes,
        }));
        var (connection, transaction) = CustodySql.Database(context);
        await auditWriter.WriteAsync(
            connection,
            transaction,
            new AuditEntry(
                Guid.NewGuid(),
                command.OrganizationId,
                command.ActorId,
                "custody.proof.finalized",
                "proof",
                result.Id,
                command.RequestId,
                redactor.Redact(document.RootElement),
                result.CreatedAt),
            cancellationToken);
    }

    private static async Task CompleteReservationAsync(
        CustodyDbContext context,
        FinalizeProofCommand command,
        byte[] requestHash,
        ProofResult result,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = CustodySql.Database(context);
        await using var sql = new NpgsqlCommand(
            """
            UPDATE platform.idempotency_keys
            SET response_status=201,response_body=@response,resource_id=@resource
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key
              AND request_hash=@hash AND response_status IS NULL
            """,
            connection,
            transaction);
        sql.Parameters.Add(CustodySql.P("response", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(result, JsonOptions)));
        sql.Parameters.Add(CustodySql.P("resource", NpgsqlDbType.Uuid, result.Id));
        sql.Parameters.Add(CustodySql.P("owner", NpgsqlDbType.Uuid, command.OrganizationId));
        sql.Parameters.Add(CustodySql.P("scope", NpgsqlDbType.Text, IdempotencyScope));
        sql.Parameters.Add(CustodySql.P("key", NpgsqlDbType.Text, command.IdempotencyKey));
        sql.Parameters.Add(CustodySql.P("hash", NpgsqlDbType.Bytea, requestHash));
        if (await sql.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new ProofConflictException("IDEMPOTENCY_CONFLICT");
        }
    }

    private sealed record SessionEvidence(
        Guid OwnerOrganizationId,
        Guid? OperatorOrganizationId,
        string ExpectedContentType,
        long MaximumBytes,
        DateTimeOffset ExpiresAt);
}
