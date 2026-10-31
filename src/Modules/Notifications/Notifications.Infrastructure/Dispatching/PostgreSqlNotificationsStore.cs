using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Notifications.Application.Dispatching;
using Notifications.Application.RecipientNotices;
using Paqueteria.Infrastructure.Database.Outbox;

namespace Notifications.Infrastructure.Dispatching;

/// <summary>
/// One row of <c>security.read_notification_delivery</c> v2 (NTF-RECIPIENT-NOTICES-2026-10-20, AI-08 NTF-003): the
/// template body exists only for IN_APP; a recipient notice (<c>ORDER_DESTINATION</c>) carries its order, its order event
/// and the time of that event, never a recipient value.
/// </summary>
internal sealed record NotificationDelivery(
    Guid NotificationId,
    Guid OwnerOrganizationId,
    string Channel,
    string Status,
    int Attempts,
    int Version,
    string TemplateKey,
    int TemplateVersion,
    string VariablesSnapshot,
    string? TemplateBody,
    string RecipientKind = NotificationDelivery.OwnerOrganizationUser,
    Guid? OrderId = null,
    Guid? OrderEventId = null,
    DateTimeOffset? CreatedAt = null)
{
    public const string OwnerOrganizationUser = "OWNER_ORG_USER";
}

/// <summary>The recipient kinds of <c>notifications.notifications</c> (NTF-003 <c>notifications_recipient_kind_check</c>).</summary>
internal static class RecipientNoticeKinds
{
    public const string OwnerOrganizationUser = NotificationDelivery.OwnerOrganizationUser;
    public const string OrderDestination = "ORDER_DESTINATION";
}

/// <summary>
/// NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 (L1, L2): the two numbers of the caps for one send request, from
/// <c>security.read_recipient_notice_counts</c>.
/// </summary>
internal sealed record RecipientNoticeCounts(int OrderChannelCount, int DayChannelCount);

/// <summary>
/// The database clock and America/Mazatlan wall-clock values a recipient notice send needs, converted in SQL with
/// <c>AT TIME ZONE</c> so the container needs no time-zone data (AI-24 <c>recipient_notices.quiet_hours</c>).
/// </summary>
internal sealed record RecipientNoticeClock(DateTimeOffset NowUtc, DateTime NowLocal, DateTime OccurredLocal);

internal interface INotificationsStore
{
    Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> ClaimNotificationsAsync(string workerId, int batchSize, TimeSpan lease, CancellationToken cancellationToken);
    Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> ClaimUnownedAsync(string workerId, int batchSize, TimeSpan lease, CancellationToken cancellationToken);
    Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> RecoverStaleAsync(string workerId, int batchSize, int maximumAttempts, TimeSpan lease, CancellationToken cancellationToken);
    Task<int> RecoverStaleUnownedAsync(int batchSize, int maximumAttempts, CancellationToken cancellationToken);
    Task<bool> SettleAsync(Guid id, Guid leaseToken, string status, string? errorCode, DateTimeOffset? availableAt, CancellationToken cancellationToken);
    Task<string> ExpandSourceAsync(ClaimedNotificationOutboxMessage source, ParsedOrderCreated parsed, IReadOnlyList<Guid> recipients, CancellationToken cancellationToken);
    Task<NotificationDelivery?> ReadDeliveryAsync(Guid notificationId, Guid ownerOrganizationId, CancellationToken cancellationToken);
    Task<bool> ApplyOutcomeAsync(ClaimedNotificationOutboxMessage request, NotificationDelivery delivery, string outcome, string code, DateTimeOffset attemptedAt, DateTimeOffset? availableAt, CancellationToken cancellationToken);
    Task<bool> FinalizeMaximumAttemptsAsync(ClaimedNotificationOutboxMessage request, NotificationDelivery delivery, DateTimeOffset finalizedAt, CancellationToken cancellationToken);
    Task<string?> ResolveConsumerAsync(string topic, CancellationToken cancellationToken);
}

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-005): the recipient notice functions of the lane, each in its own short
/// transaction under <c>paqueteria_worker</c>; nothing here reads a Notifications table directly.
/// </summary>
internal interface IRecipientNoticeStore
{
    /// <summary>NTF-005: <c>security.expand_recipient_notice_request</c> under the source row's lease.</summary>
    Task<string> ExpandRecipientNoticeAsync(ClaimedNotificationOutboxMessage source, bool enabled, TimeSpan maximumDelay, TimeOnly windowStart, TimeOnly windowEnd, string? publicId, IReadOnlyList<string> channels, CancellationToken cancellationToken);

