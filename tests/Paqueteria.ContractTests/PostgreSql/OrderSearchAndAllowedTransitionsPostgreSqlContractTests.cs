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
/// UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05 on real PostgreSQL with RLS: listOrders' exact public_id search never
/// crosses tenants and answers missing, foreign and malformed numbers alike; getOrder's allowed_transitions follow
/// the transitionOrder rules for the owner dispatcher, an MFA-less platform admin, a viewer and the operator.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
public sealed class OrderSearchAndAllowedTransitionsPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Public_id_search_is_exact_tenant_scoped_and_uniform()
    {
        await using var other = new SyntheticOrderScenario(fixture);
        await other.InitializeAsync(orderStatus: "CONFIRMED");
        await using var owner = new SyntheticOrderScenario(fixture);
        await owner.InitializeAsync(orderStatus: "CONFIRMED");
        var ownPublicId = NewPublicId();
        var foreignPublicId = NewPublicId();
        await SetPublicIdAsync(owner, ownPublicId);
        await SetPublicIdAsync(other, foreignPublicId);
        await using var scope = CreateScope();

        var found = await List(scope.Service, owner, ownPublicId);
        Assert.Equal([owner.OrderId], found.Items.Select(order => order.Id));
        Assert.Equal(ownPublicId, found.Items[0].PublicId);
        Assert.Null(found.NextCursor);

        // Another organization's order, an unknown number, a malformed one and a case variant: the same empty page.
        foreach (var probe in new[]
        {
            foreignPublicId,
            NewPublicId(),
            SwapCase(ownPublicId),
            ownPublicId[..^1],
            ownPublicId + "A",
            ownPublicId[..20] + "%",
            "ORD_" + new string('_', 22),
            "' OR 1=1 --",
        })
        {
            var page = await List(scope.Service, owner, probe);
            Assert.Empty(page.Items);
            Assert.Null(page.NextCursor);
        }

        // The other organization finds its own order and never the owner's.
        Assert.Equal([other.OrderId], (await List(scope.Service, other, foreignPublicId)).Items.Select(order => order.Id));
        Assert.Empty((await List(scope.Service, other, ownPublicId)).Items);

        // The search narrows the other filters instead of replacing them.
        Assert.Empty((await scope.Service.ListAsync(
            owner.UserId, owner.OrganizationId, "DRAFT", null, null, false, false, ownPublicId, default)).Items);
        Assert.Single((await scope.Service.ListAsync(
            owner.UserId, owner.OrganizationId, "CONFIRMED", null, null, false, false, ownPublicId, default)).Items);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Allowed_transitions_follow_role_mfa_and_owner_only_ORD002()
    {
        await using var operatorOrganization = new SyntheticOrderScenario(fixture);
        await operatorOrganization.InitializeAsync(orderStatus: "CONFIRMED");
        await using var owner = new SyntheticOrderScenario(fixture);
        await owner.InitializeAsync(orderStatus: "CONFIRMED");
        var viewer = Guid.NewGuid();
        var admin = Guid.NewGuid();
        try
        {
            await AddMemberAsync(owner, viewer, "VIEWER");
            await AddMemberAsync(owner, admin, "PLATFORM_ADMIN");
            await owner.ExecuteAdminAsync(
                "UPDATE orders.orders SET operator_org_id=@operator WHERE id=@order;",
                SyntheticOrderScenario.P("operator", operatorOrganization.OrganizationId),
                SyntheticOrderScenario.P("order", owner.OrderId));
            await using var scope = CreateScope();

            var dispatcher = await scope.Service.GetAsync(
                owner.UserId, owner.OrganizationId, owner.OrderId, false, default);
            Assert.Equal("CONFIRMED", dispatcher.Order.Status);
            Assert.Equal(
                [OrderStatus.ReadyForPickup, OrderStatus.Cancelled],
                dispatcher.AllowedTransitions.Select(item => item.Target));
            Assert.All(dispatcher.AllowedTransitions, item => Assert.Empty(item.RequiredMetadata));

            Assert.Empty((await scope.Service.GetAsync(
                viewer, owner.OrganizationId, owner.OrderId, true, default)).AllowedTransitions);
            Assert.Empty((await scope.Service.GetAsync(
                admin, owner.OrganizationId, owner.OrderId, false, default)).AllowedTransitions);
            Assert.Equal(
                dispatcher.AllowedTransitions.Select(item => item.Target),
                (await scope.Service.GetAsync(admin, owner.OrganizationId, owner.OrderId, true, default))
                    .AllowedTransitions.Select(item => item.Target));

            // The operator reads the order through RLS but ORD-002 lets only the owner transition it.
            var operatorView = await scope.Service.GetAsync(
                operatorOrganization.UserId, operatorOrganization.OrganizationId, owner.OrderId, true, default);
            Assert.Equal(owner.OrderId, operatorView.Order.Id);
            Assert.Empty(operatorView.AllowedTransitions);

            // A foreign organization that is neither owner nor operator still gets the uniform not-found.
            await using var stranger = new SyntheticOrderScenario(fixture);
            await stranger.InitializeAsync(createOrder: false);
            await Assert.ThrowsAsync<OrderNotFoundException>(() => scope.Service.GetAsync(
                stranger.UserId, stranger.OrganizationId, owner.OrderId, true, default));

            // Terminal and DRAFT orders: nothing, and the restricted-goods acknowledgement respectively.
            await SetStatusAsync(owner, "CANCELLED");
            Assert.Empty((await scope.Service.GetAsync(
                owner.UserId, owner.OrganizationId, owner.OrderId, false, default)).AllowedTransitions);
            await SetStatusAsync(owner, "DRAFT");
            var draft = await scope.Service.GetAsync(owner.UserId, owner.OrganizationId, owner.OrderId, false, default);
            var confirm = Assert.Single(draft.AllowedTransitions, item => item.Target == OrderStatus.Confirmed);
            Assert.Equal(["restricted_goods_acknowledged"], confirm.RequiredMetadata);
        }
        finally
        {
            await owner.ExecuteAdminAsync(
                """
                UPDATE orders.orders SET operator_org_id=NULL WHERE id=@order;
                DELETE FROM organizations.organization_memberships WHERE user_id IN (@viewer,@admin);
                DELETE FROM identity.users WHERE id IN (@viewer,@admin);
                """,
                SyntheticOrderScenario.P("order", owner.OrderId),
                SyntheticOrderScenario.P("viewer", viewer),
                SyntheticOrderScenario.P("admin", admin));
        }
    }

    private static Task<OrderPageResult> List(IOrderService service, SyntheticOrderScenario scenario, string publicId) =>
        service.ListAsync(scenario.UserId, scenario.OrganizationId, null, null, null, false, false, publicId, default);

    /// <summary>The same characters with the letter case of the random part swapped: a different, valid number.</summary>
    private static string SwapCase(string publicId) =>
        OrderPublicIdPolicy.Prefix + new string(publicId[OrderPublicIdPolicy.Prefix.Length..]
            .Select(character => char.IsUpper(character) ? char.ToLowerInvariant(character) : char.ToUpperInvariant(character))
            .ToArray());

    private static string NewPublicId() =>
        OrderPublicIdPolicy.Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(OrderPublicIdPolicy.EntropyBytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static Task SetPublicIdAsync(SyntheticOrderScenario scenario, string publicId) =>
        scenario.ExecuteAdminAsync(
            "UPDATE orders.orders SET public_id=@public_id WHERE id=@order;",
            SyntheticOrderScenario.P("public_id", publicId),
            SyntheticOrderScenario.P("order", scenario.OrderId));

    private static Task SetStatusAsync(SyntheticOrderScenario scenario, string status) =>
        scenario.ExecuteAdminAsync(
            "UPDATE orders.orders SET status=@status WHERE id=@order;",
            SyntheticOrderScenario.P("status", status),
            SyntheticOrderScenario.P("order", scenario.OrderId));

    private static Task AddMemberAsync(SyntheticOrderScenario scenario, Guid userId, string role) =>
        scenario.ExecuteAdminAsync(
            """
            INSERT INTO identity.users(id,identity_subject) VALUES (@user_id,@subject);
            INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default)
              VALUES (gen_random_uuid(),@user_id,@org,@role,'ACTIVE',true);
            """,
            SyntheticOrderScenario.P("user_id", userId),
            SyntheticOrderScenario.P("subject", $"oidc|ui-phase2|{userId:N}"),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("role", role));

    private RuntimeScope CreateScope()
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
                PageSize = 2,
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
