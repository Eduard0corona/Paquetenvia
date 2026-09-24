using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Incidents.Application.Incidents;
using Incidents.Domain;
using Incidents.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Incidents.Infrastructure.Incidents;

/// <summary>
/// Opens an INC-001 incident. The incident is the precondition of the ORD-002
/// <c>FAILED_ATTEMPT</c> transition, never a substitute for it: this service never writes
/// <c>orders.orders</c>, so the authoritative state machine stays the only writer of order state.
/// </summary>
public sealed class PostgreSqlIncidentService(
    TenantTransactionContext<IncidentsDbContext> transactionContext,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor redactor,
    IIncidentPiiProtector piiProtector,
    IOptions<IncidentsOptions> options,
    IClock clock) : IIncidentService
{
    internal const string IdempotencyScope = "INC-001:OPEN_INCIDENT";
    internal const string AuditAction = "incidents.incident.opened";
    internal const string AuditEntityType = "incident";

    /// <summary>
    /// The idempotency reservation outlives the request so a retry after a network failure
    /// replays the original incident instead of opening a second one for the same attempt.
    /// </summary>
    private static readonly TimeSpan ReservationLifetime = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IncidentResult> OpenAsync(
        OpenIncidentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        var policy = options.Value.OperationalPolicy;
        if (!policy.IsValid)
        {
            // A deployment configured outside the bounded operational surface serves nothing.
            throw new IncidentInfrastructureException("The incident operational policy is invalid.");
        }

        if (!IncidentRequestPolicy.IsValidCommandShape(command) ||
            !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey) ||
            !policy.IsValidOccurrence(command.OccurredAt, now) ||
            !policy.IsAllowedEvidenceCount(command.EvidenceProofIds.Count))
        {
            throw new IncidentConflictException("INVALID_REQUEST");
        }

        _ = IncidentContract.TryParseSeverity(command.Severity, out var severity);
        _ = IncidentContract.TryParseNextAction(command.NextAction, out var nextAction);
        var occurredAt = UtcMicrosecondPrecision.Normalize(command.OccurredAt);
        var slaDueAt = UtcMicrosecondPrecision.Normalize(policy.DueAt(occurredAt, severity));
        var requestHash = ComputeRequestHash(command, occurredAt);

        // The description is protected before the transaction opens. If protection is
        // unavailable the request ends as 503 having written no incident, no evidence, no
        // idempotency reservation and no audit entry.
        var protectedDescription = ProtectDescription(command.Description);

        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
                async (dbContext, token) =>
                {
                    var order = await IncidentsSql.ReadAuthorizedOrderAsync(
                        dbContext,
                        command.OrderId,
                        command.ActorId,
                        command.OrganizationId,
                        command.MfaSatisfied,
                        token) ?? throw new IncidentNotFoundException();

                    await IncidentsSql.AcquireIdempotencyLockAsync(
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

                    // AT_PICKUP may still be rescheduled, but nothing can be returned before
                    // custody was acquired: ORD-002 and ADR-014 own that precondition, so INC-001
                    // refuses an opening whose next action the state machine could never honour
                    // instead of persisting it and deriving custody_acquired=false beside it.
                    if (!IncidentOrderStatePolicy.IsAllowedNextAction(order.Status, nextAction))
                    {
                        throw new IncidentConflictException("ORDER_STATE_NOT_ALLOWED");
                    }

                    var ownedProofs = await IncidentsSql.CountOwnedProofsAsync(
                        dbContext,
                        command.OrderId,
                        command.OrganizationId,
                        command.EvidenceProofIds,
                        token);
                    if (ownedProofs != command.EvidenceProofIds.Count)
                    {
                        throw new IncidentConflictException("EVIDENCE_NOT_AVAILABLE");
                    }

                    var incidentId = Guid.NewGuid();
                    var result = new IncidentResult(
                        incidentId,
                        command.OrderId,
                        IncidentContract.Open,
                        severity.ToContractValue(),
                        command.IncidentType,
                        command.ReasonCode,
                        command.NextAction,
                        IncidentOrderStatePolicy.DerivesCustodyAcquired(order.Status),
                        occurredAt,
                        slaDueAt,
                        command.EvidenceProofIds);

                    await InsertReservationAsync(dbContext, command, requestHash, now, token);
                    await InsertIncidentAsync(
                        dbContext, command, order, result, protectedDescription, now, token);
                    await InsertEvidenceAsync(dbContext, command, order, result, now, token);
                    await WriteAuditAsync(dbContext, command, result, now, token);
                    await CompleteReservationAsync(dbContext, command, requestHash, result, now, token);
                    return result;
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IncidentException)
        {
            throw;
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.UniqueViolation
                or PostgresErrorCodes.SerializationFailure
                or PostgresErrorCodes.ForeignKeyViolation
                or PostgresErrorCodes.CheckViolation)
        {
            throw new IncidentConflictException("CONFLICT");
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException)
        {
            throw new IncidentInfrastructureException("The incident store is unavailable.", exception);
        }
    }

    /// <summary>
    /// Turns the accepted description into the ciphertext AI-06 persists. Failure is never
    /// silent and never degrades to storing the plaintext: the caller gets an infrastructure
    /// failure, which the endpoint publishes as 503, before anything is written.
    /// </summary>
    private ProtectedDescription ProtectDescription(string description)
    {
        var keyVersion = options.Value.PiiKeyVersion;
        try
        {
            if (string.IsNullOrWhiteSpace(keyVersion))
            {
                throw new IncidentPiiProtectionUnavailableException();
            }

            var ciphertext = piiProtector.Protect(description, keyVersion);
            if (ciphertext is not { Length: > 0 })
            {
                throw new IncidentPiiProtectionUnavailableException();
            }

            return new ProtectedDescription(ciphertext, keyVersion);
        }
        catch (Exception exception)
        {
            throw new IncidentInfrastructureException(
                "Incident description protection is unavailable.",
                exception);
        }
    }

    /// <summary>
    /// The canonical form of an opening request. Evidence order is irrelevant to the identity of
    /// the attempt, so it is sorted before hashing and a reordered retry still replays.
    /// </summary>
    private static byte[] ComputeRequestHash(OpenIncidentCommand command, DateTimeOffset occurredAt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("tenant", command.OrganizationId.ToString("D"));
            writer.WriteString("order_id", command.OrderId.ToString("D"));
            writer.WriteString("incident_type", command.IncidentType);
            writer.WriteString("severity", command.Severity);
            writer.WriteString("reason_code", command.ReasonCode);
            writer.WriteString("next_action", command.NextAction);
            writer.WriteString("occurred_at", occurredAt.UtcDateTime.ToString("O"));
            writer.WriteBase64String("description", Encoding.UTF8.GetBytes(command.Description));
            writer.WritePropertyName("evidence_proof_ids");
            writer.WriteStartArray();
            foreach (var proofId in command.EvidenceProofIds.Order())
            {
                writer.WriteStringValue(proofId.ToString("D"));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return SHA256.HashData(stream.ToArray());
    }

    private static async Task<IncidentResult?> ReadReplayAsync(
        IncidentsDbContext context,
        OpenIncidentCommand command,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = IncidentsSql.Database(context);
        await using var reservation = new NpgsqlCommand(
            """
            SELECT request_hash,response_status,response_body::text,resource_id
            FROM platform.idempotency_keys
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key
            """,
            connection,
            transaction);
        reservation.Parameters.Add(IncidentsSql.P("owner", NpgsqlDbType.Uuid, command.OrganizationId));
        reservation.Parameters.Add(IncidentsSql.P("scope", NpgsqlDbType.Text, IdempotencyScope));
        reservation.Parameters.Add(IncidentsSql.P("key", NpgsqlDbType.Text, command.IdempotencyKey));
        Guid resourceId;
        string responseBody;
        await using (var reader = await reservation.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            if (!CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte[]>(0), requestHash))
            {
                throw new IncidentConflictException("IDEMPOTENCY_CONFLICT");
            }

            if (reader.IsDBNull(1) || reader.GetInt32(1) != 201 || reader.IsDBNull(2) || reader.IsDBNull(3))
            {
                throw new IncidentConflictException("IDEMPOTENCY_CORRUPT");
            }

            responseBody = reader.GetString(2);
            resourceId = reader.GetGuid(3);
        }

        IncidentResult result;
        try
        {
            result = JsonSerializer.Deserialize<IncidentResult>(responseBody, JsonOptions)
                ?? throw new IncidentConflictException("IDEMPOTENCY_CORRUPT");
        }
        catch (JsonException)
        {
            throw new IncidentConflictException("IDEMPOTENCY_CORRUPT");
        }

        if (result.Id != resourceId ||
            result.OrderId != command.OrderId ||
            result.Status != IncidentContract.Open)
        {
            throw new IncidentConflictException("IDEMPOTENCY_CORRUPT");
        }

        // The stored response is only trusted once the incident it names is still present in this
        // tenant with the attributes the replay is about to hand back.
        await using var stored = new NpgsqlCommand(
            """
            SELECT i.order_id,i.status,i.severity,i.incident_type,i.reason_code,i.next_action,
                   i.custody_acquired,i.occurred_at,i.sla_due_at,
                   (SELECT count(*) FROM incidents.incident_evidence e WHERE e.incident_id=i.id)
            FROM incidents.incidents i
            WHERE i.id=@incident
            """,
            connection,
            transaction);
        stored.Parameters.Add(IncidentsSql.P("incident", NpgsqlDbType.Uuid, result.Id));
        await using var storedReader = await stored.ExecuteReaderAsync(cancellationToken);
        if (!await storedReader.ReadAsync(cancellationToken) ||
            storedReader.GetGuid(0) != command.OrderId ||
            storedReader.GetString(1) != result.Status ||
            storedReader.GetString(2) != result.Severity ||
            storedReader.GetString(3) != result.IncidentType ||
            storedReader.GetString(4) != result.ReasonCode ||
            storedReader.GetString(5) != result.NextAction ||
            storedReader.GetBoolean(6) != result.CustodyAcquired ||
            storedReader.GetFieldValue<DateTimeOffset>(7) != result.OccurredAt ||
            storedReader.GetFieldValue<DateTimeOffset>(8) != result.SlaDueAt ||
            storedReader.GetInt64(9) != result.EvidenceProofIds.Count)
        {
            throw new IncidentConflictException("IDEMPOTENCY_CORRUPT");
        }

        return result;
    }

    private static async Task InsertReservationAsync(
        IncidentsDbContext context,
        OpenIncidentCommand command,
        byte[] requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = IncidentsSql.Database(context);
        await using var reservation = new NpgsqlCommand(
            """
            INSERT INTO platform.idempotency_keys(
              owner_org_id,scope,idempotency_key,request_hash,response_status,response_body,
              resource_id,created_at,expires_at)
            VALUES (@owner,@scope,@key,@hash,NULL,NULL,NULL,@now,@expires)
            """,
            connection,
            transaction);
        reservation.Parameters.Add(IncidentsSql.P("owner", NpgsqlDbType.Uuid, command.OrganizationId));
        reservation.Parameters.Add(IncidentsSql.P("scope", NpgsqlDbType.Text, IdempotencyScope));
        reservation.Parameters.Add(IncidentsSql.P("key", NpgsqlDbType.Text, command.IdempotencyKey));
        reservation.Parameters.Add(IncidentsSql.P("hash", NpgsqlDbType.Bytea, requestHash));
        reservation.Parameters.Add(IncidentsSql.P("now", NpgsqlDbType.TimestampTz, now));
        reservation.Parameters.Add(IncidentsSql.P("expires", NpgsqlDbType.TimestampTz, now + ReservationLifetime));
        await reservation.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertIncidentAsync(
        IncidentsDbContext context,
        OpenIncidentCommand command,
        AuthorizedOrder order,
        IncidentResult result,
        ProtectedDescription description,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = IncidentsSql.Database(context);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO incidents.incidents(
              id,order_id,owner_org_id,operator_org_id,incident_type,severity,status,
              custody_acquired,description_ciphertext,pii_key_version,
              reason_code,next_action,occurred_at,sla_due_at,created_by,created_at)
            VALUES (
              @id,@order,@owner,@operator,@incident_type,@severity,'OPEN',
              @custody,@description,@pii_key_version,
              @reason_code,@next_action,@occurred,@sla_due,@actor,@now)
            """,
            connection,
            transaction);
        insert.Parameters.Add(IncidentsSql.P("id", NpgsqlDbType.Uuid, result.Id));
        insert.Parameters.Add(IncidentsSql.P("order", NpgsqlDbType.Uuid, command.OrderId));
        insert.Parameters.Add(IncidentsSql.P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
        insert.Parameters.Add(IncidentsSql.P("operator", NpgsqlDbType.Uuid, order.OperatorOrganizationId));
        insert.Parameters.Add(IncidentsSql.P("incident_type", NpgsqlDbType.Text, command.IncidentType));
        insert.Parameters.Add(IncidentsSql.P("severity", NpgsqlDbType.Text, result.Severity));
        insert.Parameters.Add(IncidentsSql.P("custody", NpgsqlDbType.Boolean, result.CustodyAcquired));
        // The description reaches the store only as AI-06 ciphertext; the plaintext is never bound.
        insert.Parameters.Add(IncidentsSql.P("description", NpgsqlDbType.Bytea, description.Ciphertext));
        insert.Parameters.Add(IncidentsSql.P("pii_key_version", NpgsqlDbType.Text, description.KeyVersion));
        insert.Parameters.Add(IncidentsSql.P("reason_code", NpgsqlDbType.Text, command.ReasonCode));
        insert.Parameters.Add(IncidentsSql.P("next_action", NpgsqlDbType.Text, command.NextAction));
        insert.Parameters.Add(IncidentsSql.P("occurred", NpgsqlDbType.TimestampTz, result.OccurredAt));
        insert.Parameters.Add(IncidentsSql.P("sla_due", NpgsqlDbType.TimestampTz, result.SlaDueAt));
        insert.Parameters.Add(IncidentsSql.P("actor", NpgsqlDbType.Uuid, command.ActorId));
        insert.Parameters.Add(IncidentsSql.P("now", NpgsqlDbType.TimestampTz, now));
        RequireOne(await insert.ExecuteNonQueryAsync(cancellationToken), "The incident was not inserted.");
    }

    private static async Task InsertEvidenceAsync(
        IncidentsDbContext context,
        OpenIncidentCommand command,
        AuthorizedOrder order,
        IncidentResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = IncidentsSql.Database(context);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO incidents.incident_evidence(
              id,incident_id,order_id,owner_org_id,operator_org_id,proof_id,created_by,created_at)
            SELECT gen_random_uuid(),@incident,@order,@owner,@operator,proof_id,@actor,@now
            FROM unnest(@proofs) AS proof_id
            """,
            connection,
            transaction);
        insert.Parameters.Add(IncidentsSql.P("incident", NpgsqlDbType.Uuid, result.Id));
        insert.Parameters.Add(IncidentsSql.P("order", NpgsqlDbType.Uuid, command.OrderId));
        insert.Parameters.Add(IncidentsSql.P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
        insert.Parameters.Add(IncidentsSql.P("operator", NpgsqlDbType.Uuid, order.OperatorOrganizationId));
        insert.Parameters.Add(IncidentsSql.P("actor", NpgsqlDbType.Uuid, command.ActorId));
        insert.Parameters.Add(IncidentsSql.P("now", NpgsqlDbType.TimestampTz, now));
        insert.Parameters.Add(IncidentsSql.P(
            "proofs",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            command.EvidenceProofIds.ToArray()));
        RequireOne(
            await insert.ExecuteNonQueryAsync(cancellationToken),
            "The incident evidence was not inserted.",
            command.EvidenceProofIds.Count);
    }

    private async Task WriteAuditAsync(
        IncidentsDbContext context,
        OpenIncidentCommand command,
        IncidentResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                order_id = command.OrderId,
                incident_type = command.IncidentType,
                severity = result.Severity,
                reason_code = result.ReasonCode,
                next_action = result.NextAction,
                custody_acquired = result.CustodyAcquired,
                occurred_at = result.OccurredAt,
                sla_due_at = result.SlaDueAt,
                evidence_count = result.EvidenceProofIds.Count,
            }, JsonOptions));
        var (connection, transaction) = IncidentsSql.Database(context);
        await auditWriter.WriteAsync(
            connection,
            transaction,
            new AuditEntry(
                Guid.NewGuid(),
                command.OrganizationId,
                command.ActorId,
                AuditAction,
                AuditEntityType,
                result.Id,
                command.RequestId,
                redactor.Redact(document.RootElement),
                now),
            cancellationToken);
    }

    private static async Task CompleteReservationAsync(
        IncidentsDbContext context,
        OpenIncidentCommand command,
        byte[] requestHash,
        IncidentResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = IncidentsSql.Database(context);
        await using var complete = new NpgsqlCommand(
            """
            UPDATE platform.idempotency_keys
            SET response_status=201,response_body=@response,resource_id=@resource,expires_at=@expires
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key
              AND request_hash=@hash AND response_status IS NULL
            """,
            connection,
            transaction);
        complete.Parameters.Add(IncidentsSql.P(
            "response",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(result, JsonOptions)));
        complete.Parameters.Add(IncidentsSql.P("resource", NpgsqlDbType.Uuid, result.Id));
        complete.Parameters.Add(IncidentsSql.P("expires", NpgsqlDbType.TimestampTz, now + ReservationLifetime));
        complete.Parameters.Add(IncidentsSql.P("owner", NpgsqlDbType.Uuid, command.OrganizationId));
        complete.Parameters.Add(IncidentsSql.P("scope", NpgsqlDbType.Text, IdempotencyScope));
        complete.Parameters.Add(IncidentsSql.P("key", NpgsqlDbType.Text, command.IdempotencyKey));
        complete.Parameters.Add(IncidentsSql.P("hash", NpgsqlDbType.Bytea, requestHash));
        if (await complete.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new IncidentConflictException("IDEMPOTENCY_CONFLICT");
        }
    }

    private static void RequireOne(int affected, string message, int expected = 1)
    {
        if (affected != expected)
        {
            throw new IncidentInfrastructureException(message);
        }
    }
}

/// <summary>The protected description and the key version stored beside it.</summary>
internal readonly record struct ProtectedDescription(byte[] Ciphertext, string KeyVersion);

public sealed class DisabledIncidentService : IIncidentService
{
    public Task<IncidentResult> OpenAsync(
        OpenIncidentCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new IncidentInfrastructureException("The incident module is disabled.");
    }
}
