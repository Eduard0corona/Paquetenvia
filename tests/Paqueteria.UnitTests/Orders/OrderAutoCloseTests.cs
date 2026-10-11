using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orders.Application.Lifecycle;
using Orders.Application.Orders;
using Orders.Domain;
using Orders.Infrastructure;
using Orders.Infrastructure.Lifecycle;
using Orders.Infrastructure.Orders;
using Orders.Infrastructure.Persistence;
using Orders.Infrastructure.Persistence.Migrations;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Scheduling;
using Paqueteria.Infrastructure.Scheduling;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.UnitTests.Orders;

/// <summary>
/// ORD-AUTO-CLOSE-2026-10-10: the bounded cycle (batches, resume point, outcomes, failures), the system-actor policy and
/// the Worker composition. Whether an order may close is never decided here: the fake attempter stands in for the
/// ORD-002 transition and its guards.
/// </summary>
public sealed class OrderAutoCloseTests
{
    [Fact]
    public async Task Closes_every_eligible_order_across_owners_in_order_and_drains_the_pass()
    {
        var store = new FakeDeliveredOrders(owners: 3, ordersPerOwner: [2, 1, 2]);

        var result = await new OrderAutoCloseCycle(store, store, store)
            .RunAsync(new OrderAutoClosePolicy(2, 10), CancellationToken.None);

        Assert.Equal(5, result.Closed);
        Assert.Equal(5, result.Attempted);
        Assert.Equal(3, result.Batches);
        Assert.True(result.Drained);
        Assert.Equal(0, result.NotEligible + result.Superseded + result.Failed);
        Assert.Equal(store.AllOrdersInDatabaseOrder, store.Attempts);
        Assert.Empty(store.Delivered);
    }

    [Fact]
    public async Task An_empty_pass_drains_without_any_batch()
    {
        var store = new FakeDeliveredOrders(owners: 0, ordersPerOwner: []);

        var result = await new OrderAutoCloseCycle(store, store, store)
            .RunAsync(new OrderAutoClosePolicy(10, 5), CancellationToken.None);

        Assert.Equal(0, result.Batches);
        Assert.Equal(0, result.Attempted);
        Assert.True(result.Drained);
        Assert.Equal([10], store.DiscoveryLimits);
    }

    [Fact]
    public async Task Orders_whose_guards_do_not_hold_stay_delivered_and_are_counted_by_rule()
    {
        var store = new FakeDeliveredOrders(owners: 2, ordersPerOwner: [3, 2]);
        var orders = store.AllOrdersInDatabaseOrder;
        store.Script(orders[0], OrderAutoCloseAttempt.NotEligible(OrderTransitionRejectionCodes.CodNotReconciled));
        store.Script(orders[2], OrderAutoCloseAttempt.NotEligible(OrderTransitionRejectionCodes.UnresolvedIncident));
        store.Script(orders[3], OrderAutoCloseAttempt.NotEligible(OrderTransitionRejectionCodes.CodNotReconciled));
        var cycle = new OrderAutoCloseCycle(store, store, store);

        var first = await cycle.RunAsync(new OrderAutoClosePolicy(100, 1), CancellationToken.None);

        Assert.Equal(2, first.Closed);
        Assert.Equal(3, first.NotEligible);
        Assert.True(first.Drained);
        Assert.Equal(2, first.NotEligibleByRule[OrderTransitionRejectionCodes.CodNotReconciled]);
        Assert.Equal(1, first.NotEligibleByRule[OrderTransitionRejectionCodes.UnresolvedIncident]);
        Assert.Equal([orders[0], orders[2], orders[3]], store.Delivered.Order(OrderIdComparer.Instance));

        // Once a guard holds, the next pass closes the order; the others are tried again.
        store.Script(orders[2], OrderAutoCloseAttempt.Closed);
        var second = await cycle.RunAsync(new OrderAutoClosePolicy(100, 1), CancellationToken.None);
        Assert.Equal(1, second.Closed);
        Assert.Equal(2, second.NotEligible);
        Assert.Equal([orders[0], orders[3]], store.Delivered.Order(OrderIdComparer.Instance));
    }

