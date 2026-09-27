using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Incidents.Application.Incidents;
using Incidents.Domain;
using Incidents.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;

namespace Incidents.Infrastructure.Incidents;

/// <summary>
/// Closes an INC-001 incident into one terminal outcome. Resolution writes only
/// <c>incidents.incidents</c> — its status and <c>resolved_at</c> — beside one audit entry and one
/// idempotency record, all in the same tenant transaction. It never writes <c>orders.orders</c>:
/// a closed incident only stops counting as unresolved for ORD-002, which stays the sole writer of
/// order state.
/// </summary>
public sealed partial class PostgreSqlIncidentService
{
    internal const string ResolveIdempotencyScope = "INC-001:RESOLVE_INCIDENT";
    internal const string ResolvedAuditAction = "incidents.incident.resolved";
    internal const string RejectedAuditAction = "incidents.incident.rejected";

    private const int ResolvedResponseStatus = 200;

    public async Task<IncidentResult> ResolveAsync(
        ResolveIncidentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        if (!IncidentRequestPolicy.IsValidResolveCommandShape(command) ||
            !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey))
        {
            throw new IncidentConflictException("INVALID_REQUEST");
        }

        _ = IncidentContract.TryParseResolutionOutcome(command.Outcome, out var outcome);
        var terminalStatus = IncidentResolutionPolicy.TerminalStatus(outcome).ToContractValue();
        var requestHash = ComputeResolutionRequestHash(command);

        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
                async (dbContext, token) =>
                {
                    // Capability is settled before the incident's existence, its status, the
                    // idempotency lock or any replay evidence is touched, so a driver — even the
                    // one who opened the incident — learns nothing by trying to close it.
                    var capability = await IncidentsSql.ReadResolutionCapabilityAsync(
                        dbContext,
                        command.ActorId,
                        command.OrganizationId,
                        token);
                    if (!IncidentResolutionAuthorizationPolicy.MayResolve(
                            capability.IsActiveDispatcher,
                            capability.IsActivePlatformAdmin,
                            command.MfaSatisfied))
                    {
                        throw new IncidentForbiddenException();
                    }

                    await IncidentsSql.AcquireIdempotencyLockAsync(
                        dbContext,
                        command.OrganizationId,
                        ResolveIdempotencyScope,
                        command.IdempotencyKey,
                        token);

                    var replay = await ReadResolutionReplayAsync(
                        dbContext, command, requestHash, terminalStatus, token);
                    if (replay is not null)
                    {
                        return replay;
                    }

                    // The row lock serializes every closing request for this incident: a racing
                    // request with another key waits here and then reads the committed terminal
                    // status, so exactly one outcome is ever applied.
                    var incident = await IncidentsSql.ReadIncidentForUpdateAsync(
                        dbContext,
                        command.IncidentId,
                        command.OrganizationId,
                        token) ?? throw new IncidentNotFoundException();

                    if (!IncidentContract.TryParseStatus(incident.Status, out var current) ||
                        !IncidentResolutionPolicy.CanResolve(current, outcome))
                    {
                        throw new IncidentConflictException("INCIDENT_STATE_CONFLICT");
                    }

                    var result = new IncidentResult(
                        incident.Id,
                        incident.OrderId,
                        terminalStatus,
                        incident.Severity,
                        incident.IncidentType,
                        incident.ReasonCode,
                        incident.NextAction,
                        incident.CustodyAcquired,
                        incident.OccurredAt,
                        incident.SlaDueAt,
                        incident.EvidenceProofIds);

                    await InsertReservationAsync(
                        dbContext, command.OrganizationId, ResolveIdempotencyScope, command.IdempotencyKey,
                        requestHash, now, token);
                    await CloseIncidentAsync(dbContext, incident.Id, terminalStatus, now, token);
                    await WriteResolutionAuditAsync(
                        dbContext, command, incident, outcome, terminalStatus, now, token);
                    await CompleteReservationAsync(
                        dbContext, command.OrganizationId, ResolveIdempotencyScope, command.IdempotencyKey,
                        requestHash, ResolvedResponseStatus, result, now, token);
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
    /// The canonical form of a resolution request: the tenant, the incident, the outcome and the
    /// exact reason. The actor is not part of it, so an authorized retry by the same operator after
    /// a network failure replays instead of conflicting.
    /// </summary>
    private static byte[] ComputeResolutionRequestHash(ResolveIncidentCommand command)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("tenant", command.OrganizationId.ToString("D"));
            writer.WriteString("incident_id", command.IncidentId.ToString("D"));
            writer.WriteString("outcome", command.Outcome);
            writer.WriteBase64String("reason", Encoding.UTF8.GetBytes(command.Reason));
            writer.WriteEndObject();
        }