    /// <summary>NTF-005: <c>security.read_recipient_notice_counts</c>; null when the lease is lost or the notice moved on.</summary>
    Task<RecipientNoticeCounts?> ReadRecipientNoticeCountsAsync(ClaimedNotificationOutboxMessage request, CancellationToken cancellationToken);

    /// <summary>NTF-005: <c>security.is_recipient_opted_out</c> with one keyed hash per configured key version.</summary>
    Task<bool> IsRecipientOptedOutAsync(string channel, IReadOnlyList<byte[]> contactHashes, CancellationToken cancellationToken);

    /// <summary>NTF-005: <c>security.cancel_notification</c> under the send request's lease.</summary>
    Task<bool> CancelNotificationAsync(ClaimedNotificationOutboxMessage request, NotificationDelivery delivery, string code, DateTimeOffset occurredAt, CancellationToken cancellationToken);

    /// <summary>NTF-005: <c>security.apply_notification_outcome</c> v2 with the provider reference of an accepted send.</summary>
    Task<bool> ApplyRecipientOutcomeAsync(ClaimedNotificationOutboxMessage request, NotificationDelivery delivery, string outcome, string code, string? providerReference, DateTimeOffset attemptedAt, DateTimeOffset? availableAt, CancellationToken cancellationToken);

    /// <summary>NTF-005: the database now and the America/Mazatlan wall clock of now and of <paramref name="occurredAt"/>.</summary>
    Task<RecipientNoticeClock> ReadRecipientNoticeClockAsync(DateTimeOffset occurredAt, CancellationToken cancellationToken);

    /// <summary>NTF-005: the America/Mazatlan wall clock of an instant.</summary>
    Task<DateTime> ToMazatlanLocalAsync(DateTimeOffset instant, CancellationToken cancellationToken);

    /// <summary>NTF-005: the instant of an America/Mazatlan wall-clock value.</summary>
    Task<DateTimeOffset> FromMazatlanLocalAsync(DateTime local, CancellationToken cancellationToken);
}