    [Fact]
    public async Task A_capped_cycle_resumes_after_the_last_order_tried_so_blocked_orders_never_starve_the_rest()
    {
        var store = new FakeDeliveredOrders(owners: 2, ordersPerOwner: [3, 3]);
        var orders = store.AllOrdersInDatabaseOrder;
        foreach (var blocked in orders.Take(5))
        {
            store.Script(blocked, OrderAutoCloseAttempt.NotEligible(OrderTransitionRejectionCodes.CodNotReconciled));
        }

        var cycle = new OrderAutoCloseCycle(store, store, store);
        var policy = new OrderAutoClosePolicy(2, 2);

        var first = await cycle.RunAsync(policy, CancellationToken.None);
        Assert.False(first.Drained);
        Assert.Equal(2, first.Batches);
        Assert.Equal(orders.Take(4), store.Attempts);

        // The next cycle resumes inside the second owner, after the last order tried, and reaches the eligible one.
        var second = await cycle.RunAsync(policy, CancellationToken.None);
        Assert.True(second.Drained);
        Assert.Equal(1, second.Closed);
        Assert.Equal(orders, store.Attempts);

        // A drained pass starts over from the first owner.
        var third = await cycle.RunAsync(policy, CancellationToken.None);
        Assert.False(third.Drained);
        Assert.Equal(orders.Take(4), store.Attempts.Skip(orders.Count));
    }

    [Fact]
    public async Task Superseded_and_failed_attempts_are_counted_and_do_not_stop_the_pass()
    {
        var store = new FakeDeliveredOrders(owners: 2, ordersPerOwner: [2, 2]);
        var orders = store.AllOrdersInDatabaseOrder;
        store.Script(orders[0], OrderAutoCloseAttempt.Superseded);
        store.Fail(orders[1], new TimeoutException("synthetic"));
        store.Fail(orders[2], new InvalidOperationException("synthetic"));

        var result = await new OrderAutoCloseCycle(store, store, store)
            .RunAsync(new OrderAutoClosePolicy(10, 10), CancellationToken.None);

        Assert.Equal(4, result.Attempted);
        Assert.Equal(1, result.Superseded);
        Assert.Equal(2, result.Failed);
        Assert.Equal(1, result.Closed);
        Assert.True(result.Drained);
        Assert.Equal(["InvalidOperationException", "TimeoutException"], result.FailureTypes);
    }

