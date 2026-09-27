using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Custody.Application.ProofUploads;
using Custody.Domain;
using Custody.Infrastructure.Persistence;
using Custody.Infrastructure.ProofStorage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Custody.Infrastructure.Proofs;

public sealed class ProofValidationProcessor(
    WorkerTenantTransactionContext<CustodyDbContext> transactionContext,
    IProofObjectStorage storage,
    IProofThreatScanner threatScanner,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor redactor,
    IOptions<ProofStorageOptions> options,
    IClock clock,
    IProofTelemetry telemetry,
    ILogger<ProofValidationProcessor> logger) : IProofValidationProcessor
{
    public async Task ProcessAvailableAsync(CancellationToken cancellationToken)
    {
        if (!storage.IsEnabled || !threatScanner.IsEnabled)
        {
            return;
        }

        using var concurrency = new SemaphoreSlim(options.Value.MaximumConcurrency);
        var tasks = new List<Task<bool>>();
        await foreach (var descriptor in storage.ListQuarantineAsync(cancellationToken))
        {
            telemetry.ObjectDiscovered();
            await concurrency.WaitAsync(cancellationToken);
            tasks.Add(ProcessWithReleaseAsync(descriptor, concurrency, cancellationToken));
        }

        if ((await Task.WhenAll(tasks)).Any(failed => failed))
        {
            throw new ProofStorageUnavailableException();
        }
    }

    private async Task<bool> ProcessWithReleaseAsync(
        ProofObjectDescriptor descriptor,
        SemaphoreSlim concurrency,
        CancellationToken cancellationToken)
    {
        try
        {
            await ProcessOneAsync(descriptor, cancellationToken);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            telemetry.StorageFailure("processing_exception");
            logger.LogError("Secure proof validation failed.");
            return true;
        }
        finally
        {
            concurrency.Release();
        }
    }

    private async Task ProcessOneAsync(
        ProofObjectDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!TryReadExpectedMetadata(descriptor, out var metadata))
        {
            telemetry.ProcessingCompleted(
                "unknown",
                "rejected",
                "metadata_mismatch",
                stopwatch.Elapsed.TotalMilliseconds);
            logger.LogWarning(
                "Ignoring a quarantine object with invalid signed metadata.");
            return;
        }

        // ADP-001: an out-of-band scanner (Defender for Storage) may not have a verdict yet. The
        // object then stays in quarantine and the session is not touched; a later pass retries.
        if (await threatScanner.IsVerdictPendingAsync(descriptor, cancellationToken))
        {
            telemetry.ProcessingCompleted(
                metadata.ProofType.ToContractValue(),
                "deferred",
                "scan_pending",
                stopwatch.Elapsed.TotalMilliseconds);
            return;
        }

        var claim = await ClaimAsync(descriptor, metadata, cancellationToken);
        if (claim is null)
        {
            telemetry.ProcessingCompleted(
                metadata.ProofType.ToContractValue(),
                "deferred",
                "claim_lost",
                stopwatch.Elapsed.TotalMilliseconds);
            return;
        }

        if (claim.CleanupOnly)
        {
            if (await HasTrustedFinalObjectAsync(descriptor, metadata, cancellationToken))
            {
                await storage.DeleteQuarantineAsync(
                    descriptor.ObjectKey,
                    descriptor.VersionId,
                    cancellationToken);
                telemetry.ProcessingCompleted(
                    metadata.ProofType.ToContractValue(),
                    "cleanup",
                    "terminal_cleanup",
                    stopwatch.Elapsed.TotalMilliseconds);
            }
            else
            {
                telemetry.ProcessingCompleted(
                    metadata.ProofType.ToContractValue(),
                    "deferred",
                    "final_object_conflict",
                    stopwatch.Elapsed.TotalMilliseconds);
            }

            return;
        }

        string rejectionCode;
        try
        {
            rejectionCode = await ValidateAndPromoteAsync(
                descriptor,
                metadata,
                claim,
                cancellationToken);
        }
        catch (Amazon.S3.AmazonS3Exception exception) when ((int)exception.StatusCode == 412)
        {
            rejectionCode = "SOURCE_OBJECT_CHANGED";
        }
        catch (ProofObjectChangedException)
        {
            rejectionCode = "SOURCE_OBJECT_CHANGED";
        }
        catch (ProofConflictException exception)
        {
            rejectionCode = exception.Code;
        }

        if (rejectionCode == ProofThreatScanResult.PendingCode)
        {
            // The verdict disappeared or went stale between the pre-check and the claim. Nothing is
            // settled: the object stays quarantined and the stale-claim recovery retries it.
            telemetry.ProcessingCompleted(
                metadata.ProofType.ToContractValue(),
                "deferred",
                "scan_pending",
                stopwatch.Elapsed.TotalMilliseconds);
            return;
        }

        if (rejectionCode != "READY")
        {
            await SettleAsync(claim, "REJECTED", rejectionCode, cancellationToken);
            telemetry.ProcessingCompleted(
                metadata.ProofType.ToContractValue(),
                "rejected",
                rejectionCode.ToLowerInvariant(),
                stopwatch.Elapsed.TotalMilliseconds);
            return;
        }

        var settled = await SettleAsync(claim, "READY", "VALIDATION_SUCCEEDED", cancellationToken);
        if (settled)
        {
            await storage.DeleteQuarantineAsync(
                descriptor.ObjectKey,
                descriptor.VersionId,
                cancellationToken);
        }
        telemetry.ProcessingCompleted(
            metadata.ProofType.ToContractValue(),
            settled ? "ready" : "deferred",
            settled ? "validated" : "claim_lost",
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private async Task<string> ValidateAndPromoteAsync(
        ProofObjectDescriptor descriptor,
        SignedObjectMetadata metadata,
        ValidationClaim claim,
        CancellationToken cancellationToken)
    {
        if (descriptor.SizeBytes <= 0 ||
            descriptor.SizeBytes > options.Value.MaximumBytes ||
            descriptor.SizeBytes != claim.MaximumBytes ||
            metadata.SizeBytes != descriptor.SizeBytes ||
            descriptor.ContentType != claim.ExpectedContentType ||
            (metadata.ProofType == ProofType.DeliveryCode &&
             descriptor.SizeBytes > options.Value.MaximumTextBytes) ||
            !ProofContentPolicy.IsAllowed(metadata.ProofType, descriptor.ContentType))
        {
            return "OBJECT_CONTRACT_MISMATCH";
        }

        await using var content = await storage.OpenReadAsync(
            descriptor.ObjectKey,
            descriptor.VersionId,
            cancellationToken);
        if (content.Length != descriptor.SizeBytes)
        {
            return "OBJECT_SIZE_CHANGED";
        }

        var bytes = ((MemoryStream)content).ToArray();
        if (!ProofContentPolicy.MatchesMagicBytes(descriptor.ContentType, bytes))
        {
            return "MAGIC_BYTES_MISMATCH";
        }

        var actualSha = SHA256.HashData(bytes);
        if (metadata.ExpectedSha256 is not null &&
            !ProofSha256.FixedTimeEquals(actualSha, metadata.ExpectedSha256))
        {
            return "SHA256_MISMATCH";
        }

        content.Position = 0;
        var scan = await threatScanner.ScanAsync(descriptor, content, cancellationToken);
        if (!scan.IsSafe)
        {
            return scan.Code;
        }

        var finalObjectKey = ProofObjectKeys.Final(
            metadata.OwnerOrganizationId,
            metadata.OrderId,
            metadata.SessionId);
        await storage.PromoteAsync(
            new ValidatedProofObject(
                metadata.SessionId,
                metadata.OrderId,
                metadata.OwnerOrganizationId,
                metadata.ProofType.ToContractValue(),
                descriptor.ObjectKey,
                finalObjectKey,
                descriptor.SizeBytes,
                descriptor.ContentType,
                actualSha,
                descriptor.ETag,
                descriptor.VersionId),
            cancellationToken);
        var promoted = await storage.HeadAsync(finalObjectKey, cancellationToken);
        return promoted is not null &&
            promoted.SizeBytes == descriptor.SizeBytes &&
            promoted.Metadata.TryGetValue(
                S3CompatibleProofObjectStorage.Sha256Metadata,
                out var promotedSha) &&
            ProofSha256.TryParseHex(promotedSha, out var promotedShaBytes) &&
            ProofSha256.FixedTimeEquals(promotedShaBytes, actualSha)
                ? "READY"
                : "PROMOTION_VERIFICATION_FAILED";
    }

    private async Task<bool> HasTrustedFinalObjectAsync(
        ProofObjectDescriptor quarantine,
        SignedObjectMetadata metadata,
        CancellationToken cancellationToken)
    {
        var finalObject = await storage.HeadAsync(
            ProofObjectKeys.Final(
                metadata.OwnerOrganizationId,
                metadata.OrderId,
                metadata.SessionId),
            cancellationToken);
        return finalObject is not null &&
            finalObject.SizeBytes == quarantine.SizeBytes &&
            finalObject.ContentType == quarantine.ContentType &&
            finalObject.Metadata.TryGetValue(
                S3CompatibleProofObjectStorage.SessionIdMetadata,
                out var sessionId) &&
            sessionId == metadata.SessionId.ToString("D") &&
            finalObject.Metadata.TryGetValue(
                S3CompatibleProofObjectStorage.OrderIdMetadata,
                out var orderId) &&
            orderId == metadata.OrderId.ToString("D") &&
            finalObject.Metadata.TryGetValue(
                S3CompatibleProofObjectStorage.OwnerOrganizationIdMetadata,
                out var ownerOrganizationId) &&
            ownerOrganizationId == metadata.OwnerOrganizationId.ToString("D") &&
            finalObject.Metadata.TryGetValue(
                S3CompatibleProofObjectStorage.ProofTypeMetadata,
                out var proofType) &&
            proofType == metadata.ProofType.ToContractValue() &&
            finalObject.Metadata.TryGetValue(
                S3CompatibleProofObjectStorage.Sha256Metadata,
                out var sha256) &&
            ProofSha256.TryParseHex(sha256, out var finalSha256) &&
            (metadata.ExpectedSha256 is null ||
             ProofSha256.FixedTimeEquals(finalSha256, metadata.ExpectedSha256));
    }

    private async Task<ValidationClaim?> ClaimAsync(
        ProofObjectDescriptor descriptor,
        SignedObjectMetadata metadata,
        CancellationToken cancellationToken) =>
        await transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(metadata.SessionId, [metadata.OwnerOrganizationId]),
            async (dbContext, token) =>
            {
                var (connection, transaction) = CustodySql.Database(dbContext);
                await using var select = new NpgsqlCommand(
                    """
                    SELECT requested_by,operator_org_id,expected_content_type,maximum_bytes,status,expires_at,updated_at
                    FROM custody.proof_upload_sessions
                    WHERE id=@session AND order_id=@order AND owner_org_id=@owner
                      AND object_key_quarantine=@object_key
                    FOR UPDATE
                    """,
                    connection,
                    transaction);
                select.Parameters.Add(P("session", NpgsqlDbType.Uuid, metadata.SessionId));
                select.Parameters.Add(P("order", NpgsqlDbType.Uuid, metadata.OrderId));
                select.Parameters.Add(P("owner", NpgsqlDbType.Uuid, metadata.OwnerOrganizationId));
                select.Parameters.Add(P("object_key", NpgsqlDbType.Text, descriptor.ObjectKey));
                await using var reader = await select.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    return null;
                }

                var requestedBy = reader.GetGuid(0);
                Guid? operatorOrganizationId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
                var expectedContentType = reader.GetString(2);
                var maximumBytes = reader.GetInt64(3);
                var status = reader.GetString(4);
                var expiresAt = reader.GetFieldValue<DateTimeOffset>(5);
                var updatedAt = reader.GetFieldValue<DateTimeOffset>(6);
                await reader.DisposeAsync();

                var now = clock.UtcNow;
                if (requestedBy != metadata.RequestedBy)
                {
                    return null;
                }

                if (status is "READY" or "CONSUMED")
                {
                    return new ValidationClaim(
                        metadata.SessionId,
                        metadata.OrderId,
                        metadata.OwnerOrganizationId,
                        operatorOrganizationId,
                        requestedBy,
                        expectedContentType,
                        maximumBytes,
                        updatedAt,
                        true);
                }

                if (expiresAt <= now)
                {
                    await UpdateStatusAsync(
                        connection,
                        transaction,
                        metadata.SessionId,
                        status,
                        "EXPIRED",
                        now,
                        token);
                    return null;
                }

                if (status == "CREATED")
                {
                    await UpdateStatusAsync(
                        connection,
                        transaction,
                        metadata.SessionId,
                        "CREATED",
                        "UPLOADED",
                        now,
                        token);
                    status = "UPLOADED";
                }

                if (status == "VALIDATING" &&
                    updatedAt > now.AddSeconds(-options.Value.StaleValidationSeconds))
                {
                    return null;
                }

                if (status is not ("UPLOADED" or "VALIDATING"))
                {
                    return null;
                }

                if (status == "VALIDATING")
                {
                    telemetry.StaleValidationRecovered(metadata.ProofType.ToContractValue());
                }

                await using var claim = new NpgsqlCommand(
                    """
                    UPDATE custody.proof_upload_sessions
                    SET status='VALIDATING',updated_at=@now
                    WHERE id=@session AND status=@status
                    RETURNING updated_at
                    """,
                    connection,
                    transaction);
                claim.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
                claim.Parameters.Add(P("session", NpgsqlDbType.Uuid, metadata.SessionId));
                claim.Parameters.Add(P("status", NpgsqlDbType.Text, status));
                var claimValue = await claim.ExecuteScalarAsync(token);
                DateTimeOffset? claimToken = claimValue switch
                {
                    DateTimeOffset value => value,
                    DateTime value => new DateTimeOffset(
                        DateTime.SpecifyKind(value, DateTimeKind.Utc)),
                    _ => null,
                };
                return claimToken is null
                    ? null
                    : new ValidationClaim(
                        metadata.SessionId,
                        metadata.OrderId,
                        metadata.OwnerOrganizationId,
                        operatorOrganizationId,
                        requestedBy,
                        expectedContentType,
                        maximumBytes,
                        claimToken.Value,
                        false);
            },
            cancellationToken);

    private async Task<bool> SettleAsync(
        ValidationClaim claim,
        string status,
        string reason,
        CancellationToken cancellationToken)
    {
        var organizations = claim.OperatorOrganizationId is { } operatorOrganizationId
            ? new[] { claim.OwnerOrganizationId, operatorOrganizationId }
            : [claim.OwnerOrganizationId];
        return await transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(claim.RequestedBy, organizations),
            async (dbContext, token) =>
            {
                var (connection, transaction) = CustodySql.Database(dbContext);
                var now = clock.UtcNow;
                await using var update = new NpgsqlCommand(
                    """
                    UPDATE custody.proof_upload_sessions
                    SET status=@next,updated_at=@now
                    WHERE id=@session AND status='VALIDATING' AND updated_at=@claim_token
                    """,
                    connection,
                    transaction);
                update.Parameters.Add(P("next", NpgsqlDbType.Text, status));
                update.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
                update.Parameters.Add(P("session", NpgsqlDbType.Uuid, claim.SessionId));
                update.Parameters.Add(P("claim_token", NpgsqlDbType.TimestampTz, claim.ClaimToken));
                if (await update.ExecuteNonQueryAsync(token) != 1)
                {
                    return false;
                }

                using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { code = reason }));
                await auditWriter.WriteAsync(
                    connection,
                    transaction,
                    new AuditEntry(
                        Guid.NewGuid(),
                        claim.OwnerOrganizationId,
                        claim.RequestedBy,
                        status == "READY"
                            ? "custody.proof_upload_session.ready"
                            : "custody.proof_upload_session.rejected",
                        "proof_upload_session",
                        claim.SessionId,
                        null,
                        redactor.Redact(document.RootElement),
                        now),
                    token);
                return true;
            },
            cancellationToken);
    }

    private static async Task UpdateStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        string current,
        string next,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand(
            """
            UPDATE custody.proof_upload_sessions
            SET status=@next,updated_at=@now
            WHERE id=@session AND status=@current
            """,
            connection,
            transaction);
        update.Parameters.Add(P("next", NpgsqlDbType.Text, next));
        update.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        update.Parameters.Add(P("session", NpgsqlDbType.Uuid, sessionId));
        update.Parameters.Add(P("current", NpgsqlDbType.Text, current));
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool TryReadExpectedMetadata(
        ProofObjectDescriptor descriptor,
        out SignedObjectMetadata value)
    {
        value = default!;
        var metadata = descriptor.Metadata;
        if (!ReadGuid(metadata, S3CompatibleProofObjectStorage.SessionIdMetadata, out var sessionId) ||
            !ReadGuid(metadata, S3CompatibleProofObjectStorage.OrderIdMetadata, out var orderId) ||
            !ReadGuid(metadata, S3CompatibleProofObjectStorage.OwnerOrganizationIdMetadata, out var ownerOrganizationId) ||
            !ReadGuid(metadata, S3CompatibleProofObjectStorage.RequestedByMetadata, out var requestedBy) ||
            !metadata.TryGetValue(S3CompatibleProofObjectStorage.ProofTypeMetadata, out var proofTypeValue) ||
            !ProofContract.TryParse(proofTypeValue, out var proofType) ||
            !metadata.TryGetValue(S3CompatibleProofObjectStorage.SizeBytesMetadata, out var sizeValue) ||
            !long.TryParse(sizeValue, NumberStyles.None, CultureInfo.InvariantCulture, out var sizeBytes) ||
            descriptor.ObjectKey != ProofObjectKeys.Quarantine(ownerOrganizationId, orderId, sessionId))
        {
            return false;
        }

        byte[]? expectedSha256 = null;
        if (metadata.TryGetValue(S3CompatibleProofObjectStorage.Sha256Metadata, out var shaValue))
        {
            if (!ProofSha256.TryParseHex(shaValue, out var parsedSha256))
            {
                return false;
            }

            expectedSha256 = parsedSha256;
        }

        value = new SignedObjectMetadata(
            sessionId,
            orderId,
            ownerOrganizationId,
            requestedBy,
            proofType,
            expectedSha256,
            sizeBytes);
        return true;
    }

    private static bool ReadGuid(
        IReadOnlyDictionary<string, string> metadata,
        string name,
        out Guid value)
    {
        value = default;
        return metadata.TryGetValue(name, out var raw) &&
            Guid.TryParseExact(raw, "D", out value) &&
            value != Guid.Empty;
    }

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type)
    {
        Value = value ?? DBNull.Value,
    };

    private sealed record SignedObjectMetadata(
        Guid SessionId,
        Guid OrderId,
        Guid OwnerOrganizationId,
        Guid RequestedBy,
        ProofType ProofType,
        byte[]? ExpectedSha256,
        long SizeBytes);

    private sealed record ValidationClaim(
        Guid SessionId,
        Guid OrderId,
        Guid OwnerOrganizationId,
        Guid? OperatorOrganizationId,
        Guid RequestedBy,
        string ExpectedContentType,
        long MaximumBytes,
        DateTimeOffset ClaimToken,
        bool CleanupOnly);
}
