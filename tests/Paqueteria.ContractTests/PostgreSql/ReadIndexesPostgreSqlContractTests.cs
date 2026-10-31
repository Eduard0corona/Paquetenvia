using System.Reflection;
using System.Text.RegularExpressions;
using Dispatch.Infrastructure.Persistence;
using Dispatch.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Orders.Infrastructure.Persistence;
using Orders.Infrastructure.Persistence.Migrations;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.ContractTests.Support;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// DP-INDEXES-ORDERS-DISPATCH-2026-10-20: the four read indexes AI-06 declares and the Orders and Dispatch lane steps
/// create for older installations. Each one serves the runtime read it was designed for, planned on real PostgreSQL as
/// <c>paqueteria_app</c> under FORCE RLS with the statement text read from the runtime source (never a copy), and both
/// lane steps roll back and forward as the migrator.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed partial class ReadIndexesPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly (string Name, string Statement, string Definition)[] AllIndexes =
        [.. AddOrderReadIndexes.Indexes, .. AddAssignmentReadIndexes.Indexes];

    [PostgreSqlContractFact]
    public void Both_lanes_carry_the_read_index_step_in_place()
    {
        var verified = ModuleMigrationCoordinator.VerifySources();
        Assert.Equal(
            AddAssignmentReadIndexes.MigrationId,
            Assert.Single(verified, state => state.Module == "Dispatch").MigrationId);
        // AI-08 NTF-004 follows the read-index step in Orders (its consent table); the read-index step keeps its own
        // source rule and is the step right before it.
        Assert.Equal(
            AddRecipientNoticeConsents.MigrationId,
            Assert.Single(verified, state => state.Module == "Orders").MigrationId);
        Assert.Equal(
            [AddOrderReadIndexes.MigrationId, AddRecipientNoticeConsents.MigrationId],
            typeof(AddOrderReadIndexes).Assembly.GetTypes()
                .Where(type => type.GetCustomAttribute<DbContextAttribute>()?.ContextType == typeof(OrdersDbContext))
                .Select(type => type.GetCustomAttribute<MigrationAttribute>()?.Id)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .TakeLast(2));
    }

    [PostgreSqlContractFact]
    public void Ai06_declares_each_index_exactly_as_its_lane_step_creates_it()
    {
        var schema = File.ReadAllText(Path.Combine(RepositoryPaths.Root, CanonicalBaselineContract.SchemaRelativePath));
        foreach (var (migration, indexes) in new[]
                 {
                     (AddOrderReadIndexes.UpSql, AddOrderReadIndexes.Indexes),
                     (AddAssignmentReadIndexes.UpSql, AddAssignmentReadIndexes.Indexes),
                 })
        {
            foreach (var index in indexes)
            {
                Assert.StartsWith("CREATE INDEX ", index.Statement, StringComparison.Ordinal);
                Assert.Equal(1, Occurrences(schema, index.Statement + "\n"));
                Assert.Equal(
                    1,
                    Occurrences(migration, "CREATE INDEX IF NOT EXISTS " + index.Statement["CREATE INDEX ".Length..]));
                // The adoption check and the verification compare against the same rendered definition.
                Assert.Equal(2, Occurrences(migration, index.Definition.Replace("'", "''", StringComparison.Ordinal)));
            }
        }
    }

    [PostgreSqlContractFact]
    public async Task The_shared_fixture_carries_every_read_index_as_ai06_declares_it()
    {
        await using var connection = new NpgsqlConnection(fixture.DeploymentConnectionString);
        await connection.OpenAsync();
        Assert.Equal(AllIndexes.Select(index => index.Definition).Order(StringComparer.Ordinal), await DefinitionsAsync(connection));
    }

    [PostgreSqlContractFact]
    public async Task The_read_indexes_serve_the_runtime_reads_and_roll_back_and_forward_as_the_migrator()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("readindexes");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            Assert.Equal(
                DatabaseBaselineApplyStatus.Applied,
                (await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString)).Status);
            var coordinator = new ModuleMigrationCoordinator();
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            Assert.Equal(AllIndexes.Select(index => index.Definition).Order(StringComparer.Ordinal), await DefinitionsAsync(connection));
            var seed = await SeedAsync(connection);

            // After: every read takes its index, and none of them walks the driver's or the tenant's history.
            var after = await PlansAsync(connection, seed);
            AssertUses(after.CompletedToday, "order_events_actor_status_time_idx", "assignments_order_driver_idx");
            AssertUses(after.AllyCompletedToday, "order_events_actor_status_time_idx", "assignments_order_driver_idx");
            AssertUses(after.Stops, "assignments_driver_open_idx");
            AssertUses(after.Fleet, "assignments_driver_open_idx");
            AssertUses(after.Picker, "assignments_driver_open_idx");
            AssertUses(after.PriceReview, "orders_price_review_idx");
            foreach (var plan in new[] { after.CompletedToday, after.AllyCompletedToday, after.Stops, after.Fleet, after.Picker })
            {
                Assert.DoesNotMatch(HistoryIndex(), plan);
            }

            Assert.DoesNotContain("orders_updated_at_idx", after.PriceReview, StringComparison.Ordinal);

            // Down, as the migrator, to the step before the read indexes (in Orders that also rolls back the later NTF-004
            // consent step, which holds no consent here): the indexes go, every other object stays, both lanes are pending
            // and the canonical baseline still holds.
            await MigrateAsMigratorAsync(connection, CreateOrdersContext(connection), AddOrderAutoCloseDiscovery.MigrationId);
            await MigrateAsMigratorAsync(connection, CreateDispatchContext(connection), AddOutboxTraceContext.MigrationId);
            Assert.Empty(await DefinitionsAsync(connection));
            var pending = await coordinator.PlanAsync(connectionString, CancellationToken.None);
            Assert.Equal("PENDING", pending.Single(state => state.Module == "Orders").Status);
            Assert.Equal("PENDING", pending.Single(state => state.Module == "Dispatch").Status);
            await new DatabaseBaselineAssertions().AssertAsync(connection);

            // Before: the same reads fall back to the driver's whole assignment history.
            var before = await PlansAsync(connection, seed);
            foreach (var plan in new[] { before.CompletedToday, before.Stops, before.Fleet, before.Picker, before.PriceReview })
            {
                Assert.DoesNotMatch(ReadIndex(), plan);
            }

            Assert.Matches(HistoryIndex(), before.Fleet);
            Assert.Matches(HistoryIndex(), before.Picker);

            // Up again through the canonical migrator, as deployment runs it.
            await coordinator.ApplyAsync(connectionString, CancellationToken.None);
            Assert.All(
                await coordinator.AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            Assert.Equal(AllIndexes.Select(index => index.Definition).Order(StringComparer.Ordinal), await DefinitionsAsync(connection));
            var again = await PlansAsync(connection, seed);
            AssertUses(again.CompletedToday, "order_events_actor_status_time_idx", "assignments_order_driver_idx");
            AssertUses(again.Fleet, "assignments_driver_open_idx");
            AssertUses(again.PriceReview, "orders_price_review_idx");

            // The Up SQL adopts what it finds and is idempotent; an index of the same name and another shape is
            // refused, never altered.
            await using (var reapply = await connection.BeginTransactionAsync())
            {
                await ExecuteAsync(connection, reapply, "SET LOCAL ROLE paqueteria_migrator");
                await ExecuteAsync(connection, reapply, AddOrderReadIndexes.UpSql);
                await ExecuteAsync(connection, reapply, AddAssignmentReadIndexes.UpSql);
                await reapply.RollbackAsync();
            }

            foreach (var (drop, create, up, message) in new[]
                     {
                         ("DROP INDEX orders.orders_price_review_idx",
                          "CREATE INDEX orders_price_review_idx ON orders.orders(updated_at)",
                          AddOrderReadIndexes.UpSql,
                          "found an orders index of another shape"),
                         ("DROP INDEX dispatch.assignments_driver_open_idx",
                          "CREATE INDEX assignments_driver_open_idx ON dispatch.assignments(driver_id)",
                          AddAssignmentReadIndexes.UpSql,
                          "found a dispatch index of another shape"),
                     })
            {
                await using var widened = await connection.BeginTransactionAsync();
                await ExecuteAsync(connection, widened, "SET LOCAL ROLE paqueteria_migrator");
                await ExecuteAsync(connection, widened, drop);
                await ExecuteAsync(connection, widened, create);
                var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, widened, up));
                Assert.Contains(message, refused.MessageText, StringComparison.Ordinal);
                await widened.RollbackAsync();
            }

            Assert.Equal(AllIndexes.Select(index => index.Definition).Order(StringComparer.Ordinal), await DefinitionsAsync(connection));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    private static void AssertUses(string plan, params string[] indexes)
    {
        foreach (var index in indexes)
        {
            Assert.True(
                Regex.IsMatch(plan, $@"\b{Regex.Escape(index)}\b"),
                $"Expected the plan to use {index}:{Environment.NewLine}{plan}");
        }
    }

    [GeneratedRegex(@"\bassignments_driver_idx\b")]
    private static partial Regex HistoryIndex();

    [GeneratedRegex(@"\b(order_events_actor_status_time_idx|assignments_order_driver_idx|assignments_driver_open_idx|orders_price_review_idx)\b")]
    private static partial Regex ReadIndex();

    private static async Task<string[]> DefinitionsAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_get_indexdef(i.indexrelid)
            FROM unnest(@names) AS listed(name)
            JOIN pg_index i ON i.indexrelid = to_regclass(listed.name)
            WHERE i.indisvalid AND i.indisready
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter<string[]>("names", AllIndexes.Select(index => index.Name).ToArray()));
        var definitions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            definitions.Add(reader.GetString(0));
        }

        return [.. definitions.Order(StringComparer.Ordinal)];
    }

    private sealed record Seed(
        Guid Organization,
        Guid Ally,
        Guid Dispatcher,
        Guid DriverUser,
        Guid Driver,
        Guid AllyDriverUser,
        Guid AllyDriver);

    private sealed record Plans(
        string CompletedToday,
        string AllyCompletedToday,
        string Stops,
        string Fleet,
        string Picker,
        string PriceReview);

    /// <summary>
    /// Synthetic history only (no real person, address or phone): four organizations (one ALLY operating 15 % of the
    /// orders), ten drivers each, 8 000 orders over a year of which 95 % are finished, one assignment per order, the
    /// usual status changes per order and a few unfinished orders with an authorized financial override. Foreign keys
    /// are not checked while seeding (the rows point at no location or quote); RLS and the indexes are untouched.
    /// </summary>
    private static async Task<Seed> SeedAsync(NpgsqlConnection connection)
    {
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, transaction, SeedSql);
            await transaction.CommitAsync();
        }

        await ExecuteAsync(
            connection,
            null,
            "ANALYZE orders.orders; ANALYZE orders.order_events; ANALYZE dispatch.assignments; ANALYZE drivers.driver_profiles;");
        await using var ids = new NpgsqlCommand(
            """
            SELECT (SELECT id FROM organizations.organizations WHERE display_name='Plan org 1'),
                   (SELECT id FROM organizations.organizations WHERE display_name='Plan org 4'),
                   (SELECT id FROM identity.users WHERE identity_subject='plan-dispatcher-1'),
                   (SELECT u.id FROM identity.users u WHERE u.identity_subject='plan-driver-1-1'),
                   (SELECT p.id FROM drivers.driver_profiles p JOIN identity.users u ON u.id=p.user_id WHERE u.identity_subject='plan-driver-1-1'),
                   (SELECT u.id FROM identity.users u WHERE u.identity_subject='plan-driver-4-1'),
                   (SELECT p.id FROM drivers.driver_profiles p JOIN identity.users u ON u.id=p.user_id WHERE u.identity_subject='plan-driver-4-1')
            """,
            connection);
        await using var reader = await ids.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new Seed(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4),
            reader.GetGuid(5), reader.GetGuid(6));
    }

    private const string SeedSql =
        """
        SET LOCAL session_replication_role = replica;
        SELECT setseed(0.2026);
        CREATE TEMP TABLE plan_orgs ON COMMIT DROP AS SELECT n, gen_random_uuid() AS id FROM generate_series(1,4) n;
        INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
        SELECT id,'Plan org '||n,'Plan org '||n,CASE WHEN n=4 THEN 'ALLY' ELSE 'BUSINESS' END FROM plan_orgs;
        CREATE TEMP TABLE plan_city ON COMMIT DROP AS SELECT gen_random_uuid() AS id;
        INSERT INTO locations.cities(id,state_code,name,timezone)
        SELECT id,'SIN','Plan city','America/Mazatlan' FROM plan_city;
        CREATE TEMP TABLE plan_drivers ON COMMIT DROP AS
        SELECT o.n AS org_n,o.id AS org_id,d AS dn,gen_random_uuid() AS driver_id,gen_random_uuid() AS user_id
        FROM plan_orgs o, generate_series(1,10) d;
        CREATE TEMP TABLE plan_dispatchers ON COMMIT DROP AS SELECT n AS org_n,gen_random_uuid() AS user_id FROM plan_orgs;
        INSERT INTO identity.users(id,identity_subject) SELECT user_id,'plan-driver-'||org_n||'-'||dn FROM plan_drivers;
        INSERT INTO identity.users(id,identity_subject) SELECT user_id,'plan-dispatcher-'||org_n FROM plan_dispatchers;
        INSERT INTO drivers.driver_profiles(id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
        SELECT driver_id,user_id,org_id,(SELECT id FROM plan_city),'OWN','MOTORCYCLE','ACTIVE' FROM plan_drivers;
        CREATE TEMP TABLE plan_orders ON COMMIT DROP AS
        SELECT n, gen_random_uuid() AS id,
               CASE WHEN random() < 0.6 THEN 1 ELSE 2 + floor(random()*2)::int END AS owner_n,
               CASE WHEN random() < 0.15 THEN 4 END AS operator_n,
               CASE WHEN random() < 0.95
                    THEN (ARRAY['CLOSED','CLOSED','CLOSED','DELIVERED','CANCELLED','RETURNED'])[1+floor(random()*6)::int]
                    ELSE (ARRAY['ASSIGNED','PICKED_UP','IN_TRANSIT','DELIVERING','RESCHEDULED'])[1+floor(random()*5)::int]
               END AS status,
               now() - random() * interval '365 days' AS created_at,
               1 + floor(random()*10)::int AS dn
        FROM generate_series(1,8000) n;
        UPDATE plan_orders SET created_at = now() - random() * interval '2 days'
        WHERE status NOT IN ('DELIVERED','RETURNED','CLAIM_RESOLVED','CLOSED','CANCELLED');
        UPDATE plan_orders
        SET created_at = date_trunc('day', now() AT TIME ZONE 'America/Mazatlan') AT TIME ZONE 'America/Mazatlan' + interval '1 hour',
            status = 'CLOSED', dn = 1
        WHERE n % 97 = 0;
        INSERT INTO orders.orders(id,public_id,quote_id,owner_org_id,operator_org_id,city_id,origin_location_id,
          destination_location_id,service_type,pricing_tier,payer_type,status,subtotal_cents,discount_cents,tax_cents,
          total_cents,minimum_total_cents_snapshot,pricing_policy_version,package_snapshot,financial_override,created_at,
          updated_at)
        SELECT o.id,'ORD_PLAN'||o.n,gen_random_uuid(),owner.id,operator.id,(SELECT id FROM plan_city),gen_random_uuid(),
          gen_random_uuid(),'SAME_DAY','OCCASIONAL','SENDER',o.status,
          CASE WHEN o.review THEN 5000 ELSE 9000 END,0,0,CASE WHEN o.review THEN 5000 ELSE 9000 END,8000,'v1','{}'::jsonb,
          CASE WHEN o.review THEN jsonb_build_object('actor_id',gen_random_uuid(),'reason','synthetic','valid_until',now() + interval '1 day') END,
          o.created_at,o.created_at + interval '3 hours'
        FROM (SELECT *, status NOT IN ('DELIVERED','RETURNED','CLAIM_RESOLVED','CLOSED','CANCELLED') AND n % 40 = 0 AS review
              FROM plan_orders) o
        JOIN plan_orgs owner ON owner.n=o.owner_n
        LEFT JOIN plan_orgs operator ON operator.n=o.operator_n;
        INSERT INTO dispatch.assignments(id,order_id,owner_org_id,operator_org_id,driver_id,assignment_type,status,
          cost_cents,accepted_at,created_at)
        SELECT gen_random_uuid(),o.id,owner.id,operator.id,d.driver_id,'OWN',
          CASE WHEN o.status='ASSIGNED' THEN 'ACCEPTED'
               WHEN o.status IN ('PICKED_UP','IN_TRANSIT','DELIVERING','RESCHEDULED') THEN 'ACTIVE'
               WHEN o.status='CANCELLED' THEN 'CANCELLED' ELSE 'COMPLETED' END,
          1000,o.created_at + interval '10 minutes',o.created_at + interval '5 minutes'
        FROM plan_orders o
        JOIN plan_orgs owner ON owner.n=o.owner_n
        LEFT JOIN plan_orgs operator ON operator.n=o.operator_n
        JOIN plan_drivers d ON d.org_n=COALESCE(o.operator_n,o.owner_n) AND d.dn=o.dn;
        INSERT INTO orders.order_events(id,order_id,owner_org_id,operator_org_id,aggregate_version,event_type,
          public_event_code,payload,actor_id,occurred_at)
        SELECT gen_random_uuid(),o.id,owner.id,operator.id,v.version,v.event_type,v.code,v.payload,
          CASE v.actor WHEN 'dispatcher' THEN dispatcher.user_id WHEN 'driver' THEN d.user_id END,
          o.created_at + v.offset_minutes * interval '1 minute'
        FROM plan_orders o
        JOIN plan_orgs owner ON owner.n=o.owner_n
        LEFT JOIN plan_orgs operator ON operator.n=o.operator_n
        JOIN plan_drivers d ON d.org_n=COALESCE(o.operator_n,o.owner_n) AND d.dn=o.dn
        JOIN plan_dispatchers dispatcher ON dispatcher.org_n=o.owner_n
        CROSS JOIN LATERAL (VALUES
          (1,'ORDER_CREATED','ORDER_CREATED','{}'::jsonb,'dispatcher',0),
          (2,'ORDER_STATUS_CHANGED',NULL,'{"new_status":"ASSIGNED"}'::jsonb,'dispatcher',5),
          (3,'ORDER_STATUS_CHANGED','PICKED_UP','{"new_status":"PICKED_UP"}'::jsonb,'driver',30),
          (4,'ORDER_STATUS_CHANGED','IN_TRANSIT','{"new_status":"IN_TRANSIT"}'::jsonb,'driver',35),
          (5,'ORDER_STATUS_CHANGED','DELIVERED','{"new_status":"DELIVERED"}'::jsonb,'driver',90),
          (6,'ORDER_STATUS_CHANGED',NULL,'{"new_status":"CLOSED"}'::jsonb,'system',150)
        ) AS v(version,event_type,code,payload,actor,offset_minutes)
        WHERE (o.status <> 'CANCELLED' OR v.version = 1) AND (v.version < 6 OR o.status = 'CLOSED');
        """;

    /// <summary>
    /// The six runtime reads, each with the statement text of its runtime source and its parameters bound with the
    /// runtime types, planned (EXPLAIN, not ANALYZE) inside a tenant transaction as the runtime sets it: the context
    /// after BEGIN, then <c>SET LOCAL ROLE paqueteria_app</c>.
    /// </summary>
    private static async Task<Plans> PlansAsync(NpgsqlConnection connection, Seed seed)
    {
        var stopsSource = Source("src/Modules/Dispatch/Dispatch.Infrastructure/Stops/PostgreSqlDriverStopsQuery.cs");
        var completedToday = RawSql(stopsSource, "private static async Task<int> CountCompletedTodayAsync(");
        var stops = RawSql(stopsSource, "private static async Task<IReadOnlyList<StopRow>> ReadStopsAsync(");
        var fleet = RawSql(
            Source("src/Modules/Dispatch/Dispatch.Infrastructure/Fleet/PostgreSqlOrganizationDriversQuery.cs"),
            "private static async Task<IReadOnlyList<DriverRow>> ReadDriversAsync(");
        var picker = RawSql(
            Source("src/Modules/Dispatch/Dispatch.Infrastructure/Assignments/PostgreSqlAssignableDriversQuery.cs"),
            "private static async Task<IReadOnlyList<Candidate>> ReadCandidatesAsync(");
        var dashboard = RawSql(
            Source("src/Modules/Reporting/Reporting.Infrastructure/Operations/PostgreSqlOperationsDashboardReader.cs"),
            "private const string ProjectionSql =");
        var now = DateTimeOffset.UtcNow;
        string[] completing = ["PICKED_UP", "DELIVERED", "RETURNED"];
        string[] stopStatuses =
            ["ASSIGNED", "AT_PICKUP", "PICKED_UP", "IN_TRANSIT", "DELIVERING", "FAILED_ATTEMPT", "RESCHEDULED", "RETURNING"];

        NpgsqlParameter[] CompletedParameters(Guid actor, Guid driver, Guid organization) =>
        [
            P("actor_id", NpgsqlDbType.Uuid, actor),
            P("driver_id", NpgsqlDbType.Uuid, driver),
            P("organization_id", NpgsqlDbType.Uuid, organization),
            P("completing", NpgsqlDbType.Array | NpgsqlDbType.Text, completing),
            P("now", NpgsqlDbType.TimestampTz, now),
            P("zone", NpgsqlDbType.Text, "America/Mazatlan"),
        ];

        return new Plans(
            await PlanAsync(connection, seed.DriverUser, seed.Organization, completedToday,
                CompletedParameters(seed.DriverUser, seed.Driver, seed.Organization)),
            await PlanAsync(connection, seed.AllyDriverUser, seed.Ally, completedToday,
                CompletedParameters(seed.AllyDriverUser, seed.AllyDriver, seed.Ally)),
            await PlanAsync(connection, seed.DriverUser, seed.Organization, stops,
                P("driver_id", NpgsqlDbType.Uuid, seed.Driver),
                P("organization_id", NpgsqlDbType.Uuid, seed.Organization)),
            await PlanAsync(connection, seed.Dispatcher, seed.Organization, fleet,
                P("organization_id", NpgsqlDbType.Uuid, seed.Organization),
                P("stop_statuses", NpgsqlDbType.Array | NpgsqlDbType.Text, stopStatuses),
                P("after", NpgsqlDbType.Uuid, null),
                P("limit", NpgsqlDbType.Integer, 101)),
            await PlanAsync(connection, seed.Dispatcher, seed.Organization, picker,
                P("organization_id", NpgsqlDbType.Uuid, seed.Organization),
                P("service_area_id", NpgsqlDbType.Uuid, null),
                P("city_id", NpgsqlDbType.Uuid, Guid.NewGuid()),
                P("after", NpgsqlDbType.Uuid, null),
                P("limit", NpgsqlDbType.Integer, 101)),
            await PlanAsync(connection, seed.Dispatcher, seed.Organization, dashboard,
                P("order_id", NpgsqlDbType.Uuid, null),
                P("status", NpgsqlDbType.Text, null),
                P("delivery_zone_id", NpgsqlDbType.Uuid, null),
                P("client_account_id", NpgsqlDbType.Uuid, null),
                P("owner_org_id", NpgsqlDbType.Uuid, null),
                P("operator_org_id", NpgsqlDbType.Uuid, null),
                P("service_type", NpgsqlDbType.Text, null),
                P("created_from", NpgsqlDbType.TimestampTz, null),
                P("created_to", NpgsqlDbType.TimestampTz, null),
                P("unassigned", NpgsqlDbType.Boolean, null),
                P("price_review", NpgsqlDbType.Boolean, true),
                P("cursor_updated_at", NpgsqlDbType.TimestampTz, null),
                P("cursor_order_id", NpgsqlDbType.Uuid, null),
                P("page_limit", NpgsqlDbType.Integer, 51)));
    }

    private static async Task<string> PlanAsync(
        NpgsqlConnection connection,
        Guid actor,
        Guid organization,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            """
            SELECT set_config('app.current_user_id', @user_id::uuid::text, true);
            SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true);
            SET LOCAL ROLE paqueteria_app;
            """,
            connection,
            transaction))
        {
            context.Parameters.Add(P("user_id", NpgsqlDbType.Uuid, actor));
            context.Parameters.Add(P("organization_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, new[] { organization }));
            await context.ExecuteNonQueryAsync();
        }

        var lines = new List<string>();
        await using (var explain = new NpgsqlCommand("EXPLAIN (COSTS OFF) " + sql, connection, transaction))
        {
            explain.Parameters.AddRange(parameters);
            await using var reader = await explain.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add(reader.GetString(0));
            }
        }

        await transaction.RollbackAsync();
        return string.Join('\n', lines);
    }

    private static string Source(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryPaths.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The first raw string literal after <paramref name="anchor"/>: the statement the runtime sends.</summary>
    private static string RawSql(string source, string anchor)
    {
        var start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The runtime source lost its anchor {anchor}");
        var open = source.IndexOf("\"\"\"", start, StringComparison.Ordinal);
        var close = source.IndexOf("\"\"\"", open + 3, StringComparison.Ordinal);
        Assert.True(open > start && close > open, $"No raw SQL literal follows {anchor}");
        var sql = source[(open + 3)..close].Trim();
        Assert.True(
            sql.StartsWith("SELECT", StringComparison.Ordinal) || sql.StartsWith("WITH", StringComparison.Ordinal),
            $"The literal after {anchor} is not a query");
        return sql;
    }

    private static async Task MigrateAsMigratorAsync(NpgsqlConnection connection, DbContext context, string target)
    {
        await using (context)
        {
            await ExecuteAsync(connection, null, "SET ROLE paqueteria_migrator");
            await context.GetService<IMigrator>().MigrateAsync(target);
            await ExecuteAsync(connection, null, "RESET ROLE");
        }
    }

    private static OrdersDbContext CreateOrdersContext(NpgsqlConnection connection) =>
        new(new DbContextOptionsBuilder<OrdersDbContext>()
                .UseNpgsql(connection, postgres =>
                {
                    postgres.MigrationsAssembly(typeof(OrdersDbContext).Assembly.FullName);
                    postgres.MigrationsHistoryTable("__ef_migrations_history_orders", "platform");
                })
                .Options,
            new TenantDatabaseExecutionState());

    private static DispatchDbContext CreateDispatchContext(NpgsqlConnection connection) =>
        new(new DbContextOptionsBuilder<DispatchDbContext>()
                .UseNpgsql(connection, postgres =>
                {
                    postgres.MigrationsAssembly(typeof(DispatchDbContext).Assembly.FullName);
                    postgres.MigrationsHistoryTable("__ef_migrations_history_dispatch", "platform");
                })
                .Options,
            new TenantDatabaseExecutionState());

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type)
    {
        Value = value ?? DBNull.Value,
    };

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
