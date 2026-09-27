using Identity.Application.Notifications;
using Microsoft.Extensions.Options;
using Notifications.Application.Audience;
using Notifications.Application.Dispatching;
using Notifications.Domain;
using Notifications.Infrastructure;
using Notifications.Infrastructure.Delivery;
using Organizations.Application.Notifications;

namespace Paqueteria.UnitTests.Notifications;

public sealed class NotificationLifecycleTests
{
    [Theory]
    [InlineData("orders.status-changed", OutboxConsumer.Realtime)]
    [InlineData("orders.timeline-event-added", OutboxConsumer.Realtime)]
    [InlineData("dispatch.assignment-changed", OutboxConsumer.Realtime)]
    [InlineData("notifications.status-changed", OutboxConsumer.Realtime)]
    [InlineData("orders.created", OutboxConsumer.Notifications)]
    [InlineData("notifications.send-requested", OutboxConsumer.Notifications)]
    [InlineData("dispatch.order-status-reaction-requested", OutboxConsumer.Dispatch)]
    [InlineData("unknown.value", OutboxConsumer.Unrouted)]
    [InlineData("", OutboxConsumer.Unrouted)]
    public void Routing_is_exhaustive_and_disjoint(string topic, OutboxConsumer expected) =>
        Assert.Equal(expected, NotificationOutboxTopics.Resolve(topic));

    [Fact]
    public void Orders_created_parser_accepts_only_the_minimal_tenant_consistent_contract()
    {
        var owner = Guid.NewGuid();
        var order = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var message = Message(
            owner,
            order,
            $$"""{"order_id":"{{order:D}}","public_id":"ORD_1234567890123456789012","status":"DRAFT"}""",
            createdAt);

        var parsed = OrderCreatedParser.Parse(message);

        Assert.Equal(order, parsed.OrderId);
        Assert.Equal("ORD_1234567890123456789012", parsed.OrderPublicId);
        Assert.Equal(createdAt, parsed.OccurredAt);
    }

    [Fact]
    public void Orders_created_parser_fails_closed_on_tenant_mismatch()
    {
        var message = Message(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "{}",
            DateTimeOffset.UtcNow) with
        {
            TenantContextJson = $$"""{"organization_ids":["{{Guid.NewGuid():D}}"]}""",
        };

        var exception = Assert.Throws<NotificationMessageException>(() => OrderCreatedParser.Parse(message));

        Assert.Equal(NotificationErrorCodes.TenantContextMismatch, exception.ErrorCode);
    }

    [Fact]
    public void Template_rendering_is_deterministic_and_rejects_unknown_variables()
    {
        var variables = new Dictionary<string, string>
        {
            ["order_status"] = "DRAFT",
            ["occurred_at"] = "2026-08-15T12:00:00Z",
            ["order_public_id"] = "ORD_1234567890123456789012",
        };

        var rendered = NotificationTemplateRenderer.Render(
            "{{order_public_id}}|{{order_status}}|{{occurred_at}}",
            variables);

        Assert.Equal("ORD_1234567890123456789012|DRAFT|2026-08-15T12:00:00Z", rendered);
        variables["foreign"] = "forbidden";
        Assert.Equal(
            NotificationErrorCodes.TemplateVariableUnknown,
            Assert.Throws<NotificationMessageException>(() =>
                NotificationTemplateRenderer.Render("{{foreign}}", variables)).ErrorCode);
    }

