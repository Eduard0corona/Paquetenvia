using System.Security.Cryptography;
using System.Text.Json;
using Finance.Application;
using Finance.Application.Cod;
using Finance.Application.Settlements;
using Finance.Domain;
using Finance.Domain.Settlements;
using Finance.Infrastructure.Financials;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application.Auditing;
using static Finance.Infrastructure.Persistence.FinanceSql;

namespace Finance.Infrastructure.Settlements;

public sealed partial class PostgreSqlSettlementService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// The one active membership that decides settlement capability: the most privileged one, PLATFORM_ADMIN
    /// before FINANCE, so holding FINANCE as well never lets a PLATFORM_ADMIN skip MFA.
    /// </summary>
    private const string AuthorizationSql =
        """
        SELECT u.status,m.role
        FROM identity.users u
        LEFT JOIN organizations.organization_memberships m
          ON m.user_id=u.id AND m.organization_id=@organization AND m.status='ACTIVE'
        WHERE u.id=@actor
        ORDER BY CASE m.role
                   WHEN 'PLATFORM_ADMIN' THEN 0
                   WHEN 'FINANCE' THEN 1
                   ELSE 2 END,
                 m.role
        LIMIT 1
        """;

    /// <summary>
    /// The driver's payable work in the period. An assignment is a source when it bears cost exactly as
    /// FIN-001 counts it (same statuses, same cost_cents), is a modality the driver is paid for directly, and
    /// its order's latest ORD-002 outcome event — the append-only DELIVERED or RETURNED status change and its
    /// occurred_at — falls inside the period's UTC boundaries. Sources already held by a settlement that is not
    /// VOID are left out, so overlapping periods never pay a source twice; the ledger guard enforces the same.
    /// </summary>
    private const string EligibleSourcesSql =
        """
        SELECT a.id,a.order_id,a.cost_cents,o.status,outcome.public_event_code,outcome.occurred_at
        FROM dispatch.assignments a
        JOIN orders.orders o ON o.id=a.order_id
        JOIN LATERAL (
          SELECT e.public_event_code,e.occurred_at
          FROM orders.order_events e
          WHERE e.order_id=a.order_id
            AND e.event_type='ORDER_STATUS_CHANGED'
            AND e.public_event_code = ANY(@outcomes)
          ORDER BY e.aggregate_version DESC
          LIMIT 1) outcome ON true
        WHERE a.driver_id=@driver
          AND a.status = ANY(@assignment_statuses)
          AND a.assignment_type = ANY(@assignment_types)
          AND (a.owner_org_id=@organization OR a.operator_org_id=@organization)
          AND outcome.occurred_at >= @starts
          AND outcome.occurred_at < @ends
          AND NOT EXISTS (
            SELECT 1
            FROM finance.settlement_lines l
            JOIN finance.settlements s ON s.id=l.settlement_id
            WHERE l.owner_org_id=@organization
              AND l.source_reference=@source_prefix || a.id::text
              AND l.line_type<>'ADJUSTMENT'
              AND s.status<>'VOID')
        ORDER BY outcome.occurred_at,a.id
        """;

    private const string SourceStatesSql =
        """
        SELECT l.line_type,o.status,o.cod_expected_cents,c.status,c.amount_cents,
               EXISTS (
                 SELECT 1
                 FROM incidents.incidents i
                 WHERE i.order_id=l.order_id AND i.status = ANY(@pending_incidents))
        FROM finance.settlement_lines l
        LEFT JOIN orders.orders o ON o.id=l.order_id
        LEFT JOIN finance.cod_transactions c ON c.order_id=l.order_id
        WHERE l.settlement_id=@settlement AND l.owner_org_id=@organization AND l.order_id IS NOT NULL
        ORDER BY l.created_at,l.id
        """;

    private const string HeaderSql =
        """
        SELECT id,payee_type,payee_id,status,total_cents,period_from,period_to,created_at
        FROM finance.settlements
        WHERE id=@settlement AND owner_org_id=@organization
        """;

    private const string LinesSql =
        """
        SELECT id,line_type,order_id,amount_cents,source_reference,created_at
        FROM finance.settlement_lines
        WHERE settlement_id=@settlement AND owner_org_id=@organization
        ORDER BY created_at,id
        """;

    /// <summary>
    /// One keyset page of settlement headers, newest first. The tenant predicate is explicit and RLS still
    /// applies underneath it; a period filter keeps the settlements whose whole period lies inside it.
    /// </summary>
    private const string PageHeadersSql =
        """
        SELECT id,payee_type,payee_id,status,total_cents,period_from,period_to,created_at
        FROM finance.settlements
        WHERE owner_org_id=@organization
          AND (@status::text IS NULL OR status=@status::text)
          AND (@period_from::date IS NULL OR period_from >= @period_from::date)
          AND (@period_to::date IS NULL OR period_to <= @period_to::date)
          AND (@cursor_created::timestamptz IS NULL
            OR (created_at,id) < (@cursor_created::timestamptz,@cursor_id::uuid))
        ORDER BY created_at DESC,id DESC
        LIMIT @limit
        """;

    private const string PageLinesSql =
        """
        SELECT settlement_id,id,line_type,order_id,amount_cents,source_reference,created_at
        FROM finance.settlement_lines
        WHERE owner_org_id=@organization AND settlement_id = ANY(@settlements)
        ORDER BY settlement_id,created_at,id
        """;

    private async Task<IReadOnlyList<SettlementResult>> ReadPageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ListSettlementsQuery query,
        int limit,
        CancellationToken cancellationToken)
    {
        var headers = new List<(Guid Id, string PayeeType, Guid PayeeId, SettlementStatus Status, long TotalCents,
            DateOnly PeriodFrom, DateOnly PeriodTo, DateTimeOffset CreatedAt)>();
        await using (var command = Create(connection, transaction, PageHeadersSql, gateway.CommandTimeoutSeconds))
        {
            command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, query.OrganizationId));
            command.Parameters.Add(P("status", NpgsqlDbType.Text, query.Status));
            command.Parameters.Add(P("period_from", NpgsqlDbType.Date, query.PeriodFrom));
            command.Parameters.Add(P("period_to", NpgsqlDbType.Date, query.PeriodTo));
            command.Parameters.Add(P("cursor_created", NpgsqlDbType.TimestampTz, query.Cursor?.CreatedAt));
            command.Parameters.Add(P("cursor_id", NpgsqlDbType.Uuid, query.Cursor?.Id));
            command.Parameters.Add(P("limit", NpgsqlDbType.Integer, limit));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!SettlementContractValues.TryParsePayeeType(reader.GetString(1), out var payee) ||
                    !SettlementContractValues.TryParseStatus(reader.GetString(3), out var status))
                {
                    throw Inconsistent();
                }

                headers.Add((
                    reader.GetGuid(0),
                    payee.ToContractValue(),
                    reader.GetGuid(2),
                    status,
                    reader.GetInt64(4),
                    reader.GetFieldValue<DateOnly>(5),
                    reader.GetFieldValue<DateOnly>(6),
                    reader.GetFieldValue<DateTimeOffset>(7)));
            }
        }

        if (headers.Count == 0)
        {
            return [];
        }

        var lines = headers.ToDictionary(header => header.Id, _ => new List<SettlementLineResult>());
        await using (var command = Create(connection, transaction, PageLinesSql, gateway.CommandTimeoutSeconds))
        {
            command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, query.OrganizationId));
            command.Parameters.Add(P(
                "settlements", NpgsqlDbType.Array | NpgsqlDbType.Uuid, headers.Select(header => header.Id).ToArray()));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!SettlementContractValues.TryParseLineType(reader.GetString(2), out var lineType) ||
                    !lines.TryGetValue(reader.GetGuid(0), out var owner))
                {
                    throw Inconsistent();
                }

                owner.Add(new(
                    reader.GetGuid(1),
                    lineType.ToContractValue(),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.GetInt64(4),
                    reader.GetString(5),
                    reader.GetFieldValue<DateTimeOffset>(6)));
            }
        }

        return headers.Select(header =>
        {
            var owned = lines[header.Id];
            if (!SettlementLedger.Reconciles(header.TotalCents, owned.Select(line => line.AmountCents)))
            {
                throw Inconsistent();
            }

            return new SettlementResult(
                header.Id,
                header.PayeeType,
                header.PayeeId,
                header.Status.ToContractValue(),
                header.TotalCents,
                header.PeriodFrom,
                header.PeriodTo,
                header.CreatedAt,
                owned);
        }).ToArray();
    }

    private async Task<FinanceAuthorizationContext> ReadAuthorizationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        bool mfaSatisfied,
        CancellationToken cancellationToken)
    {
        await using var command = Create(connection, transaction, AuthorizationSql, gateway.CommandTimeoutSeconds);
        command.Parameters.Add(P("actor", NpgsqlDbType.Uuid, actorId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(0) == "ACTIVE",
                !reader.IsDBNull(1),
                mfaSatisfied,
                false)
            : new(null, false, false, mfaSatisfied, false);
    }

    private async Task<bool> DriverIsVisibleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid driverId,
        CancellationToken cancellationToken)
    {
        await using var command = Create(
            connection,
            transaction,
            "SELECT EXISTS (SELECT 1 FROM drivers.driver_profiles WHERE id=@driver AND org_id=@organization)",
            gateway.CommandTimeoutSeconds);
        command.Parameters.Add(P("driver", NpgsqlDbType.Uuid, driverId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task AcquirePayeeLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid driverId,
        CancellationToken cancellationToken)
    {
        await using var command = Create(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,0));",
            gateway.CommandTimeoutSeconds);
        command.Parameters.Add(P(
            "key",
            NpgsqlDbType.Text,
            $"SET-001:SETTLEMENT_PAYEE:{organizationId:D}:DRIVER:{driverId:D}"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertDraftAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid settlementId,
        CreateSettlementCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var insert = Create(
            connection,
            transaction,
            """
            INSERT INTO finance.settlements(
              id,owner_org_id,payee_type,payee_id,status,total_cents,period_from,period_to,created_at)
            VALUES (@id,@organization,@payee_type,@payee,'DRAFT',0,@from,@to,@now)
            """,
            gateway.CommandTimeoutSeconds);
        insert.Parameters.Add(P("id", NpgsqlDbType.Uuid, settlementId));
        insert.Parameters.Add(P("organization", NpgsqlDbType.Uuid, command.OrganizationId));
        insert.Parameters.Add(P("payee_type", NpgsqlDbType.Text, SettlementPayeeType.Driver.ToContractValue()));
        insert.Parameters.Add(P("payee", NpgsqlDbType.Uuid, command.DriverId));
        insert.Parameters.Add(P("from", NpgsqlDbType.Date, command.PeriodFrom));
        insert.Parameters.Add(P("to", NpgsqlDbType.Date, command.PeriodTo));
        insert.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        RequireOne(await insert.ExecuteNonQueryAsync(cancellationToken), "Settlement insert failed.");
    }

    private async Task<IReadOnlyList<EligibleSource>> ReadEligibleSourcesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid driverId,
        SettlementPeriod period,
        CancellationToken cancellationToken)
    {
        await using var command = Create(connection, transaction, EligibleSourcesSql, gateway.CommandTimeoutSeconds);
        command.Parameters.Add(Texts("outcomes", SettlementSourcePolicy.OutcomeEventCodes));
        command.Parameters.Add(P("driver", NpgsqlDbType.Uuid, driverId));
        command.Parameters.Add(Texts(
            "assignment_statuses", PostgreSqlOrderFinancialsService.CostBearingAssignmentStatuses));
        command.Parameters.Add(Texts("assignment_types", SettlementSourcePolicy.DriverPayableAssignmentTypes));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(P("starts", NpgsqlDbType.TimestampTz, period.StartsAtUtc));
        command.Parameters.Add(P("ends", NpgsqlDbType.TimestampTz, period.EndsBeforeUtc));
        command.Parameters.Add(P("source_prefix", NpgsqlDbType.Text, SettlementSourcePolicy.AssignmentSourcePrefix));

        var sources = new List<EligibleSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // The outcome event says when the work happened; the current order status must still prove it.
            if (SettlementSourcePolicy.Classify(reader.GetString(4), reader.GetString(3)) is { } lineType)
            {
                sources.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), lineType));
            }
        }

        return sources;
    }

    private async Task InsertLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid lineId,
        Guid settlementId,
        Guid organizationId,
        SettlementLineType lineType,
        Guid? orderId,
        long amountCents,
        string sourceReference,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var insert = Create(
            connection,
            transaction,
            """
            INSERT INTO finance.settlement_lines(
              id,settlement_id,owner_org_id,order_id,line_type,amount_cents,source_reference,created_at)
            VALUES (@id,@settlement,@organization,@order,@line_type,@amount,@source,@now)
            """,
            gateway.CommandTimeoutSeconds);
        insert.Parameters.Add(P("id", NpgsqlDbType.Uuid, lineId));
        insert.Parameters.Add(P("settlement", NpgsqlDbType.Uuid, settlementId));
        insert.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        insert.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        insert.Parameters.Add(P("line_type", NpgsqlDbType.Text, lineType.ToContractValue()));
        insert.Parameters.Add(P("amount", NpgsqlDbType.Bigint, amountCents));
        insert.Parameters.Add(P("source", NpgsqlDbType.Text, sourceReference));
        insert.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        RequireOne(await insert.ExecuteNonQueryAsync(cancellationToken), "Settlement line insert failed.");
    }

    /// <summary>
    /// Moves the header from exactly the observed status and total. The caller holds the row lock (or created
    /// the row), so any other outcome is an internal inconsistency and fails closed.
    /// </summary>
    private async Task UpdateHeaderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid settlementId,
        SettlementStatus source,
        long sourceTotalCents,
        SettlementStatus target,
        long targetTotalCents,
        CancellationToken cancellationToken)
    {
        await using var update = Create(
            connection,
            transaction,
            """
            UPDATE finance.settlements
            SET status=@target,total_cents=@target_total
            WHERE id=@settlement AND owner_org_id=@organization
              AND status=@source AND total_cents=@source_total
            """,
            gateway.CommandTimeoutSeconds);
        update.Parameters.Add(P("target", NpgsqlDbType.Text, target.ToContractValue()));
        update.Parameters.Add(P("target_total", NpgsqlDbType.Bigint, targetTotalCents));
        update.Parameters.Add(P("settlement", NpgsqlDbType.Uuid, settlementId));
        update.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        update.Parameters.Add(P("source", NpgsqlDbType.Text, source.ToContractValue()));
        update.Parameters.Add(P("source_total", NpgsqlDbType.Bigint, sourceTotalCents));
        RequireOne(await update.ExecuteNonQueryAsync(cancellationToken), "Settlement update failed.");
    }

    /// <summary>
    /// The persisted settlement and its lines, verified before anything relies on them: every value must be
    /// in the published vocabulary and the header total must equal the exact sum of the lines. Anything else
    /// is refused as unavailable rather than shown, and it is never repaired.
    /// </summary>
    private async Task<SettlementSnapshot?> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid settlementId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        SettlementStatus status;
        Guid id;
        string payeeType;
        Guid payeeId;
        long totalCents;
        DateOnly periodFrom;
        DateOnly periodTo;
        DateTimeOffset createdAt;
        await using (var header = Create(
            connection,
            transaction,
            HeaderSql + (forUpdate ? " FOR UPDATE" : string.Empty),
            gateway.CommandTimeoutSeconds))
        {
            header.Parameters.Add(P("settlement", NpgsqlDbType.Uuid, settlementId));
            header.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
            await using var reader = await header.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            if (!SettlementContractValues.TryParsePayeeType(reader.GetString(1), out var payee) ||
                !SettlementContractValues.TryParseStatus(reader.GetString(3), out status))
            {
                throw Inconsistent();
            }

            id = reader.GetGuid(0);
            payeeType = payee.ToContractValue();
            payeeId = reader.GetGuid(2);
            totalCents = reader.GetInt64(4);
            periodFrom = reader.GetFieldValue<DateOnly>(5);
            periodTo = reader.GetFieldValue<DateOnly>(6);
            createdAt = reader.GetFieldValue<DateTimeOffset>(7);
        }

        var lines = new List<SettlementLineResult>();
        await using (var query = Create(connection, transaction, LinesSql, gateway.CommandTimeoutSeconds))
        {
            query.Parameters.Add(P("settlement", NpgsqlDbType.Uuid, settlementId));
            query.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!SettlementContractValues.TryParseLineType(reader.GetString(1), out var lineType))
                {
                    throw Inconsistent();
                }

                lines.Add(new(
                    reader.GetGuid(0),
                    lineType.ToContractValue(),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.GetInt64(3),
                    reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }

        if (!SettlementLedger.Reconciles(totalCents, lines.Select(line => line.AmountCents)))
        {
            throw Inconsistent();
        }

        return new(
            status,
            new(
                id,
                payeeType,
                payeeId,
                status.ToContractValue(),
                totalCents,
                periodFrom,
                periodTo,
                createdAt,
                lines));
    }

    /// <summary>
    /// The current state behind every order-bearing line, in one statement. A line whose order is no longer
    /// visible, carries an unknown vocabulary or no longer proves the outcome it was paid for is an internal
    /// inconsistency: approval fails closed instead of deciding on partial evidence.
    /// </summary>
    private async Task<IReadOnlyList<SettlementSourceState>> ReadSourceStatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid settlementId,
        CancellationToken cancellationToken)
    {
        await using var command = Create(connection, transaction, SourceStatesSql, gateway.CommandTimeoutSeconds);
        command.Parameters.Add(Texts("pending_incidents", SettlementSourcePolicy.PendingIncidentStatuses));
        command.Parameters.Add(P("settlement", NpgsqlDbType.Uuid, settlementId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));

        var states = new List<SettlementSourceState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!SettlementContractValues.TryParseLineType(reader.GetString(0), out var lineType) ||
                reader.IsDBNull(1))
            {
                throw Inconsistent();
            }

            var orderStatus = reader.GetString(1);
            var outcome = lineType switch
            {
                SettlementLineType.Delivery => SettlementSourcePolicy.DeliveredOutcome,
                SettlementLineType.Return => SettlementSourcePolicy.ReturnedOutcome,
                _ => null,
            };
            if (SettlementSourcePolicy.Classify(outcome, orderStatus) != lineType)
            {
                throw Inconsistent();
            }

            CodStatus? codStatus = null;
            if (!reader.IsDBNull(3))
            {
                if (!FinanceContractValues.TryParseCodStatus(reader.GetString(3), out var parsed))
                {
                    throw Inconsistent();
                }

                codStatus = parsed;
            }

            states.Add(new(
                lineType,
                orderStatus,
                MoneyCents.FromNonNegative(reader.GetInt64(2)),
                codStatus,
                reader.IsDBNull(4) ? null : new MoneyCents(reader.GetInt64(4)),
                reader.GetBoolean(5)));
        }

        return states;
    }

    private async Task<SettlementResult?> BeginIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        string scope,
        string key,
        byte[] requestHash,
        int expectedStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken)
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
                throw Conflict(SettlementConflictCode.IdempotencyConflict);
            }

            if (stored.Status != expectedStatus || stored.ResourceId is null ||
                string.IsNullOrWhiteSpace(stored.Body))
            {
                throw Conflict(SettlementConflictCode.InconsistentReplayEvidence);
            }

            SettlementResult? replay;
            try
            {
                // A body without its lines fails in the constructor rather than in the reader.
                replay = JsonSerializer.Deserialize<SettlementResult>(stored.Body, JsonOptions);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw Conflict(SettlementConflictCode.InconsistentReplayEvidence, exception);
            }

            return replay is not null && replay.Id == stored.ResourceId
                ? replay
                : throw Conflict(SettlementConflictCode.InconsistentReplayEvidence);
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

    private async Task CompleteIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        string scope,
        string key,
        byte[] requestHash,
        int status,
        Guid resourceId,
        SettlementResult response,
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

    private async Task WriteAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid auditId,
        Guid actorId,
        Guid organizationId,
        string action,
        Guid settlementId,
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
                auditId,
                organizationId,
                actorId,
                action,
                AuditEntityType,
                settlementId,
                requestId,
                redacted,
                occurredAt),
            cancellationToken);
        await failureInjector.OnStageAsync(FinanceTransactionStage.AuditInserted, cancellationToken);
    }

    private static NpgsqlParameter<string[]> Texts(string name, IEnumerable<string> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = values.ToArray() };

    private static FinanceUnavailableException Inconsistent() =>
        new("The persisted settlement ledger is inconsistent.");

    private sealed record EligibleSource(
        Guid AssignmentId,
        Guid OrderId,
        long AmountCents,
        SettlementLineType LineType);

    private sealed record SettlementSnapshot(SettlementStatus Status, SettlementResult Result);

    private sealed record IdempotencyRow(byte[] Hash, int? Status, string? Body, Guid? ResourceId);
}
