using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orders.Application.Orders;
using Orders.Domain;

namespace Paqueteria.IntegrationTests.Orders;

public sealed class OrderHttpWebApplicationFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid MissingQuoteId = Guid.Parse("81000000-0000-0000-0000-000000000001");
    internal static readonly Guid ForeignQuoteId = Guid.Parse("81000000-0000-0000-0000-000000000002");
    internal static readonly Guid ExpiredQuoteId = Guid.Parse("81000000-0000-0000-0000-000000000003");
    internal static readonly Guid UsedQuoteId = Guid.Parse("81000000-0000-0000-0000-000000000004");
    internal static readonly Guid ForeignOrderId = Guid.Parse("82000000-0000-0000-0000-000000000001");
    private readonly StubOrderService orderService = new();

    internal int CreateCallCount => orderService.CreateCallCount;
    internal int TransitionCallCount => orderService.TransitionCallCount;
    internal CreateOrderCommand? LastCreateCommand => orderService.LastCreateCommand;
    internal TransitionOrderCommand? LastTransitionCommand => orderService.LastTransitionCommand;

    internal void ResetCreateObservations() => orderService.ResetCreateObservations();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "Mock",
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOrderService>();
            services.RemoveAll<IOrderTransitionService>();
            services.AddSingleton<IOrderService>(orderService);
            services.AddSingleton<IOrderTransitionService>(orderService);
        });
    }

    private sealed class StubOrderService : IOrderService, IOrderTransitionService
    {
        private readonly object gate = new();
        private readonly ConcurrentDictionary<(Guid Tenant, string Key), StoredResponse> responses = new();
        private readonly ConcurrentDictionary<Guid, OrderResult> orders = new();
        private readonly ConcurrentDictionary<Guid, List<OrderTimelineItem>> timelines = new();
        private readonly ConcurrentDictionary<Guid, Guid> quoteOrders = new();
        private readonly ConcurrentDictionary<(Guid Tenant, string Key), StoredResponse> transitionResponses = new();
        private int createCallCount;
        private int transitionCallCount;
        private CreateOrderCommand? lastCreateCommand;
        private TransitionOrderCommand? lastTransitionCommand;

        internal int CreateCallCount => Volatile.Read(ref createCallCount);
        internal int TransitionCallCount => Volatile.Read(ref transitionCallCount);
        internal CreateOrderCommand? LastCreateCommand => Volatile.Read(ref lastCreateCommand);
        internal TransitionOrderCommand? LastTransitionCommand => Volatile.Read(ref lastTransitionCommand);

        internal void ResetCreateObservations()
        {
            Interlocked.Exchange(ref createCallCount, 0);
            Volatile.Write(ref lastCreateCommand, null);
        }

        public Task<OrderResult> CreateAsync(CreateOrderCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref createCallCount);
            Volatile.Write(ref lastCreateCommand, command);
            var signature = string.Join('|',
                command.OrganizationId, command.QuoteId, command.PayerType,
                command.Acceptance.TermsVersion, command.Acceptance.PrivacyVersion,
                command.Acceptance.AcceptedAt.ToUniversalTime().ToString("O"),
                command.Acceptance.AcceptanceChannel);
            lock (gate)
            {
                if (responses.TryGetValue((command.OrganizationId, command.IdempotencyKey), out var stored))
                {
                    if (!string.Equals(stored.Signature, signature, StringComparison.Ordinal))
                    {
                        throw new OrderConflictException(OrderConflictCode.IdempotencyConflict);
                    }

                    return Task.FromResult(stored.Result);
                }

                if (command.QuoteId == MissingQuoteId || command.QuoteId == ForeignQuoteId ||
                    command.QuoteId == ExpiredQuoteId || command.QuoteId == UsedQuoteId ||
                    quoteOrders.ContainsKey(command.QuoteId))
                {
                    throw new OrderConflictException(OrderConflictCode.QuoteUnavailable);
                }

                var result = Result(Guid.NewGuid(), command.QuoteId, command.OrganizationId);
                quoteOrders[command.QuoteId] = result.Id;
                orders[result.Id] = result;
                timelines[result.Id] =
                [
                    new OrderTimelineItem(
                        "ORDER_CREATED",
                        new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero)),
                ];
                responses[(command.OrganizationId, command.IdempotencyKey)] = new StoredResponse(signature, result);
                return Task.FromResult(result);
            }
        }

        public Task<OrderResult> TransitionAsync(
            TransitionOrderCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref transitionCallCount);
            Volatile.Write(ref lastTransitionCommand, command);
            var signature = string.Join(
                '|',
                command.OrganizationId,
                command.OrderId,
                command.TargetStatus,
                command.Reason,
                command.ExpectedVersion,
                command.MetadataJson);
            lock (gate)
            {
                if (transitionResponses.TryGetValue(
                        (command.OrganizationId, command.IdempotencyKey),
                        out var stored))
                {
                    if (!string.Equals(stored.Signature, signature, StringComparison.Ordinal))
                    {
                        throw new OrderTransitionConflictException(
                            OrderTransitionConflictCode.IdempotencyConflict);
                    }

                    return Task.FromResult(stored.Result);
                }

                if (!IsAuthorized(command) ||
                    !orders.TryGetValue(command.OrderId, out var current) ||
                    current.OwnerOrganizationId != command.OrganizationId)
                {
                    if (!IsAuthorized(command))
                    {
                        throw new OrderTransitionForbiddenException();
                    }

                    throw new OrderTransitionConflictException(OrderTransitionConflictCode.OrderUnavailable);
                }

                if (current.Version != command.ExpectedVersion ||
                    !OrderContractValues.TryParseOrderStatus(current.Status, out var source) ||
                    !OrderContractValues.TryParseOrderStatus(command.TargetStatus, out var target))
                {
                    throw new OrderTransitionConflictException(OrderTransitionConflictCode.VersionConflict);
                }

                var matrix = OrderTransitionMatrix.Evaluate(
                    source,
                    target,
                    DateTimeOffset.UtcNow,
                    current.ClaimWindowEndsAt ?? DateTimeOffset.UtcNow.AddHours(72),
                    current.FinalizedAt);
                if (!matrix.Allowed)
                {
                    throw new OrderTransitionConflictException(
                        matrix.Code == OrderTransitionRuleCode.TerminalState
                            ? OrderTransitionConflictCode.TerminalState
                            : OrderTransitionConflictCode.InvalidState);
                }

                if (target == OrderStatus.Confirmed &&
                    !string.Equals(
                        command.MetadataJson,
                        """{"restricted_goods_acknowledged":true}""",
                        StringComparison.Ordinal))
                {
                    throw new OrderTransitionConflictException(
                        OrderTransitionConflictCode.GuardNotSatisfied,
                        "restricted_goods_check");
                }

                var updated = current with
                {
                    Status = target.ToContractValue(),
                    Version = checked(current.Version + 1),
                    ClaimWindowEndsAt = target == OrderStatus.Delivered
                        ? current.ClaimWindowEndsAt ?? DateTimeOffset.UtcNow.AddHours(72)
                        : current.ClaimWindowEndsAt,
                    FinalizedAt = target == OrderStatus.ClaimResolved
                        ? DateTimeOffset.UtcNow
                        : current.FinalizedAt,
                };
                orders[command.OrderId] = updated;
                timelines[command.OrderId].Add(new OrderTimelineItem(
                    "ORDER_STATUS_CHANGED",
                    DateTimeOffset.UtcNow));
                transitionResponses[(command.OrganizationId, command.IdempotencyKey)] =
                    new StoredResponse(signature, updated);
                return Task.FromResult(updated);
            }
        }

        public Task<OrderPageResult> ListAsync(
            Guid actorId,
            Guid organizationId,
            string? status,
            Guid? ownerOrganizationId,
            string? cursor,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ownerOrganizationId is { } owner && owner != organizationId)
            {
                return Task.FromResult(new OrderPageResult([], null));
            }

            if (cursor is not null && !OrderCursorCodec.TryDecode(cursor, out _, out _))
            {
                return Task.FromResult(new OrderPageResult([], null));
            }

            var items = orders.Values
                .Where(order => order.OwnerOrganizationId == organizationId)
                .Where(order => status is null || order.Status == status)
                .OrderByDescending(order => order.Id)
                .ToArray();
            return Task.FromResult(new OrderPageResult(items, null));
        }

        public Task<OrderDetailResult> GetAsync(
            Guid actorId,
            Guid organizationId,
            Guid orderId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (orderId == ForeignOrderId ||
                !orders.TryGetValue(orderId, out var order) ||
                order.OwnerOrganizationId != organizationId)
            {
                throw new OrderNotFoundException();
            }

            return Task.FromResult(new OrderDetailResult(
                order,
                timelines[orderId]));
        }

        private static bool IsAuthorized(TransitionOrderCommand command) =>
            command.ActorId switch
            {
                var id when id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2") =>
                    command.MfaSatisfied,
                var id when id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3") =>
                    command.MfaSatisfied,
                var id when id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4") =>
                    command.OrganizationId == Identity.Infrastructure.Mock.MockIdentityProfiles.OperationsOrganizationId,
                _ => false,
            };

        private static OrderResult Result(Guid id, Guid quoteId, Guid organizationId) => new(
            id,
            $"ORD_{Convert.ToBase64String(id.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_')}",
            organizationId,
            null,
            "DRAFT",
            new MoneyResult("MXN", 3_000_000_000L),
            1,
            Guid.Parse("83000000-0000-0000-0000-000000000001"),
            Guid.Parse("83000000-0000-0000-0000-000000000002"),
            "SAME_DAY",
            quoteId,
            Guid.Parse("83000000-0000-0000-0000-000000000003"),
            null,
            "OCCASIONAL",
            new MoneyResult("MXN", 3_000_000_000L),
            null,
            null);

        private sealed record StoredResponse(string Signature, OrderResult Result);
    }
}