    [Fact]
    public void Idempotency_key_is_stable_and_attempt_independent()
    {
        var id = Guid.NewGuid();

        var first = NotificationIdempotencyKey.Create(id, NotificationTemplate.Key, 1, "IN_APP");
        var second = NotificationIdempotencyKey.Create(id, NotificationTemplate.Key, 1, "IN_APP");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public async Task Audience_is_deduplicated_sorted_and_filtered_to_active_users()
    {
        var low = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var high = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var resolver = new NotificationAudienceResolver(
            new DispatcherReader([high, low, high]),
            new ActiveReader([high, low, high]));

        var result = await resolver.ResolveAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal([low, high], result);
    }

    [Fact]
    public async Task Audience_rejects_foreign_identity_results()
    {
        var requested = Guid.NewGuid();
        var resolver = new NotificationAudienceResolver(
            new DispatcherReader([requested]),
            new ActiveReader([requested, Guid.NewGuid()]));

        var exception = await Assert.ThrowsAsync<NotificationAudienceException>(() =>
            resolver.ResolveAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(NotificationAudienceErrorCodes.ContractViolation, exception.ErrorCode);
    }

    [Fact]
    public async Task Audience_rejects_more_than_five_hundred_recipients_before_identity_lookup()
    {
        var candidates = Enumerable.Range(0, 501)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        var active = new ActiveReader(candidates);
        var resolver = new NotificationAudienceResolver(new DispatcherReader(candidates), active);

        var exception = await Assert.ThrowsAsync<NotificationAudienceException>(() =>
            resolver.ResolveAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(NotificationAudienceErrorCodes.LimitExceeded, exception.ErrorCode);
        Assert.Equal(0, active.Calls);
    }

    [Theory]
    [InlineData(SyntheticInAppOutcome.Success, "SUCCESS", "SYNTHETIC_ACCEPTED")]
    [InlineData(SyntheticInAppOutcome.TransientFailure, "TRANSIENT", "SYNTHETIC_TRANSIENT")]
    [InlineData(SyntheticInAppOutcome.PermanentFailure, "PERMANENT", "SYNTHETIC_PERMANENT")]
    [InlineData(SyntheticInAppOutcome.AmbiguousTimeout, "AMBIGUOUS", "SYNTHETIC_AMBIGUOUS_TIMEOUT")]
    public async Task Synthetic_provider_has_bounded_deterministic_mappings(
        SyntheticInAppOutcome configured,
        string outcome,
        string code)
    {
        var notificationId = Guid.NewGuid();
        var options = Options.Create(new NotificationsOptions { SyntheticOutcome = configured });
        var provider = new SyntheticInAppProvider(options);
        var variables = Variables();
        var key = NotificationIdempotencyKey.Create(notificationId, NotificationTemplate.Key, 1, "IN_APP");
        var request = new SyntheticInAppRequest(
            notificationId,
            NotificationTemplate.Key,
            1,
            "IN_APP",
            "{{order_public_id}} {{order_status}} {{occurred_at}}",
            variables,
            key);

        var first = await provider.SendAsync(request, CancellationToken.None);
        var second = await provider.SendAsync(request, CancellationToken.None);

        Assert.Equal(outcome, first.Outcome);
        Assert.Equal(code, first.Code);
        Assert.Equal(first.Receipt, second.Receipt);
    }

    [Fact]
    public void Retry_backoff_is_deterministic_and_bounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), NotificationRetryPolicy.CalculateDelay(1, 2, 30));
        Assert.Equal(TimeSpan.FromSeconds(8), NotificationRetryPolicy.CalculateDelay(3, 2, 30));
        Assert.Equal(TimeSpan.FromSeconds(30), NotificationRetryPolicy.CalculateDelay(20, 2, 30));
    }

    [Fact]
    public void Provider_outcomes_increment_attempts_and_version_once()
    {
        var notification = DomainNotification();
        notification.RecordProviderOutcome(NotificationStatus.Pending, "SYNTHETIC_TRANSIENT", DateTimeOffset.UtcNow, 1);

        Assert.Equal(1, notification.Attempts);
        Assert.Equal(2, notification.Version);
        Assert.Throws<InvalidOperationException>(() =>
            notification.RecordProviderOutcome(NotificationStatus.Sent, "SYNTHETIC_ACCEPTED", DateTimeOffset.UtcNow, 1));
    }

    [Fact]
    public void Maximum_attempt_finalization_does_not_increment_attempts()
    {
        var notification = DomainNotification();

        notification.FinalizeMaximumAttempts(DateTimeOffset.UtcNow, 1);

        Assert.Equal(NotificationStatus.Failed, notification.Status);
        Assert.Equal(0, notification.Attempts);
        Assert.Equal(2, notification.Version);
    }

    private static ClaimedNotificationOutboxMessage Message(
        Guid owner,
        Guid order,
        string payload,
        DateTimeOffset createdAt) =>
        new(
            Guid.NewGuid(),
            owner,
            $$"""{"organization_ids":["{{owner:D}}"]}""",
            NotificationOutboxTopics.OrdersCreated,
            "Order",
            order,
            1,
            payload,
            1,
            Guid.NewGuid(),
            createdAt.AddMinutes(2),
            createdAt,
            createdAt);

    private static IReadOnlyDictionary<string, string> Variables() =>
        new Dictionary<string, string>
        {
            ["order_public_id"] = "ORD_1234567890123456789012",
            ["order_status"] = "DRAFT",
            ["occurred_at"] = "2026-08-15T12:00:00Z",
        };

    private static Notification DomainNotification() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            NotificationTemplate.Key,
            1,
            "{}",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);

    private sealed class DispatcherReader(IReadOnlyList<Guid> ids) : IOwnerOrganizationDispatcherReader
    {
        public Task<IReadOnlyList<Guid>> ReadActiveDispatcherUserIdsAsync(
            Guid ownerOrganizationId,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult(ids);
    }

    private sealed class ActiveReader(IReadOnlyList<Guid> ids) : IActiveNotificationRecipientReader
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<Guid>> ReadActiveUserIdsAsync(
            IReadOnlyCollection<Guid> requestedUserIds,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ids);
        }
    }
}
