using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Notifications.Application.Dispatching;

namespace Notifications.Infrastructure.Dispatching;

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
    string TemplateBody);

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

internal sealed class PostgreSqlNotificationsStore(NotificationsWorkerConnectionFactory connections)
    : INotificationsStore
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
                    reader.GetString(reader.GetOrdinal("template_body")));
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

    private async Task<bool> ExecuteCasAsync(
        string function,
        ClaimedNotificationOutboxMessage request,
        NotificationDelivery delivery,
        string? outcome,
        string code,
        DateTimeOffset occurredAt,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken)
    {
        await using var scope = await OpenWorkerScopeAsync(cancellationToken);
        var sql = outcome is null
            ? $"SELECT {function}(@outbox_id,@lease_token,@notification_id,@expected_version,@occurred_at);"
            : $"SELECT {function}(@outbox_id,@lease_token,@notification_id,@expected_version,@outcome,@code,@occurred_at,@available_at);";
        await using var command = new NpgsqlCommand(sql, scope.Connection, scope.Transaction);
        command.Parameters.Add(P("outbox_id", NpgsqlDbType.Uuid, request.Id));
        command.Parameters.Add(P("lease_token", NpgsqlDbType.Uuid, request.LeaseToken));
        command.Parameters.Add(P("notification_id", NpgsqlDbType.Uuid, delivery.NotificationId));
        command.Parameters.Add(P("expected_version", NpgsqlDbType.Integer, delivery.Version));
        if (outcome is not null)
        {
            command.Parameters.Add(P("outcome", NpgsqlDbType.Text, outcome));
            command.Parameters.Add(P("code", NpgsqlDbType.Text, code));
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
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("available_at"))));
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
