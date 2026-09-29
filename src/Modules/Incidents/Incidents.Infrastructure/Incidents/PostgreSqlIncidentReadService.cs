using Incidents.Application.Incidents;
using Incidents.Domain;
using Incidents.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Incidents.Infrastructure.Incidents;

/// <summary>
/// API-INC-LIST-PROOFS-2026-09-29: listIncidents and getIncident. Each read runs in one explicit
/// tenant transaction (set_config after BEGIN, FORCE RLS), settles the resolution capability before
/// any incident row is touched and never writes. The protected description, the actor and the
/// resolution reason are never read, so no personal data can reach the response.
/// </summary>
public sealed class PostgreSqlIncidentReadService(
    TenantTransactionContext<IncidentsDbContext> transactionContext) : IIncidentReadService
{
    private const string SelectIncident =
        """
        SELECT i.id,i.order_id,i.status,i.severity,i.incident_type,i.reason_code,i.next_action,
               i.custody_acquired,i.occurred_at,i.sla_due_at,i.created_at,
               COALESCE(
                 (SELECT array_agg(e.proof_id ORDER BY e.proof_id)
                  FROM incidents.incident_evidence e WHERE e.incident_id=i.id),
                 '{}'::uuid[])
        FROM incidents.incidents i
        """;

    public async Task<IncidentPage> ListAsync(ListIncidentsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!IncidentReadPolicy.IsValidShape(query))
        {
            throw new IncidentConflictException("INVALID_REQUEST");
        }

        return await ExecuteAsync(
            query.ActorId,
            query.OrganizationId,
            query.MfaSatisfied,
            async (dbContext, token) =>
            {
                var (connection, transaction) = IncidentsSql.Database(dbContext);
                // FORCE RLS already hides every other tenant's incident; the explicit predicate
                // keeps the operator organization's view and the owner's view on the same plan.
                await using var command = new NpgsqlCommand(
                    SelectIncident +
                    """

                    WHERE (i.owner_org_id=@organization OR i.operator_org_id=@organization)
                      AND (@status::text IS NULL OR i.status=@status::text)
                      AND (@order::uuid IS NULL OR i.order_id=@order::uuid)
                      AND (@has_cursor=false OR (i.created_at,i.id) < (@cursor_created_at,@cursor_id))
                    ORDER BY i.created_at DESC,i.id DESC
                    LIMIT @take
                    """,
                    connection,
                    transaction);
                command.Parameters.Add(IncidentsSql.P("organization", NpgsqlDbType.Uuid, query.OrganizationId));
                command.Parameters.Add(IncidentsSql.P("status", NpgsqlDbType.Text, query.Status));
                command.Parameters.Add(IncidentsSql.P("order", NpgsqlDbType.Uuid, query.OrderId));
                command.Parameters.Add(IncidentsSql.P("has_cursor", NpgsqlDbType.Boolean, query.Cursor is not null));
                command.Parameters.Add(IncidentsSql.P(
                    "cursor_created_at",
                    NpgsqlDbType.TimestampTz,
                    query.Cursor?.CreatedAt ?? DateTimeOffset.UnixEpoch));
                command.Parameters.Add(IncidentsSql.P("cursor_id", NpgsqlDbType.Uuid, query.Cursor?.Id ?? Guid.Empty));
                command.Parameters.Add(IncidentsSql.P("take", NpgsqlDbType.Integer, IncidentReadPolicy.PageSize + 1));

                var rows = new List<(IncidentResult Incident, DateTimeOffset CreatedAt)>();
                await using (var reader = await command.ExecuteReaderAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        rows.Add(ReadIncident(reader));
                    }
                }

                var hasMore = rows.Count > IncidentReadPolicy.PageSize;
                if (hasMore)
                {
                    rows.RemoveAt(rows.Count - 1);
                }

                var next = hasMore
                    ? IncidentCursorCodec.Encode(new IncidentCursor(rows[^1].CreatedAt, rows[^1].Incident.Id))
                    : null;
                return new IncidentPage(rows.Select(row => row.Incident).ToArray(), next);
            },
            cancellationToken);
    }

    public async Task<IncidentResult> GetAsync(GetIncidentQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!IncidentReadPolicy.IsValidShape(query))
        {
            throw new IncidentNotFoundException();
        }

        return await ExecuteAsync(
            query.ActorId,
            query.OrganizationId,
            query.MfaSatisfied,
            async (dbContext, token) =>
            {
                var (connection, transaction) = IncidentsSql.Database(dbContext);
                // A missing incident and another tenant's incident take the same plan and are the
                // same uniform not-found.
                await using var command = new NpgsqlCommand(
                    SelectIncident +
                    """

                    WHERE i.id=@incident
                      AND (i.owner_org_id=@organization OR i.operator_org_id=@organization)
                    """,
                    connection,
                    transaction);
                command.Parameters.Add(IncidentsSql.P("incident", NpgsqlDbType.Uuid, query.IncidentId));
                command.Parameters.Add(IncidentsSql.P("organization", NpgsqlDbType.Uuid, query.OrganizationId));
                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    throw new IncidentNotFoundException();
                }

                return ReadIncident(reader).Incident;
            },
            cancellationToken);
    }

    /// <summary>
    /// Opens the tenant transaction and settles the capability first, inside it, from the persisted
    /// memberships: nothing about any incident is read for an actor who may not read incidents.
    /// </summary>
    private async Task<T> ExecuteAsync<T>(
        Guid actorId,
        Guid organizationId,
        bool mfaSatisfied,
        Func<IncidentsDbContext, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(actorId, [organizationId]),
                async (dbContext, token) =>
                {
                    var capability = await IncidentsSql.ReadResolutionCapabilityAsync(
                        dbContext,
                        actorId,
                        organizationId,
                        token);
                    if (!IncidentReadPolicy.MayRead(
                            capability.IsActiveDispatcher,
                            capability.IsActivePlatformAdmin,
                            mfaSatisfied))
                    {
                        throw new IncidentForbiddenException();
                    }

                    return await read(dbContext, token);
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
        catch (IncidentInfrastructureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException)
        {
            throw new IncidentInfrastructureException("The incident store is unavailable.", exception);
        }
    }

    /// <summary>
    /// Maps one row to the published Incident. A stored value outside the published vocabulary, or
    /// an incident without its mandatory evidence, fails closed instead of being published.
    /// </summary>
    private static (IncidentResult Incident, DateTimeOffset CreatedAt) ReadIncident(NpgsqlDataReader reader)
    {
        var status = reader.GetString(2);
        var severity = reader.GetString(3);
        var reasonCode = reader.GetString(5);
        var nextAction = reader.GetString(6);
        var evidence = reader.GetFieldValue<Guid[]>(11);
        if (!IncidentContract.TryParseStatus(status, out _) ||
            !IncidentContract.TryParseSeverity(severity, out _) ||
            !IncidentContract.TryParseReasonCode(reasonCode, out _) ||
            !IncidentContract.TryParseNextAction(nextAction, out _) ||
            evidence.Length is < IncidentEvidencePolicy.MinimumEvidenceCount or > IncidentEvidencePolicy.MaximumEvidenceCount)
        {
            throw new IncidentInfrastructureException("A stored incident is outside the published contract.");
        }

        return (
            new IncidentResult(
                reader.GetGuid(0),
                reader.GetGuid(1),
                status,
                severity,
                reader.GetString(4),
                reasonCode,
                nextAction,
                reader.GetBoolean(7),
                reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                evidence),
            reader.GetFieldValue<DateTimeOffset>(10));
    }
}
