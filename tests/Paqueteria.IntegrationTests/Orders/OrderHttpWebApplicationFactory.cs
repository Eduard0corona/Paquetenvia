using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orders.Application.Csv;
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
    private readonly InMemoryCsvOrderImportBatchIdempotencyStore csvBatches = new();

    internal int CreateCallCount => orderService.CreateCallCount;
    internal int TransitionCallCount => orderService.TransitionCallCount;
    internal int ListCallCount => orderService.ListCallCount;
    internal bool? LastListCodPendingReconciliation => orderService.LastListCodPendingReconciliation;
    internal bool? LastListMfaSatisfied => orderService.LastListMfaSatisfied;
    internal CreateOrderCommand? LastCreateCommand => orderService.LastCreateCommand;
    internal TransitionOrderCommand? LastTransitionCommand => orderService.LastTransitionCommand;

    internal void ResetCreateObservations() => orderService.ResetCreateObservations();
    internal void SetDriverAssignment(Guid orderId, bool active) =>
        orderService.SetDriverAssignment(orderId, active);
    internal int TransitionEffectCount(Guid orderId) => orderService.TransitionEffectCount(orderId);

    /// <summary>Marks an order as having a RECORDED, not yet reconciled COD collection for the pending list.</summary>
    internal void MarkCodPending(Guid orderId) => orderService.MarkCodPending(orderId);

    /// <summary>
    /// FIN-PENDING-COD-LIST-FINANCE-2026-10-02: makes the COD pending list fail its in-transaction authorization
    /// re-check, as the PostgreSQL service does when the membership changed after the endpoint decision.
    /// </summary>
    internal bool RefuseCodPendingListInTransaction
    {
        get => orderService.RefuseCodPendingListInTransaction;
        set => orderService.RefuseCodPendingListInTransaction = value;
    }

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
            services.RemoveAll<ICsvOrderImportBatchIdempotencyStore>();
            services.AddSingleton<IOrderService>(orderService);
            services.AddSingleton<IOrderTransitionService>(orderService);
            services.AddSingleton<ICsvOrderImportBatchIdempotencyStore>(csvBatches);
        });
    }

    /// <summary>
    /// The batch reservation CSV-001 takes out on <c>platform.idempotency_keys</c>, kept in memory
    /// because this host has no database. The semantics are the persistent store's: a reservation
    /// bound to the canonical request hash, a conflict when the key is reused for other content, a
    /// replay of the stored response once the batch has completed, and a re-run when a reservation
    /// exists but never completed.
    /// </summary>
    private sealed class InMemoryCsvOrderImportBatchIdempotencyStore : ICsvOrderImportBatchIdempotencyStore
    {
        private readonly object gate = new();
        private readonly Dictionary<(Guid Tenant, string Key), Reservation> reservations = [];

        public Task<CsvOrderImportCommitResult?> ReserveOrReplayAsync(
            CsvOrderImportBatchIdentity identity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestHash = CsvOrderImportIdempotency.ComputeBatchRequestHash(identity);
            lock (gate)
            {
                var address = (identity.OrganizationId, identity.IdempotencyKey);
                if (reservations.TryGetValue(address, out var existing))
                {
                    if (!existing.RequestHash.SequenceEqual(requestHash))
                    {
                        throw new CsvOrderImportBatchConflictException();
                    }

                    return Task.FromResult(existing.Result);
                }

                reservations[address] = new Reservation(requestHash, null);
                return Task.FromResult<CsvOrderImportCommitResult?>(null);
            }
        }

        public Task<CsvOrderImportCommitResult> CompleteAsync(
            CsvOrderImportBatchIdentity identity,
            CsvOrderImportCommitResult result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestHash = CsvOrderImportIdempotency.ComputeBatchRequestHash(identity);
            lock (gate)
            {
                var address = (identity.OrganizationId, identity.IdempotencyKey);
                if (reservations.TryGetValue(address, out var existing) &&
                    existing.RequestHash.SequenceEqual(requestHash) &&
                    existing.Result is { } stored)
                {
                    return Task.FromResult(stored);
                }

                reservations[address] = new Reservation(requestHash, result);
                return Task.FromResult(result);
            }
        }

        private sealed record Reservation(byte[] RequestHash, CsvOrderImportCommitResult? Result);
    }

    private sealed class StubOrderService : IOrderService, IOrderTransitionService
    {
        private readonly object gate = new();
        private readonly ConcurrentDictionary<(Guid Tenant, string Key), StoredResponse> responses = new();
        private readonly ConcurrentDictionary<Guid, OrderResult> orders = new();
        private readonly ConcurrentDictionary<Guid, List<OrderTimelineItem>> timelines = new();
        private readonly ConcurrentDictionary<Guid, Guid> quoteOrders = new();
        private readonly ConcurrentDictionary<(Guid Tenant, string Key), StoredResponse> transitionResponses = new();
        private readonly ConcurrentDictionary<Guid, byte> activeDriverAssignments = new();
        private int createCallCount;
        private int transitionCallCount;
        private int listCallCount;
        private int lastListCodPending = -1;
        private int lastListMfa = -1;
        private volatile bool refuseCodPendingListInTransaction;
        private readonly ConcurrentDictionary<Guid, byte> codPendingOrders = new();
        private CreateOrderCommand? lastCreateCommand;
        private TransitionOrderCommand? lastTransitionCommand;

        internal int CreateCallCount => Volatile.Read(ref createCallCount);
        internal int TransitionCallCount => Volatile.Read(ref transitionCallCount);
        internal int ListCallCount => Volatile.Read(ref listCallCount);

        internal bool? LastListCodPendingReconciliation => Volatile.Read(ref lastListCodPending) switch
        {
            -1 => null,
            1 => true,
            _ => false,
        };
        internal bool? LastListMfaSatisfied => Volatile.Read(ref lastListMfa) switch
        {
            -1 => null,
            1 => true,
            _ => false,
        };

        internal bool RefuseCodPendingListInTransaction
        {
            get => refuseCodPendingListInTransaction;
            set => refuseCodPendingListInTransaction = value;
        }

        internal void MarkCodPending(Guid orderId) => codPendingOrders[orderId] = 0;

        internal CreateOrderCommand? LastCreateCommand => Volatile.Read(ref lastCreateCommand);
        internal TransitionOrderCommand? LastTransitionCommand => Volatile.Read(ref lastTransitionCommand);

        internal void ResetCreateObservations()
        {
            Interlocked.Exchange(ref createCallCount, 0);
            Volatile.Write(ref lastCreateCommand, null);
        }

        internal void SetDriverAssignment(Guid orderId, bool active)
        {
            if (active)
            {
                activeDriverAssignments[orderId] = 0;
            }
            else
            {
                activeDriverAssignments.TryRemove(orderId, out _);
            }
        }

        internal int TransitionEffectCount(Guid orderId) =>
            timelines.TryGetValue(orderId, out var timeline)
                ? timeline.Count(item => item.EventType == "ORDER_STATUS_CHANGED")
                : 0;

        public Task<OrderResult> CreateAsync(CreateOrderCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref createCallCount);
            Volatile.Write(ref lastCreateCommand, command);
            var signature = string.Join('|',
                command.OrganizationId, command.QuoteId, command.PayerType,
                command.Acceptance.TermsVersion, command.Acceptance.PrivacyVersion,
                command.Acceptance.AcceptedAt.ToUniversalTime().ToString("O"),
                command.Acceptance.AcceptanceChannel,
                command.CodExpectedCents,
                command.ServiceWindow?.From.ToString("O"),
                command.ServiceWindow?.To.ToString("O"));
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

                var result = Result(Guid.NewGuid(), command.QuoteId, command.OrganizationId) with
                {
                    ServiceWindow = command.ServiceWindow,
                };
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

                    if (!IsAuthorized(command, stored.Source, stored.Target))
                    {
                        throw new OrderTransitionForbiddenException();
                    }

                    return Task.FromResult(stored.Result);
                }

                if (!orders.TryGetValue(command.OrderId, out var current) ||
                    current.OwnerOrganizationId != command.OrganizationId)
                {
                    throw new OrderTransitionConflictException(OrderTransitionConflictCode.OrderUnavailable);
                }

                // ORD-002-GUARD-CODES-2026-10-05: like the PostgreSQL service, a rule code only for an order of the
                // selected organization whose caller holds the transitionOrder capability on it.
                var disclosesCode = HoldsCapability(command);
                if (current.Version != command.ExpectedVersion ||
                    !OrderContractValues.TryParseOrderStatus(current.Status, out var source) ||
                    !OrderContractValues.TryParseOrderStatus(command.TargetStatus, out var target))
                {
                    throw new OrderTransitionConflictException(
                        OrderTransitionConflictCode.VersionConflict,
                        rejectionCode: disclosesCode ? OrderTransitionRejectionCodes.VersionConflict : null);
                }

                if (!IsAuthorized(command, source, target))
                {
                    throw new OrderTransitionForbiddenException();
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
                            : OrderTransitionConflictCode.InvalidState,
                        rejectionCode: disclosesCode ? OrderTransitionRejectionCodes.ForRule(matrix.Code) : null);
                }

                if (target == OrderStatus.Confirmed &&
                    !string.Equals(
                        command.MetadataJson,
                        """{"restricted_goods_acknowledged":true}""",
                        StringComparison.Ordinal))
                {
                    throw new OrderTransitionConflictException(
                        OrderTransitionConflictCode.GuardNotSatisfied,
                        "restricted_goods_check",
                        OrderTransitionRejectionCodes.ForGuard("restricted_goods_check"));
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
                    new StoredResponse(signature, updated, source, target);
                return Task.FromResult(updated);
            }
        }

        public Task<OrderPageResult> ListAsync(
            Guid actorId,
            Guid organizationId,
            string? status,
            Guid? ownerOrganizationId,
            string? cursor,
            bool codPendingReconciliation,
            bool mfaSatisfied,
            string? publicId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref listCallCount);
            Volatile.Write(ref lastListCodPending, codPendingReconciliation ? 1 : 0);
            Volatile.Write(ref lastListMfa, mfaSatisfied ? 1 : 0);
            if (codPendingReconciliation && refuseCodPendingListInTransaction)
            {
                throw new OrderListForbiddenException();
            }

            if (ownerOrganizationId is { } owner && owner != organizationId)
            {
                return Task.FromResult(new OrderPageResult([], null));
            }

            if (cursor is not null && !OrderCursorCodec.TryDecode(cursor, out _, out _))
            {
                return Task.FromResult(new OrderPageResult([], null));
            }

            // UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: like the PostgreSQL service, a malformed tracking number
            // matches nothing; a well-formed one matches exactly.
            if (publicId is not null && !OrderPublicIdPolicy.IsValid(publicId))
            {
                return Task.FromResult(new OrderPageResult([], null));
            }

            var items = orders.Values
                .Where(order => order.OwnerOrganizationId == organizationId)
                .Where(order => status is null || order.Status == status)
                .Where(order => publicId is null || string.Equals(order.PublicId, publicId, StringComparison.Ordinal))
                // Only orders marked with a RECORDED, unreconciled COD collection are pending reconciliation.
                .Where(order => !codPendingReconciliation || codPendingOrders.ContainsKey(order.Id))
                .OrderByDescending(order => order.Id)
                .ToArray();
            return Task.FromResult(new OrderPageResult(items, null));
        }

        public Task<OrderDetailResult> GetAsync(
            Guid actorId,
            Guid organizationId,
            Guid orderId,
            bool mfaSatisfied,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (orderId == ForeignOrderId ||
                !orders.TryGetValue(orderId, out var order) ||
                order.OwnerOrganizationId != organizationId)
            {
                throw new OrderNotFoundException();
            }

            // UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: the same policy the PostgreSQL service applies, with this
            // stub's role table standing in for the membership snapshot.
            var allowed = OrderAllowedTransitionsPolicy.Compute(
                new StubAuthorizer(this, actorId, organizationId, orderId),
                organizationId,
                order,
                new OrderTransitionAuthorizationSnapshot(null, false),
                mfaSatisfied,
                DateTimeOffset.UtcNow);
            return Task.FromResult(new OrderDetailResult(
                order,
                timelines[orderId],
                allowed));
        }

        private sealed class StubAuthorizer(
            StubOrderService service,
            Guid actorId,
            Guid organizationId,
            Guid orderId) : IOrderTransitionAuthorizer
        {
            public bool IsAuthorized(OrderTransitionAuthorizationContext context) =>
                service.IsAuthorized(
                    new TransitionOrderCommand(
                        actorId,
                        organizationId,
                        "stub-allowed-transitions",
                        orderId,
                        context.Target.ToContractValue(),
                        "stub",
                        1,
                        null,
                        context.MfaSatisfied,
                        null),
                    context.Source,
                    context.Target);

            public bool HoldsTransitionCapability(
                string? activeRole,
                bool mfaSatisfied,
                bool hasMatchingDriverAssignment) =>
                new OrderTransitionAuthorizer().HoldsTransitionCapability(
                    activeRole,
                    mfaSatisfied,
                    hasMatchingDriverAssignment);
        }

        /// <summary>The stub's role table without the per-edge driver list, as HoldsTransitionCapability.</summary>
        private bool HoldsCapability(TransitionOrderCommand command) =>
            command.ActorId switch
            {
                var id when id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2") =>
                    command.MfaSatisfied,
                var id when id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3") =>
                    command.MfaSatisfied,
                var id when id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4") =>
                    command.OrganizationId == Identity.Infrastructure.Mock.MockIdentityProfiles.OperationsOrganizationId,
                var id when id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa10") =>
                    true,
                var id when id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11") =>
                    activeDriverAssignments.ContainsKey(command.OrderId),
                _ => false,
            };

        private bool IsAuthorized(
            TransitionOrderCommand command,
            OrderStatus source,
            OrderStatus target) =>
            command.ActorId switch
            {
                var id when id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2") =>
                    command.MfaSatisfied,
                var id when id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3") =>
                    command.MfaSatisfied,
                var id when id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4") =>
                    command.OrganizationId == Identity.Infrastructure.Mock.MockIdentityProfiles.OperationsOrganizationId,
                var id when id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa10") =>
                    true,
                var id when id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11") =>
                    activeDriverAssignments.ContainsKey(command.OrderId) &&
                    new OrderTransitionAuthorizer().IsAuthorized(new(
                        "DRIVER",
                        source,
                        target,
                        command.MfaSatisfied,
                        true)),
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

        private sealed record StoredResponse(
            string Signature,
            OrderResult Result,
            OrderStatus Source = default,
            OrderStatus Target = default);
    }
}
