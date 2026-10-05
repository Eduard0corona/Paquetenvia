using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Reporting.Application.Operations;
using Reporting.Infrastructure;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// UI-PHASE2-QUEUE-COUNTS-2026-10-05 (getOperationsQueueCounts) on real PostgreSQL as the NOBYPASSRLS runtime role:
/// exact per-status and per-queue counts over the orders the selected organization reads as owner or operator,
/// never another organization's, all 17 statuses present with zeros, and the operations roles re-checked inside the
/// tenant transaction. The read never writes.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
public sealed class OperationsQueueCountsPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Override =
        """{"actor_id":"synthetic","reason":"synthetic","valid_until":"2026-12-01T00:00:00Z"}""";

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Counts_are_exact_for_owner_and_operator_and_never_include_other_organizations()
    {
        // Disposal runs in reverse: the third organization, then B (whose orders A operates), then A.
        await using var a = new SyntheticOrderScenario(fixture);
        await a.InitializeAsync(orderStatus: "READY_FOR_PICKUP");
        await using var b = new SyntheticOrderScenario(fixture);
        await b.InitializeAsync(orderStatus: "CLOSED");
        await using var c = new SyntheticOrderScenario(fixture);
        await c.InitializeAsync(orderStatus: "READY_FOR_PICKUP");

        // A owns: READY_FOR_PICKUP (scenario order, no assignment), READY_FOR_PICKUP with an ACCEPTED assignment,
        // RESCHEDULED without one, FAILED_ATTEMPT, IN_TRANSIT, DELIVERING, DELIVERED, a DRAFT below the minimum with
        // an authorized override, a CONFIRMED with an override, and a RESCHEDULED holding a COMPLETED assignment only.
        var assigned = await AddOrderAsync(a, "READY_FOR_PICKUP");
        await AddOrderAsync(a, "RESCHEDULED");
        await AddOrderAsync(a, "FAILED_ATTEMPT");
        await AddOrderAsync(a, "IN_TRANSIT");
        await AddOrderAsync(a, "DELIVERING");
        await AddOrderAsync(a, "DELIVERED");
        await AddOrderAsync(a, "DRAFT", priceReview: PriceReview.BelowMinimum);
        await AddOrderAsync(a, "CONFIRMED", priceReview: PriceReview.Override);
        var completedOnly = await AddOrderAsync(a, "RESCHEDULED");
        var driverId = await AddDriverAsync(a);
        await AddAssignmentAsync(a, assigned, driverId, "ACCEPTED");
        await AddAssignmentAsync(a, completedOnly, driverId, "COMPLETED");

        // B owns its CLOSED scenario order, a CLAIM_OPEN and a RETURNING with an override that A operates, and an
        // IN_TRANSIT order of its own; C owns a READY_FOR_PICKUP and a DELIVERING that B operates.
        await AddOrderAsync(b, "CLAIM_OPEN", operatorOrganizationId: a.OrganizationId);
        await AddOrderAsync(b, "RETURNING", operatorOrganizationId: a.OrganizationId, priceReview: PriceReview.Override);
        await AddOrderAsync(b, "IN_TRANSIT");
        await AddOrderAsync(c, "DELIVERING", operatorOrganizationId: b.OrganizationId);

        var reader = CreateReader();
        var forA = await reader.ReadAsync(Request(a), default);
        AssertStatuses(forA, new Dictionary<string, long>
        {
            ["DRAFT"] = 1,
            ["CONFIRMED"] = 1,
            ["READY_FOR_PICKUP"] = 2,
            ["IN_TRANSIT"] = 1,
            ["DELIVERING"] = 1,
            ["FAILED_ATTEMPT"] = 1,
            ["RESCHEDULED"] = 2,
            ["RETURNING"] = 1,
            ["DELIVERED"] = 1,
            ["CLAIM_OPEN"] = 1,
        });
        Assert.Equal(12, forA.Total);
        Assert.Equal(3, forA.Unassigned); // scenario order and both RESCHEDULED; the ACCEPTED one is assigned
        Assert.Equal(5, forA.NeedsAttention);
        Assert.Equal(3, forA.PriceReview);
        Assert.Equal(1, forA.DeliveredNotClosed);
        Assert.Equal(2, forA.EnRoute);
        Assert.Equal(TimeSpan.Zero, forA.GeneratedAt.Offset);

        var forB = await reader.ReadAsync(Request(b), default);
        AssertStatuses(forB, new Dictionary<string, long>
        {
            ["CLOSED"] = 1,
            ["CLAIM_OPEN"] = 1,
            ["RETURNING"] = 1,
            ["IN_TRANSIT"] = 1,
            ["DELIVERING"] = 1,
        });
        Assert.Equal(5, forB.Total);
        Assert.Equal(0, forB.Unassigned);
        Assert.Equal(2, forB.NeedsAttention);
        Assert.Equal(1, forB.PriceReview);
        Assert.Equal(0, forB.DeliveredNotClosed);
        Assert.Equal(2, forB.EnRoute);

        var forC = await reader.ReadAsync(Request(c), default);
        AssertStatuses(forC, new Dictionary<string, long> { ["READY_FOR_PICKUP"] = 1, ["DELIVERING"] = 1 });
        Assert.Equal(1, forC.Unassigned);

        // The aggregate equals a superuser count with the owner/operator predicate written out, for every
        // organization: RLS, not a filter of the query, scopes the counts.
        foreach (var scenario in new[] { a, b, c })
        {
            var counts = await reader.ReadAsync(Request(scenario), default);
            Assert.Equal(
                await AdminCountsAsync(scenario.OrganizationId),
                counts.ByStatus.Where(entry => entry.Value > 0).ToDictionary(entry => entry.Key, entry => entry.Value));
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task An_organization_without_orders_gets_all_seventeen_statuses_at_zero()
    {
        await using var empty = new SyntheticOrderScenario(fixture);
        await empty.InitializeAsync(createOrder: false);

        var counts = await CreateReader().ReadAsync(Request(empty), default);

        Assert.Equal(OperationsDashboardVocabulary.Statuses, counts.ByStatus.Select(entry => entry.Key));
        Assert.All(counts.ByStatus, entry => Assert.Equal(0, entry.Value));
        Assert.Equal(0, counts.Total);
        Assert.Equal(0, counts.Unassigned + counts.NeedsAttention + counts.PriceReview + counts.DeliveredNotClosed + counts.EnRoute);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Only_dispatchers_and_platform_admins_with_mfa_of_the_selected_organization_are_admitted()
    {
        await using var a = new SyntheticOrderScenario(fixture);
        await a.InitializeAsync(orderStatus: "DELIVERED");
        await using var b = new SyntheticOrderScenario(fixture);
        await b.InitializeAsync(orderStatus: "DELIVERED");
        var viewer = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var suspended = Guid.NewGuid();
        var finance = Guid.NewGuid();
        var reader = CreateReader();
        try
        {
            await a.ExecuteAdminAsync(
                """
                INSERT INTO identity.users(id,identity_subject) VALUES
                  (@viewer,'oidc|queue|' || @viewer::text),
                  (@admin,'oidc|queue|' || @admin::text),
                  (@suspended,'oidc|queue|' || @suspended::text),
                  (@finance,'oidc|queue|' || @finance::text);
                INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default) VALUES
                  (gen_random_uuid(),@viewer,@org,'VIEWER','ACTIVE',true),
                  (gen_random_uuid(),@admin,@org,'PLATFORM_ADMIN','ACTIVE',true),
                  (gen_random_uuid(),@suspended,@org,'DISPATCHER','SUSPENDED',true),
                  (gen_random_uuid(),@finance,@org,'FINANCE','ACTIVE',true);
                """,
                P("viewer", viewer), P("admin", admin), P("suspended", suspended), P("finance", finance),
                P("org", a.OrganizationId));

            Assert.Equal(1, (await reader.ReadAsync(Request(a), default)).DeliveredNotClosed);
            Assert.Equal(1, (await reader.ReadAsync(new(admin, a.OrganizationId, MfaSatisfied: true), default)).Total);

            foreach (var refused in new OperationsQueueCountsRequest[]
                     {
                         new(viewer, a.OrganizationId, MfaSatisfied: true),
                         new(admin, a.OrganizationId, MfaSatisfied: false),
                         new(suspended, a.OrganizationId, MfaSatisfied: true),
                         new(finance, a.OrganizationId, MfaSatisfied: true),
                         // A's dispatcher asking for B, where it holds no membership.
                         new(a.UserId, b.OrganizationId, MfaSatisfied: true),
                         new(Guid.NewGuid(), a.OrganizationId, MfaSatisfied: true),
                     })
            {
                await Assert.ThrowsAsync<OperationsDashboardForbiddenException>(() => reader.ReadAsync(refused, default));
            }
        }
        finally
        {
            await a.ExecuteAdminAsync(
                """
                DELETE FROM organizations.organization_memberships WHERE user_id = ANY(@users);
                DELETE FROM identity.users WHERE id = ANY(@users);
                """,
                P("users", new[] { viewer, admin, suspended, finance }));
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task The_counts_query_sees_nothing_without_the_tenant_context_of_the_runtime_role()
    {
        await using var a = new SyntheticOrderScenario(fixture);
        await a.InitializeAsync(orderStatus: "IN_TRANSIT");

        // The runtime login without app.current_org_ids: RLS hides every order (no BYPASSRLS, no fallback).
        await using var connection = await fixture.AppDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_app", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        // The reader's own aggregate (an internal constant, read by reflection so the test runs the shipped SQL).
        var countsSql = (string)typeof(OperationsDashboardOptions).Assembly
            .GetType("Reporting.Infrastructure.Operations.PostgreSqlOperationsQueueCountsReader", throwOnError: true)!
            .GetField("CountsSql", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()!;
        await using var command = new NpgsqlCommand(countsSql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }

    private IOperationsQueueCountsReader CreateReader()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OperationsDashboard:Provider"] = "PostgreSql",
                ["OperationsDashboard:CommandTimeoutSeconds"] = "10",
                ["ConnectionStrings:Paqueteria"] = fixture.AppConnectionString,
            })
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddReportingInfrastructure(configuration)
            .BuildServiceProvider();
        return services.CreateScope().ServiceProvider.GetRequiredService<IOperationsQueueCountsReader>();
    }

    private static OperationsQueueCountsRequest Request(SyntheticOrderScenario scenario) =>
        new(scenario.UserId, scenario.OrganizationId, MfaSatisfied: false);

    private static void AssertStatuses(OperationsQueueCounts counts, IReadOnlyDictionary<string, long> nonZero)
    {
        Assert.Equal(OperationsDashboardVocabulary.Statuses, counts.ByStatus.Select(entry => entry.Key));
        foreach (var (status, count) in counts.ByStatus)
        {
            Assert.True(
                (nonZero.TryGetValue(status, out var expected) ? expected : 0) == count,
                $"{status}: expected {(nonZero.TryGetValue(status, out var value) ? value : 0)}, read {count}.");
        }

        Assert.Equal(nonZero.Values.Sum(), counts.Total);
    }

    private async Task<Dictionary<string, long>> AdminCountsAsync(Guid organizationId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT status, count(*)::bigint
            FROM orders.orders
            WHERE owner_org_id = @org OR operator_org_id = @org
            GROUP BY status
            """);
        command.Parameters.Add(P("org", organizationId));
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0), reader.GetInt64(1));
        }

        return result;
    }

    private enum PriceReview
    {
        None,
        Override,
        BelowMinimum,
    }

    /// <summary>A synthetic copy of the scenario order (and its quote) with another id, status and operator.</summary>
    private static async Task<Guid> AddOrderAsync(
        SyntheticOrderScenario scenario,
        string status,
        Guid? operatorOrganizationId = null,
        PriceReview priceReview = PriceReview.None)
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
              'operator_org_id',@operator::uuid,
              'financial_override',CASE WHEN @override THEN @override_value::jsonb END,
              'minimum_total_cents_snapshot',
                CASE WHEN @below THEN o.total_cents + 1 ELSE o.minimum_total_cents_snapshot END))).*
            FROM orders.orders o WHERE o.id=@source_order;
            """,
            P("quote", quoteId),
            P("source_quote", scenario.QuoteId),
            P("order", orderId),
            P("public_id", $"QCNT-{orderId:N}"),
            P("status", status),
            new NpgsqlParameter("operator", NpgsqlTypes.NpgsqlDbType.Uuid)
            {
                Value = operatorOrganizationId is { } id ? id : DBNull.Value,
            },
            P("override", priceReview != PriceReview.None),
            P("override_value", Override),
            P("below", priceReview == PriceReview.BelowMinimum),
            P("source_order", scenario.OrderId));
        return orderId;
    }

    private static async Task<Guid> AddDriverAsync(SyntheticOrderScenario scenario)
    {
        var driverId = Guid.NewGuid();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO drivers.driver_profiles(id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
            VALUES (@driver,@user,@org,@city,'OWN','MOTORCYCLE','ACTIVE');
            """,
            P("driver", driverId),
            P("user", scenario.UserId),
            P("org", scenario.OrganizationId),
            P("city", scenario.CityId));
        return driverId;
    }

    private static Task AddAssignmentAsync(
        SyntheticOrderScenario scenario,
        Guid orderId,
        Guid driverId,
        string status) =>
        scenario.ExecuteAdminAsync(
            """
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,operator_org_id,driver_id,route_id,assignment_type,status,cost_cents,accepted_at)
            VALUES (gen_random_uuid(),@order,@org,NULL,@driver,NULL,'OWN',@status,0,clock_timestamp());
            """,
            P("order", orderId),
            P("org", scenario.OrganizationId),
            P("driver", driverId),
            P("status", status));

    private static NpgsqlParameter P(string name, object value) => new(name, value);
}
