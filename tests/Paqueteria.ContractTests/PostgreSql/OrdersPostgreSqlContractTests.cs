using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Orders.Application.Orders;
using Orders.Infrastructure;
using Orders.Infrastructure.Orders;
using Orders.Infrastructure.Persistence;
using Paqueteria.Application.Auditing;
using Paqueteria.Contracts.Legal;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
public sealed class OrdersPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Create_replay_queries_and_all_transactional_artifacts_match_the_quote()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(createOrder: false);
        await scenario.ExecuteAdminAsync(
            """
            UPDATE pricing.quotes
            SET package_snapshot='[
              {"description":"synthetic compact","weight_grams":500,"declared_value_cents":3000000000,
               "length_mm":100,"width_mm":200,"height_mm":300},
              {"description":"synthetic no dimensions","weight_grams":750,"declared_value_cents":4000000000}
            ]'::jsonb,
            breakdown='{"origin_location_id":"ffffffff-ffff-ffff-ffff-ffffffffffff",
                        "destination_location_id":"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"}'::jsonb
            WHERE id=@quote;
            """,
            SyntheticOrderScenario.P("quote", scenario.QuoteId));
        await using var scope = CreateScope(new SequencePublicIdGenerator("ORD_AAAAAAAAAAAAAAAAAAAAAA"));
        var command = CreateCommand(scenario, "orders-pg-complete-0001");

        var created = await scope.Service.CreateAsync(command, CancellationToken.None);
        var replay = await scope.Service.CreateAsync(command, CancellationToken.None);

        Assert.Equal(created, replay);
        Assert.Equal(scenario.OrganizationId, created.OwnerOrganizationId);
        Assert.Equal(scenario.QuoteId, created.QuoteId);
        Assert.Equal("DRAFT", created.Status);
        Assert.Equal(1, created.Version);
        Assert.Equal(scenario.SubtotalCents - scenario.DiscountCents, created.PriceNet.AmountCents);
        Assert.Equal(scenario.TotalCents, created.Total.AmountCents);
        Assert.True(created.Total.AmountCents > int.MaxValue);
        Assert.True(Orders.Domain.OrderPublicIdPolicy.IsValid(created.PublicId));

        var page = await scope.Service.ListAsync(
            scenario.UserId, scenario.OrganizationId, "DRAFT", scenario.OrganizationId, null, CancellationToken.None);
        Assert.Single(page.Items);
        var detail = await scope.Service.GetAsync(
            scenario.UserId, scenario.OrganizationId, created.Id, CancellationToken.None);
        Assert.Equal(created, detail.Order);
        Assert.Collection(detail.Timeline, item => Assert.Equal("ORDER_CREATED", item.EventType));

        await using var commandReader = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              q.status,q.consumed_at IS NOT NULL,
              o.owner_org_id=o2.owner_org_id,
              o.quote_id=q.id,o.city_id=q.city_id,o.service_area_id IS NOT DISTINCT FROM q.service_area_id,
              o.origin_location_id=q.origin_location_id,o.destination_location_id=q.destination_location_id,
              o.service_type=q.service_type,o.pricing_tier=q.pricing_tier,o.consolidated_route=q.consolidated_route,
              o.subtotal_cents=q.subtotal_cents,o.discount_cents=q.discount_cents,o.tax_cents=q.tax_cents,
              o.total_cents=q.total_cents,o.minimum_total_cents_snapshot=q.minimum_total_cents_snapshot,
              o.currency=q.currency,o.pricing_policy_version=q.pricing_policy_version,
              o.package_snapshot=q.package_snapshot,o.financial_override IS NOT DISTINCT FROM q.financial_override,
              o.operator_org_id IS NULL,o.cod_expected_cents,o.version,
              o.claim_window_ends_at IS NULL,o.finalized_at IS NULL,o.archived_at IS NULL,
              (SELECT count(*) FROM orders.package_items p WHERE p.order_id=o.id),
              (SELECT count(*) FROM orders.order_acceptances a WHERE a.order_id=o.id),
              (SELECT count(*) FROM orders.order_events e WHERE e.order_id=o.id),
              (SELECT count(*) FROM platform.outbox_events x WHERE x.aggregate_id=o.id),
              (SELECT count(*) FROM platform.audit_logs a WHERE a.entity_id=o.id AND a.action='ORDER_CREATED'),
              (SELECT count(*) FROM platform.idempotency_keys i
                WHERE i.owner_org_id=o.owner_org_id AND i.scope='ORD-001:CREATE_ORDER'
                  AND i.resource_id=o.id AND i.response_status=201),
              (SELECT evidence_hash FROM orders.order_acceptances a WHERE a.order_id=o.id),
              (SELECT payload::text FROM orders.order_events e WHERE e.order_id=o.id),
              (SELECT payload::text FROM platform.outbox_events x WHERE x.aggregate_id=o.id),
              (SELECT payload_redacted::text FROM platform.audit_logs a
                WHERE a.entity_id=o.id AND a.action='ORDER_CREATED')
            FROM pricing.quotes q
            JOIN orders.orders o ON o.quote_id=q.id
            CROSS JOIN LATERAL (SELECT q.owner_org_id) o2
            WHERE q.id=@quote;
            """);
        commandReader.Parameters.AddWithValue("quote", scenario.QuoteId);
        await using var reader = await commandReader.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("USED", reader.GetString(0));
        for (var index = 1; index <= 20; index++)
        {
            Assert.True(reader.GetBoolean(index), $"Snapshot equality column {index} failed.");
        }
        Assert.Equal(0, reader.GetInt64(21));
        Assert.Equal(1, reader.GetInt32(22));
        Assert.True(reader.GetBoolean(23));
        Assert.True(reader.GetBoolean(24));
        Assert.True(reader.GetBoolean(25));
        Assert.Equal(2L, reader.GetInt64(26));
        Assert.Equal(1L, reader.GetInt64(27));
        Assert.Equal(1L, reader.GetInt64(28));
        Assert.Equal(1L, reader.GetInt64(29));
        Assert.Equal(1L, reader.GetInt64(30));
        Assert.Equal(1L, reader.GetInt64(31));

        var expectedEvidence = new OrderAcceptanceEvidence(
            created.Id, scenario.QuoteId, scenario.OrganizationId, scenario.UserId,
            "terms-synthetic-v1", "privacy-synthetic-v1",
            new DateTimeOffset(2026, 7, 22, 12, 34, 56, TimeSpan.Zero).AddTicks(7),
            "WEB");
        Assert.Equal(OrderAcceptanceCanonicalizer.ComputeSha256(expectedEvidence), reader.GetFieldValue<byte[]>(32));
        foreach (var jsonOrdinal in new[] { 33, 34, 35 })
        {
            var json = reader.GetString(jsonOrdinal);
            Assert.DoesNotContain("terms-synthetic", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("privacy-synthetic", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("address", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("phone", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("package", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cipher", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Every_significant_injected_failure_rolls_back_quote_order_evidence_and_idempotency()
    {
        foreach (var stage in Enum.GetValues<OrderCreationStage>())
        {
            await using var scenario = new SyntheticOrderScenario(fixture);
            await scenario.InitializeAsync(createOrder: false);
            await using var scope = CreateScope(
                new SequencePublicIdGenerator("ORD_AAAAAAAAAAAAAAAAAAAAAA"),
                new ThrowAtStageFailureInjector(stage));

            await Assert.ThrowsAsync<InjectedOrderFailureException>(() =>
                scope.Service.CreateAsync(CreateCommand(scenario, $"orders-pg-rollback-{(int)stage:D2}"), CancellationToken.None));

            await using var command = fixture.AdminDataSource.CreateCommand(
                """
                SELECT status,consumed_at IS NULL,
                  (SELECT count(*) FROM orders.orders WHERE quote_id=@quote),
                  (SELECT count(*) FROM orders.package_items WHERE owner_org_id=@org),
                  (SELECT count(*) FROM orders.order_acceptances WHERE owner_org_id=@org),
                  (SELECT count(*) FROM orders.order_events WHERE owner_org_id=@org),
                  (SELECT count(*) FROM platform.outbox_events WHERE owner_org_id=@org),
                  (SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND action='ORDER_CREATED'),
                  (SELECT count(*) FROM platform.idempotency_keys WHERE owner_org_id=@org AND scope='ORD-001:CREATE_ORDER')
                FROM pricing.quotes WHERE id=@quote;
                """);
            command.Parameters.AddWithValue("quote", scenario.QuoteId);
            command.Parameters.AddWithValue("org", scenario.OrganizationId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("ACTIVE", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            for (var ordinal = 2; ordinal <= 8; ordinal++)
            {
                Assert.Equal(0L, reader.GetInt64(ordinal));
            }
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Default_acceptance_timestamp_is_rejected_before_any_PostgreSQL_side_effect()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(createOrder: false);
        var generator = new SequencePublicIdGenerator("ORD_ZZZZZZZZZZZZZZZZZZZZZZ");
        await using var scope = CreateScope(generator);
        var command = CreateCommand(scenario, "orders-pg-default-accepted-at") with
        {
            Acceptance = new OrderAcceptanceInput(
                "terms-synthetic-v1",
                "privacy-synthetic-v1",
                default,
                "WEB"),
        };

        var exception = await Assert.ThrowsAsync<OrderConflictException>(() =>
            scope.Service.CreateAsync(command, CancellationToken.None));

        Assert.Equal(OrderConflictCode.InvalidRequest, exception.Code);
        Assert.Equal(0, generator.CallCount);
        await using var verify = fixture.AdminDataSource.CreateCommand(
            """
            SELECT q.status,q.consumed_at IS NULL,
              (SELECT count(*) FROM platform.idempotency_keys WHERE owner_org_id=@org),
              (SELECT count(*) FROM orders.orders WHERE owner_org_id=@org),
              (SELECT count(*) FROM orders.package_items WHERE owner_org_id=@org),
              (SELECT count(*) FROM orders.order_acceptances WHERE owner_org_id=@org),
              (SELECT count(*) FROM orders.order_events WHERE owner_org_id=@org),
              (SELECT count(*) FROM platform.outbox_events WHERE owner_org_id=@org),
              (SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND action='ORDER_CREATED')
            FROM pricing.quotes q
            WHERE q.id=@quote;
            """);
        verify.Parameters.AddWithValue("org", scenario.OrganizationId);
        verify.Parameters.AddWithValue("quote", scenario.QuoteId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("ACTIVE", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        for (var ordinal = 2; ordinal <= 8; ordinal++)
        {
            Assert.Equal(0L, reader.GetInt64(ordinal));
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Concurrency_hash_conflict_collision_retry_and_migration_contract_hold()
    {
        await using (var sameKeyScenario = new SyntheticOrderScenario(fixture))
        {
            await sameKeyScenario.InitializeAsync(createOrder: false);
            await using var first = CreateScope(new SequencePublicIdGenerator("ORD_AAAAAAAAAAAAAAAAAAAAAA"));
            await using var second = CreateScope(new SequencePublicIdGenerator("ORD_BBBBBBBBBBBBBBBBBBBBBB"));
            var command = CreateCommand(sameKeyScenario, "orders-pg-same-key-0001");
            var results = await Task.WhenAll(
                first.Service.CreateAsync(command, CancellationToken.None),
                second.Service.CreateAsync(command, CancellationToken.None));
            Assert.Single(results.Select(result => result.Id).Distinct());

            await Assert.ThrowsAsync<OrderConflictException>(() =>
                first.Service.CreateAsync(
                    command with { Acceptance = command.Acceptance with { TermsVersion = "terms-synthetic-v2" } },
                    CancellationToken.None));
        }

        await using (var distinctKeyScenario = new SyntheticOrderScenario(fixture))
        {
            await distinctKeyScenario.InitializeAsync(createOrder: false);
            await using var first = CreateScope(new SequencePublicIdGenerator("ORD_CCCCCCCCCCCCCCCCCCCCCC"));
            await using var second = CreateScope(new SequencePublicIdGenerator("ORD_DDDDDDDDDDDDDDDDDDDDDD"));
            var tasks = new[]
            {
                TryCreateAsync(first.Service, CreateCommand(distinctKeyScenario, "orders-pg-distinct-0001")),
                TryCreateAsync(second.Service, CreateCommand(distinctKeyScenario, "orders-pg-distinct-0002")),
            };
            var outcomes = await Task.WhenAll(tasks);
            Assert.Equal(1, outcomes.Count(value => value));
            Assert.Equal(1, outcomes.Count(value => !value));
        }

        await using (var invalidPackageScenario = new SyntheticOrderScenario(fixture))
        {
            await invalidPackageScenario.InitializeAsync(createOrder: false);
            await invalidPackageScenario.ExecuteAdminAsync(
                "UPDATE pricing.quotes SET package_snapshot='{}'::jsonb WHERE id=@quote;",
                SyntheticOrderScenario.P("quote", invalidPackageScenario.QuoteId));
            await using var scope = CreateScope(new SequencePublicIdGenerator("ORD_HHHHHHHHHHHHHHHHHHHHHH"));
            await Assert.ThrowsAsync<OrderConflictException>(() => scope.Service.CreateAsync(
                CreateCommand(invalidPackageScenario, "orders-pg-invalid-package"), CancellationToken.None));
            await using var verify = fixture.AdminDataSource.CreateCommand(
                "SELECT status,consumed_at IS NULL,(SELECT count(*) FROM orders.orders WHERE quote_id=@quote) FROM pricing.quotes WHERE id=@quote;");
            verify.Parameters.AddWithValue("quote", invalidPackageScenario.QuoteId);
            await using var invalidReader = await verify.ExecuteReaderAsync();
            Assert.True(await invalidReader.ReadAsync());
            Assert.Equal("ACTIVE", invalidReader.GetString(0));
            Assert.True(invalidReader.GetBoolean(1));
            Assert.Equal(0L, invalidReader.GetInt64(2));
        }

        await using (var existing = new SyntheticOrderScenario(fixture))
        await using (var collision = new SyntheticOrderScenario(fixture))
        {
            await existing.InitializeAsync(createOrder: true);
            await collision.InitializeAsync(createOrder: false);
            const string collidingPublicId = "ORD_EEEEEEEEEEEEEEEEEEEEEE";
            const string replacementPublicId = "ORD_FFFFFFFFFFFFFFFFFFFFFF";
            await existing.ExecuteAdminAsync(
                "UPDATE orders.orders SET public_id=@public WHERE id=@order;",
                SyntheticOrderScenario.P("public", collidingPublicId),
                SyntheticOrderScenario.P("order", existing.OrderId));
            await using var scope = CreateScope(new SequencePublicIdGenerator(
                collidingPublicId, replacementPublicId));
            var created = await scope.Service.CreateAsync(
                CreateCommand(collision, "orders-pg-collision-0001"), CancellationToken.None);
            Assert.Equal(replacementPublicId, created.PublicId);
        }

        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(fixture.AdminDataSource, postgres =>
            {
                postgres.MigrationsAssembly(typeof(OrdersDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_orders", "platform");
            }).Options;
        await using var context = new OrdersDbContext(options, state);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Single(await context.Database.GetAppliedMigrationsAsync());

        var cancelled = new CancellationToken(canceled: true);
        await using var cancellationScenario = new SyntheticOrderScenario(fixture);
        await cancellationScenario.InitializeAsync(createOrder: false);
        await using var cancellationScope = CreateScope(new SequencePublicIdGenerator("ORD_GGGGGGGGGGGGGGGGGGGGGG"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancellationScope.Service.CreateAsync(
                CreateCommand(cancellationScenario, "orders-pg-cancel-0001"), cancelled));
    }

    private RuntimeScope CreateScope(
        IOrderPublicIdGenerator generator,
        IOrderCreationFailureInjector? failureInjector = null)
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new OrdersDbContext(options, state);
        var service = new QuoteSnapshotToOrderCoordinator(
            new TenantTransactionContext<OrdersDbContext>(context, state),
            generator,
            failureInjector ?? new NoOpOrderCreationFailureInjector(),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            Options.Create(new OrdersOptions
            {
                Provider = OrdersProviderKind.PostgreSql,
                CommandTimeoutSeconds = 30,
                PageSize = 2,
                IdempotencyLifetimeMinutes = 60,
                PublicIdCollisionRetryCount = 2,
            }),
            new SystemClock());
        return new RuntimeScope(context, service);
    }

    private static CreateOrderCommand CreateCommand(SyntheticOrderScenario scenario, string key) => new(
        scenario.UserId,
        scenario.OrganizationId,
        key,
        scenario.QuoteId,
        "SENDER",
        new OrderAcceptanceInput(
            "terms-synthetic-v1",
            "privacy-synthetic-v1",
            new DateTimeOffset(2026, 7, 22, 12, 34, 56, TimeSpan.Zero).AddTicks(7),
            "WEB"),
        "synthetic-request-id");

    private static async Task<bool> TryCreateAsync(IOrderService service, CreateOrderCommand command)
    {
        try
        {
            await service.CreateAsync(command, CancellationToken.None);
            return true;
        }
        catch (OrderConflictException)
        {
            return false;
        }
    }

    private sealed class RuntimeScope(OrdersDbContext context, IOrderService service) : IAsyncDisposable
    {
        internal IOrderService Service { get; } = service;
        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private sealed class SequencePublicIdGenerator(params string[] values) : IOrderPublicIdGenerator
    {
        private int index;
        internal int CallCount => Volatile.Read(ref index);

        public string Create()
        {
            var current = Interlocked.Increment(ref index) - 1;
            return values[Math.Min(current, values.Length - 1)];
        }
    }

    private sealed class ThrowAtStageFailureInjector(OrderCreationStage target) : IOrderCreationFailureInjector
    {
        public Task OnStageAsync(OrderCreationStage stage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stage == target)
            {
                throw new InjectedOrderFailureException(stage);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InjectedOrderFailureException(OrderCreationStage stage)
        : Exception($"Injected failure at {stage}.");
}
