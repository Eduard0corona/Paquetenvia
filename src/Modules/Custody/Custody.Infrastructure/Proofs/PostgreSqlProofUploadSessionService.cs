using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Custody.Application.ProofUploads;
using Custody.Domain;
using Custody.Infrastructure.Persistence;
using Custody.Infrastructure.ProofStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Custody.Infrastructure.Proofs;

public sealed class PostgreSqlProofUploadSessionService(
    TenantTransactionContext<CustodyDbContext> transactionContext,
    IProofObjectStorage storage,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor redactor,
    IOptions<ProofStorageOptions> options,
    IClock clock,
    IProofTelemetry telemetry,
    IProofThreatScanner threatScanner) : IProofUploadSessionService
{
    internal const string IdempotencyScope = "POD-001:CREATE_PROOF_UPLOAD_SESSION";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ProofUploadSessionResult> CreateAsync(
        CreateProofUploadSessionCommand command,
        CancellationToken cancellationToken)
    {
        Validate(command);
        _ = ProofContract.TryParse(command.ProofType, out var proofType);
        if (command.SizeBytes > options.Value.MaximumBytes)
        {
            throw new ProofConflictException("INVALID_REQUEST");
        }

        if (proofType == ProofType.DeliveryCode &&
            command.SizeBytes > options.Value.MaximumTextBytes)
        {
            throw new ProofConflictException("INVALID_REQUEST");
        }

        var requestHash = ComputeHash(
            command.OrderId,
            command.ProofType,
            command.ContentType,
            command.SizeBytes,
            command.Sha256 is null ? string.Empty : Convert.ToHexString(command.Sha256));
        var result = await transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
            async (dbContext, token) =>
            {
                var order = await CustodySql.ReadAuthorizedOrderAsync(
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
                var replay = await ReadReplayAsync(
                    dbContext,
                    command,
                    order.OwnerOrganizationId,
                    requestHash,
                    token);
                if (replay is not null)
                {
                    return replay;
                }

                if (!storage.IsEnabled || !threatScanner.IsEnabled)
                {
                    throw new ProofStorageUnavailableException();
                }

                if (!ProofContract.IsAllowedOrderState(proofType, order.Status))
                {
                    throw new ProofConflictException("ORDER_STATE_NOT_ALLOWED");
                }

                var now = clock.UtcNow;
                var expiresAt = now.AddMinutes(options.Value.SessionLifetimeMinutes);
                var sessionId = Guid.NewGuid();
                var grant = await storage.CreateUploadGrantAsync(
                    order.OwnerOrganizationId,
                    command.OrderId,
                    sessionId,
                    command.ActorId,
                    proofType,
                    command.ContentType,
                    command.SizeBytes,
                    command.Sha256,
                    now.AddMinutes(options.Value.UploadUrlLifetimeMinutes),
                    token);
                var result = new ProofUploadSessionResult(
                    sessionId,
                    command.OrderId,
                    grant.ObjectKey,
                    grant.Url,
                    grant.RequiredHeaders,
                    expiresAt,
                    "CREATED");
                await InsertReservationAsync(dbContext, command, requestHash, now, expiresAt, token);
                await InsertSessionAsync(dbContext, command, order, result, now, token);
                await WriteAuditAsync(dbContext, command, sessionId, now, token);
                await CompleteReservationAsync(
                    dbContext,
                    command,
                    requestHash,
                    result,
                    expiresAt,
                    token);
                return result;
            },
            cancellationToken);
        telemetry.SessionCompleted(command.ProofType);
        return result;
    }

    private static void Validate(CreateProofUploadSessionCommand command)
    {
        if (command.ActorId == Guid.Empty ||
            command.OrganizationId == Guid.Empty ||
            command.OrderId == Guid.Empty ||
            command.SizeBytes <= 0 ||
            !ProofContract.TryParse(command.ProofType, out var proofType) ||
            !ProofContentPolicy.IsAllowed(proofType, command.ContentType) ||
            (command.Sha256 is not null &&
             !ProofSha256.FixedTimeEquals(command.Sha256, command.Sha256)))
        {
            throw new ProofConflictException("INVALID_REQUEST");
        }
    }

    private static byte[] ComputeHash(params object[] values) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values)));

    private async Task<ProofUploadSessionResult?> ReadReplayAsync(
        CustodyDbContext context,
        CreateProofUploadSessionCommand create,
        Guid ownerOrganizationId,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = CustodySql.Database(context);
        await using var command = new NpgsqlCommand(
            """
            SELECT request_hash,response_status,response_body::text,resource_id
            FROM platform.idempotency_keys
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key
            """,
            connection,
            transaction);
        command.Parameters.Add(CustodySql.P("owner", NpgsqlDbType.Uuid, create.OrganizationId));
        command.Parameters.Add(CustodySql.P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(CustodySql.P("key", NpgsqlDbType.Text, create.IdempotencyKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var storedHash = reader.GetFieldValue<byte[]>(0);
        if (!CryptographicOperations.FixedTimeEquals(storedHash, requestHash))
        {
            throw new ProofConflictException("IDEMPOTENCY_CONFLICT");
        }

        if (reader.IsDBNull(1) ||
            reader.GetInt32(1) != 201 ||
            reader.IsDBNull(2) ||
            reader.IsDBNull(3))
        {
            throw CorruptReplay();
        }

        var responseBody = reader.GetString(2);
        var resourceId = reader.GetGuid(3);
        await reader.DisposeAsync();
        ProofUploadSessionResult result;
        try
        {
            using var responseDocument = JsonDocument.Parse(responseBody);
            if (!HasExactResponseShape(responseDocument.RootElement))
            {
                throw CorruptReplay();
            }

            result = JsonSerializer.Deserialize<ProofUploadSessionResult>(responseBody, JsonOptions)
                ?? throw CorruptReplay();
        }
        catch (JsonException)
        {
            throw CorruptReplay();
        }

        if (result.Id == Guid.Empty ||
            result.Id != resourceId ||
            result.OrderId != create.OrderId ||
            !string.Equals(
                result.ObjectKey,
                ProofObjectKeys.Quarantine(ownerOrganizationId, create.OrderId, result.Id),
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(result.UploadUrl) ||
            !Uri.TryCreate(result.UploadUrl, UriKind.Absolute, out _) ||
            result.RequiredHeaders is null ||
            result.RequiredHeaders.Count == 0 ||
            result.RequiredHeaders.Any(header =>
                string.IsNullOrWhiteSpace(header.Key) ||
                string.IsNullOrWhiteSpace(header.Value)) ||
            result.ExpiresAt == default ||
            !string.Equals(result.Status, "CREATED", StringComparison.Ordinal))
        {
            throw CorruptReplay();
        }

        await using var sessionCommand = new NpgsqlCommand(
            """
            SELECT id,order_id,owner_org_id,object_key_quarantine,
                   expected_content_type,maximum_bytes,requested_by
            FROM custody.proof_upload_sessions
            WHERE id=@session
            """,
            connection,
            transaction);
        sessionCommand.Parameters.Add(CustodySql.P("session", NpgsqlDbType.Uuid, result.Id));
        await using var sessionReader = await sessionCommand.ExecuteReaderAsync(cancellationToken);
        if (!await sessionReader.ReadAsync(cancellationToken) ||
            sessionReader.GetGuid(0) != result.Id ||
            sessionReader.GetGuid(1) != create.OrderId ||
            sessionReader.GetGuid(2) != ownerOrganizationId ||
            !string.Equals(sessionReader.GetString(3), result.ObjectKey, StringComparison.Ordinal) ||
            !string.Equals(sessionReader.GetString(4), create.ContentType, StringComparison.Ordinal) ||
            sessionReader.GetInt64(5) != create.SizeBytes ||
            !HasExactRequiredHeaders(
                result.RequiredHeaders,
                create,
                ownerOrganizationId,
                result.Id,
                sessionReader.GetGuid(6)) ||
            await sessionReader.ReadAsync(cancellationToken))
        {
            throw CorruptReplay();
        }

        return result;
    }

    private static ProofConflictException CorruptReplay() =>
        new("IDEMPOTENCY_CORRUPT");

    private static bool HasExactResponseShape(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "id",
            "orderId",
            "objectKey",
            "uploadUrl",
            "requiredHeaders",
            "expiresAt",
            "status",
        };
        var properties = response.EnumerateObject().Select(property => property.Name).ToArray();
        return properties.Length == expected.Count &&
            properties.All(expected.Contains);
    }

    private bool HasExactRequiredHeaders(
        IReadOnlyDictionary<string, string> headers,
        CreateProofUploadSessionCommand create,
        Guid ownerOrganizationId,
        Guid sessionId,
        Guid requestedBy)
    {
        // ADP-001: a provider with its own header shape (Azure Blob) recomputes it; S3 keeps the
        // POD-001 shape below unchanged.
        var expected = storage is IProofUploadHeaderShape shape
            ? new Dictionary<string, string>(
                shape.RequiredUploadHeaders(
                    ownerOrganizationId,
                    create.OrderId,
                    sessionId,
                    requestedBy,
                    create.ProofType,
                    create.ContentType,
                    create.SizeBytes,
                    create.Sha256),
                StringComparer.OrdinalIgnoreCase)
            : S3RequiredHeaders(create, ownerOrganizationId, sessionId, requestedBy);
        return headers.Count == expected.Count &&
            expected.All(expectedHeader =>
                headers.Count(header =>
                    string.Equals(
                        header.Key,
                        expectedHeader.Key,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        header.Value,
                        expectedHeader.Value,
                        StringComparison.Ordinal)) == 1);
    }

    private static Dictionary<string, string> S3RequiredHeaders(
        CreateProofUploadSessionCommand create,
        Guid ownerOrganizationId,
        Guid sessionId,
        Guid requestedBy)
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = create.ContentType,
            [$"x-amz-meta-{S3CompatibleProofObjectStorage.SessionIdMetadata}"] =
                sessionId.ToString("D"),
            [$"x-amz-meta-{S3CompatibleProofObjectStorage.OrderIdMetadata}"] =
                create.OrderId.ToString("D"),
            [$"x-amz-meta-{S3CompatibleProofObjectStorage.OwnerOrganizationIdMetadata}"] =
                ownerOrganizationId.ToString("D"),
            [$"x-amz-meta-{S3CompatibleProofObjectStorage.RequestedByMetadata}"] =
                requestedBy.ToString("D"),
            [$"x-amz-meta-{S3CompatibleProofObjectStorage.ProofTypeMetadata}"] =
                create.ProofType,
            [$"x-amz-meta-{S3CompatibleProofObjectStorage.SizeBytesMetadata}"] =
                create.SizeBytes.ToString(CultureInfo.InvariantCulture),
        };
        if (create.Sha256 is not null)
        {
            expected[$"x-amz-meta-{S3CompatibleProofObjectStorage.Sha256Metadata}"] =
                Convert.ToHexString(create.Sha256).ToLowerInvariant();
        }

        return expected;
    }

    private static async Task InsertReservationAsync(
        CustodyDbContext context,
        CreateProofUploadSessionCommand create,
        byte[] requestHash,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = CustodySql.Database(context);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.idempotency_keys(
              owner_org_id,scope,idempotency_key,request_hash,response_status,response_body,
              resource_id,created_at,expires_at)
            VALUES (@owner,@scope,@key,@hash,NULL,NULL,NULL,@now,@expires)
            """,
            connection,
            transaction);
        command.Parameters.Add(CustodySql.P("owner", NpgsqlDbType.Uuid, create.OrganizationId));
        command.Parameters.Add(CustodySql.P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(CustodySql.P("key", NpgsqlDbType.Text, create.IdempotencyKey));
        command.Parameters.Add(CustodySql.P("hash", NpgsqlDbType.Bytea, requestHash));
        command.Parameters.Add(CustodySql.P("now", NpgsqlDbType.TimestampTz, now));
        command.Parameters.Add(CustodySql.P("expires", NpgsqlDbType.TimestampTz, expiresAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSessionAsync(
        CustodyDbContext context,
        CreateProofUploadSessionCommand create,
        AuthorizedOrder order,
        ProofUploadSessionResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = CustodySql.Database(context);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO custody.proof_upload_sessions(
              id,order_id,owner_org_id,operator_org_id,requested_by,object_key_quarantine,
              expected_content_type,maximum_bytes,status,expires_at,created_at,updated_at)
            VALUES (
              @id,@order,@owner,@operator,@actor,@object_key,@content_type,@maximum_bytes,
              'CREATED',@expires,@now,@now)
            """,
            connection,
            transaction);
        command.Parameters.Add(CustodySql.P("id", NpgsqlDbType.Uuid, result.Id));
        command.Parameters.Add(CustodySql.P("order", NpgsqlDbType.Uuid, create.OrderId));
        command.Parameters.Add(CustodySql.P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
        command.Parameters.Add(CustodySql.P("operator", NpgsqlDbType.Uuid, order.OperatorOrganizationId));
        command.Parameters.Add(CustodySql.P("actor", NpgsqlDbType.Uuid, create.ActorId));
        command.Parameters.Add(CustodySql.P("object_key", NpgsqlDbType.Text, result.ObjectKey));
        command.Parameters.Add(CustodySql.P("content_type", NpgsqlDbType.Text, create.ContentType));
        command.Parameters.Add(CustodySql.P("maximum_bytes", NpgsqlDbType.Bigint, create.SizeBytes));
        command.Parameters.Add(CustodySql.P("expires", NpgsqlDbType.TimestampTz, result.ExpiresAt));
        command.Parameters.Add(CustodySql.P("now", NpgsqlDbType.TimestampTz, now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task WriteAuditAsync(
        CustodyDbContext context,
        CreateProofUploadSessionCommand create,
        Guid sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                order_id = create.OrderId,
                proof_type = create.ProofType,
                content_type = create.ContentType,
                size_bytes = create.SizeBytes,
            }));
        var (connection, transaction) = CustodySql.Database(context);
        await auditWriter.WriteAsync(
            connection,
            transaction,
            new AuditEntry(
                Guid.NewGuid(),
                create.OrganizationId,
                create.ActorId,
                "custody.proof_upload_session.created",
                "proof_upload_session",
                sessionId,
                create.RequestId,
                redactor.Redact(document.RootElement),
                now),
            cancellationToken);
    }

    private static async Task CompleteReservationAsync(
        CustodyDbContext context,
        CreateProofUploadSessionCommand create,
        byte[] requestHash,
        ProofUploadSessionResult result,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = CustodySql.Database(context);
        await using var command = new NpgsqlCommand(
            """
            UPDATE platform.idempotency_keys
            SET response_status=201,response_body=@response,resource_id=@resource,expires_at=@expires
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key
              AND request_hash=@hash AND response_status IS NULL
            """,
            connection,
            transaction);
        command.Parameters.Add(CustodySql.P("response", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(result, JsonOptions)));
        command.Parameters.Add(CustodySql.P("resource", NpgsqlDbType.Uuid, result.Id));
        command.Parameters.Add(CustodySql.P("expires", NpgsqlDbType.TimestampTz, expiresAt));
        command.Parameters.Add(CustodySql.P("owner", NpgsqlDbType.Uuid, create.OrganizationId));
        command.Parameters.Add(CustodySql.P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(CustodySql.P("key", NpgsqlDbType.Text, create.IdempotencyKey));
        command.Parameters.Add(CustodySql.P("hash", NpgsqlDbType.Bytea, requestHash));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new ProofConflictException("IDEMPOTENCY_CONFLICT");
        }
    }
}
