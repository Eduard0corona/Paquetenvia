using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Drivers.Application.Documents;
using Drivers.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;
using static Drivers.Infrastructure.Voice.DriverVoiceSql;

namespace Drivers.Infrastructure.Documents;

/// <summary>
/// DRV-DOC-001-IN-PERSON-2026-11-02 (docs/adr/ADR-DRV-DOC-001-IN-PERSON-DRIVER-DOCUMENTS.md): the in-person driver
/// documents of the Drivers module. Every statement runs in one explicit tenant transaction of the selected organization
/// (set_config after BEGIN, paqueteria_app, FORCE RLS on <c>drivers.driver_documents</c>), and every write touches only
/// <c>drivers.driver_documents</c>, <c>platform.audit_logs</c> and <c>platform.idempotency_keys</c>: no other module's
/// schema, so no AI-13 flow is added (Y10: revoking leaves current stops to dispatch).
/// <list type="bullet">
/// <item>A record locks the driver profile (an OWN profile of the organization that is not INACTIVE; anything else is the
/// uniform 404), checks the type against the vehicle's required list (Y11), computes the last valid local day (Y5, Y12)
/// and inserts an IN_PERSON row with no key or hash, its audit row (types and codes only) and its idempotency answer in
/// the same transaction.</item>
/// <item>A revoke locks the same profile and turns only the current VALID row of its type into REVOKED; revoking a
/// REVOKED row again answers it unchanged.</item>
/// <item>The lists read the latest row per required type, the DSP-001 rule (<c>created_at DESC, id DESC</c>), and fail
/// closed (503) on a stored value outside the published vocabulary.</item>
/// </list>
/// Nothing is read or written while <c>Drivers:Documents:Enabled</c> is false (Y2). No log line, audit payload or answer
/// carries a document number, a file, an object key or a hash; none exists for an in-person document.
/// </summary>
public sealed class PostgreSqlDriverDocumentService(
    TenantTransactionContext<DriversDbContext> transactionContext,
    IOptions<DriversOptions> options,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor auditRedactor,
    IClock clock,
    ILogger<PostgreSqlDriverDocumentService> logger) : IDriverDocumentService
{
    public const string RecordedAction = "DRIVER_DOCUMENT_RECORDED";
    public const string RevokedAction = "DRIVER_DOCUMENT_REVOKED";
    public const string EntityType = "DriverDocument";
    public const string IdempotencyScope = "DRV-DOC-001:RECORD_DRIVER_DOCUMENT";
    public static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions StoredJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private const string LatestDocumentsSql =
        """
        SELECT DISTINCT ON (d.driver_id,d.document_type)
               d.driver_id,d.id,d.document_type,d.status,d.rejection_code,d.expires_at,d.expiry_basis,d.created_at,
               d.revoked_at,((d.expires_at - interval '1 microsecond') AT TIME ZONE 'America/Mazatlan')::date
        FROM drivers.driver_documents d
        WHERE d.org_id=@organization AND d.driver_id=ANY(@drivers) AND d.document_type=ANY(@types)
        ORDER BY d.driver_id,d.document_type,d.created_at DESC,d.id DESC
        """;

    private const string DocumentColumns =
        """
        d.id,d.document_type,d.status,d.rejection_code,d.expires_at,d.expiry_basis,d.created_at,d.revoked_at,
        ((d.expires_at - interval '1 microsecond') AT TIME ZONE 'America/Mazatlan')::date
        """;

    private bool Enabled => options.Value.Documents?.Enabled == true;

    public async Task<DriverDocumentPageResult> ListAsync(ListDriverDocumentsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!Enabled)
        {
            return new DriverDocumentPageResult(false, [], null);
        }

        return await RunAsync(query.ActorId, query.OrganizationId, async (connection, transaction, token) =>
        {
            var drivers = new List<(Guid DriverId, string VehicleType)>();
            await using (var command = Command(
                connection,
                transaction,
                """
                SELECT p.id,p.vehicle_type
                FROM drivers.driver_profiles p
                WHERE p.org_id=@organization AND p.driver_type='OWN' AND p.status<>'INACTIVE'
                  AND (@after IS NULL OR p.id>@after)
                ORDER BY p.id
                LIMIT @limit
                """))
            {
                command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, query.OrganizationId));
                command.Parameters.Add(P("after", NpgsqlDbType.Uuid, query.AfterDriverId));
                command.Parameters.Add(P("limit", NpgsqlDbType.Integer, DriverDocumentPaging.PageSize + 1));
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    drivers.Add((reader.GetGuid(0), reader.GetString(1)));
                }
            }

            var hasMore = drivers.Count > DriverDocumentPaging.PageSize;
            var listed = drivers.Take(DriverDocumentPaging.PageSize).ToArray();
            var latest = await ReadLatestAsync(
                    connection, transaction, query.OrganizationId, listed.Select(row => row.DriverId).ToArray(), token)
                .ConfigureAwait(false);
            var items = listed
                .Select(row => new DriverDocumentSetResult(row.DriverId, Slots(row.DriverId, row.VehicleType, latest)))
                .ToArray();
            return new DriverDocumentPageResult(true, items, hasMore ? listed[^1].DriverId : null);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MyDriverDocumentsResult> ListMineAsync(
        Guid actorId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return new MyDriverDocumentsResult(false, []);
        }

        return await RunAsync(actorId, organizationId, async (connection, transaction, token) =>
        {
            // The driver's own profile whatever its status (Y8: the driver reads its documents in "Cuenta"); the session
            // already requires an ACTIVE DRIVER membership. Without a profile there, 403.
            Guid driverId;
            string vehicleType;
            await using (var command = Command(
                connection,
                transaction,
                "SELECT p.id,p.vehicle_type FROM drivers.driver_profiles p WHERE p.user_id=@actor AND p.org_id=@organization"))
            {
                command.Parameters.Add(P("actor", NpgsqlDbType.Uuid, actorId));
                command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    throw new DriverDocumentForbiddenException();
                }

                driverId = reader.GetGuid(0);
                vehicleType = reader.GetString(1);
            }

            var latest = await ReadLatestAsync(connection, transaction, organizationId, [driverId], token)
                .ConfigureAwait(false);
            return new MyDriverDocumentsResult(true, Slots(driverId, vehicleType, latest));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DriverDocumentResult> RecordAsync(
        RecordDriverDocumentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Enabled)
        {
            throw new DriverDocumentUnavailableException();
        }

        if (!IsWellFormed(command))
        {
            throw new ArgumentException("The driver document command is invalid.", nameof(command));
        }

        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        var requestHash = RequestHash(command);
        var documentId = Guid.NewGuid();
        var result = await RunAsync(command.ActorId, command.OrganizationId, async (connection, transaction, token) =>
        {
            await AdvisoryLockAsync(
                    connection,
                    transaction,
                    $"{command.OrganizationId:D}:{IdempotencyScope}:{command.IdempotencyKey}",
                    token)
                .ConfigureAwait(false);
            if (await ReadStoredAsync(connection, transaction, command, token).ConfigureAwait(false) is { } stored)
            {
                if (!CryptographicOperations.FixedTimeEquals(stored.RequestHash, requestHash) || stored.Body is null)
                {
                    throw new DriverDocumentConflictException(DriverDocumentConflictCodes.IdempotencyConflict);
                }

                return JsonSerializer.Deserialize<DriverDocumentResult>(stored.Body, StoredJsonOptions) ??
                    throw new DriverDocumentUnavailableException();
            }

            var vehicleType = await LockDriverAsync(
                    connection, transaction, command.OrganizationId, command.DriverId, token)
                .ConfigureAwait(false) ?? throw new DriverDocumentNotFoundException();
            if (!SlotTypes(vehicleType).Contains(command.DocumentType, StringComparer.Ordinal))
            {
                throw new DriverDocumentConflictException(DriverDocumentConflictCodes.DocumentTypeNotRequired);
            }

            DateOnly? validThrough = null;
            if (command.Decision == DriverDocumentVocabulary.Valid)
            {
                var today = await TodayAsync(connection, transaction, now, token).ConfigureAwait(false);
                var day = DriverDocumentCalendar.ValidThrough(today, command.ExpiryBasis!, command.PrintedDate);
                if (!DriverDocumentCalendar.IsAcceptable(today, day))
                {
                    throw new DriverDocumentConflictException(DriverDocumentConflictCodes.ExpiryDateOutOfRange);
                }

                validThrough = day;
            }

            DriverDocumentResult recorded;
            // statement_timestamp() is read after the profile lock, so documents of one driver are ordered by commit.
            await using (var insert = Command(
                connection,
                transaction,
                $"""
                INSERT INTO drivers.driver_documents AS d (
                  id,driver_id,org_id,document_type,object_key,sha256,expires_at,status,created_at,
                  evidence_kind,expiry_basis,rejection_code,reviewed_by,reviewed_at)
                VALUES (
                  @id,@driver,@organization,@type,NULL,NULL,
                  CASE WHEN @valid_through IS NULL THEN NULL
                       ELSE ((@valid_through + 1)::timestamp AT TIME ZONE 'America/Mazatlan') END,
                  @status,statement_timestamp(),'IN_PERSON',@basis,@rejection,@actor,statement_timestamp())
                RETURNING {DocumentColumns}
                """))
            {
                insert.Parameters.Add(P("id", NpgsqlDbType.Uuid, documentId));
                insert.Parameters.Add(P("driver", NpgsqlDbType.Uuid, command.DriverId));
                insert.Parameters.Add(P("organization", NpgsqlDbType.Uuid, command.OrganizationId));
                insert.Parameters.Add(P("type", NpgsqlDbType.Text, command.DocumentType));
                insert.Parameters.Add(P("valid_through", NpgsqlDbType.Date, validThrough));
                insert.Parameters.Add(P("status", NpgsqlDbType.Text, command.Decision));
                insert.Parameters.Add(P(
                    "basis",
                    NpgsqlDbType.Text,
                    command.Decision == DriverDocumentVocabulary.Valid ? command.ExpiryBasis : null));
                insert.Parameters.Add(P("rejection", NpgsqlDbType.Text, command.RejectionCode));
                insert.Parameters.Add(P("actor", NpgsqlDbType.Uuid, command.ActorId));
                await using var reader = await insert.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    throw new DriverDocumentUnavailableException();
                }

                recorded = ReadDocument(reader, 0);
            }

            await WriteAuditAsync(
                auditWriter,
                auditRedactor,
                connection,
                transaction,
                command.OrganizationId,
                command.ActorId,
                RecordedAction,
                EntityType,
                documentId,
                command.RequestId,
                new
                {
                    driver_id = command.DriverId,
                    document_id = documentId,
                    document_type = command.DocumentType,
                    evidence_kind = "IN_PERSON",
                    status = command.Decision,
                    expiry_basis = command.Decision == DriverDocumentVocabulary.Valid ? command.ExpiryBasis : null,
                    rejection_code = command.RejectionCode,
                },
                recorded.RecordedAt,
                token).ConfigureAwait(false);

            await using (var store = Command(
                connection,
                transaction,
                """
                INSERT INTO platform.idempotency_keys(
                  owner_org_id,scope,idempotency_key,request_hash,response_status,response_body,resource_id,created_at,expires_at)
                VALUES (@organization,@scope,@key,@hash,201,@body::jsonb,@resource,@now,@expires)
                """))
            {
                store.Parameters.Add(P("organization", NpgsqlDbType.Uuid, command.OrganizationId));
                store.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
                store.Parameters.Add(P("key", NpgsqlDbType.Text, command.IdempotencyKey));
                store.Parameters.Add(P("hash", NpgsqlDbType.Bytea, requestHash));
                store.Parameters.Add(P("body", NpgsqlDbType.Text, JsonSerializer.Serialize(recorded, StoredJsonOptions)));
                store.Parameters.Add(P("resource", NpgsqlDbType.Uuid, documentId));
                store.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
                store.Parameters.Add(P("expires", NpgsqlDbType.TimestampTz, now + IdempotencyLifetime));
                await store.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            return recorded;
        }, cancellationToken).ConfigureAwait(false);

        // Types and codes only: the organization and the actor reach the line as the logger factory's pseudonyms.
        logger.LogInformation(
            "Driver document recorded in person; type {DocumentType}, status {Status}",
            result.DocumentType,
            result.Status);
        return result;
    }

    public async Task<DriverDocumentResult> RevokeAsync(
        RevokeDriverDocumentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Enabled)
        {
            throw new DriverDocumentUnavailableException();
        }

        if (command.DriverId == Guid.Empty || command.DocumentId == Guid.Empty)
        {
            throw new DriverDocumentNotFoundException();
        }

        var (result, revoked) = await RunAsync(command.ActorId, command.OrganizationId, async (connection, transaction, token) =>
        {
            _ = await LockDriverAsync(connection, transaction, command.OrganizationId, command.DriverId, token)
                .ConfigureAwait(false) ?? throw new DriverDocumentNotFoundException();

            DriverDocumentResult current;
            bool isLatest;
            await using (var read = Command(
                connection,
                transaction,
                $"""
                SELECT {DocumentColumns},
                       NOT EXISTS (
                         SELECT 1 FROM drivers.driver_documents n
                         WHERE n.org_id=d.org_id AND n.driver_id=d.driver_id AND n.document_type=d.document_type
                           AND (n.created_at,n.id) > (d.created_at,d.id))
                FROM drivers.driver_documents d
                WHERE d.id=@document AND d.driver_id=@driver AND d.org_id=@organization AND d.document_type=ANY(@types)
                """))
            {
                read.Parameters.Add(P("document", NpgsqlDbType.Uuid, command.DocumentId));
                read.Parameters.Add(P("driver", NpgsqlDbType.Uuid, command.DriverId));
                read.Parameters.Add(P("organization", NpgsqlDbType.Uuid, command.OrganizationId));
                read.Parameters.Add(P(
                    "types", NpgsqlDbType.Array | NpgsqlDbType.Text, DriverDocumentVocabulary.RecordableTypes.ToArray()));
                await using var reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    throw new DriverDocumentNotFoundException();
                }

                current = ReadDocument(reader, 0);
                isLatest = reader.GetBoolean(9);
            }

            if (current.Status == DriverDocumentVocabulary.Revoked)
            {
                // Already revoked (a repeated press or a retry): the same answer, nothing written or audited again.
                return (current, false);
            }

            if (current.Status != DriverDocumentVocabulary.Valid || !isLatest)
            {
                throw new DriverDocumentConflictException(DriverDocumentConflictCodes.DocumentNotRevocable);
            }

            DriverDocumentResult updated;
            await using (var update = Command(
                connection,
                transaction,
                $"""
                UPDATE drivers.driver_documents d
                SET status='REVOKED',revoked_by=@actor,revoked_at=statement_timestamp()
                WHERE d.id=@document AND d.driver_id=@driver AND d.org_id=@organization AND d.status='VALID'
                RETURNING {DocumentColumns}
                """))
            {
                update.Parameters.Add(P("actor", NpgsqlDbType.Uuid, command.ActorId));
                update.Parameters.Add(P("document", NpgsqlDbType.Uuid, command.DocumentId));
                update.Parameters.Add(P("driver", NpgsqlDbType.Uuid, command.DriverId));
                update.Parameters.Add(P("organization", NpgsqlDbType.Uuid, command.OrganizationId));
                await using var reader = await update.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    throw new DriverDocumentConflictException(DriverDocumentConflictCodes.DocumentNotRevocable);
                }

                updated = ReadDocument(reader, 0);
            }

            await WriteAuditAsync(
                auditWriter,
                auditRedactor,
                connection,
                transaction,
                command.OrganizationId,
                command.ActorId,
                RevokedAction,
                EntityType,
                command.DocumentId,
                command.RequestId,
                new
                {
                    driver_id = command.DriverId,
                    document_id = command.DocumentId,
                    document_type = updated.DocumentType,
                    status = DriverDocumentVocabulary.Revoked,
                },
                updated.RevokedAt ?? updated.RecordedAt,
                token).ConfigureAwait(false);
            return (updated, true);
        }, cancellationToken).ConfigureAwait(false);

        if (revoked)
        {
            logger.LogInformation("Driver document revoked; type {DocumentType}", result.DocumentType);
        }

        return result;
    }

    /// <summary>Y11: the vehicle's required types that a dispatcher may record, in the screens' order.</summary>
    private IReadOnlyList<string> SlotTypes(string vehicleType)
    {
        var required = options.Value.Eligibility.RequiredDocumentTypesByVehicleType.TryGetValue(vehicleType, out var types)
            ? types
            : [];
        return DriverDocumentVocabulary.RecordableTypes.Where(type => required.Contains(type, StringComparer.Ordinal)).ToArray();
    }

    private IReadOnlyList<DriverDocumentSlotResult> Slots(
        Guid driverId,
        string vehicleType,
        IReadOnlyDictionary<(Guid DriverId, string DocumentType), DriverDocumentResult> latest) =>
        SlotTypes(vehicleType)
            .Select(type => new DriverDocumentSlotResult(type, latest.GetValueOrDefault((driverId, type))))
            .ToArray();

    private static async Task<IReadOnlyDictionary<(Guid DriverId, string DocumentType), DriverDocumentResult>> ReadLatestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid[] driverIds,
        CancellationToken cancellationToken)
    {
        var latest = new Dictionary<(Guid, string), DriverDocumentResult>();
        if (driverIds.Length == 0)
        {
            return latest;
        }

        await using var command = Command(connection, transaction, LatestDocumentsSql);
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(P("drivers", NpgsqlDbType.Array | NpgsqlDbType.Uuid, driverIds));
        command.Parameters.Add(P(
            "types", NpgsqlDbType.Array | NpgsqlDbType.Text, DriverDocumentVocabulary.RecordableTypes.ToArray()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var document = ReadDocument(reader, 1);
            latest[(reader.GetGuid(0), document.DocumentType)] = document;
        }

        return latest;
    }

    /// <summary>
    /// One document from <see cref="DocumentColumns"/> starting at <paramref name="offset"/>. A type, status, reason or
    /// expiry basis outside the published vocabulary fails closed: it is never passed on.
    /// </summary>
    private static DriverDocumentResult ReadDocument(NpgsqlDataReader reader, int offset)
    {
        var type = reader.GetString(offset + 1);
        var status = reader.GetString(offset + 2);
        var rejection = reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3);
        var basis = reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5);
        if (!DriverDocumentVocabulary.RecordableTypes.Contains(type) ||
            !DriverDocumentVocabulary.Statuses.Contains(status) ||
            (rejection is not null && !DriverDocumentVocabulary.RejectionCodes.Contains(rejection)) ||
            (basis is not null && !DriverDocumentVocabulary.ExpiryBases.Contains(basis)))
        {
            throw new DriverDocumentStoreValueException();
        }

        return new DriverDocumentResult(
            reader.GetGuid(offset),
            type,
            status,
            rejection,
            reader.IsDBNull(offset + 4) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 4),
            reader.IsDBNull(offset + 8) ? null : reader.GetFieldValue<DateOnly>(offset + 8),
            basis,
            reader.GetFieldValue<DateTimeOffset>(offset + 6),
            reader.IsDBNull(offset + 7) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 7));
    }

    /// <summary>
    /// The driver a dispatcher records for: an OWN profile of the selected organization that is not INACTIVE (the
    /// roster's population), locked so that one driver's documents are written one at a time. Null otherwise.
    /// </summary>
    private static async Task<string?> LockDriverAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid driverId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            connection,
            transaction,
            """
            SELECT p.vehicle_type
            FROM drivers.driver_profiles p
            WHERE p.id=@driver AND p.org_id=@organization AND p.driver_type='OWN' AND p.status<>'INACTIVE'
            FOR UPDATE
            """);
        command.Parameters.Add(P("driver", NpgsqlDbType.Uuid, driverId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    /// <summary>Today in Culiacán (America/Mazatlan) at the server clock, computed by PostgreSQL.</summary>
    private static async Task<DateOnly> TodayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            connection,
            transaction,
            "SELECT (@now AT TIME ZONE 'America/Mazatlan')::date");
        command.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is DateOnly today
            ? today
            : throw new DriverDocumentUnavailableException();
    }

    private static async Task AdvisoryLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@key,0));");
        command.Parameters.Add(P("key", NpgsqlDbType.Text, key));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StoredAnswer?> ReadStoredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RecordDriverDocumentCommand command,
        CancellationToken cancellationToken)
    {
        await using var read = Command(
            connection,
            transaction,
            """
            SELECT request_hash,response_body::text
            FROM platform.idempotency_keys
            WHERE owner_org_id=@organization AND scope=@scope AND idempotency_key=@key
            """);
        read.Parameters.Add(P("organization", NpgsqlDbType.Uuid, command.OrganizationId));
        read.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
        read.Parameters.Add(P("key", NpgsqlDbType.Text, command.IdempotencyKey));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredAnswer(reader.GetFieldValue<byte[]>(0), reader.IsDBNull(1) ? null : reader.GetString(1))
            : null;
    }

    /// <summary>
    /// The request identity binds the organization, the person, the driver and everything recorded, so the same key
    /// sent by another person or with another body is IDEMPOTENCY_CONFLICT.
    /// </summary>
    public static byte[] RequestHash(RecordDriverDocumentCommand command) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '|',
            "DRV-DOC-001",
            "RECORD_DRIVER_DOCUMENT",
            "v1",
            command.OrganizationId.ToString("D"),
            command.ActorId.ToString("D"),
            command.DriverId.ToString("D"),
            command.DocumentType,
            command.Decision,
            command.ExpiryBasis ?? string.Empty,
            command.PrintedDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            command.RejectionCode ?? string.Empty)));

    /// <summary>The endpoint already refused every other shape; the service refuses it again (defense in depth).</summary>
    public static bool IsWellFormed(RecordDriverDocumentCommand command) =>
        command.ActorId != Guid.Empty &&
        command.OrganizationId != Guid.Empty &&
        command.DriverId != Guid.Empty &&
        IdempotencyKeyPolicy.IsValid(command.IdempotencyKey) &&
        DriverDocumentVocabulary.RecordableTypes.Contains(command.DocumentType) &&
        command.Decision switch
        {
            DriverDocumentVocabulary.Valid =>
                command.RejectionCode is null &&
                (command.ExpiryBasis == DriverDocumentVocabulary.PrintedExpiry
                    ? command.PrintedDate is not null
                    : command.ExpiryBasis == DriverDocumentVocabulary.PermanentLicenseReview &&
                      command.PrintedDate is null &&
                      command.DocumentType == DriverDocumentVocabulary.DriverLicense),
            DriverDocumentVocabulary.Rejected =>
                command.ExpiryBasis is null &&
                command.PrintedDate is null &&
                command.RejectionCode is not null &&
                DriverDocumentVocabulary.RejectionCodes.Contains(command.RejectionCode),
            _ => false,
        };

    private async Task<T> RunAsync<T>(
        Guid actorId,
        Guid organizationId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new DriverDocumentForbiddenException();
        }

        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(actorId, [organizationId]),
                async (dbContext, token) =>
                {
                    var (connection, transaction) = Database(dbContext);
                    return await operation(connection, transaction, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DriverDocumentStoreValueException exception)
        {
            logger.LogWarning("Driver document store returned a value outside the published vocabulary.");
            throw new DriverDocumentUnavailableException(exception);
        }
        catch (Exception exception) when (IsStoreFailure(exception) || exception is JsonException)
        {
            logger.LogWarning("Driver document store unavailable with error {ErrorType}.", exception.GetType().Name);
            throw new DriverDocumentUnavailableException(exception);
        }
    }

    private sealed record StoredAnswer(byte[] RequestHash, string? Body);

    private sealed class DriverDocumentStoreValueException() : Exception("A stored driver document value is outside the vocabulary.");
}

/// <summary>
/// Drivers:Provider is Disabled, so documents are off too (the start validation refuses Drivers:Documents:Enabled without
/// the PostgreSQL provider): the lists answer documents_enabled=false without reading anything, and recording or
/// revoking is unavailable (503).
/// </summary>
public sealed class DisabledDriverDocumentService : IDriverDocumentService
{
    public Task<DriverDocumentPageResult> ListAsync(ListDriverDocumentsQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(new DriverDocumentPageResult(false, [], null));

    public Task<MyDriverDocumentsResult> ListMineAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken) =>
        Task.FromResult(new MyDriverDocumentsResult(false, []));

    public Task<DriverDocumentResult> RecordAsync(RecordDriverDocumentCommand command, CancellationToken cancellationToken) =>
        throw new DriverDocumentUnavailableException();

    public Task<DriverDocumentResult> RevokeAsync(RevokeDriverDocumentCommand command, CancellationToken cancellationToken) =>
        throw new DriverDocumentUnavailableException();
}
