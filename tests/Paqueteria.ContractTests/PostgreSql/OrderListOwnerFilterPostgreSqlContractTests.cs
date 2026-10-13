using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
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
/// ORD-LIST-OWNER-FILTER on real PostgreSQL with RLS: listOrders with owner_org_id equal to the selected organization
/// lists only the orders it owns, never the ones it only operates, alone or combined with status, the creation window,
/// public_id, the cursor and the FINANCE COD pending list; any other owner_org_id is still the empty page (AI-05).
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
public sealed class OrderListOwnerFilterPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly DateTimeOffset WindowStart = new(2026, 10, 1, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 10, 8, 6, 59, 59, TimeSpan.Zero);

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Own_organization_as_owner_lists_only_owned_orders_and_any_other_owner_is_the_empty_page()
    {
        await using var operatorOrganization = new SyntheticOrderScenario(fixture);
        await operatorOrganization.InitializeAsync(orderStatus: "DELIVERED");
        await using var owner = new SyntheticOrderScenario(fixture);
        await owner.InitializeAsync(orderStatus: "DELIVERED");
        await using var stranger = new SyntheticOrderScenario(fixture);
        await stranger.InitializeAsync(orderStatus: "DELIVERED");
        try
        {
            var orders = await ArrangeAsync(operatorOrganization, owner);
            await using var scope = CreateScope(pageSize: 20);

            // (a) Without the filter, RLS lists what the organization owns and what it operates, newest first.
            Assert.Equal(
                [
                    orders.OwnedOutside.Id, orders.OperatedClosed.Id, orders.OwnedClosed.Id, orders.OwnedLate.Id,
                    orders.OperatedMiddle.Id, orders.OwnedMiddle.Id, owner.OrderId, operatorOrganization.OrderId,
                ],
                Ids(await ListAsync(scope.Service, operatorOrganization)));

            // (b) owner_org_id equal to itself: only the orders it owns, newest first.
            Assert.Equal(
                [
                    orders.OwnedOutside.Id, orders.OwnedClosed.Id, orders.OwnedLate.Id, orders.OwnedMiddle.Id,
                    operatorOrganization.OrderId,
                ],
                Ids(await ListAsync(scope.Service, operatorOrganization, ownerOrganizationId: operatorOrganization.OrganizationId)));
            // The owner sees its orders with or without the filter: it owns all of them.
            var ownerOrders = new[] { orders.OperatedClosed.Id, orders.OperatedMiddle.Id, owner.OrderId };
            Assert.Equal(ownerOrders, Ids(await ListAsync(scope.Service, owner)));
            Assert.Equal(
                ownerOrders,
                Ids(await ListAsync(scope.Service, owner, ownerOrganizationId: owner.OrganizationId)));

            // (c) The owner of the orders it operates is another organization: still the empty page, as AI-05 says;
            // and the other way round.
            Assert.Empty((await ListAsync(
                scope.Service, operatorOrganization, ownerOrganizationId: owner.OrganizationId)).Items);
            Assert.Empty((await ListAsync(
                scope.Service, owner, ownerOrganizationId: operatorOrganization.OrganizationId)).Items);

            // (d) An unrelated organization, or an id of no organization: the empty page.
            foreach (var unrelated in new[] { stranger.OrganizationId, Guid.NewGuid() })
            {
                var page = await ListAsync(scope.Service, operatorOrganization, ownerOrganizationId: unrelated);
                Assert.Empty(page.Items);
                Assert.Null(page.NextCursor);
            }

            // The other filters still narrow the owned orders only.
            var window = new OrderListCreatedRange(WindowStart, WindowEnd);
            Assert.Equal(
                [orders.OwnedClosed.Id],
                Ids(await ListAsync(
                    scope.Service, operatorOrganization, status: "CLOSED", ownerOrganizationId: operatorOrganization.OrganizationId)));
            Assert.Equal(
                [orders.OwnedLate.Id, orders.OwnedMiddle.Id, operatorOrganization.OrderId],
                Ids(await ListAsync(
                    scope.Service,
                    operatorOrganization,
                    window,
                    status: "DELIVERED",
                    ownerOrganizationId: operatorOrganization.OrganizationId)));
            Assert.Equal(
                [orders.OwnedMiddle.Id],
                Ids(await ListAsync(
                    scope.Service,
                    operatorOrganization,
                    publicId: orders.OwnedMiddle.PublicId,
                    ownerOrganizationId: operatorOrganization.OrganizationId)));
            Assert.Empty((await ListAsync(
                scope.Service,
                operatorOrganization,
                publicId: orders.OperatedMiddle.PublicId,
                ownerOrganizationId: operatorOrganization.OrganizationId)).Items);
            // Without the owner filter the exact tracking number of an operated order is still found, as before.
            Assert.Equal(
                [orders.OperatedMiddle.Id],
                Ids(await ListAsync(scope.Service, operatorOrganization, publicId: orders.OperatedMiddle.PublicId)));
        }
        finally
        {
            await ReleaseOperatedAsync(owner);
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Own_owner_filter_combined_with_status_window_and_cursor_keeps_the_second_page_filtered()
    {
        await using var operatorOrganization = new SyntheticOrderScenario(fixture);
        await operatorOrganization.InitializeAsync(orderStatus: "DELIVERED");
        await using var owner = new SyntheticOrderScenario(fixture);
        await owner.InitializeAsync(orderStatus: "DELIVERED");
        try
        {
            var orders = await ArrangeAsync(operatorOrganization, owner);
            await using var scope = CreateScope(pageSize: 2);
            var window = new OrderListCreatedRange(WindowStart, WindowEnd);
            var self = operatorOrganization.OrganizationId;

            // (e) DELIVERED inside the window: three owned orders interleaved with two operated ones. The first page
            // ends on an owned order and the next order in the list is an operated one: the second page skips it.
            var first = await ListAsync(
                scope.Service, operatorOrganization, window, status: "DELIVERED", ownerOrganizationId: self);
            Assert.Equal([orders.OwnedLate.Id, orders.OwnedMiddle.Id], Ids(first));
            var cursor = Assert.IsType<string>(first.NextCursor);
            var second = await ListAsync(
                scope.Service, operatorOrganization, window, status: "DELIVERED", cursor: cursor, ownerOrganizationId: self);
            Assert.Equal([operatorOrganization.OrderId], Ids(second));
            Assert.Null(second.NextCursor);

            // The same walk without the owner filter still pages owned and operated orders, as before.
            var seen = new List<Guid>();
            string? next = null;
            var pages = 0;
            do
            {
                var page = await ListAsync(scope.Service, operatorOrganization, window, status: "DELIVERED", cursor: next);
                seen.AddRange(Ids(page));
                next = page.NextCursor;
                pages++;
            }
            while (next is not null && pages < 10);
            Assert.Equal(
                [
                    orders.OwnedLate.Id, orders.OperatedMiddle.Id, orders.OwnedMiddle.Id, owner.OrderId,
                    operatorOrganization.OrderId,
                ],
                seen);
            Assert.Equal(3, pages);

            // The cursor stays bound to owner_org_id (unchanged): without it, or the other way round, it is refused.
            var unfiltered = await ListAsync(scope.Service, operatorOrganization, window, status: "DELIVERED");
            var unfilteredCursor = Assert.IsType<string>(unfiltered.NextCursor);
            foreach (var call in new Func<Task<OrderPageResult>>[]
                     {
                         () => ListAsync(scope.Service, operatorOrganization, window, status: "DELIVERED", cursor: cursor),
                         () => ListAsync(
                             scope.Service,
                             operatorOrganization,
                             window,
                             status: "DELIVERED",
                             cursor: unfilteredCursor,
                             ownerOrganizationId: self),
                     })
            {
                var refused = await Assert.ThrowsAsync<OrderConflictException>(call);
                Assert.Equal(OrderConflictCode.InvalidRequest, refused.Code);
            }

            // Another owner_org_id is the empty page before the cursor is read, as before.
            Assert.Empty((await ListAsync(
                scope.Service,
                operatorOrganization,
                window,
                status: "DELIVERED",
                cursor: cursor,
                ownerOrganizationId: owner.OrganizationId)).Items);
        }
        finally
        {
            await ReleaseOperatedAsync(owner);
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Finance_cod_pending_list_with_its_own_organization_as_owner_lists_only_owned_orders()
    {
        await using var operatorOrganization = new SyntheticOrderScenario(fixture);
        await operatorOrganization.InitializeAsync(orderStatus: "DELIVERED");
        await using var owner = new SyntheticOrderScenario(fixture);
        await owner.InitializeAsync(orderStatus: "DELIVERED");
        try
        {
            var orders = await ArrangeAsync(operatorOrganization, owner);
            // A RECORDED, unreconciled collection on one owned and on one operated order.
            await RecordCodAsync(orders.OwnedMiddle.Id, operatorOrganization.OrganizationId, operatorId: null);
            await RecordCodAsync(orders.OperatedMiddle.Id, owner.OrganizationId, operatorOrganization.OrganizationId);
            await operatorOrganization.ExecuteAdminAsync(
                "UPDATE organizations.organization_memberships SET role='FINANCE' WHERE user_id=@actor AND organization_id=@org;",
                SyntheticOrderScenario.P("actor", operatorOrganization.UserId),
                SyntheticOrderScenario.P("org", operatorOrganization.OrganizationId));
            await using var scope = CreateScope(pageSize: 20);
            var self = operatorOrganization.OrganizationId;

            // (f) FINANCE with MFA: without the owner filter the pending list keeps both, as before; with its own
            // organization as owner only the owned order; with the other owner the empty page.
            Assert.Equal(
                [orders.OperatedMiddle.Id, orders.OwnedMiddle.Id],
                Ids(await ListPendingAsync(scope.Service, operatorOrganization, ownerOrganizationId: null)));
            Assert.Equal(
                [orders.OwnedMiddle.Id],
                Ids(await ListPendingAsync(scope.Service, operatorOrganization, ownerOrganizationId: self)));
            Assert.Equal(
                [orders.OwnedMiddle.Id],
                Ids(await ListPendingAsync(
                    scope.Service,
                    operatorOrganization,
                    ownerOrganizationId: self,
                    window: new OrderListCreatedRange(WindowStart, WindowEnd))));
            Assert.Empty((await ListPendingAsync(
                scope.Service, operatorOrganization, ownerOrganizationId: owner.OrganizationId)).Items);

            // The in-transaction role re-check still comes first: without MFA FINANCE is refused, filter or not.
            await Assert.ThrowsAsync<OrderListForbiddenException>(() =>
                ListPendingAsync(scope.Service, operatorOrganization, ownerOrganizationId: self, mfaSatisfied: false));
        }
        finally
        {
            await ReleaseOperatedAsync(owner);
        }
    }

    /// <summary>
    /// The operator organization owns its scenario order (+1 h) and four more; the owner organization owns its
    /// scenario order (+2 h) and two more, all three operated by the operator organization.
    /// </summary>
    private static async Task<ArrangedOrders> ArrangeAsync(
        SyntheticOrderScenario operatorOrganization,
        SyntheticOrderScenario owner)
    {
        await SetCreatedAtAsync(operatorOrganization, operatorOrganization.OrderId, WindowStart.AddHours(1));
        await SetCreatedAtAsync(owner, owner.OrderId, WindowStart.AddHours(2));
        var arranged = new ArrangedOrders(
            OwnedMiddle: await AddOrderAsync(operatorOrganization, WindowStart.AddHours(3), "DELIVERED"),
            OperatedMiddle: await AddOrderAsync(owner, WindowStart.AddHours(4), "DELIVERED"),
            OwnedLate: await AddOrderAsync(operatorOrganization, WindowStart.AddHours(5), "DELIVERED"),
            OwnedClosed: await AddOrderAsync(operatorOrganization, WindowStart.AddHours(6), "CLOSED"),
            OperatedClosed: await AddOrderAsync(owner, WindowStart.AddHours(7), "CLOSED"),
            OwnedOutside: await AddOrderAsync(operatorOrganization, WindowEnd.AddDays(1), "DELIVERED"));
        await owner.ExecuteAdminAsync(
            "UPDATE orders.orders SET operator_org_id=@operator WHERE owner_org_id=@owner;",
            SyntheticOrderScenario.P("operator", operatorOrganization.OrganizationId),
            SyntheticOrderScenario.P("owner", owner.OrganizationId));
        return arranged;
    }

    private static Task ReleaseOperatedAsync(SyntheticOrderScenario owner) =>
        owner.ExecuteAdminAsync(
            """
            DELETE FROM finance.cod_transactions WHERE owner_org_id=@owner;
            UPDATE orders.orders SET operator_org_id=NULL WHERE owner_org_id=@owner;
            """,
            SyntheticOrderScenario.P("owner", owner.OrganizationId));

    private Task RecordCodAsync(Guid orderId, Guid ownerId, Guid? operatorId) =>
        ExecuteAdminAsync(
            """
            INSERT INTO finance.cod_transactions(
              id,order_id,owner_org_id,operator_org_id,amount_cents,status,recorded_at)
            VALUES (gen_random_uuid(),@order,@owner,@operator,5000,'RECORDED',clock_timestamp());
            """,
            SyntheticOrderScenario.P("order", orderId),
            SyntheticOrderScenario.P("owner", ownerId),
            new NpgsqlParameter("operator", NpgsqlDbType.Uuid) { Value = (object?)operatorId ?? DBNull.Value });

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static Guid[] Ids(OrderPageResult page) => page.Items.Select(order => order.Id).ToArray();

    private static Task<OrderPageResult> ListAsync(
        IOrderService service,
        SyntheticOrderScenario scenario,
        OrderListCreatedRange window = default,
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

    private static Task<OrderPageResult> ListPendingAsync(
        IOrderService service,
        SyntheticOrderScenario scenario,
        Guid? ownerOrganizationId,
        bool mfaSatisfied = true,
        OrderListCreatedRange window = default) =>
        service.ListAsync(
            scenario.UserId,
            scenario.OrganizationId,
            null,
            ownerOrganizationId,
            null,
            true,
            mfaSatisfied,
            null,
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

    private sealed record ArrangedOrders(
        (Guid Id, string PublicId) OwnedMiddle,
        (Guid Id, string PublicId) OperatedMiddle,
        (Guid Id, string PublicId) OwnedLate,
        (Guid Id, string PublicId) OwnedClosed,
        (Guid Id, string PublicId) OperatedClosed,
        (Guid Id, string PublicId) OwnedOutside);

    private sealed class RuntimeScope(OrdersDbContext context, IOrderService service) : IAsyncDisposable
    {
        internal IOrderService Service { get; } = service;
        public ValueTask DisposeAsync() => context.DisposeAsync();
    }
}
