using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Reporting.Application.Operations;
using Reporting.Infrastructure;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11 (owner: "Solo órdenes activas"): the dashboard <c>price_review</c>
/// filter that feeds the inbox "Precio por revisar" list, on real PostgreSQL as the NOBYPASSRLS runtime role. It lists
/// exactly the orders with a cost warning that have not finished, pages them with the dashboard cursor, never shows
/// another organization's orders, and lists exactly what getOperationsQueueCounts counts as price_review.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
public sealed class OperationsPriceReviewPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Override =
        """{"actor_id":"synthetic","reason":"synthetic","valid_until":"2026-12-01T00:00:00Z"}""";

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Price_review_lists_only_unfinished_orders_with_a_warning_of_the_selected_organization()
    {
        await using var a = new SyntheticOrderScenario(fixture);
        await a.InitializeAsync(orderStatus: "ASSIGNED");
        await using var c = new SyntheticOrderScenario(fixture);
        await c.InitializeAsync(orderStatus: "IN_TRANSIT");

        // A: three active orders with a warning (CLAIM_OPEN is active again), the five finished statuses with a
        // warning, and active orders without one (the ASSIGNED scenario order and an IN_TRANSIT copy).
        var expectedA = new HashSet<Guid>
        {
            await AddOrderAsync(a, "DRAFT", PriceReview.BelowMinimum),
            await AddOrderAsync(a, "READY_FOR_PICKUP", PriceReview.Override),
            await AddOrderAsync(a, "CLAIM_OPEN", PriceReview.Override),
        };
        foreach (var finished in OperationsQueuePolicy.FinishedStatuses)
        {
            await AddOrderAsync(a, finished, PriceReview.Override);
        }

        await AddOrderAsync(a, "DELIVERED", PriceReview.BelowMinimum);
        await AddOrderAsync(a, "IN_TRANSIT", PriceReview.None);

        // C, another organization: active orders with a warning that A must never list or count.
        var expectedC = new HashSet<Guid>
        {
            await AddOrderAsync(c, "READY_FOR_PICKUP", PriceReview.Override),
            await AddOrderAsync(c, "DELIVERING", PriceReview.BelowMinimum),
        };
        await AddOrderAsync(c, "CLOSED", PriceReview.Override);

        // A page of two rows makes the three matches of A span two pages of the same filtered query.
        var dashboard = CreateDashboardReader(pageSize: 2);
        var counts = CreateCountsReader();

        var listA = await ReadAllAsync(dashboard, a, PriceReviewFilters());
        Assert.Equal(expectedA.Order(), listA.Select(item => item.OrderId).Order());
        Assert.All(listA, item =>
        {
            Assert.True(OperationsQueuePolicy.IsPriceReview(item.Status, item.CostWarning));
            Assert.Equal(a.OrganizationId, item.Owner.OrganizationId);
        });
        Assert.Equal(
            listA.Select(item => item.OrderId),
            listA.OrderByDescending(item => item.UpdatedAt).ThenByDescending(item => item.OrderId).Select(item => item.OrderId));
        Assert.Equal(listA.Count, (await counts.ReadAsync(CountsRequest(a), default)).PriceReview);

        var listC = await ReadAllAsync(dashboard, c, PriceReviewFilters());
        Assert.Equal(expectedC.Order(), listC.Select(item => item.OrderId).Order());
        Assert.Equal(listC.Count, (await counts.ReadAsync(CountsRequest(c), default)).PriceReview);
        Assert.Empty(listA.Select(item => item.OrderId).Intersect(listC.Select(item => item.OrderId)));

        // The filter combines with the others: a finished status never matches; false applies no filter.
        Assert.Empty(await ReadAllAsync(dashboard, a, PriceReviewFilters() with { Status = "DELIVERED" }));
        Assert.Equal(
            [expectedA.Single(id => listA.Single(item => item.OrderId == id).Status == "CLAIM_OPEN")],
            (await ReadAllAsync(dashboard, a, PriceReviewFilters() with { Status = "CLAIM_OPEN" })).Select(item => item.OrderId));
        var unfiltered = await ReadAllAsync(dashboard, a, PriceReviewFilters() with { PriceReview = false });
        Assert.Equal((await counts.ReadAsync(CountsRequest(a), default)).Total, unfiltered.Count);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Price_review_is_read_only_by_the_dashboard_roles_of_the_selected_organization()
    {
        await using var a = new SyntheticOrderScenario(fixture);
        await a.InitializeAsync(orderStatus: "READY_FOR_PICKUP");
        await using var b = new SyntheticOrderScenario(fixture);
        await b.InitializeAsync(orderStatus: "READY_FOR_PICKUP");
        await AddOrderAsync(a, "CONFIRMED", PriceReview.Override);
        var viewer = Guid.NewGuid();
        var finance = Guid.NewGuid();
        var dashboard = CreateDashboardReader(pageSize: 50);
        try
        {
            await a.ExecuteAdminAsync(
                """
                INSERT INTO identity.users(id,identity_subject) VALUES
                  (@viewer,'oidc|price-review|' || @viewer::text),
                  (@finance,'oidc|price-review|' || @finance::text);
                INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default) VALUES
                  (gen_random_uuid(),@viewer,@org,'VIEWER','ACTIVE',true),
                  (gen_random_uuid(),@finance,@org,'FINANCE','ACTIVE',true);
                """,
                P("viewer", viewer), P("finance", finance), P("org", a.OrganizationId));

            Assert.Single((await dashboard.ReadAsync(DashboardRequest(a.UserId, a.OrganizationId, PriceReviewFilters()), default)).Items);
            foreach (var (actor, organization) in new[]
                     {
                         (viewer, a.OrganizationId),
                         (finance, a.OrganizationId),
                         // A's dispatcher asking for B, where it holds no membership.
                         (a.UserId, b.OrganizationId),
                     })
            {
                await Assert.ThrowsAsync<OperationsDashboardForbiddenException>(() =>
                    dashboard.ReadAsync(DashboardRequest(actor, organization, PriceReviewFilters()), default));
            }
        }
        finally
        {
            await a.ExecuteAdminAsync(
                """
                DELETE FROM organizations.organization_memberships WHERE user_id = ANY(@users);
                DELETE FROM identity.users WHERE id = ANY(@users);
                """,
                P("users", new[] { viewer, finance }));
        }
    }

    private static OperationsDashboardFilters PriceReviewFilters() =>
        new(null, null, null, null, null, null, null, null, null, null, PriceReview: true, Cursor: null);

    private static OperationsDashboardRequest DashboardRequest(
        Guid actorId,
        Guid organizationId,
        OperationsDashboardFilters filters) =>
        new(actorId, organizationId, MfaSatisfied: false, filters);

    private static OperationsQueueCountsRequest CountsRequest(SyntheticOrderScenario scenario) =>
        new(scenario.UserId, scenario.OrganizationId, MfaSatisfied: false);

    /// <summary>Every page of one dashboard query, following the cursor; a repeated cursor fails the test.</summary>
    private static async Task<List<OperationsDashboardOrder>> ReadAllAsync(
        IOperationsDashboardReader reader,
        SyntheticOrderScenario scenario,
        OperationsDashboardFilters filters)
    {
        var items = new List<OperationsDashboardOrder>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        OperationsDashboardCursor? cursor = null;
        while (true)
        {
            var page = await reader.ReadAsync(
                DashboardRequest(scenario.UserId, scenario.OrganizationId, filters with { Cursor = cursor }),
                default);
            items.AddRange(page.Items);
            if (page.NextCursor is null)
            {
                return items;
            }

            Assert.True(cursors.Add(page.NextCursor), "The dashboard cursor repeated.");
            Assert.True(OperationsDashboardCursorCodec.TryDecode(page.NextCursor, out cursor));
        }
    }

    private IOperationsDashboardReader CreateDashboardReader(int pageSize) =>
        CreateServices(pageSize).GetRequiredService<IOperationsDashboardReader>();

    private IOperationsQueueCountsReader CreateCountsReader() =>
        CreateServices(pageSize: 50).GetRequiredService<IOperationsQueueCountsReader>();

    private IServiceProvider CreateServices(int pageSize)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OperationsDashboard:Provider"] = "PostgreSql",
                ["OperationsDashboard:CommandTimeoutSeconds"] = "10",
                ["OperationsDashboard:DefaultPageSize"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ConnectionStrings:Paqueteria"] = fixture.AppConnectionString,
            })
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddReportingInfrastructure(configuration)
            .BuildServiceProvider();
        return services.CreateScope().ServiceProvider;
    }

    private enum PriceReview
    {
        None,
        Override,
        BelowMinimum,
    }

    /// <summary>A synthetic copy of the scenario order (and its quote) with another id, status and price warning.</summary>
    private static async Task<Guid> AddOrderAsync(
        SyntheticOrderScenario scenario,
        string status,
        PriceReview priceReview)
    {
        var orderId = Guid.NewGuid();
        var quoteId = Guid.NewGuid();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO pricing.quotes
            SELECT (jsonb_populate_record(NULL::pricing.quotes, to_jsonb(q) || jsonb_build_object('id',@quote))).*
            FROM pricing.quotes q WHERE q.id=@source_quote;
            INSERT INTO orders.orders
            SELECT (jsonb_populate_record(NULL::orders.orders, to_jsonb(o) || jsonb_build_object(
              'id',@order,'quote_id',@quote,'public_id',@public_id,'status',@status,
              'financial_override',CASE WHEN @override THEN @override_value::jsonb END,
              'minimum_total_cents_snapshot',
                CASE WHEN @below THEN o.total_cents + 1 ELSE o.minimum_total_cents_snapshot END))).*
            FROM orders.orders o WHERE o.id=@source_order;
            """,
            P("quote", quoteId),
            P("source_quote", scenario.QuoteId),
            P("order", orderId),
            P("public_id", $"PRV-{orderId:N}"),
            P("status", status),
            P("override", priceReview != PriceReview.None),
            P("override_value", Override),
            P("below", priceReview == PriceReview.BelowMinimum),
            P("source_order", scenario.OrderId));
        return orderId;
    }

    private static NpgsqlParameter P(string name, object value) => new(name, value);
}