internal sealed class PostgreSqlNotificationsStore(NotificationsWorkerConnectionFactory connections)
    : INotificationsStore, IRecipientNoticeStore, IRecipientAutoReplyStore
{
    public Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> ClaimNotificationsAsync(
        string workerId,
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken) =>
        ClaimAsync("security.claim_notifications_outbox", workerId, batchSize, lease, cancellationToken);

    public Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> ClaimUnownedAsync(
        string workerId,
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken) =>
        ClaimAsync("security.claim_unowned_outbox", workerId, batchSize, lease, cancellationToken);

    public async Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> RecoverStaleAsync(
        string workerId,
        int batchSize,
        int maximumAttempts,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT * FROM security.recover_stale_notifications_outbox(@worker_id,@batch_size,@maximum_attempts,@lease);",
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("worker_id", NpgsqlDbType.Text, workerId));
        command.Parameters.Add(P("batch_size", NpgsqlDbType.Integer, batchSize));
        command.Parameters.Add(P("maximum_attempts", NpgsqlDbType.Integer, maximumAttempts));
        command.Parameters.Add(P("lease", NpgsqlDbType.Interval, lease));
        var result = await ReadClaimsAsync(command, cancellationToken);
        await scope.Transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<int> RecoverStaleUnownedAsync(
        int batchSize,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT security.requeue_stale_unowned_outbox(interval '0 seconds',@batch_size,@maximum_attempts);",
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("batch_size", NpgsqlDbType.Integer, batchSize));
        command.Parameters.Add(P("maximum_attempts", NpgsqlDbType.Integer, maximumAttempts));
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        await scope.Transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<bool> SettleAsync(
        Guid id,
        Guid leaseToken,
        string status,
        string? errorCode,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT security.settle_outbox(@id,@lease_token,@status,@error_code,@available_at);",
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, id));
        command.Parameters.Add(P("lease_token", NpgsqlDbType.Uuid, leaseToken));
        command.Parameters.Add(P("status", NpgsqlDbType.Text, status));
        command.Parameters.Add(P("error_code", NpgsqlDbType.Text, errorCode));
        command.Parameters.Add(P("available_at", NpgsqlDbType.TimestampTz, availableAt));
        var result = await command.ExecuteScalarAsync(cancellationToken) is true;
        await scope.Transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<string> ExpandSourceAsync(
        ClaimedNotificationOutboxMessage source,
        ParsedOrderCreated parsed,
        IReadOnlyList<Guid> recipients,
        CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT security.expand_order_created_notifications(@source_id,@lease_token,@order_id,@public_id,@status,@occurred_at,@recipient_ids);",
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("source_id", NpgsqlDbType.Uuid, source.Id));
        command.Parameters.Add(P("lease_token", NpgsqlDbType.Uuid, source.LeaseToken));
        command.Parameters.Add(P("order_id", NpgsqlDbType.Uuid, parsed.OrderId));
        command.Parameters.Add(P("public_id", NpgsqlDbType.Text, parsed.OrderPublicId));
        command.Parameters.Add(P("status", NpgsqlDbType.Text, parsed.OrderStatus));
        command.Parameters.Add(P("occurred_at", NpgsqlDbType.TimestampTz, parsed.OccurredAt));
        command.Parameters.Add(P("recipient_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, recipients.ToArray()));
        var result = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) ?? NotificationErrorCodes.LeaseLost;
        await scope.Transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<NotificationDelivery?> ReadDeliveryAsync(
        Guid notificationId,
        Guid ownerOrganizationId,
        CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT * FROM security.read_notification_delivery(@notification_id,@owner_org_id);",
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("notification_id", NpgsqlDbType.Uuid, notificationId));
        command.Parameters.Add(P("owner_org_id", NpgsqlDbType.Uuid, ownerOrganizationId));
        NotificationDelivery? result = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                result = new(
                    reader.GetGuid(reader.GetOrdinal("notification_id")),
                    reader.GetGuid(reader.GetOrdinal("owner_org_id")),
                    reader.GetString(reader.GetOrdinal("channel")),
                    reader.GetString(reader.GetOrdinal("status")),
                    reader.GetInt32(reader.GetOrdinal("attempts")),
                    reader.GetInt32(reader.GetOrdinal("version")),
                    reader.GetString(reader.GetOrdinal("template_key")),
                    reader.GetInt32(reader.GetOrdinal("template_version")),
                    reader.GetString(reader.GetOrdinal("variables_snapshot")),
                    reader.IsDBNull(reader.GetOrdinal("template_body"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("template_body")),
                    reader.GetString(reader.GetOrdinal("recipient_kind")),
                    reader.IsDBNull(reader.GetOrdinal("order_id"))
                        ? null
                        : reader.GetGuid(reader.GetOrdinal("order_id")),
                    reader.IsDBNull(reader.GetOrdinal("order_event_id"))
                        ? null
                        : reader.GetGuid(reader.GetOrdinal("order_event_id")),
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")));
            }
        }

        await scope.Transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task<bool> ApplyOutcomeAsync(
        ClaimedNotificationOutboxMessage request,
        NotificationDelivery delivery,
        string outcome,
        string code,
        DateTimeOffset attemptedAt,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken) =>
        ExecuteCasAsync(
            "security.apply_notification_outcome",
            request,
            delivery,
            outcome,
            code,
            attemptedAt,
            availableAt,
            cancellationToken);

    public Task<bool> FinalizeMaximumAttemptsAsync(
        ClaimedNotificationOutboxMessage request,
        NotificationDelivery delivery,
        DateTimeOffset finalizedAt,
        CancellationToken cancellationToken) =>
        ExecuteCasAsync(
            "security.finalize_notification_max_attempts",
            request,
            delivery,
            null,
            NotificationErrorCodes.MaxAttemptsExhausted,
            finalizedAt,
            null,
            cancellationToken);

    public async Task<string?> ResolveConsumerAsync(string topic, CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT security.resolve_outbox_consumer(@topic);",
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("topic", NpgsqlDbType.Text, topic));
        var result = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        await scope.Transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<string> ExpandRecipientNoticeAsync(
        ClaimedNotificationOutboxMessage source,
        bool enabled,
        TimeSpan maximumDelay,
        TimeOnly windowStart,
        TimeOnly windowEnd,
        string? publicId,
        IReadOnlyList<string> channels,
        CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT security.expand_recipient_notice_request(@source_id,@lease_token,@enabled,@maximum_delay,@window_start,@window_end,@public_id,@channels);",
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("source_id", NpgsqlDbType.Uuid, source.Id));
        command.Parameters.Add(P("lease_token", NpgsqlDbType.Uuid, source.LeaseToken));
        command.Parameters.Add(P("enabled", NpgsqlDbType.Boolean, enabled));
        command.Parameters.Add(P("maximum_delay", NpgsqlDbType.Interval, enabled ? maximumDelay : null));
        command.Parameters.Add(P("window_start", NpgsqlDbType.Time, enabled ? windowStart : null));
        command.Parameters.Add(P("window_end", NpgsqlDbType.Time, enabled ? windowEnd : null));
        command.Parameters.Add(P("public_id", NpgsqlDbType.Text, publicId));
        command.Parameters.Add(P("channels", NpgsqlDbType.Array | NpgsqlDbType.Text, enabled ? channels.ToArray() : null));
        var result = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) ?? NotificationErrorCodes.LeaseLost;
        await scope.Transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<RecipientNoticeCounts?> ReadRecipientNoticeCountsAsync(
        ClaimedNotificationOutboxMessage request,
        CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT order_channel_count,day_channel_count FROM security.read_recipient_notice_counts(@outbox_id,@lease_token);",
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("outbox_id", NpgsqlDbType.Uuid, request.Id));
        command.Parameters.Add(P("lease_token", NpgsqlDbType.Uuid, request.LeaseToken));
        RecipientNoticeCounts? result = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                result = new RecipientNoticeCounts(reader.GetInt32(0), reader.GetInt32(1));
            }
        }

        await scope.Transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<bool> IsRecipientOptedOutAsync(
        string channel,
        IReadOnlyList<byte[]> contactHashes,
        CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT security.is_recipient_opted_out(@channel,@hashes);",
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("channel", NpgsqlDbType.Text, channel));
        command.Parameters.Add(P("hashes", NpgsqlDbType.Array | NpgsqlDbType.Bytea, contactHashes.ToArray()));
        var result = await command.ExecuteScalarAsync(cancellationToken) is true;
        await scope.Transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25: <c>security.claim_recipient_auto_replies</c> - stale leases end FAILED,
    /// requests older than 23 hours end DROPPED, then a batch of pending requests is leased.
    /// </summary>
    public async Task<IReadOnlyList<ClaimedRecipientAutoReply>> ClaimAutoRepliesAsync(
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT request_id,kind,sealed_number,number_key_version,received_at,lease_token FROM security.claim_recipient_auto_replies(@batch_size,@lease);",
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("batch_size", NpgsqlDbType.Integer, batchSize));
        command.Parameters.Add(P("lease", NpgsqlDbType.Interval, lease));
        var claimed = new List<ClaimedRecipientAutoReply>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(new ClaimedRecipientAutoReply(
                    reader.GetGuid(0),
                    reader.GetString(1) == "OPT_OUT_CONFIRMATION"
                        ? RecipientAutoReplyKind.OptOutConfirmation
                        : RecipientAutoReplyKind.AutoReply,
                    reader.GetFieldValue<byte[]>(2),
                    reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    reader.GetGuid(5)));
            }
        }

        await scope.Transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    /// <summary>NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25: <c>security.settle_recipient_auto_reply</c> under the lease.</summary>
    public async Task<bool> SettleAutoReplyAsync(
        ClaimedRecipientAutoReply request,
        RecipientAutoReplyOutcome outcome,
        string? code,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT security.settle_recipient_auto_reply(@request_id,@lease_token,@outcome,@code,@available_at);",
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("request_id", NpgsqlDbType.Uuid, request.RequestId));
        command.Parameters.Add(P("lease_token", NpgsqlDbType.Uuid, request.LeaseToken));
        command.Parameters.Add(P("outcome", NpgsqlDbType.Text, outcome switch
        {
            RecipientAutoReplyOutcome.Sent => "SENT",
            RecipientAutoReplyOutcome.Failed => "FAILED",
            RecipientAutoReplyOutcome.Dropped => "DROPPED",
            _ => "RETRY",
        }));
        command.Parameters.Add(P("code", NpgsqlDbType.Text, code));
        command.Parameters.Add(P("available_at", NpgsqlDbType.TimestampTz, availableAt?.ToUniversalTime()));
        var settled = await command.ExecuteScalarAsync(cancellationToken) is true;
        await scope.Transaction.CommitAsync(cancellationToken);
        return settled;
    }

    public async Task<bool> CancelNotificationAsync(
        ClaimedNotificationOutboxMessage request,
        NotificationDelivery delivery,
        string code,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT security.cancel_notification(@outbox_id,@lease_token,@notification_id,@expected_version,@code,@occurred_at);",
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("outbox_id", NpgsqlDbType.Uuid, request.Id));
        command.Parameters.Add(P("lease_token", NpgsqlDbType.Uuid, request.LeaseToken));
        command.Parameters.Add(P("notification_id", NpgsqlDbType.Uuid, delivery.NotificationId));
        command.Parameters.Add(P("expected_version", NpgsqlDbType.Integer, delivery.Version));
        command.Parameters.Add(P("code", NpgsqlDbType.Text, code));
        command.Parameters.Add(P("occurred_at", NpgsqlDbType.TimestampTz, occurredAt));
        var result = await command.ExecuteScalarAsync(cancellationToken) is true;
        await scope.Transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task<bool> ApplyRecipientOutcomeAsync(
        ClaimedNotificationOutboxMessage request,
        NotificationDelivery delivery,
        string outcome,
        string code,
        string? providerReference,
        DateTimeOffset attemptedAt,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken) =>
        ExecuteCasAsync(
            "security.apply_notification_outcome",
            request,
            delivery,
            outcome,
            code,
            attemptedAt,
            availableAt,
            cancellationToken,
            providerReference);

    public async Task<RecipientNoticeClock> ReadRecipientNoticeClockAsync(
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT now_utc, now_utc AT TIME ZONE 'America/Mazatlan', @occurred_at AT TIME ZONE 'America/Mazatlan'
            FROM (SELECT clock_timestamp() AS now_utc) clock;
            """,
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("occurred_at", NpgsqlDbType.TimestampTz, occurredAt));
        RecipientNoticeClock result;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            result = new RecipientNoticeClock(
                reader.GetFieldValue<DateTimeOffset>(0),
                DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Unspecified),
                DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Unspecified));
        }

        await scope.Transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<DateTime> ToMazatlanLocalAsync(DateTimeOffset instant, CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT @instant AT TIME ZONE 'America/Mazatlan';",
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("instant", NpgsqlDbType.TimestampTz, instant));
        var result = DateTime.SpecifyKind(
            (DateTime)(await command.ExecuteScalarAsync(cancellationToken))!,
            DateTimeKind.Unspecified);
        await scope.Transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<DateTimeOffset> FromMazatlanLocalAsync(DateTime local, CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT @local AT TIME ZONE 'America/Mazatlan';",
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("local", NpgsqlDbType.Timestamp, DateTime.SpecifyKind(local, DateTimeKind.Unspecified)));
        DateTimeOffset result;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            result = reader.GetFieldValue<DateTimeOffset>(0);
        }

        await scope.Transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<bool> ExecuteCasAsync(
        string function,
        ClaimedNotificationOutboxMessage request,
        NotificationDelivery delivery,
        string? outcome,
        string code,
        DateTimeOffset occurredAt,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken,
        string? providerReference = null)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        // NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-003): apply_notification_outcome v2 takes the provider reference;
        // the synthetic IN_APP provider has none, a recipient notice passes the reference of an accepted send (NTF-005).
        var sql = outcome is null
            ? $"SELECT {function}(@outbox_id,@lease_token,@notification_id,@expected_version,@occurred_at);"
            : $"SELECT {function}(@outbox_id,@lease_token,@notification_id,@expected_version,@outcome,@code,@provider_reference,@occurred_at,@available_at);";
        await using var command = new NpgsqlCommand(sql, scope.Connection, scope.Transaction);
        command.Parameters.Add(P("outbox_id", NpgsqlDbType.Uuid, request.Id));
        command.Parameters.Add(P("lease_token", NpgsqlDbType.Uuid, request.LeaseToken));
        command.Parameters.Add(P("notification_id", NpgsqlDbType.Uuid, delivery.NotificationId));
        command.Parameters.Add(P("expected_version", NpgsqlDbType.Integer, delivery.Version));
        if (outcome is not null)
        {
            command.Parameters.Add(P("outcome", NpgsqlDbType.Text, outcome));
            command.Parameters.Add(P("code", NpgsqlDbType.Text, code));
            command.Parameters.Add(P("provider_reference", NpgsqlDbType.Text, providerReference));
            command.Parameters.Add(P("available_at", NpgsqlDbType.TimestampTz, availableAt));
        }

        command.Parameters.Add(P("occurred_at", NpgsqlDbType.TimestampTz, occurredAt));
        var result = await command.ExecuteScalarAsync(cancellationToken) is true;
        await scope.Transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> ClaimAsync(
        string function,
        string workerId,
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT * FROM {function}(@worker_id,@batch_size,@lease);",
            scope.Connection,
            scope.Transaction);
        command.Parameters.Add(P("worker_id", NpgsqlDbType.Text, workerId));
        command.Parameters.Add(P("batch_size", NpgsqlDbType.Integer, batchSize));
        command.Parameters.Add(P("lease", NpgsqlDbType.Interval, lease));
        var result = await ReadClaimsAsync(command, cancellationToken);
        await scope.Transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> ReadClaimsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<ClaimedNotificationOutboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetGuid(reader.GetOrdinal("owner_org_id")),
                reader.GetString(reader.GetOrdinal("tenant_context")),
                reader.GetString(reader.GetOrdinal("topic")),
                reader.GetString(reader.GetOrdinal("aggregate_type")),
                reader.GetGuid(reader.GetOrdinal("aggregate_id")),
                reader.IsDBNull(reader.GetOrdinal("aggregate_version"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("aggregate_version")),
                reader.GetString(reader.GetOrdinal("payload")),
                reader.GetInt32(reader.GetOrdinal("attempts")),
                reader.GetGuid(reader.GetOrdinal("lease_token")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("lease_expires_at")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("available_at")),
                // OBS-003 PR 7: the producing trace context, parent of this message's processing activity.
                OutboxTraceContextColumn.Read(reader)));
        }

        return result;
    }

    private async Task<WorkerScope> OpenWorkerScopeAsync(CancellationToken cancellationToken)
    {
        var connection = await connections.OpenAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(
                "SET LOCAL ROLE paqueteria_worker;",
                connection,
                transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new WorkerScope(connection, transaction);
        }
        catch
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    private sealed class WorkerScope(NpgsqlConnection connection, NpgsqlTransaction transaction)
        : IAsyncDisposable
    {
        public NpgsqlConnection Connection { get; } = connection;
        public NpgsqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