        return SHA256.HashData(stream.ToArray());
    }

    /// <summary>
    /// A stored resolution is only replayed while the incident it names is still visible in this
    /// tenant and still carries the recorded terminal outcome, which nothing may change afterwards.
    /// </summary>
    private static async Task<IncidentResult?> ReadResolutionReplayAsync(
        IncidentsDbContext context,
        ResolveIncidentCommand command,
        byte[] requestHash,
        string terminalStatus,
        CancellationToken cancellationToken)
    {
        var result = await ReadStoredResultAsync(
            context,
            command.OrganizationId,
            ResolveIdempotencyScope,
            command.IdempotencyKey,
            requestHash,
            ResolvedResponseStatus,
            cancellationToken);
        if (result is null)
        {
            return null;
        }

        if (result.Id != command.IncidentId || result.Status != terminalStatus)
        {
            throw new IncidentConflictException("IDEMPOTENCY_CORRUPT");
        }

        var (connection, transaction) = IncidentsSql.Database(context);
        await using var stored = new NpgsqlCommand(
            """
            SELECT i.order_id,i.status,i.resolved_at IS NOT NULL
            FROM incidents.incidents i
            WHERE i.id=@incident AND (i.owner_org_id=@organization OR i.operator_org_id=@organization)
            """,
            connection,
            transaction);
        stored.Parameters.Add(IncidentsSql.P("incident", NpgsqlDbType.Uuid, result.Id));
        stored.Parameters.Add(IncidentsSql.P("organization", NpgsqlDbType.Uuid, command.OrganizationId));
        await using var reader = await stored.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetGuid(0) != result.OrderId ||
            reader.GetString(1) != result.Status ||
            !reader.GetBoolean(2))
        {
            throw new IncidentConflictException("IDEMPOTENCY_CORRUPT");
        }

        return result;
    }

    /// <summary>
    /// The only write resolution makes to the incident: the terminal status and the moment it was
    /// reached. The pending-status predicate repeats, at the store, the state machine the caller
    /// already enforced under the row lock.
    /// </summary>
    private static async Task CloseIncidentAsync(
        IncidentsDbContext context,
        Guid incidentId,
        string terminalStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = IncidentsSql.Database(context);
        await using var update = new NpgsqlCommand(
            """
            UPDATE incidents.incidents
            SET status=@status,resolved_at=@resolved_at
            WHERE id=@incident AND status IN ('OPEN','INVESTIGATING')
            """,
            connection,
            transaction);
        update.Parameters.Add(IncidentsSql.P("status", NpgsqlDbType.Text, terminalStatus));
        update.Parameters.Add(IncidentsSql.P("resolved_at", NpgsqlDbType.TimestampTz, now));
        update.Parameters.Add(IncidentsSql.P("incident", NpgsqlDbType.Uuid, incidentId));
        RequireOne(await update.ExecuteNonQueryAsync(cancellationToken), "The incident was not closed.");
    }

    /// <summary>
    /// The resolution evidence. The reason lives only here, append-only and redacted like every
    /// other audit payload; the incident's protected description is never copied into it.
    /// </summary>
    private async Task WriteResolutionAuditAsync(
        IncidentsDbContext context,
        ResolveIncidentCommand command,
        LockedIncident incident,
        IncidentResolutionOutcome outcome,
        string terminalStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                incident_id = incident.Id,
                order_id = incident.OrderId,
                previous_status = incident.Status,
                outcome = terminalStatus,
                resolution_reason = command.Reason,
                resolved_at = now,
            }, JsonOptions));
        var (connection, transaction) = IncidentsSql.Database(context);
        await auditWriter.WriteAsync(
            connection,
            transaction,
            new AuditEntry(
                Guid.NewGuid(),
                command.OrganizationId,
                command.ActorId,
                outcome == IncidentResolutionOutcome.Resolved ? ResolvedAuditAction : RejectedAuditAction,
                AuditEntityType,
                incident.Id,
                command.RequestId,
                redactor.Redact(document.RootElement),
                now),
            cancellationToken);
    }
}
