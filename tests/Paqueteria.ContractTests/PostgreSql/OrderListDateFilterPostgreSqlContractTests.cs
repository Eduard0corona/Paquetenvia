using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orders.Application.Orders;
using Orders.Domain;
using Orders.Infrastructure;
using Orders.Infrastructure.Orders;
using Orders.Infrastructure.Persistence;
using Paqueteria.Application.Auditing;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// ORD-LIST-DATE-FILTER-2026-10-12 on real PostgreSQL with RLS: listOrders' created_from and created_to keep exactly
/// the orders created inside the inclusive window, newest first, combined with status and public_id; the cursor pages
/// that list only and is refused for any other filter; owned and operated orders follow the same RLS as before and
/// another organization's orders are never listed.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
public sealed class OrderListDateFilterPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    /// <summary>Mazatlán midnight of 2026-10-01 (UTC-7, no daylight saving time).</summary>
    private static readonly DateTimeOffset WindowStart = new(2026, 10, 1, 7, 0, 0, TimeSpan.Zero);

    /// <summary>The last microsecond of 2026-10-07 in Mazatlán, as the web sends it.</summary>
    private static readonly DateTimeOffset WindowEnd =
        new DateTimeOffset(2026, 10, 8, 6, 59, 59, TimeSpan.Zero).AddTicks(9_999_990);

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Creation_window_is_inclusive_newest_first_and_combines_with_the_other_filters()
    {
        await using var owner = new SyntheticOrderScenario(fixture);
        await owner.InitializeAsync(orderStatus: "DELIVERED");
        await SetCreatedAtAsync(owner, owner.OrderId, WindowStart);
        var beforeStart = await AddOrderAsync(owner, WindowStart.AddTicks(-10), "DELIVERED");
        var afterEnd = await AddOrderAsync(owner, WindowEnd.AddTicks(10), "DELIVERED");
        var atEnd = await AddOrderAsync(owner, WindowEnd, "CLOSED");
        var middle = await AddOrderAsync(owner, WindowStart.AddDays(3), "DELIVERED");
        await using var scope = CreateScope(pageSize: 10);

        var window = new OrderListCreatedRange(WindowStart, WindowEnd);
        var page = await ListAsync(scope.Service, owner, window);
        // Both bounds are inclusive and the order stays created_at DESC, id DESC.
        Assert.Equal([atEnd.Id, middle.Id, owner.OrderId], page.Items.Select(order => order.Id));
        Assert.Null(page.NextCursor);

        // Each bound alone.
        Assert.Equal(
            [afterEnd.Id, atEnd.Id, middle.Id, owner.OrderId],
            (await ListAsync(scope.Service, owner, new OrderListCreatedRange(WindowStart, null))).Items.Select(order => order.Id));
        Assert.Equal(
            [atEnd.Id, middle.Id, owner.OrderId, beforeStart.Id],
            (await ListAsync(scope.Service, owner, new OrderListCreatedRange(null, WindowEnd))).Items.Select(order => order.Id));
        // A single instant keeps the order created exactly then.
        Assert.Equal(
            [owner.OrderId],
            (await ListAsync(scope.Service, owner, new OrderListCreatedRange(WindowStart, WindowStart))).Items.Select(order => order.Id));
        // No window lists them all, as before.
        Assert.Equal(5, (await ListAsync(scope.Service, owner, default)).Items.Count);

        // The window narrows the status and the exact public_id instead of replacing them.
        Assert.Equal(
            [middle.Id, owner.OrderId],
            (await ListAsync(scope.Service, owner, window, status: "DELIVERED")).Items.Select(order => order.Id));
        Assert.Equal(
            [atEnd.Id],
            (await ListAsync(scope.Service, owner, window, status: "CLOSED")).Items.Select(order => order.Id));
        Assert.Equal(
            [middle.Id],
            (await ListAsync(scope.Service, owner, window, publicId: middle.PublicId)).Items.Select(order => order.Id));
        Assert.Empty((await ListAsync(scope.Service, owner, window, publicId: afterEnd.PublicId)).Items);
        Assert.Empty((await ListAsync(scope.Service, owner, window, status: "CANCELLED")).Items);

        // A window outside the policy is refused before any order is read.
        foreach (var invalid in new[]
                 {
                     new OrderListCreatedRange(WindowEnd, WindowStart),
                     new OrderListCreatedRange(WindowStart, WindowStart + OrderListCreatedRange.MaximumSpan + TimeSpan.FromTicks(10)),
                 })
        {
            var refused = await Assert.ThrowsAsync<OrderConflictException>(() => ListAsync(scope.Service, owner, invalid));
            Assert.Equal(OrderConflictCode.InvalidRequest, refused.Code);
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Cursor_pages_the_filtered_list_only_and_is_refused_for_other_filters()
    {
        await using var owner = new SyntheticOrderScenario(fixture);
        await owner.InitializeAsync(orderStatus: "DELIVERED");
        await SetCreatedAtAsync(owner, owner.OrderId, WindowStart.AddHours(1));
        var second = await AddOrderAsync(owner, WindowStart.AddHours(2), "DELIVERED");
        var third = await AddOrderAsync(owner, WindowStart.AddHours(3), "DELIVERED");
        // Same created_at as the third: the id breaks the tie.
        var tie = await AddOrderAsync(owner, WindowStart.AddHours(3), "DELIVERED");
        var outside = await AddOrderAsync(owner, WindowEnd.AddDays(1), "DELIVERED");
        await using var scope = CreateScope(pageSize: 2);
        var window = new OrderListCreatedRange(WindowStart, WindowEnd);

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await ListAsync(scope.Service, owner, window, status: "DELIVERED", cursor: cursor);
            seen.AddRange(page.Items.Select(order => order.Id));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        // PostgreSQL orders uuid by its bytes, the order of the canonical lowercase text.
        var newestFirst = new[] { third.Id, tie.Id }
            .OrderByDescending(id => id.ToString("D"), StringComparer.Ordinal)
            .Concat([second.Id, owner.OrderId])
            .ToArray();
        Assert.Equal(newestFirst, seen);
        Assert.DoesNotContain(outside.Id, seen);
        Assert.Equal(2, pages);

        var first = await ListAsync(scope.Service, owner, window, status: "DELIVERED");
        var issued = Assert.IsType<string>(first.NextCursor);
        Assert.True(issued.Length <= OrderCursorCodec.MaximumLength);
        Assert.Equal(
            [second.Id, owner.OrderId],
            (await ListAsync(scope.Service, owner, window, status: "DELIVERED", cursor: issued)).Items.Select(order => order.Id));

        // The same position under any other filter, window or organization is refused, never paged.
        await using var stranger = new SyntheticOrderScenario(fixture);
        await stranger.InitializeAsync(createOrder: false);
        foreach (var call in new Func<Task<OrderPageResult>>[]
                 {
                     () => ListAsync(scope.Service, owner, window, cursor: issued),
                     () => ListAsync(scope.Service, owner, window, status: "CLOSED", cursor: issued),
                     () => ListAsync(scope.Service, owner, default, status: "DELIVERED", cursor: issued),
                     () => ListAsync(scope.Service, owner, window with { To = null }, status: "DELIVERED", cursor: issued),
                     () => ListAsync(scope.Service, owner, window, status: "DELIVERED", publicId: second.PublicId, cursor: issued),
                     () => ListAsync(scope.Service, owner, window, status: "DELIVERED", cursor: issued + "A"),
                     () => ListAsync(scope.Service, stranger, window, status: "DELIVERED", cursor: issued),
                 })
        {
            var refused = await Assert.ThrowsAsync<OrderConflictException>(call);
            Assert.Equal(OrderConflictCode.InvalidRequest, refused.Code);
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Owned_and_operated_orders_follow_the_same_RLS_with_a_window()
    {
        await using var operatorOrganization = new SyntheticOrderScenario(fixture);
        await operatorOrganization.InitializeAsync(orderStatus: "ASSIGNED");
        await using var owner = new SyntheticOrderScenario(fixture);
        await owner.InitializeAsync(orderStatus: "ASSIGNED");
        await using var stranger = new SyntheticOrderScenario(fixture);
        await stranger.InitializeAsync(orderStatus: "ASSIGNED");
        try
        {
            await SetCreatedAtAsync(owner, owner.OrderId, WindowStart.AddDays(1));
            await SetCreatedAtAsync(operatorOrganization, operatorOrganization.OrderId, WindowStart.AddDays(2));
            await SetCreatedAtAsync(stranger, stranger.OrderId, WindowStart.AddDays(3));
            await owner.ExecuteAdminAsync(
                "UPDATE orders.orders SET operator_org_id=@operator WHERE id=@order;",
                SyntheticOrderScenario.P("operator", operatorOrganization.OrganizationId),
                SyntheticOrderScenario.P("order", owner.OrderId));
            await using var scope = CreateScope(pageSize: 10);
            var window = new OrderListCreatedRange(WindowStart, WindowEnd);

            // The owner lists its order; the operator lists the order it operates and its own, newest first; the
            // stranger lists only its own. Nobody lists an order of an organization it neither owns nor operates.
            Assert.Equal(
                [owner.OrderId],
                (await ListAsync(scope.Service, owner, window)).Items.Select(order => order.Id));
            Assert.Equal(
                [operatorOrganization.OrderId, owner.OrderId],
                (await ListAsync(scope.Service, operatorOrganization, window)).Items.Select(order => order.Id));
            Assert.Equal(
                [stranger.OrderId],
                (await ListAsync(scope.Service, stranger, window)).Items.Select(order => order.Id));

            // owner_org_id of another organization is still the empty page, with or without a window.
            Assert.Empty((await ListAsync(
                scope.Service, operatorOrganization, window, ownerOrganizationId: owner.OrganizationId)).Items);

            // The operated order outside the window is not listed for anybody.
            await SetCreatedAtAsync(owner, owner.OrderId, WindowEnd.AddDays(2));
            Assert.Empty((await ListAsync(scope.Service, owner, window)).Items);
            Assert.Equal(
                [operatorOrganization.OrderId],
                (await ListAsync(scope.Service, operatorOrganization, window)).Items.Select(order => order.Id));
        }
        finally
        {
            await owner.ExecuteAdminAsync(
                "UPDATE orders.orders SET operator_org_id=NULL WHERE id=@order;",
                SyntheticOrderScenario.P("order", owner.OrderId));
        }
    }

    private static Task<OrderPageResult> ListAsync(
        IOrderService service,
        SyntheticOrderScenario scenario,
        OrderListCreatedRange window,
        string? status = null,
        string? publicId = null,
        string? cursor = null,
        Guid? ownerOrganizationId = null) =>
        service.ListAsync(
            scenario.UserId,
            scenario.OrganizationId,
            status,
            ownerOrganizationId,
            cursor,
            false,
            false,
            publicId,
            window,
            default);

    private static Task SetCreatedAtAsync(SyntheticOrderScenario scenario, Guid orderId, DateTimeOffset createdAt) =>
        scenario.ExecuteAdminAsync(
            "UPDATE orders.orders SET created_at=@created_at WHERE id=@order;",
            SyntheticOrderScenario.P("created_at", createdAt),
            SyntheticOrderScenario.P("order", orderId));

    /// <summary>Another order of the scenario's organization, from a copy of its quote, created at the given instant.</summary>
    private static async Task<(Guid Id, string PublicId)> AddOrderAsync(
        SyntheticOrderScenario scenario,
        DateTimeOffset createdAt,
        string status)
    {
        var orderId = Guid.NewGuid();
        var quoteId = Guid.NewGuid();
        var publicId = OrderPublicIdPolicy.Prefix +
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(OrderPublicIdPolicy.EntropyBytes))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO pricing.quotes(
              id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,consolidated_route,
              subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,
              request_snapshot_redacted,package_snapshot,breakdown,input_hash,status,expires_at)
            SELECT @quote,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,consolidated_route,
              subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,
              request_snapshot_redacted,package_snapshot,breakdown,input_hash,'ACTIVE',expires_at
            FROM pricing.quotes WHERE id=@source_quote;
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,
              consolidated_route,payer_type,status,subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,
              currency,pricing_policy_version,package_snapshot,cod_expected_cents,version,created_at)
            SELECT @order,@public_id,q.id,q.owner_org_id,q.city_id,q.origin_location_id,q.destination_location_id,q.service_type,
              q.pricing_tier,q.consolidated_route,'SENDER',@status,q.subtotal_cents,q.discount_cents,q.tax_cents,q.total_cents,
              q.minimum_total_cents_snapshot,q.currency,q.pricing_policy_version,q.package_snapshot,0,1,@created_at
            FROM pricing.quotes q WHERE q.id=@quote;
            """,
            SyntheticOrderScenario.P("quote", quoteId),
            SyntheticOrderScenario.P("source_quote", scenario.QuoteId),
            SyntheticOrderScenario.P("order", orderId),
            SyntheticOrderScenario.P("public_id", publicId),
            SyntheticOrderScenario.P("status", status),
            SyntheticOrderScenario.P("created_at", createdAt));
        return (orderId, publicId);
    }

    private RuntimeScope CreateScope(int pageSize)
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(fixture.AppDataSource)
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new OrdersDbContext(options, state);
        var service = new QuoteSnapshotToOrderCoordinator(
            new TenantTransactionContext<OrdersDbContext>(context, state),
            new CryptographicOrderPublicIdGenerator(),
            new NoOpOrderCreationFailureInjector(),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            Options.Create(new OrdersOptions
            {
                Provider = OrdersProviderKind.PostgreSql,
                CommandTimeoutSeconds = 30,
                PageSize = pageSize,
                IdempotencyLifetimeMinutes = 60,
                PublicIdCollisionRetryCount = 2,
            }),
            new SystemClock());
        return new RuntimeScope(context, service);
    }

    private sealed class RuntimeScope(OrdersDbContext context, IOrderService service) : IAsyncDisposable
    {
        internal IOrderService Service { get; } = service;
        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
