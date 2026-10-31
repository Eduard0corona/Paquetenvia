using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Notifications.Application.RecipientNotices;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Database.Pooling;

namespace Notifications.Infrastructure.RecipientNotices.Webhooks;

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-18 recipient notices step 3): the API's store on its one
/// <c>paqueteria_app</c> pool (NOBYPASSRLS). Every write is one short transaction - BEGIN,
/// <c>set_config('app.current_org_ids', '{}', true)</c> (a webhook carries no tenant), <c>SET LOCAL ROLE
/// paqueteria_app</c> - that calls the receipt executor's SECURITY DEFINER functions and nothing else. The unsubscribe
/// read runs in a tenant transaction of the token's organization with a fresh system user id (the VOICE-001 provider
/// callback precedent) and reads only the destination e-mail copy of an ORDER_DESTINATION e-mail notice under FORCE RLS.
/// No value is logged; a database failure is <see cref="RecipientNoticeStoreUnavailableException"/>.
/// </summary>
internal sealed class PostgreSqlRecipientNoticeReceiptStore(
    [FromKeyedServices(RuntimeDatabaseRole.Application)] NpgsqlDataSource dataSource,
    ILogger<PostgreSqlRecipientNoticeReceiptStore> logger) : IRecipientNoticeReceiptStore
{
    private const int CommandTimeoutSeconds = 10;

    public Task<MessagingReceiptResult> RecordReceiptAsync(MessagingReceipt receipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return RunAsync([], async (connection, transaction, token) =>
        {
            await using var command = Command(
                connection,
                transaction,
                "SELECT security.record_messaging_receipt(@channel,@reference,@status,@code,@reported_at);");
            command.Parameters.Add(Text("channel", receipt.Channel));
            command.Parameters.Add(Text("reference", receipt.ProviderReference));
            command.Parameters.Add(Text("status", receipt.Status == MessagingReceiptStatus.Delivered ? "DELIVERED" : "FAILED"));
            command.Parameters.Add(Text("code", receipt.Status == MessagingReceiptStatus.Delivered ? null : receipt.Code));
            command.Parameters.Add(new NpgsqlParameter("reported_at", NpgsqlDbType.TimestampTz)
            {
                Value = (receipt.ReportedAt ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            });
            var result = (string?)await command.ExecuteScalarAsync(token);
            return result switch
            {
                "RECORDED" => MessagingReceiptResult.Recorded,
                "UNCHANGED" => MessagingReceiptResult.Unchanged,
                "UNKNOWN_REFERENCE" => MessagingReceiptResult.UnknownReference,
                _ => throw new InvalidOperationException("The receipt function returned an unknown result."),
            };
        }, cancellationToken);
    }

    public Task<RecipientInboundResult> RecordWhatsAppInboundAsync(
        RecipientInboundRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        return RunAsync([], async (connection, transaction, token) =>
        {
            if (record.IsOptOut)
            {
                if (!await RecordOptOutAsync(connection, transaction, "WHATSAPP", "RECIPIENT_REPLY", record.Hashes, token))
                {
                    return RecipientInboundResult.OptOutAlreadyRecorded;
                }

                // E4-P15: the one confirmation, in the same transaction as the opt-out it confirms; a message older than
                // 23 hours still records the opt-out and gets no confirmation.
                _ = await RequestAsync(connection, transaction, record, "OPT_OUT_CONFIRMATION", token);
                return RecipientInboundResult.OptOutRecorded;
            }

            return await RequestAsync(connection, transaction, record, "AUTO_REPLY", token) switch
            {
                "RECORDED" => RecipientInboundResult.AutoReplyRequested,
                "LIMITED" => RecipientInboundResult.AutoReplyLimited,
                "STALE" => RecipientInboundResult.Stale,
                _ => throw new InvalidOperationException("The automatic reply function returned an unknown result."),
            };
        }, cancellationToken);
    }

    public Task<bool> RecordEmailOptOutAsync(IReadOnlyList<ContactLookupHash> hashes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        return RunAsync([], (connection, transaction, token) =>
            RecordOptOutAsync(connection, transaction, "EMAIL", "EMAIL_LINK", hashes, token), cancellationToken);
    }

    public Task<RecipientNoticeUnsubscribeContact?> ReadUnsubscribeContactAsync(
        Guid ownerOrganizationId,
        Guid notificationId,
        CancellationToken cancellationToken) =>
        RunAsync([ownerOrganizationId], async (connection, transaction, token) =>
        {
            await using var command = Command(
                connection,
                transaction,
                """
                SELECT l.id,l.pii_key_version,l.email_ciphertext
                FROM notifications.notifications n
                JOIN orders.orders o ON o.id=n.order_id AND o.owner_org_id=n.owner_org_id
                JOIN locations.locations l ON l.id=o.destination_location_id AND l.owner_org_id=o.owner_org_id
                WHERE n.id=@notification_id AND n.owner_org_id=@owner_org_id
                  AND n.recipient_kind='ORDER_DESTINATION' AND n.channel='EMAIL'
                  AND l.email_ciphertext IS NOT NULL
                """);
            command.Parameters.Add(new NpgsqlParameter<Guid>("notification_id", NpgsqlDbType.Uuid) { TypedValue = notificationId });
            command.Parameters.Add(new NpgsqlParameter<Guid>("owner_org_id", NpgsqlDbType.Uuid) { TypedValue = ownerOrganizationId });
            await using var reader = await command.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token)
                ? new RecipientNoticeUnsubscribeContact(
                    ownerOrganizationId,
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetFieldValue<byte[]>(2))
                : null;
        }, cancellationToken);

    private static async Task<bool> RecordOptOutAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string channel,
        string source,
        IReadOnlyList<ContactLookupHash> hashes,
        CancellationToken cancellationToken)
    {
        if (hashes.Count == 0)
        {
            throw new ArgumentException("At least one contact hash is required.", nameof(hashes));
        }

        await using var command = Command(
            connection,
            transaction,
            "SELECT security.record_recipient_opt_out(@channel,@source,@hash,@key_version,@known_hashes);");
        command.Parameters.Add(Text("channel", channel));
        command.Parameters.Add(Text("source", source));
        command.Parameters.Add(new NpgsqlParameter<byte[]>("hash", NpgsqlDbType.Bytea) { TypedValue = hashes[0].Hash });
        command.Parameters.Add(new NpgsqlParameter<int>("key_version", NpgsqlDbType.Integer) { TypedValue = hashes[0].KeyVersion });
        command.Parameters.Add(KnownHashes(hashes));
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<string?> RequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RecipientInboundRecord record,
        string kind,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            connection,
            transaction,
            """
            SELECT security.request_recipient_auto_reply(
              @request_id,@kind,@hash,@key_version,@known_hashes,@sealed_number,@number_key_version,@received_at);
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid>("request_id", NpgsqlDbType.Uuid) { TypedValue = record.RequestId });
        command.Parameters.Add(Text("kind", kind));
        command.Parameters.Add(new NpgsqlParameter<byte[]>("hash", NpgsqlDbType.Bytea) { TypedValue = record.Hashes[0].Hash });
        command.Parameters.Add(new NpgsqlParameter<int>("key_version", NpgsqlDbType.Integer) { TypedValue = record.Hashes[0].KeyVersion });
        command.Parameters.Add(KnownHashes(record.Hashes));
        command.Parameters.Add(new NpgsqlParameter<byte[]>("sealed_number", NpgsqlDbType.Bytea) { TypedValue = record.SealedNumber });
        command.Parameters.Add(Text("number_key_version", record.NumberKeyVersion));
        command.Parameters.Add(new NpgsqlParameter("received_at", NpgsqlDbType.TimestampTz)
        {
            Value = record.ReceivedAt.ToUniversalTime(),
        });
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<T> RunAsync<T>(
        Guid[] organizationIds,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var context = Command(
                connection,
                transaction,
                """
                SELECT set_config('app.current_user_id', @user_id::uuid::text, true);
                SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true);
                SET LOCAL ROLE paqueteria_app;
                """))
            {
                // The provider is no user: a fresh random identity names only this transaction (VOICE-001 RunSystemAsync).
                context.Parameters.Add(new NpgsqlParameter<Guid>("user_id", NpgsqlDbType.Uuid) { TypedValue = Guid.NewGuid() });
                context.Parameters.Add(new NpgsqlParameter<Guid[]>("organization_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                {
                    TypedValue = organizationIds,
                });
                await context.ExecuteNonQueryAsync(cancellationToken);
            }

            var result = await operation(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning("Recipient notice store unavailable with error class {ErrorClass}.", exception.GetType().Name);
            throw new RecipientNoticeStoreUnavailableException(exception);
        }
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql) =>
        new(sql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds };

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter KnownHashes(IReadOnlyList<ContactLookupHash> hashes) =>
        new NpgsqlParameter<byte[][]>("known_hashes", NpgsqlDbType.Array | NpgsqlDbType.Bytea)
        {
            TypedValue = hashes.Select(static hash => hash.Hash).ToArray(),
        };
}
