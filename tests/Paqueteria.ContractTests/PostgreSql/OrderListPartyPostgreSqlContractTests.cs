using System.Globalization;
using System.Text.RegularExpressions;
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
using Paqueteria.ContractTests.Support;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// DP-INDEXES-ORDERS-DISPATCH-2026-10-20 (I1, project owner: "Reescribir la consulta (Recommended)"): the order list
/// without an owner, public_id or COD-pending filter is read in two parts (the orders an organization owns, and the ones
/// it only operates) and must list exactly what the statement it replaces lists. Both statements are read from the
/// runtime source (never a copy) and compared on real PostgreSQL as <c>paqueteria_app</c> under FORCE RLS: every filter
/// combination, every page, ties on <c>created_at</c> across both parts and page boundaries inside a tie; then the
/// runtime list itself against the previous statement; then each part's plan on a larger synthetic history.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed partial class OrderListPartyPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string CoordinatorPath = "src/Modules/Orders/Orders.Infrastructure/Orders/QuoteSnapshotToOrderCoordinator.cs";
    private const string CurrentOrgIdsArray =
        "COALESCE(NULLIF(current_setting('app.current_org_ids', true),'')::uuid[], ARRAY[]::uuid[])";
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowFrom = Base.AddMinutes(10);
    private static readonly DateTimeOffset WindowTo = Base.AddMinutes(40);

    [PostgreSqlContractFact]
    public void The_party_statement_reads_the_allowed_organizations_as_the_rls_function_does()
    {
        var statements = Statements.Read();
        var schema = File.ReadAllText(Path.Combine(RepositoryPaths.Root, CanonicalBaselineContract.SchemaRelativePath));
        var function = schema[schema.IndexOf("CREATE OR REPLACE FUNCTION security.app_allowed_org(", StringComparison.Ordinal)..];
        function = function[..function.IndexOf("$$;", StringComparison.Ordinal)];
        Assert.Contains("p_org = ANY(" + CurrentOrgIdsArray + ")", function, StringComparison.Ordinal);
        Assert.Contains("unnest(" + CurrentOrgIdsArray + ")", statements.Party, StringComparison.Ordinal);
        Assert.Contains("WHERE security.app_allowed_org(listed.org_id)", statements.Party, StringComparison.Ordinal);
        Assert.Contains("AND NOT security.app_allowed_org(owner_org_id)", statements.Party, StringComparison.Ordinal);
        // The party statement has no owner, public_id or COD term (those lists keep the filtered statement), and the
        // planner setting is transaction-local.
        foreach (var absent in new[] { "@owner", "@public_id", "@cod_pending", "cod_transactions" })
        {
            Assert.DoesNotContain(absent, statements.Party, StringComparison.Ordinal);
        }

        Assert.Equal("SET LOCAL enable_bitmapscan = off", statements.Planner);
    }

    [PostgreSqlContractFact]
    public async Task The_order_list_by_party_lists_every_page_the_filtered_statement_lists_and_each_part_walks_its_index()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("orderlistparty");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            Assert.Equal(
                DatabaseBaselineApplyStatus.Applied,
                (await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString)).Status);
            await new ModuleMigrationCoordinator().ApplyAsync(connectionString, CancellationToken.None);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            var seed = await SeedAsync(connection);
            var statements = Statements.Read();

            // (1) Both statements, every context shape (one organization as the runtime sets it, two or three at once,
            // none), every status and creation-window combination, page sizes 3 and 50, every page.
            Guid[][] contexts =
            [
                [seed.A.Id], [seed.B.Id], [seed.C.Id], [seed.D.Id], [seed.A.Id, seed.C.Id],
                [seed.A.Id, seed.B.Id, seed.C.Id], [],
            ];
            var tieBoundaries = 0;
            var walks = 0;
            foreach (var context in contexts)
            {
                foreach (var status in new[] { null, "DELIVERED", "CLOSED", "CLAIM_OPEN" })
                {
                    foreach (var (from, to) in Windows())
                    {
                        foreach (var take in new[] { 4, 51 })
                        {
                            var filter = new Filter(status, null, null, false, from, to);
                            var expected = await WalkAsync(connection, seed.A.Actor, context, statements.Filtered, null, true, filter, take);
                            var actual = await WalkAsync(connection, seed.A.Actor, context, statements.Party, statements.Planner, false, filter, take);
                            Assert.Equal(expected.Pages, actual.Pages);
                            tieBoundaries += actual.TieBoundaries;
                            walks++;
                        }
                    }
                }
            }

            Assert.Equal(7 * 4 * 4 * 2, walks);
            Assert.True(tieBoundaries > 0, "No page boundary fell inside a created_at tie.");

            // The data covers what the comparison claims: for A, orders it owns alone, orders it owns and another
            // organization operates, orders it owns and operates, and orders it only operates; with A and C at once
            // the orders both of them see are listed once.
            var aDefault = await WalkAsync(connection, seed.A.Actor, [seed.A.Id], statements.Party, statements.Planner, false, Filter.None, 1000);
            Assert.Equal(await VisibleCountAsync(connection, [seed.A.Id]), aDefault.Rows);
            Assert.Equal(100, aDefault.Rows);
            var both = await WalkAsync(connection, seed.A.Actor, [seed.A.Id, seed.C.Id], statements.Party, statements.Planner, false, Filter.None, 1000);
            Assert.Equal(await VisibleCountAsync(connection, [seed.A.Id, seed.C.Id]), both.Rows);
            Assert.Equal(both.Rows, both.Ids.Distinct().Count());
            Assert.Equal(120, both.Rows);
            Assert.Equal(0, (await WalkAsync(connection, seed.A.Actor, [], statements.Party, statements.Planner, false, Filter.None, 51)).Rows);

            // (2) The runtime list (QuoteSnapshotToOrderCoordinator, as the API calls it, app login, page size 3)
            // against the filtered statement for every filter combination it accepts, following its own cursors.
            await CompareRuntimeAsync(connectionString, connection, seed, statements);

            // (3) Plans on a larger synthetic history: each part walks its own index in list order, also from a cursor;
            // the filtered statement sorts the party's orders instead. And the first pages still match.
            foreach (var party in new[] { seed.P, seed.Q, seed.R })
            {
                foreach (var filter in new[]
                         {
                             Filter.None, Filter.None with { Status = "CLOSED" }, Filter.None with { Status = "IN_TRANSIT" },
                             Filter.None with { From = seed.Now.AddDays(-30), To = seed.Now },
                         })
                {
                    var expected = await WalkAsync(connection, party.Actor, [party.Id], statements.Filtered, null, true, filter, 51, maxPages: 3);
                    var actual = await WalkAsync(connection, party.Actor, [party.Id], statements.Party, statements.Planner, false, filter, 51, maxPages: 3);
                    Assert.Equal(expected.Pages, actual.Pages);
                }
            }

            foreach (var party in new[] { seed.P, seed.Q })
            {
                var firstPage = await WalkAsync(connection, party.Actor, [party.Id], statements.Party, statements.Planner, false, Filter.None, 51, maxPages: 1);
                Assert.NotNull(firstPage.LastCursor);
                foreach (var cursor in new[] { null, firstPage.LastCursor })
                {
                    var plan = await PlanAsync(connection, party, statements.Party, statements.Planner, false, Filter.None, cursor);
                    Assert.Matches(@"Index Scan using orders_owner_created_idx on orders\b", plan);
                    Assert.Matches(@"Index Scan using orders_operator_created_idx on orders\b", plan);
                    Assert.DoesNotContain("Bitmap", plan, StringComparison.Ordinal);
                    Assert.DoesNotContain("Seq Scan on orders", plan, StringComparison.Ordinal);
                    if (cursor is not null)
                    {
                        Assert.Matches(@"Index Cond: \(\(owner_org_id = listed\.org_id\) AND \(created_at <= ", plan);
                        Assert.Matches(@"Index Cond: \(\(operator_org_id = listed\.org_id\) AND \(created_at <= ", plan);
                    }
                }

                var filtered = await PlanAsync(connection, party, statements.Filtered, null, true, Filter.None, null);
                Assert.Contains("Sort Key: created_at DESC, id DESC", filtered, StringComparison.Ordinal);
                Assert.DoesNotContain("Index Scan using orders_owner_created_idx", filtered, StringComparison.Ordinal);
                Assert.DoesNotContain("Index Scan using orders_operator_created_idx", filtered, StringComparison.Ordinal);
            }
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    private async Task CompareRuntimeAsync(
        string connectionString,
        NpgsqlConnection connection,
        Seeded seed,
        Statements statements)
    {
        var appConnectionString = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(connectionString).Database,
            Pooling = false,
        }.ConnectionString;
        await using var dataSource = NpgsqlDataSource.Create(appConnectionString);
        var state = new TenantDatabaseExecutionState();
        await using var context = new OrdersDbContext(
            new DbContextOptionsBuilder<OrdersDbContext>()
                .UseNpgsql(dataSource)
                .AddInterceptors(new TenantTransactionGuardInterceptor(state), new TenantSaveChangesGuardInterceptor(state))
                .Options,
            state);
        IOrderService service = new QuoteSnapshotToOrderCoordinator(
            new TenantTransactionContext<OrdersDbContext>(context, state),
            new CryptographicOrderPublicIdGenerator(),
            new NoOpOrderCreationFailureInjector(),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            Options.Create(new OrdersOptions
            {
                Provider = OrdersProviderKind.PostgreSql,
                CommandTimeoutSeconds = 30,
                PageSize = 3,
                IdempotencyLifetimeMinutes = 60,
                PublicIdCollisionRetryCount = 2,
            }),
            new SystemClock());

        var byParty = 0;
        var filtered = 0;
        foreach (var party in new[] { seed.A, seed.B, seed.C })
        {
            foreach (var status in new[] { null, "DELIVERED", "CLAIM_OPEN" })
            {
                foreach (var owner in new Guid?[] { null, party.Id })
                {
                    foreach (var publicId in new[] { null, seed.PublicIds[0], seed.PublicIds[2], seed.PublicIds[5], seed.PublicIds[7] })
                    {
                        foreach (var codPending in new[] { false, true })
                        {
                            foreach (var (from, to) in new (DateTimeOffset? From, DateTimeOffset? To)[] { (null, null), (WindowFrom, WindowTo) })
                            {
                                var filter = new Filter(status, owner, publicId, codPending, from, to);
                                var expected = await WalkAsync(connection, party.Actor, [party.Id], statements.Filtered, null, true, filter, 4, shortRows: true);
                                var actual = await WalkServiceAsync(service, party, filter);
                                Assert.Equal(expected.Pages, actual);
                                if (owner is null && !codPending && publicId is null)
                                {
                                    byParty++;
                                }
                                else
                                {
                                    filtered++;
                                }
                            }
                        }
                    }
                }
            }
        }

        Assert.Equal(3 * 3 * 2, byParty);
        Assert.Equal(3 * 3 * 2 * 5 * 2 * 2 - byParty, filtered);
    }

    private static IEnumerable<(DateTimeOffset? From, DateTimeOffset? To)> Windows() =>
        [(null, null), (WindowFrom, null), (null, WindowTo), (WindowFrom, WindowTo)];

    private static async Task<List<string>> WalkServiceAsync(IOrderService service, Party party, Filter filter)
    {
        var pages = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 1000; page++)
        {
            var result = await service.ListAsync(
                party.Actor,
                party.Id,
                filter.Status,
                filter.Owner,
                cursor,
                filter.CodPending,
                false,
                filter.PublicId,
                new OrderListCreatedRange(filter.From, filter.To),
                CancellationToken.None);
            var more = result.NextCursor is not null;
            pages.Add(PageText(page, more, result.Items.Select(order =>
                ShortRow(order.Id, order.PublicId, order.Status, order.CreatedAt))));
            if (!more)
            {
                return pages;
            }

            cursor = result.NextCursor;
        }

        throw new InvalidOperationException("The runtime list did not end.");
    }

    /// <summary>
    /// Pages through one statement as the runtime does: <c>take</c> rows asked, <c>take - 1</c> shown, more when the
    /// extra row came back, the next page after the last shown row. The tenant context is set after BEGIN, then
    /// <c>SET LOCAL ROLE paqueteria_app</c>, then (party statement only) its planner setting.
    /// </summary>
    private static async Task<Walk> WalkAsync(
        NpgsqlConnection connection,
        Guid actor,
        Guid[] organizations,
        string sql,
        string? planner,
        bool filteredStatement,
        Filter filter,
        int take,
        int maxPages = 1000,
        bool shortRows = false)
    {
        var pages = new List<string>();
        var ids = new List<Guid>();
        var tieBoundaries = 0;
        (DateTimeOffset At, Guid Id)? cursor = null;
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContextAsync(connection, transaction, actor, organizations, planner);
        for (var page = 0; page < maxPages; page++)
        {
            var rows = new List<(string Text, DateTimeOffset At, Guid Id)>();
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddRange(Parameters(filteredStatement, filter, cursor, take));
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var id = reader.GetGuid(0);
                    var at = reader.GetFieldValue<DateTimeOffset>(19);
                    rows.Add((shortRows ? ShortRow(id, reader.GetString(1), reader.GetString(4), at) : FullRow(reader), at, id));
                }
            }

            var more = rows.Count == take;
            var shown = more ? rows.Take(rows.Count - 1).ToList() : rows;
            if (cursor is { } previous && shown.Count > 0 && shown[0].At == previous.At)
            {
                tieBoundaries++;
            }

            ids.AddRange(shown.Select(row => row.Id));
            pages.Add(PageText(page, more, shown.Select(row => row.Text)));
            if (!more || shown.Count == 0)
            {
                break;
            }

            cursor = (shown[^1].At, shown[^1].Id);
        }

        await transaction.RollbackAsync();
        return new Walk(pages, ids, tieBoundaries, cursor);
    }

    private static async Task<string> PlanAsync(
        NpgsqlConnection connection,
        Party party,
        string sql,
        string? planner,
        bool filteredStatement,
        Filter filter,
        (DateTimeOffset At, Guid Id)? cursor)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContextAsync(connection, transaction, party.Actor, [party.Id], planner);
        var lines = new List<string>();
        await using (var explain = new NpgsqlCommand("EXPLAIN (COSTS OFF) " + sql, connection, transaction))
        {
            explain.Parameters.AddRange(Parameters(filteredStatement, filter, cursor, 51));
            await using var reader = await explain.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add(reader.GetString(0));
            }
        }

        await transaction.RollbackAsync();
        return string.Join('\n', lines);
    }

    private static async Task SetContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actor,
        Guid[] organizations,
        string? planner)
    {
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
            context.Parameters.Add(P("organization_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, organizations));
            await context.ExecuteNonQueryAsync();
        }

        if (planner is not null)
        {
            await using var command = new NpgsqlCommand(planner, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>The parameters the runtime binds, with its types; the party statement has no owner, public_id or COD term.</summary>
    private static NpgsqlParameter[] Parameters(
        bool filteredStatement,
        Filter filter,
        (DateTimeOffset At, Guid Id)? cursor,
        int take)
    {
        var parameters = new List<NpgsqlParameter> { P("status", NpgsqlDbType.Text, filter.Status) };
        if (filteredStatement)
        {
            parameters.Add(P("owner", NpgsqlDbType.Uuid, filter.Owner));
            parameters.Add(P("public_id", NpgsqlDbType.Text, filter.PublicId));
            parameters.Add(P("cod_pending", NpgsqlDbType.Boolean, filter.CodPending));
        }

        parameters.Add(P("created_from", NpgsqlDbType.TimestampTz, filter.From?.ToUniversalTime()));
        parameters.Add(P("created_to", NpgsqlDbType.TimestampTz, filter.To?.ToUniversalTime()));
        parameters.Add(P("has_cursor", NpgsqlDbType.Boolean, cursor is not null));
        parameters.Add(P("cursor_created_at", NpgsqlDbType.TimestampTz, cursor?.At ?? default));
        parameters.Add(P("cursor_id", NpgsqlDbType.Uuid, cursor?.Id ?? Guid.Empty));
        parameters.Add(P("take", NpgsqlDbType.Integer, take));
        return [.. parameters];
    }

    private static string PageText(int page, bool more, IEnumerable<string> rows) =>
        $"page {page} more={more}: {string.Join(" / ", rows)}";

    private static string ShortRow(Guid id, string publicId, string status, DateTimeOffset createdAt) =>
        string.Join('|', id, publicId, status, createdAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

    /// <summary>Every column the list returns, in order (22 columns), as invariant text.</summary>
    private static string FullRow(NpgsqlDataReader reader)
    {
        Assert.Equal(22, reader.FieldCount);
        var values = new string[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            values[index] = reader.IsDBNull(index)
                ? "NULL"
                : reader.GetValue(index) switch
                {
                    DateTime instant => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    var value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                };
        }

        return string.Join('|', values);
    }

    private static async Task<int> VisibleCountAsync(NpgsqlConnection connection, Guid[] organizations)
    {
        // As the superuser (no RLS): what orders_tenant admits for these organizations.
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::int FROM orders.orders WHERE owner_org_id = ANY(@orgs) OR operator_org_id = ANY(@orgs)",
            connection);
        command.Parameters.Add(P("orgs", NpgsqlDbType.Array | NpgsqlDbType.Uuid, organizations));
        return (int)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Synthetic rows only (no real person, address or phone). The comparison set: A, B and D (BUSINESS) and C (ALLY),
    /// 160 orders in eight kinds - A alone, A operated by C, B operated by A, A operated by A, B alone, B operated by C,
    /// D operated by A, D alone - five statuses, every three orders created in the same minute and eighteen in one
    /// minute, COD pending on every fifth order. The plan set: 10 000 orders over a year owned by P (50 %), R (30 %) and S
    /// (20 %), 40 % operated by the ALLY Q. Foreign keys are not checked while seeding (the rows point at no location or
    /// quote); RLS and the indexes are untouched.
    /// </summary>
    private static async Task<Seeded> SeedAsync(NpgsqlConnection connection)
    {
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var command = new NpgsqlCommand(SeedSql, connection, transaction))
            {
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        await using (var analyze = new NpgsqlCommand("ANALYZE orders.orders; ANALYZE finance.cod_transactions;", connection))
        {
            await analyze.ExecuteNonQueryAsync();
        }

        var parties = new Dictionary<string, Party>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
            """
            SELECT substr(o.display_name, 12), o.id, u.id
            FROM organizations.organizations o
            JOIN organizations.organization_memberships m ON m.organization_id = o.id
            JOIN identity.users u ON u.id = m.user_id
            WHERE o.display_name LIKE 'Party list %'
            """,
            connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                parties.Add(reader.GetString(0), new Party(reader.GetGuid(1), reader.GetGuid(2)));
            }
        }

        // One order of each kind (n = 0..7), as the seed spells its public_id; each one exists and is a valid public_id.
        var publicIds = Enumerable.Range(0, 8)
            .Select(n => OrderPublicIdPolicy.Prefix + ("EQ" + n.ToString(CultureInfo.InvariantCulture)).PadLeft(22, 'x'))
            .ToArray();
        Assert.All(publicIds, id => Assert.True(OrderPublicIdPolicy.IsValid(id), id));
        await using (var command = new NpgsqlCommand(
            "SELECT count(*)::int FROM orders.orders WHERE public_id = ANY(@ids)",
            connection))
        {
            command.Parameters.Add(P("ids", NpgsqlDbType.Array | NpgsqlDbType.Text, publicIds));
            Assert.Equal(8, (int)(await command.ExecuteScalarAsync())!);
        }
        await using var now = new NpgsqlCommand("SELECT now()", connection);
        return new Seeded(
            parties["A"], parties["B"], parties["C"], parties["D"], parties["P"], parties["Q"], parties["R"],
            publicIds, ((DateTime)(await now.ExecuteScalarAsync())!).ToUniversalTime());
    }

    private const string SeedSql =
        """
        SET LOCAL session_replication_role = replica;
        SELECT setseed(0.1020);
        CREATE TEMP TABLE party_orgs ON COMMIT DROP AS
        SELECT code, kind, gen_random_uuid() AS id, gen_random_uuid() AS user_id
        FROM (VALUES ('A','BUSINESS'),('B','BUSINESS'),('C','ALLY'),('D','BUSINESS'),
                     ('P','BUSINESS'),('R','BUSINESS'),('S','BUSINESS'),('Q','ALLY')) AS v(code, kind);
        INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
        SELECT id,'Party list '||code,'Party list '||code,kind FROM party_orgs;
        INSERT INTO identity.users(id,identity_subject) SELECT user_id,'party-list-dispatcher-'||code FROM party_orgs;
        INSERT INTO organizations.organization_memberships(user_id,organization_id,role)
        SELECT user_id,id,'DISPATCHER' FROM party_orgs;
        CREATE TEMP TABLE party_city ON COMMIT DROP AS SELECT gen_random_uuid() AS id;
        INSERT INTO locations.cities(id,state_code,name,timezone)
        SELECT id,'SIN','Party list city','America/Mazatlan' FROM party_city;
        CREATE TEMP TABLE party_orders ON COMMIT DROP AS
        SELECT n, gen_random_uuid() AS id,
               (ARRAY['A','A','B','A','B','B','D','D'])[1 + n % 8] AS owner_code,
               (ARRAY[NULL,'C','A','A',NULL,'C','A',NULL])[1 + n % 8] AS operator_code,
               (ARRAY['DELIVERED','CLOSED','IN_TRANSIT','CANCELLED','DELIVERED'])[1 + (n / 8) % 5] AS status,
               CASE WHEN n BETWEEN 60 AND 77 THEN timestamptz '2026-10-01 12:00:00+00' + interval '20 minutes'
                    ELSE timestamptz '2026-10-01 12:00:00+00' + (n / 3) * interval '1 minute' END AS created_at,
               'ORD_' || lpad('EQ' || n, 22, 'x') AS public_id
        FROM generate_series(0, 159) AS n;
        INSERT INTO party_orders(n,id,owner_code,operator_code,status,created_at,public_id)
        SELECT 1000 + n, gen_random_uuid(),
               CASE WHEN r1 < 0.5 THEN 'P' WHEN r1 < 0.8 THEN 'R' ELSE 'S' END,
               CASE WHEN r2 < 0.4 THEN 'Q' END,
               (ARRAY['CLOSED','CLOSED','CLOSED','DELIVERED','CANCELLED','IN_TRANSIT'])[1 + floor(r3 * 6)::int],
               now() - r4 * interval '365 days',
               'ORD_' || lpad('BULK' || n, 22, 'y')
        FROM (SELECT n, random() AS r1, random() AS r2, random() AS r3, random() AS r4
              FROM generate_series(1, 10000) AS n) AS r;
        INSERT INTO orders.orders(id,public_id,quote_id,owner_org_id,operator_org_id,city_id,origin_location_id,
          destination_location_id,service_type,pricing_tier,payer_type,status,subtotal_cents,discount_cents,tax_cents,
          total_cents,minimum_total_cents_snapshot,pricing_policy_version,package_snapshot,created_at,updated_at)
        SELECT o.id,o.public_id,gen_random_uuid(),owner.id,operator.id,(SELECT id FROM party_city),gen_random_uuid(),
          gen_random_uuid(),'SAME_DAY','OCCASIONAL','SENDER',o.status,9000,0,0,9000,8000,'v1','{}'::jsonb,
          o.created_at,o.created_at
        FROM party_orders o
        JOIN party_orgs owner ON owner.code = o.owner_code
        LEFT JOIN party_orgs operator ON operator.code = o.operator_code;
        INSERT INTO finance.cod_transactions(id,order_id,owner_org_id,operator_org_id,amount_cents,status,recorded_at,
          reconciled_at,created_at)
        SELECT gen_random_uuid(),o.id,owner.id,operator.id,9000,
               CASE WHEN o.n % 5 = 0 THEN 'RECORDED' ELSE 'RECONCILED' END,
               o.created_at + interval '1 hour',
               CASE WHEN o.n % 5 = 0 THEN NULL ELSE o.created_at + interval '1 day' END,
               o.created_at
        FROM party_orders o
        JOIN party_orgs owner ON owner.code = o.owner_code
        LEFT JOIN party_orgs operator ON operator.code = o.operator_code
        WHERE o.n < 1000 AND o.n % 5 IN (0, 1);
        """;

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type)
    {
        Value = value ?? DBNull.Value,
    };

    private sealed record Party(Guid Id, Guid Actor);

    private sealed record Seeded(
        Party A,
        Party B,
        Party C,
        Party D,
        Party P,
        Party Q,
        Party R,
        IReadOnlyList<string> PublicIds,
        DateTimeOffset Now);

    private sealed record Filter(
        string? Status,
        Guid? Owner,
        string? PublicId,
        bool CodPending,
        DateTimeOffset? From,
        DateTimeOffset? To)
    {
        public static Filter None { get; } = new(null, null, null, false, null, null);
    }

    private sealed record Walk(
        List<string> Pages,
        List<Guid> Ids,
        int TieBoundaries,
        (DateTimeOffset At, Guid Id)? LastCursor)
    {
        public int Rows => Ids.Count;
    }

    /// <summary>The order list statements as the runtime sends them, read from its source.</summary>
    private sealed partial record Statements(string Filtered, string Party, string Planner)
    {
        public static Statements Read()
        {
            var source = File.ReadAllText(Path.Combine(RepositoryPaths.Root, CoordinatorPath));
            var readers = File.ReadAllText(Path.Combine(
                RepositoryPaths.Root,
                "src/Modules/Orders/Orders.Infrastructure/Orders/PostgreSqlOrderTransitionReaders.cs"));
            var predicate = CodPredicate().Match(readers);
            Assert.True(predicate.Success, "The COD-pending predicate lost its shape.");
            var cod = string.Concat(Literal().Matches(predicate.Groups[1].Value).Select(match => match.Groups[1].Value));
            Assert.StartsWith("EXISTS (SELECT 1 FROM finance.cod_transactions", cod, StringComparison.Ordinal);
            const string hole = "{OrderCodPendingReconciliationPredicate.Sql}";
            var filtered = RawSql(source, "private const string FilteredListSql =");
            Assert.Contains(hole, filtered, StringComparison.Ordinal);
            var planner = PlannerSql().Match(source);
            Assert.True(planner.Success, "The party statement lost its planner setting.");
            return new Statements(
                filtered.Replace(hole, cod, StringComparison.Ordinal),
                RawSql(source, "private const string PartyListSql ="),
                planner.Groups[1].Value);
        }

        /// <summary>The first raw string literal after <paramref name="anchor"/>: the statement the runtime sends.</summary>
        private static string RawSql(string source, string anchor)
        {
            var start = source.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(start >= 0, $"The runtime source lost its anchor {anchor}");
            var open = source.IndexOf("\"\"\"", start, StringComparison.Ordinal);
            var close = source.IndexOf("\"\"\"", open + 3, StringComparison.Ordinal);
            Assert.True(open > start && close > open, $"No raw SQL literal follows {anchor}");
            var sql = source[(open + 3)..close].Trim();
            Assert.StartsWith("SELECT", sql, StringComparison.Ordinal);
            return sql;
        }

        [GeneratedRegex("""class OrderCodPendingReconciliationPredicate\s*\{\s*internal const string Sql =\s*((?:"[^"]*"\s*\+?\s*)+);""")]
        private static partial Regex CodPredicate();

        [GeneratedRegex("\"([^\"]*)\"")]
        private static partial Regex Literal();

        [GeneratedRegex("""private const string PartyListPlannerSql = "([^"]+)";""")]
        private static partial Regex PlannerSql();
    }
}
