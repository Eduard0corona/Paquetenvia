using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Notifications.Application.RecipientNotices;
using Notifications.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Orders;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Notifications.Infrastructure.RecipientNotices.Panel;

/// <summary>
/// NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007): the panel's store on the API's one <c>paqueteria_app</c> pool.
/// Every call is one tenant transaction of the selected organization - BEGIN, <c>set_config('app.current_user_id', ...,
/// true)</c>, <c>set_config('app.current_org_ids', ..., true)</c> with that organization only, <c>SET LOCAL ROLE
/// paqueteria_app</c> (NOBYPASSRLS, FORCE RLS) - through <see cref="TenantTransactionContext{TDbContext}"/>.
/// <list type="bullet">
/// <item>RLS on <c>orders.orders</c> admits the owner and the operator; the explicit <c>owner_org_id</c> filter makes an
/// order the organization only operates the same uniform 404 as a missing one (AI-24 <c>dispatcher_view.roles</c>).</item>
/// <item>It reads Notifications, Orders and Locations rows and writes only through
/// <c>security.record_recipient_opt_out</c> (AI-18 recipient notices step 3) plus the shared
/// <c>platform.idempotency_keys</c> and <c>platform.audit_logs</c> rows (AI-13 §7.2; no seventh flow).</item>
/// <item>It never reads the notice copies, the opt-out store or the outbox, and never logs a value.</item>
/// </list>
/// </summary>
internal sealed class PostgreSqlRecipientNoticePanelStore(
    TenantTransactionContext<NotificationsDbContext> transactions,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor auditRedactor,
    ILogger<PostgreSqlRecipientNoticePanelStore> logger) : IRecipientNoticePanelStore
{
    private const int CommandTimeoutSeconds = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<RecipientNoticeListResult?> ListAsync(
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken) =>
        RunAsync(actorId, organizationId, async (connection, transaction, token) =>
        {
            bool consent;
            bool emailOnFile;
            await using (var order = Command(
                connection,
                transaction,
                """
                SELECT EXISTS (
                         SELECT 1 FROM orders.recipient_notice_consents c
                         WHERE c.order_id=o.id AND c.owner_org_id=o.owner_org_id
                           AND c.channel='WHATSAPP' AND c.consent_version=@consent_version),
                       COALESCE(l.email_ciphertext IS NOT NULL,false)
                FROM orders.orders o
                LEFT JOIN locations.locations l ON l.id=o.destination_location_id AND l.owner_org_id=o.owner_org_id
                WHERE o.id=@order_id AND o.owner_org_id=@owner_org_id
                """))
            {
                AddOrder(order, organizationId, orderId);
                order.Parameters.Add(new NpgsqlParameter<string>("consent_version", NpgsqlDbType.Text)
                {
                    TypedValue = RecipientNoticeConsentPolicy.Version,
                });
                await using var reader = await order.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    return null;
                }

                consent = reader.GetBoolean(0);
                emailOnFile = reader.GetBoolean(1);
            }

            var items = new List<RecipientNoticeListItem>();
            await using (var notices = Command(
                connection,
                transaction,
                """
                SELECT n.id,n.template_key,n.channel,n.status,n.attempts,n.last_provider_attempt_code,
                       n.sent_at,n.delivered_at,n.updated_at
                FROM notifications.notifications n
                WHERE n.owner_org_id=@owner_org_id AND n.order_id=@order_id
                  AND n.recipient_kind='ORDER_DESTINATION'
                ORDER BY n.created_at,(n.channel<>'WHATSAPP'),n.id
                LIMIT @limit
                """))
            {
                AddOrder(notices, organizationId, orderId);
                notices.Parameters.Add(new NpgsqlParameter<int>("limit", NpgsqlDbType.Integer)
                {
                    TypedValue = RecipientNoticePanelContract.MaximumItems,
                });
                await using var reader = await notices.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var row = new RecipientNoticeStoredRow(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                        reader.GetFieldValue<DateTimeOffset>(8));
                    if (!RecipientNoticePanelContract.TryMap(row, out var item))
                    {
                        // T6 choice 4: a value outside the closed sets is a server error and is never passed on.
                        logger.LogWarning("Recipient notice panel read a value outside the closed sets.");
                        throw new RecipientNoticePanelUnavailableException();
                    }

                    items.Add(item);
                }
            }

            return new RecipientNoticeListResult(items, consent, emailOnFile);
        }, cancellationToken);

    public Task<RecipientNoticeStopRead?> ReadStopAsync(
        StopOrderRecipientNoticesCommand command,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(requestHash);
        return RunAsync<RecipientNoticeStopRead?>(command.ActorId, command.OrganizationId, async (connection, transaction, token) =>
        {
            if (await ReadStoredAsync(connection, transaction, command, requestHash, token) is { } stored)
            {
                return RecipientNoticeStopRead.ForReplay(stored);
            }

            await using var order = Command(
                connection,
                transaction,
                """
                SELECT l.id,l.pii_key_version,l.phone_ciphertext,l.email_ciphertext
                FROM orders.orders o
                LEFT JOIN locations.locations l ON l.id=o.destination_location_id AND l.owner_org_id=o.owner_org_id
                WHERE o.id=@order_id AND o.owner_org_id=@owner_org_id
                """);
            AddOrder(order, command.OrganizationId, command.OrderId);
            await using var reader = await order.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                return null;
            }

            return RecipientNoticeStopRead.ForContact(reader.IsDBNull(0)
                ? null
                : new RecipientNoticeStopContact(
                    command.OrganizationId,
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3)));
        }, cancellationToken);
    }

    public Task<RecipientNoticeStopResult> RecordStopAsync(
        RecipientNoticeStopWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var command = write.Command;
        return RunAsync(command.ActorId, command.OrganizationId, async (connection, transaction, token) =>
        {
            // One press at a time per key: a concurrent retry of the same press waits here and then replays.
            await using (var lockCommand = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@key,0));"))
            {
                lockCommand.Parameters.Add(new NpgsqlParameter<string>("key", NpgsqlDbType.Text)
                {
                    TypedValue = $"{command.OrganizationId:D}:{RecipientNoticePanelContract.IdempotencyScope}:{command.IdempotencyKey}",
                });
                await lockCommand.ExecuteNonQueryAsync(token);
            }

            if (await ReadStoredAsync(connection, transaction, command, write.RequestHash, token) is { } stored)
            {
                return stored;
            }

            foreach (var optOut in write.OptOuts)
            {
                await using var record = Command(
                    connection,
                    transaction,
                    "SELECT security.record_recipient_opt_out(@channel,@source,@hash,@key_version,@known_hashes);");
                record.Parameters.Add(new NpgsqlParameter<string>("channel", NpgsqlDbType.Text) { TypedValue = optOut.Channel });
                record.Parameters.Add(new NpgsqlParameter<string>("source", NpgsqlDbType.Text)
                {
                    TypedValue = RecipientNoticePanelContract.OptOutSource,
                });
                record.Parameters.Add(new NpgsqlParameter<byte[]>("hash", NpgsqlDbType.Bytea) { TypedValue = optOut.Hashes[0].Hash });
                record.Parameters.Add(new NpgsqlParameter<int>("key_version", NpgsqlDbType.Integer)
                {
                    TypedValue = optOut.Hashes[0].KeyVersion,
                });
                record.Parameters.Add(new NpgsqlParameter<byte[][]>("known_hashes", NpgsqlDbType.Array | NpgsqlDbType.Bytea)
                {
                    TypedValue = optOut.Hashes.Select(static hash => hash.Hash).ToArray(),
                });
                // T6 choice 6: whether this press recorded it or it was already on record is not told apart.
                _ = await record.ExecuteScalarAsync(token);
            }

            var result = new RecipientNoticeStopResult(
                command.OrderId,
                write.OptOuts.Select(static optOut => optOut.Channel).ToArray(),
                write.RecordedAt);

            // T6 choice 7: the audit row of the order, the key as its request_id and the channels only.
            await auditWriter.WriteAsync(
                connection,
                transaction,
                new AuditEntry(
                    Guid.NewGuid(),
                    command.OrganizationId,
                    command.ActorId,
                    RecipientNoticePanelContract.StoppedAuditAction,
                    RecipientNoticePanelContract.AuditEntityType,
                    command.OrderId,
                    command.IdempotencyKey,
                    auditRedactor.Redact(JsonSerializer.SerializeToElement(
                        new StoppedAuditPayload(result.Channels.ToArray()),
                        JsonOptions)),
                    write.RecordedAt),
                token);

            await using (var store = Command(
                connection,
                transaction,
                """
                INSERT INTO platform.idempotency_keys(
                  owner_org_id,scope,idempotency_key,request_hash,response_status,response_body,resource_id,created_at,expires_at)
                VALUES (@owner_org_id,@scope,@key,@hash,200,@body,@order_id,@now,@expires)
                """))
            {
                store.Parameters.Add(new NpgsqlParameter<Guid>("owner_org_id", NpgsqlDbType.Uuid) { TypedValue = command.OrganizationId });
                store.Parameters.Add(new NpgsqlParameter<string>("scope", NpgsqlDbType.Text)
                {
                    TypedValue = RecipientNoticePanelContract.IdempotencyScope,
                });
                store.Parameters.Add(new NpgsqlParameter<string>("key", NpgsqlDbType.Text) { TypedValue = command.IdempotencyKey });
                store.Parameters.Add(new NpgsqlParameter<byte[]>("hash", NpgsqlDbType.Bytea) { TypedValue = write.RequestHash });
                store.Parameters.Add(new NpgsqlParameter<string>("body", NpgsqlDbType.Jsonb)
                {
                    TypedValue = JsonSerializer.Serialize(StoredStop.From(result), JsonOptions),
                });
                store.Parameters.Add(new NpgsqlParameter<Guid>("order_id", NpgsqlDbType.Uuid) { TypedValue = command.OrderId });
                store.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", NpgsqlDbType.TimestampTz) { TypedValue = write.RecordedAt });
                store.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("expires", NpgsqlDbType.TimestampTz)
                {
                    TypedValue = write.RecordedAt + RecipientNoticePanelContract.IdempotencyLifetime,
                });
                await store.ExecuteNonQueryAsync(token);
            }

            return result;
        }, cancellationToken);
    }

    /// <summary>
    /// The stored answer of this organization's key, when there is one. Another request under the key is
    /// IDEMPOTENCY_CONFLICT; a record that is incomplete, for another order or unreadable fails closed (503).
    /// </summary>
    private async Task<RecipientNoticeStopResult?> ReadStoredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StopOrderRecipientNoticesCommand command,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        await using var read = Command(
            connection,
            transaction,
            """
            SELECT request_hash,response_status,response_body::text,resource_id
            FROM platform.idempotency_keys
            WHERE owner_org_id=@owner_org_id AND scope=@scope AND idempotency_key=@key
            """);
        read.Parameters.Add(new NpgsqlParameter<Guid>("owner_org_id", NpgsqlDbType.Uuid) { TypedValue = command.OrganizationId });
        read.Parameters.Add(new NpgsqlParameter<string>("scope", NpgsqlDbType.Text) { TypedValue = RecipientNoticePanelContract.IdempotencyScope });
        read.Parameters.Add(new NpgsqlParameter<string>("key", NpgsqlDbType.Text) { TypedValue = command.IdempotencyKey });
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte[]>(0), requestHash))
        {
            throw new RecipientNoticeStopConflictException(RecipientNoticeStopConflictCodes.IdempotencyConflict);
        }

        if (reader.IsDBNull(1) || reader.GetInt32(1) != 200 || reader.IsDBNull(2) || reader.IsDBNull(3) ||
            reader.GetGuid(3) != command.OrderId ||
            StoredStop.TryRead(reader.GetString(2), command.OrderId) is not { } stored)
        {
            logger.LogWarning("Recipient notice panel found an unreadable stored answer.");
            throw new RecipientNoticePanelUnavailableException();
        }

        return stored;
    }

    private async Task<T> RunAsync<T>(
        Guid actorId,
        Guid organizationId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await transactions.ExecuteAsync(
                    new TenantDatabaseExecutionContext(actorId, [organizationId]),
                    (dbContext, token) => operation(
                        (NpgsqlConnection)dbContext.Database.GetDbConnection(),
                        (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction(),
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or TimeoutException or
                                              RetryLimitExceededException)
        {
            logger.LogWarning("Recipient notice panel store unavailable with error class {ErrorClass}.", exception.GetType().Name);
            throw new RecipientNoticePanelUnavailableException(exception);
        }
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql) =>
        new(sql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds };

    private static void AddOrder(NpgsqlCommand command, Guid organizationId, Guid orderId)
    {
        command.Parameters.Add(new NpgsqlParameter<Guid>("order_id", NpgsqlDbType.Uuid) { TypedValue = orderId });
        command.Parameters.Add(new NpgsqlParameter<Guid>("owner_org_id", NpgsqlDbType.Uuid) { TypedValue = organizationId });
    }

    private sealed record StoppedAuditPayload([property: JsonPropertyName("channels")] string[] Channels);

    /// <summary>The stored 200 body, exactly the AI-05 <c>RecipientNoticeStopResult</c> the replay answers.</summary>
    private sealed record StoredStop(
        [property: JsonPropertyName("order_id")] Guid OrderId,
        [property: JsonPropertyName("channels")] string[] Channels,
        [property: JsonPropertyName("recorded_at")] DateTimeOffset RecordedAt)
    {
        public static StoredStop From(RecipientNoticeStopResult result) =>
            new(result.OrderId, result.Channels.ToArray(), result.RecordedAt);

        public static RecipientNoticeStopResult? TryRead(string json, Guid orderId)
        {
            StoredStop? stored;
            try
            {
                stored = JsonSerializer.Deserialize<StoredStop>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }

            if (stored is null || stored.OrderId != orderId || stored.Channels is not { Length: 1 or 2 } channels ||
                channels.Distinct(StringComparer.Ordinal).Count() != channels.Length ||
                !channels.All(channel => RecipientNoticePanelContract.Channels.Contains(channel, StringComparer.Ordinal)))
            {
                return null;
            }

            return new RecipientNoticeStopResult(stored.OrderId, channels, stored.RecordedAt.ToUniversalTime());
        }
    }
}