    [Fact]
    public async Task Discovery_answers_outside_the_contract_fail_the_cycle()
    {
        var owner = FakeDeliveredOrders.Id(1, 0);
        foreach (var owners in new[]
                 {
                     new[] { owner, FakeDeliveredOrders.Id(2, 0), FakeDeliveredOrders.Id(3, 0) },
                     new[] { owner, owner },
                     new[] { Guid.Empty },
                 })
        {
            var cycle = new OrderAutoCloseCycle(
                new ScriptedDiscovery(owners), new FakeDeliveredOrders(0, []), new FakeDeliveredOrders(0, []));
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cycle.RunAsync(new OrderAutoClosePolicy(2, 1), CancellationToken.None));
            Assert.Equal("ORD_AUTO_CLOSE_DISCOVERY_OUT_OF_RANGE", exception.Message);
        }
    }

    [Fact]
    public async Task Candidates_of_another_owner_or_beyond_the_limit_fail_the_cycle()
    {
        var store = new FakeDeliveredOrders(owners: 2, ordersPerOwner: [1, 1]);
        var foreign = new OrderAutoCloseCandidate(FakeDeliveredOrders.Id(9, 0), FakeDeliveredOrders.Id(9, 1), 1);
        var cycle = new OrderAutoCloseCycle(store, new ScriptedReader([foreign]), store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cycle.RunAsync(new OrderAutoClosePolicy(5, 1), CancellationToken.None));

        Assert.Equal("ORD_AUTO_CLOSE_CANDIDATES_OUT_OF_RANGE", exception.Message);
        Assert.Empty(store.Attempts);
    }

    [Fact]
    public async Task Cancellation_stops_the_cycle_between_attempts()
    {
        var store = new FakeDeliveredOrders(owners: 1, ordersPerOwner: [3]);
        using var cancellation = new CancellationTokenSource();
        store.OnAttempt = attempts =>
        {
            if (attempts == 1)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OrderAutoCloseCycle(store, store, store).RunAsync(new OrderAutoClosePolicy(10, 10), cancellation.Token));

        Assert.Single(store.Attempts);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1_001, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void Policy_rejects_out_of_range_bounds(int batchSize, int maxBatches) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new OrderAutoClosePolicy(batchSize, maxBatches));

    [Fact]
    public void The_batch_bound_is_the_bound_the_discovery_function_enforces() =>
        Assert.Equal(AddOrderAutoCloseDiscovery.MaximumLimit, OrderAutoClosePolicy.MaximumBatchSize);

    [Fact]
    public void The_system_actor_may_take_delivered_to_closed_and_nothing_else()
    {
        foreach (var source in Enum.GetValues<OrderStatus>())
        {
            foreach (var target in Enum.GetValues<OrderStatus>())
            {
                Assert.Equal(
                    source == OrderStatus.Delivered && target == OrderStatus.Closed,
                    OrderSystemTransitionPolicy.IsAllowed(source, target));
            }
        }

        IReadOnlySet<OrderStatus> deliveredTargets = OrderTransitionMatrix.AllowedTransitions[OrderStatus.Delivered];
        Assert.Contains(OrderStatus.Closed, deliveredTargets);
    }

    [Fact]
    public void The_automatic_close_reason_and_keys_pass_the_transition_input_rules_unchanged()
    {
        var reason = OrderSystemTransitionPolicy.AutoCloseReason;
        Assert.StartsWith("Cierre automático:", reason, StringComparison.Ordinal);
        Assert.InRange(reason.Length, 1, OrderTransitionInputPolicy.MaximumReasonLength);
        Assert.True(IdempotencyKeyPolicy.IsValid($"{OrderSystemTransitionPolicy.IdempotencyKeyPrefix}{Guid.NewGuid():N}"));

        // The event and audit carry reason_redacted: the redactor must leave the automatic reason intact.
        var redacted = new AuditPayloadRedactor().Redact(JsonSerializer.SerializeToElement(new { reason }));
        using var document = JsonDocument.Parse(redacted.Json);
        Assert.Equal(reason, document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void Options_default_to_off_with_the_lif001_bounds()
    {
        using var provider = Provider([]);
        var options = provider.GetRequiredService<IOptions<OrderAutoCloseOptions>>().Value;

        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(60), options.PollInterval);
        Assert.Equal(new OrderAutoClosePolicy(100, 10).BatchSize, options.ToPolicy().BatchSize);
        Assert.Equal(10, options.ToPolicy().MaxBatchesPerCycle);
    }

    [Theory]
    [InlineData("PollIntervalSeconds", "0")]
    [InlineData("PollIntervalSeconds", "3601")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "1001")]
    [InlineData("MaxBatchesPerCycle", "0")]
    [InlineData("MaxBatchesPerCycle", "101")]
    public void Out_of_range_options_fail_validation(string key, string value)
    {
        using var provider = Provider(new() { [$"Orders:AutoClose:{key}"] = value });
        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<OrderAutoCloseOptions>>().Value);
    }

    [Fact]
    public void Enabling_requires_the_worker_connection_string()
    {
        using var withoutConnection = Provider(new() { ["Orders:AutoClose:Enabled"] = "true" });
        var exception = Assert.Throws<OptionsValidationException>(() =>
            withoutConnection.GetRequiredService<IOptions<OrderAutoCloseOptions>>().Value);
        Assert.Contains("ConnectionStrings:PaqueteriaWorker", exception.Message, StringComparison.Ordinal);

        using var withConnection = Provider(new()
        {
            ["Orders:AutoClose:Enabled"] = "true",
            ["ConnectionStrings:PaqueteriaWorker"] = WorkerConnection,
        });
        Assert.True(withConnection.GetRequiredService<IOptions<OrderAutoCloseOptions>>().Value.Enabled);
    }

    [Fact]
    public async Task Registration_composes_the_job_the_ready_check_and_the_worker_transition()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrdersAutoClose(new ConfigurationBuilder().Build());

        Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(OrderAutoCloseHostedService));
        await using var provider = services.BuildServiceProvider();
        Assert.IsType<PeriodicJobScheduler>(provider.GetRequiredService<IJobScheduler>());
        var job = provider.GetRequiredService<OrderAutoCloseJob>();
        Assert.Equal("orders.auto-close", job.Name);
        Assert.Equal(TimeSpan.FromSeconds(60), job.Interval);
        var check = Assert.Single(
            provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations,
            registration => registration.Name == "orders_auto_close");
        Assert.Contains("ready", check.Tags);

        // The Worker reaches the ORD-002 transition only as the system service, under the Worker role.
        await using var scope = provider.CreateAsyncScope();
        Assert.IsType<PostgreSqlOrderTransitionService>(
            scope.ServiceProvider.GetRequiredService<IOrderSystemTransitionService>());
        Assert.IsType<WorkerTenantTransactionContext<OrdersDbContext>>(
            scope.ServiceProvider.GetRequiredService<ITenantTransactionRunner<OrdersDbContext>>());

        // Every AI-04 guard, never the empty registry a type registration would compose from IEnumerable<IOrderTransitionGuard>.
        Assert.Equal(
            new OrderTransitionGuardRegistry().Guards.Select(guard => guard.Code),
            provider.GetRequiredService<OrderTransitionGuardRegistry>().Guards.Select(guard => guard.Code));
        Assert.Null(scope.ServiceProvider.GetService<IOrderTransitionService>());
    }

    [Fact]
    public async Task Disabled_job_never_reaches_the_scheduler_and_reports_healthy()
    {
        var scheduler = new RecordingScheduler();
        await using var provider = Provider([], scheduler);

        await RunHostedServiceAsync(provider);
        var health = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Empty(scheduler.Jobs);
        Assert.Equal(HealthStatus.Healthy, health.Entries["orders_auto_close"].Status);
    }

    [Fact]
    public async Task Enabled_job_is_handed_to_the_scheduler_with_its_bounded_interval()
    {
        var scheduler = new RecordingScheduler();
        await using var provider = Provider(
            new()
            {
                ["Orders:AutoClose:Enabled"] = "true",
                ["Orders:AutoClose:PollIntervalSeconds"] = "15",
                ["ConnectionStrings:PaqueteriaWorker"] = WorkerConnection,
            },
            scheduler);

        await RunHostedServiceAsync(provider);

        var job = Assert.Single(scheduler.Jobs);
        Assert.Equal("orders.auto-close", job.Name);
        Assert.Equal(TimeSpan.FromSeconds(15), job.Interval);
    }

    [Fact]
    public async Task A_job_run_with_failed_attempts_finishes_the_pass_then_reports_the_cycle_failed()
    {
        var store = new FakeDeliveredOrders(owners: 1, ordersPerOwner: [3]);
        store.Fail(store.AllOrdersInDatabaseOrder[1], new TimeoutException("synthetic"));
        await using var provider = Provider(
            new() { ["Orders:AutoClose:BatchSize"] = "2", ["Orders:AutoClose:MaxBatchesPerCycle"] = "5" },
            store: store);
        var job = provider.GetRequiredService<OrderAutoCloseJob>();

        var failed = await Assert.ThrowsAsync<OrderAutoCloseCycleException>(() => job.RunOnceAsync(CancellationToken.None));

        Assert.Equal(1, failed.Failed);
        Assert.Equal(["TimeoutException"], failed.FailureTypes);
        Assert.Equal(3, store.Attempts.Count);
        Assert.Single(store.Delivered);

        // The retried order closes on the next run, which then succeeds.
        store.Script(store.AllOrdersInDatabaseOrder[1], OrderAutoCloseAttempt.Closed);
        await job.RunOnceAsync(CancellationToken.None);
        Assert.Empty(store.Delivered);
    }

    [Fact]
    public async Task The_attempter_maps_transition_rejections_and_lets_unexpected_errors_through()
    {
        var candidate = new OrderAutoCloseCandidate(Guid.NewGuid(), Guid.NewGuid(), 7);
        foreach (var (thrown, expected) in new (Exception? Thrown, OrderAutoCloseAttempt Expected)[]
                 {
                     (null, OrderAutoCloseAttempt.Closed),
                     (new OrderTransitionConflictException(
                         OrderTransitionConflictCode.GuardNotSatisfied,
                         "if_cod_expected_then_cod_status_reconciled",
                         OrderTransitionRejectionCodes.CodNotReconciled),
                      OrderAutoCloseAttempt.NotEligible(OrderTransitionRejectionCodes.CodNotReconciled)),
                     (new OrderTransitionConflictException(OrderTransitionConflictCode.VersionConflict),
                      OrderAutoCloseAttempt.Superseded),
                     (new OrderTransitionConflictException(OrderTransitionConflictCode.InvalidState),
                      OrderAutoCloseAttempt.Superseded),
                     (new OrderTransitionConflictException(OrderTransitionConflictCode.TerminalState),
                      OrderAutoCloseAttempt.Superseded),
                     (new OrderTransitionConflictException(OrderTransitionConflictCode.OrderUnavailable),
                      OrderAutoCloseAttempt.Superseded),
                     (new OrderTransitionConflictException(OrderTransitionConflictCode.ConcurrencyConflict),
                      OrderAutoCloseAttempt.Superseded),
                 })
        {
            var transitions = new ScriptedSystemTransitions(thrown);
            Assert.Equal(expected, await Attempter(transitions).CloseAsync(candidate, CancellationToken.None));
            Assert.Equal(
                new SystemCloseOrderCommand(candidate.OwnerOrganizationId, candidate.OrderId, candidate.Version),
                Assert.Single(transitions.Commands));
        }

        foreach (var unexpected in new Exception[]
                 {
                     new OrderTransitionForbiddenException(),
                     new OrderTransitionConflictException(OrderTransitionConflictCode.InvalidRequest),
                     new OrderTransitionConflictException(OrderTransitionConflictCode.IdempotencyConflict),
                     new OrderTransitionInfrastructureException("synthetic"),
                 })
        {
            var thrown = await Assert.ThrowsAnyAsync<Exception>(() =>
                Attempter(new ScriptedSystemTransitions(unexpected)).CloseAsync(candidate, CancellationToken.None));
            Assert.Same(unexpected, thrown);
        }

        static OrderAutoCloseAttempter Attempter(ScriptedSystemTransitions transitions)
        {
            var services = new ServiceCollection();
            services.AddScoped<IOrderSystemTransitionService>(_ => transitions);
            return new OrderAutoCloseAttempter(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        }
    }

    private const string WorkerConnection = "Host=127.0.0.1;Database=ordautoclose;Username=worker;Password=unused";

    private static ServiceProvider Provider(
        Dictionary<string, string?> settings,
        IJobScheduler? scheduler = null,
        FakeDeliveredOrders? store = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (scheduler is not null)
        {
            services.AddSingleton(scheduler);
        }

        services.AddOrdersAutoClose(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        if (store is not null)
        {
            services.AddSingleton<IOrderAutoCloseOwnerDiscovery>(store);
            services.AddSingleton<IOrderAutoCloseCandidateReader>(store);
            services.AddSingleton<IOrderAutoCloseAttempter>(store);
        }

        return services.BuildServiceProvider();
    }

    private static async Task RunHostedServiceAsync(ServiceProvider provider)
    {
        var hosted = provider.GetServices<IHostedService>().OfType<OrderAutoCloseHostedService>().Single();
        await hosted.StartAsync(CancellationToken.None);
        await hosted.ExecuteTask!;
        await hosted.StopAsync(CancellationToken.None);
    }

    /// <summary>Orders identifiers the way PostgreSQL orders uuid values: by their canonical text.</summary>
    private sealed class OrderIdComparer : IComparer<Guid>
    {
        public static OrderIdComparer Instance { get; } = new();

        public int Compare(Guid x, Guid y) => string.CompareOrdinal(x.ToString("D"), y.ToString("D"));
    }

    /// <summary>
    /// An in-memory stand-in for the three ports: owners and their DELIVERED orders, keyset reads in database order,
    /// and a scripted transition that closes an order unless told otherwise.
    /// </summary>
    private sealed class FakeDeliveredOrders : IOrderAutoCloseOwnerDiscovery, IOrderAutoCloseCandidateReader, IOrderAutoCloseAttempter
    {
        private readonly SortedDictionary<Guid, SortedSet<Guid>> _delivered = new(OrderIdComparer.Instance);
        private readonly Dictionary<Guid, OrderAutoCloseAttempt> _scripted = [];
        private readonly Dictionary<Guid, Exception> _failures = [];

        public FakeDeliveredOrders(int owners, int[] ordersPerOwner)
        {
            for (var owner = 0; owner < owners; owner++)
            {
                var orders = new SortedSet<Guid>(OrderIdComparer.Instance);
                for (var order = 1; order <= ordersPerOwner[owner]; order++)
                {
                    orders.Add(Id(owner + 1, order));
                }

                _delivered.Add(Id(owner + 1, 0), orders);
            }

            AllOrdersInDatabaseOrder = _delivered.Values.SelectMany(orders => orders).ToArray();
        }

        public IReadOnlyList<Guid> AllOrdersInDatabaseOrder { get; }

        public List<Guid> Attempts { get; } = [];

        public List<int> DiscoveryLimits { get; } = [];

        public Action<int>? OnAttempt { get; set; }

        public IReadOnlyList<Guid> Delivered => _delivered.Values.SelectMany(orders => orders).ToArray();

        public static Guid Id(int owner, int order) => Guid.Parse($"{owner:D8}-0000-4000-8000-{order:D12}");

        public void Script(Guid order, OrderAutoCloseAttempt attempt)
        {
            _failures.Remove(order);
            _scripted[order] = attempt;
        }

        public void Fail(Guid order, Exception exception) => _failures[order] = exception;

        public Task<IReadOnlyList<Guid>> ListOwnersAsync(Guid? afterOwnerOrganizationId, int limit, CancellationToken cancellationToken)
        {
            DiscoveryLimits.Add(limit);
            IReadOnlyList<Guid> owners = _delivered
                .Where(pair => pair.Value.Count > 0 &&
                    (afterOwnerOrganizationId is null ||
                     OrderIdComparer.Instance.Compare(pair.Key, afterOwnerOrganizationId.Value) > 0))
                .Select(pair => pair.Key)
                .Take(limit)
                .ToArray();
            return Task.FromResult(owners);
        }

        public Task<IReadOnlyList<OrderAutoCloseCandidate>> ListDeliveredAsync(
            Guid ownerOrganizationId,
            Guid? afterOrderId,
            int limit,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<OrderAutoCloseCandidate> candidates = _delivered.TryGetValue(ownerOrganizationId, out var orders)
                ? orders
                    .Where(order => afterOrderId is null || OrderIdComparer.Instance.Compare(order, afterOrderId.Value) > 0)
                    .Take(limit)
                    .Select(order => new OrderAutoCloseCandidate(ownerOrganizationId, order, 3))
                    .ToArray()
                : [];
            return Task.FromResult(candidates);
        }

        public Task<OrderAutoCloseAttempt> CloseAsync(OrderAutoCloseCandidate candidate, CancellationToken cancellationToken)
        {
            Attempts.Add(candidate.OrderId);
            OnAttempt?.Invoke(Attempts.Count);
            if (_failures.TryGetValue(candidate.OrderId, out var failure))
            {
                return Task.FromException<OrderAutoCloseAttempt>(failure);
            }

            var attempt = _scripted.GetValueOrDefault(candidate.OrderId, OrderAutoCloseAttempt.Closed);
            if (attempt.Outcome == OrderAutoCloseOutcome.Closed)
            {
                _delivered[candidate.OwnerOrganizationId].Remove(candidate.OrderId);
            }

            return Task.FromResult(attempt);
        }
    }

    private sealed class ScriptedDiscovery(IReadOnlyList<Guid> owners) : IOrderAutoCloseOwnerDiscovery
    {
        public Task<IReadOnlyList<Guid>> ListOwnersAsync(Guid? afterOwnerOrganizationId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(owners);
    }

    private sealed class ScriptedReader(IReadOnlyList<OrderAutoCloseCandidate> candidates) : IOrderAutoCloseCandidateReader
    {
        public Task<IReadOnlyList<OrderAutoCloseCandidate>> ListDeliveredAsync(
            Guid ownerOrganizationId,
            Guid? afterOrderId,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult(candidates);
    }

    private sealed class ScriptedSystemTransitions(Exception? thrown) : IOrderSystemTransitionService
    {
        public List<SystemCloseOrderCommand> Commands { get; } = [];

        public Task<OrderResult> CloseDeliveredAsync(SystemCloseOrderCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return thrown is null
                ? Task.FromResult<OrderResult>(null!)
                : Task.FromException<OrderResult>(thrown);
        }
    }

    private sealed class RecordingScheduler : IJobScheduler
    {
        public List<IScheduledJob> Jobs { get; } = [];

        public Task RunAsync(IScheduledJob job, CancellationToken cancellationToken)
        {
            Jobs.Add(job);
            return Task.CompletedTask;
        }
    }
}
