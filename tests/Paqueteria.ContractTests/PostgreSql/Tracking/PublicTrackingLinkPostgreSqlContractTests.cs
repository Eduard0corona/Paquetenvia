using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Orders.Application.Orders;
using Orders.Application.Tracking;
using Orders.Infrastructure;
using Orders.Infrastructure.Orders;
using Orders.Infrastructure.Persistence;
using Orders.Infrastructure.Tracking;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Contracts.Tracking;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql.Tracking;

/// <summary>
/// TRK-002-AUTO-LINK against the real schema and the least-privilege runtime role (NOBYPASSRLS). Every order gets
/// generation 1 of its link in the order-creation transaction, audited; get-or-create re-derives the same link and
/// writes nothing; revocation is the uniform public 404 and the next get-or-create derives the next generation; a
/// finished order gets no new link and its link lasts 24 hours after the order first reaches a final public status;
/// only the SHA-256 of a token is ever stored, and every other organization is refused without touching a row.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class PublicTrackingLinkPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    /// <summary>A synthetic key derived at runtime from a public label; it protects nothing.</summary>
    private static readonly byte[] KeyOne = SHA256.HashData(Encoding.UTF8.GetBytes("trk-002 auto-link contract key one"));
    private static readonly byte[] KeyTwo = SHA256.HashData(Encoding.UTF8.GetBytes("trk-002 auto-link contract key two"));
    private static readonly byte[] KeyThree = SHA256.HashData(Encoding.UTF8.GetBytes("trk-002 auto-link contract key three"));

    private static readonly DateTimeOffset AcceptedAtClient = new DateTimeOffset(
        DateTimeOffset.UtcNow.AddHours(-1).UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
        TimeSpan.Zero);

    [PostgreSqlContractFact]
    public async Task Derivation_matches_an_independent_PostgreSQL_HMAC_of_the_canonical_input()
    {
        var orderId = Guid.NewGuid();
        foreach (var (keyVersion, generation) in new[] { (1, 1), (1, 2), (7, 1), (32767, 2_000_000_000) })
        {
            var input = $"paquetenvia-trk-v1|{keyVersion}|{orderId.ToString("D").ToLowerInvariant()}|{generation}";
            Assert.Equal(input, TrackingLinkTokenDerivation.CanonicalInput(keyVersion, orderId, generation));
            await using var command = fixture.AdminDataSource.CreateCommand(
                """
                SELECT pg_catalog.rtrim(pg_catalog.translate(pg_catalog.encode(
                  extensions.hmac(pg_catalog.convert_to(@input,'UTF8'),@key,'sha256'),'base64'),'+/','-_'),'=');
                """);
            command.Parameters.AddWithValue("input", input);
            command.Parameters.AddWithValue("key", KeyOne);
            var expected = (string)(await command.ExecuteScalarAsync())!;
            var derived = TrackingLinkTokenDerivation.DeriveToken(KeyOne, keyVersion, orderId, generation);
            Assert.Equal(expected, derived);
            Assert.Matches("^[A-Za-z0-9_-]{43}$", derived);
        }
    }

    [PostgreSqlContractFact]
    public async Task Order_creation_issues_generation_one_with_audit_and_get_or_create_returns_it_without_writing()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(createOrder: false);
        var created = await CreateOrderAsync(scenario, "trk002-auto-create-0001", "trk002-auto-request-0001");

        var row = Assert.Single(await ReadTokenRowsAsync(created.Id));
        Assert.Equal(1, row.Generation);
        Assert.Equal(1, row.KeyVersion);
        Assert.False(row.Revoked);
        Assert.True(row.ExpiresAtInfinity);
        var expected = TrackingLinkTokenDerivation.DeriveToken(KeyOne, 1, created.Id, 1);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), row.Hash);
        Assert.DoesNotContain(expected, row.Json, StringComparison.Ordinal);
        Assert.Equal("public_id", await ReadProjectionPublicIdKeyAsync(expected));

        var audit = Assert.Single(await ReadAuditsAsync(created.Id));
        Assert.Equal("TRACKING_TOKEN_ISSUED", audit.Action);
        Assert.Equal(scenario.UserId, audit.ActorId);
        Assert.Equal("trk002-auto-request-0001", audit.RequestId);
        AssertAuditPayload(audit.Payload, row.Id, generation: 1, keyVersion: 1, revokedCount: 0);
        Assert.DoesNotContain(expected, audit.Payload, StringComparison.Ordinal);

        // Get-or-create, with any Idempotency-Key, returns that same link and writes nothing.
        foreach (var key in new[] { "trk002-auto-read-0001", "trk002-auto-read-0001", "trk002-auto-read-0002" })
        {
            var grant = await GetOrCreateAsync(scenario, created.Id, key);
            Assert.Equal(row.Id, grant.TokenId);
            Assert.Equal(expected, grant.Token);
            Assert.Equal(1, grant.Generation);
            Assert.Null(grant.ValidUntil);
            Assert.DoesNotContain(grant.Token, grant.ToString(), StringComparison.Ordinal);
        }

        Assert.Single(await ReadTokenRowsAsync(created.Id));
        Assert.Single(await ReadAuditsAsync(created.Id));
    }

    [PostgreSqlContractFact]
    public async Task A_retired_generation_is_the_uniform_404_and_is_never_derived_again()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");

        // An order created before TRK-002-AUTO-LINK (no row yet) gets generation 1 on first read, audited.
        var first = await GetOrCreateAsync(scenario, scenario.OrderId, "trk002-revoke-read-0001");
        Assert.Equal(1, first.Generation);
        Assert.Equal("public_id", await ReadProjectionPublicIdKeyAsync(first.Token));

        // TRK-002-NO-REVOCATION: no operation revokes a link. A row can still be retired (revoked_at) when a
        // pre-derivation token or a removed key version is replaced; retiring it here directly proves that the lookup
        // fails closed for a retired row and that its generation never comes back.
        await RetireAsync(scenario, scenario.OrderId);
        Assert.Null(await ReadProjectionPublicIdKeyAsync(first.Token));

        var second = await GetOrCreateAsync(scenario, scenario.OrderId, "trk002-revoke-read-0002");
        Assert.Equal(2, second.Generation);
        Assert.NotEqual(first.Token, second.Token);
        Assert.NotEqual(first.TokenId, second.TokenId);
        Assert.Equal(TrackingLinkTokenDerivation.DeriveToken(KeyOne, 1, scenario.OrderId, 2), second.Token);
        Assert.Null(await ReadProjectionPublicIdKeyAsync(first.Token));
        Assert.Equal("public_id", await ReadProjectionPublicIdKeyAsync(second.Token));
        Assert.Equal(second.Token, (await GetOrCreateAsync(scenario, scenario.OrderId, "trk002-revoke-read-0003")).Token);

        var rows = await ReadTokenRowsAsync(scenario.OrderId);
        Assert.Equal([1, 2], rows.Select(row => row.Generation));
        Assert.Equal([true, false], rows.Select(row => row.Revoked));
        var audits = await ReadAuditsAsync(scenario.OrderId);
        Assert.Equal(
            ["TRACKING_TOKEN_ISSUED", "TRACKING_TOKEN_ISSUED"],
            audits.Select(audit => audit.Action));
        Assert.Equal(
            ["trk002-revoke-read-0001", "trk002-revoke-read-0002"],
            audits.Select(audit => audit.RequestId));
        AssertAuditPayload(audits[1].Payload, second.TokenId, generation: 2, keyVersion: 1, revokedCount: 0);
        Assert.All(audits, audit =>
        {
            Assert.DoesNotContain(first.Token, audit.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain(second.Token, audit.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(second.Token))),
                audit.Payload,
                StringComparison.OrdinalIgnoreCase);
        });

        // The database itself keeps one live derived link per order and one row per derived generation.
        await Assert.ThrowsAsync<PostgresException>(() => scenario.ExecuteAdminAsync(
            """
            INSERT INTO orders.public_tracking_tokens(order_id,owner_org_id,token_hash,expires_at,generation,key_version)
            VALUES (@order,@org,extensions.digest('trk002-second-live','sha256'),'infinity',3,1);
            """,
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId)));
        await Assert.ThrowsAsync<PostgresException>(() => scenario.ExecuteAdminAsync(
            """
            INSERT INTO orders.public_tracking_tokens(
              order_id,owner_org_id,token_hash,expires_at,revoked_at,generation,key_version)
            VALUES (@order,@org,extensions.digest('trk002-same-generation','sha256'),'infinity',clock_timestamp(),1,1);
            """,
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId)));
    }

    [PostgreSqlContractFact]
    public async Task A_finished_order_keeps_its_link_for_24_hours_after_its_final_event_and_gets_no_new_one()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var link = await GetOrCreateAsync(scenario, scenario.OrderId, "trk002-final-read-0001");

        var finishedAt = await DatabaseNowAsync() - TimeSpan.FromHours(23);
        await FinishAsync(scenario, "DELIVERED", "DELIVERED", finishedAt);
        Assert.Equal("public_id", await ReadProjectionPublicIdKeyAsync(link.Token));
        var withinGrace = await GetOrCreateAsync(scenario, scenario.OrderId, "trk002-final-read-0002");
        Assert.Equal(link.Token, withinGrace.Token);
        Assert.Equal(finishedAt + TimeSpan.FromHours(24), withinGrace.ValidUntil);

        // CLOSED, CLAIM_OPEN and CLAIM_RESOLVED are DELIVERED for the public: the same first event still bounds it.
        await SetStatusAsync(scenario, "CLOSED");
        Assert.Equal("public_id", await ReadProjectionPublicIdKeyAsync(link.Token));

        // A retired link of a finished order is never replaced.
        await RetireAsync(scenario, scenario.OrderId);
        await Assert.ThrowsAsync<PublicTrackingLinkOrderFinishedException>(() =>
            GetOrCreateAsync(scenario, scenario.OrderId, "trk002-final-read-0003"));
        Assert.Single(await ReadTokenRowsAsync(scenario.OrderId));

        // Past the grace the link is the uniform 404 and get-or-create refuses.
        await using var expired = new SyntheticOrderScenario(fixture);
        await expired.InitializeAsync(orderStatus: "DELIVERING");
        var expiredLink = await GetOrCreateAsync(expired, expired.OrderId, "trk002-final-read-0004");
        await FinishAsync(expired, "DELIVERED", "DELIVERED", await DatabaseNowAsync() - TimeSpan.FromHours(25));
        Assert.Null(await ReadProjectionPublicIdKeyAsync(expiredLink.Token));
        await Assert.ThrowsAsync<PublicTrackingLinkOrderFinishedException>(() =>
            GetOrCreateAsync(expired, expired.OrderId, "trk002-final-read-0005"));
    }

    [PostgreSqlContractFact]
    public async Task Cancelled_returned_and_eventless_finished_orders_fail_closed_while_rescheduled_stays_alive()
    {
        // CANCELLED and RETURNED end the link after 24 hours exactly like DELIVERED (order events are append-only,
        // so each side of the boundary is its own order).
        foreach (var status in new[] { "CANCELLED", "RETURNED" })
        {
            foreach (var (ago, resolves) in new[]
                     {
                         (TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1), true),
                         (TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1), false),
                     })
            {
                await using var scenario = new SyntheticOrderScenario(fixture);
                await scenario.InitializeAsync(orderStatus: "READY_FOR_PICKUP");
                var link = await GetOrCreateAsync(scenario, scenario.OrderId, $"trk002-{status.ToLowerInvariant()}-0001");
                await FinishAsync(scenario, status, status, await DatabaseNowAsync() - ago);
                Assert.Equal(resolves ? "public_id" : null, await ReadProjectionPublicIdKeyAsync(link.Token));
            }
        }

        // A finished order without a link never gets one.
        await using (var cancelled = new SyntheticOrderScenario(fixture))
        {
            await cancelled.InitializeAsync(orderStatus: "CANCELLED");
            await Assert.ThrowsAsync<PublicTrackingLinkOrderFinishedException>(() =>
                GetOrCreateAsync(cancelled, cancelled.OrderId, "trk002-cancelled-read-0001"));
            Assert.Empty(await ReadTokenRowsAsync(cancelled.OrderId));
            Assert.Empty(await ReadAuditsAsync(cancelled.OrderId));
        }

        // A final status without its final event fails closed in SQL and in the service.
        await using (var eventless = new SyntheticOrderScenario(fixture))
        {
            await eventless.InitializeAsync(orderStatus: "IN_TRANSIT");
            var link = await GetOrCreateAsync(eventless, eventless.OrderId, "trk002-eventless-read-0001");
            await SetStatusAsync(eventless, "DELIVERED");
            Assert.Null(await ReadProjectionPublicIdKeyAsync(link.Token));
            await Assert.ThrowsAsync<PublicTrackingLinkOrderFinishedException>(() =>
                GetOrCreateAsync(eventless, eventless.OrderId, "trk002-eventless-read-0002"));
        }

        // RESCHEDULED is not final: the order is assigned again and its link stays alive, unchanged.
        await using var rescheduled = new SyntheticOrderScenario(fixture);
        await rescheduled.InitializeAsync(orderStatus: "FAILED_ATTEMPT");
        var alive = await GetOrCreateAsync(rescheduled, rescheduled.OrderId, "trk002-rescheduled-read-0001");
        await FinishAsync(rescheduled, "RESCHEDULED", "RESCHEDULED", await DatabaseNowAsync() - TimeSpan.FromDays(3));
        Assert.Equal("public_id", await ReadProjectionPublicIdKeyAsync(alive.Token));
        var again = await GetOrCreateAsync(rescheduled, rescheduled.OrderId, "trk002-rescheduled-read-0002");
        Assert.Equal(alive.Token, again.Token);
        Assert.Null(again.ValidUntil);
    }

    [PostgreSqlContractFact]
    public async Task A_pre_derivation_random_link_is_retired_and_replaced_by_the_next_derived_generation()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "IN_TRANSIT");
        var legacy = new TrackingTokenHasher().CreateToken();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO orders.public_tracking_tokens(id,order_id,owner_org_id,token_hash,expires_at)
            VALUES (gen_random_uuid(),@order,@org,@hash,clock_timestamp()+interval '7 days');
            """,
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("hash", SHA256.HashData(Encoding.UTF8.GetBytes(legacy))));
        var legacyRow = Assert.Single(await ReadTokenRowsAsync(scenario.OrderId));
        Assert.Equal(1, legacyRow.Generation);
        Assert.Null(legacyRow.KeyVersion);
        Assert.Equal("public_id", await ReadProjectionPublicIdKeyAsync(legacy));

        var derived = await GetOrCreateAsync(scenario, scenario.OrderId, "trk002-legacy-read-0001");
        Assert.Equal(2, derived.Generation);
        Assert.Null(await ReadProjectionPublicIdKeyAsync(legacy));
        Assert.Equal("public_id", await ReadProjectionPublicIdKeyAsync(derived.Token));
        var audit = Assert.Single(await ReadAuditsAsync(scenario.OrderId));
        AssertAuditPayload(audit.Payload, derived.TokenId, generation: 2, keyVersion: 1, revokedCount: 1);
    }

    [PostgreSqlContractFact]
    public async Task Key_rotation_keeps_configured_versions_replaces_removed_ones_and_fails_closed_on_changed_key()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "ASSIGNED");
        var first = await GetOrCreateAsync(scenario, scenario.OrderId, "trk002-rotation-read-0001");

        // Version 2 becomes current while version 1 stays configured: the version-1 link is still re-derived.
        var both = Ring((1, KeyOne), (2, KeyTwo));
        Assert.Equal(first.Token, (await GetOrCreateAsync(scenario, scenario.OrderId, "trk002-rotation-read-0002", both)).Token);

        // Version 1 removed: the link can no longer be shown, so it is retired and version 2 derives generation 2.
        var onlyTwo = Ring((2, KeyTwo));
        var replaced = await GetOrCreateAsync(scenario, scenario.OrderId, "trk002-rotation-read-0003", onlyTwo);
        Assert.Equal(2, replaced.Generation);
        Assert.Equal(TrackingLinkTokenDerivation.DeriveToken(KeyTwo, 2, scenario.OrderId, 2), replaced.Token);
        Assert.Null(await ReadProjectionPublicIdKeyAsync(first.Token));
        Assert.Equal(2, (await ReadTokenRowsAsync(scenario.OrderId)).Last().KeyVersion);

        // Different bytes under the same version never hand out a link that does not resolve: fail closed.
        var before = await ReadTokenRowsAsync(scenario.OrderId);
        await Assert.ThrowsAsync<PublicTrackingTokenInfrastructureException>(() =>
            GetOrCreateAsync(scenario, scenario.OrderId, "trk002-rotation-read-0004", Ring((2, KeyThree))));
        Assert.Equal(before, await ReadTokenRowsAsync(scenario.OrderId));
        Assert.Equal("public_id", await ReadProjectionPublicIdKeyAsync(replaced.Token));
    }

    [PostgreSqlContractFact]
    public async Task Another_organization_can_neither_see_nor_get_the_link()
    {
        await using var owner = new SyntheticOrderScenario(fixture);
        await owner.InitializeAsync(orderStatus: "DELIVERING");
        await using var foreign = new SyntheticOrderScenario(fixture);
        await foreign.InitializeAsync(orderStatus: "DELIVERING");

        var grant = await GetOrCreateAsync(owner, owner.OrderId, "trk002-contract-owner-0001");
        var before = await ReadTokenRowsAsync(owner.OrderId);

        await Assert.ThrowsAsync<PublicTrackingTokenNotFoundException>(() => WithServiceAsync(Ring((1, KeyOne)), service =>
            service.GetOrCreateAsync(
                new GetOrCreatePublicTrackingLinkCommand(
                    foreign.UserId, foreign.OrganizationId, owner.OrderId, "trk002-contract-foreign-0001"),
                CancellationToken.None)));

        // Nothing changed for the owner, the link still resolves, the foreign organization wrote no audit and RLS
        // hides the owner's token rows from the foreign tenant context entirely.
        Assert.Equal(before, await ReadTokenRowsAsync(owner.OrderId));
        Assert.Equal("public_id", await ReadProjectionPublicIdKeyAsync(grant.Token));
        Assert.Single(await ReadAuditsAsync(owner.OrderId));
        Assert.Equal(0L, await CountAuditsForOrganizationAsync(foreign.OrganizationId));
        Assert.Equal(0L, await CountTokenRowsAsTenantAsync(foreign.UserId, foreign.OrganizationId, owner.OrderId));
        Assert.Equal(1L, await CountTokenRowsAsTenantAsync(owner.UserId, owner.OrganizationId, owner.OrderId));
    }

    private static PublicTrackingLinkKeyRing Ring(params (int Version, byte[] Key)[] keys)
    {
        var options = new PublicTrackingOptions
        {
            Provider = PublicTrackingProviderKind.PostgreSql,
            CurrentLinkKeyVersion = keys.Max(key => key.Version),
        };
        foreach (var (version, key) in keys)
        {
            options.LinkKeys[version] = Convert.ToBase64String(key);
        }

        var ring = new PublicTrackingLinkKeyRing(options, allowSyntheticKey: false);
        Assert.True(ring.IsAvailable);
        Assert.False(ring.IsSynthetic);
        return ring;
    }

    private static void AssertAuditPayload(string payload, Guid tokenId, int generation, int keyVersion, int revokedCount)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal(tokenId.ToString("D"), root.GetProperty("token_id").GetString(), ignoreCase: true);
        Assert.Equal(generation, root.GetProperty("generation").GetInt32());
        Assert.Equal(keyVersion, root.GetProperty("key_version").GetInt32());
        Assert.Equal(revokedCount, root.GetProperty("previous_tokens_revoked_count").GetInt32());
        Assert.False(root.TryGetProperty("token", out _));
        Assert.False(root.TryGetProperty("token_hash", out _));
    }

    private Task<PublicTrackingTokenGrant> GetOrCreateAsync(
        SyntheticOrderScenario scenario,
        Guid orderId,
        string requestId,
        PublicTrackingLinkKeyRing? ring = null) =>
        WithServiceAsync(ring ?? Ring((1, KeyOne)), service => service.GetOrCreateAsync(
            new GetOrCreatePublicTrackingLinkCommand(scenario.UserId, scenario.OrganizationId, orderId, requestId),
            CancellationToken.None));

    /// <summary>
    /// Retires the order's live link directly (test setup only). No runtime operation revokes a link
    /// (TRK-002-NO-REVOCATION); get-or-create retires only a link it cannot re-derive.
    /// </summary>
    private static Task RetireAsync(SyntheticOrderScenario scenario, Guid orderId) =>
        scenario.ExecuteAdminAsync(
            """
            UPDATE orders.public_tracking_tokens
            SET revoked_at=clock_timestamp()
            WHERE order_id=@order AND revoked_at IS NULL;
            """,
            SyntheticOrderScenario.P("order", orderId));

    private async Task<T> WithServiceAsync<T>(
        PublicTrackingLinkKeyRing ring,
        Func<IPublicTrackingTokenService, Task<T>> operation)
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(fixture.AppDataSource)
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        await using var context = new OrdersDbContext(options, state);
        var service = new PostgreSqlPublicTrackingTokenService(
            new TenantTransactionContext<OrdersDbContext>(context, state),
            new TrackingTokenHasher(),
            ring,
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            new SystemClock());
        return await operation(service);
    }

    private async Task<OrderResult> CreateOrderAsync(SyntheticOrderScenario scenario, string idempotencyKey, string requestId)
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(fixture.AppDataSource)
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        await using var context = new OrdersDbContext(options, state);
        var coordinator = new QuoteSnapshotToOrderCoordinator(
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
            new SystemClock(),
            new PostgreSqlOrderTrackingLinkIssuer(
                new TrackingTokenHasher(),
                Ring((1, KeyOne)),
                new PostgreSqlAppendOnlyAuditWriter(state),
                new AuditPayloadRedactor()));
        return await coordinator.CreateAsync(
            new CreateOrderCommand(
                scenario.UserId,
                scenario.OrganizationId,
                idempotencyKey,
                scenario.QuoteId,
                "SENDER",
                new OrderAcceptanceInput("terms-synthetic-v1", "privacy-synthetic-v1", AcceptedAtClient, "WEB"),
                requestId,
                RestrictedGoodsAcknowledged: true),
            CancellationToken.None);
    }

    private async Task<DateTimeOffset> DatabaseNowAsync()
    {
        await using var command = fixture.AdminDataSource.CreateCommand("SELECT clock_timestamp();");
        var value = await command.ExecuteScalarAsync();
        return value switch
        {
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            DateTimeOffset offset => offset,
            _ => throw new InvalidOperationException("No database clock."),
        };
    }

    private static Task SetStatusAsync(SyntheticOrderScenario scenario, string status) =>
        scenario.ExecuteAdminAsync(
            "UPDATE orders.orders SET status=@status WHERE id=@order;",
            SyntheticOrderScenario.P("status", status),
            SyntheticOrderScenario.P("order", scenario.OrderId));

    /// <summary>What the productive transition writes: the new status and its ORDER_STATUS_CHANGED public event.</summary>
    private static async Task FinishAsync(
        SyntheticOrderScenario scenario,
        string status,
        string publicEventCode,
        DateTimeOffset occurredAt)
    {
        await SetStatusAsync(scenario, status);
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO orders.order_events(id,order_id,owner_org_id,aggregate_version,event_type,public_event_code,payload,occurred_at)
            VALUES (gen_random_uuid(),@order,@org,
              (SELECT COALESCE(max(aggregate_version),0)+1 FROM orders.order_events WHERE order_id=@order),
              'ORDER_STATUS_CHANGED',@code,jsonb_build_object('new_status',@status::text),@at);
            """,
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("code", publicEventCode),
            SyntheticOrderScenario.P("status", status),
            SyntheticOrderScenario.P("at", occurredAt));
    }

    private async Task<IReadOnlyList<TokenRow>> ReadTokenRowsAsync(Guid orderId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT id, token_hash, revoked_at IS NOT NULL, generation, key_version, expires_at='infinity',
                   pg_catalog.to_jsonb(t)::text
            FROM orders.public_tracking_tokens t
            WHERE order_id=@order
            ORDER BY generation, created_at, id;
            """);
        command.Parameters.AddWithValue("order", orderId);
        var rows = new List<TokenRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new TokenRow(
                reader.GetGuid(0),
                reader.GetFieldValue<byte[]>(1),
                reader.GetBoolean(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.GetBoolean(5),
                reader.GetString(6)));
        }

        return rows;
    }

    private async Task<IReadOnlyList<AuditRow>> ReadAuditsAsync(Guid orderId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT action, org_id, actor_id, entity_type, request_id, payload_redacted::text
            FROM platform.audit_logs
            WHERE entity_id=@order
              AND action LIKE 'TRACKING_TOKEN_%'
            ORDER BY occurred_at, action DESC;
            """);
        command.Parameters.AddWithValue("order", orderId);
        var rows = new List<AuditRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new AuditRow(
                reader.GetString(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return rows;
    }

    private async Task<long> CountAuditsForOrganizationAsync(Guid organizationId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND action LIKE 'TRACKING_TOKEN_%';");
        command.Parameters.AddWithValue("org", organizationId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountTokenRowsAsTenantAsync(Guid actorId, Guid organizationId, Guid orderId)
    {
        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            actorId,
            [organizationId]);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM orders.public_tracking_tokens WHERE order_id=@order;",
            tenant.Connection,
            tenant.Transaction);
        command.Parameters.AddWithValue("order", orderId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// What publicTracking reads: the SQL projection as the runtime role. Returns the first key of the projection
    /// (<c>public_id</c>) or null for the uniform not-found.
    /// </summary>
    private async Task<string?> ReadProjectionPublicIdKeyAsync(string token)
    {
        await using var connection = await fixture.AppDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_app", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "SELECT security.get_public_tracking_projection(@token)::text",
            connection,
            transaction);
        command.Parameters.AddWithValue("token", token);
        var result = await command.ExecuteScalarAsync();
        if (result is null or DBNull)
        {
            return null;
        }

        using var document = JsonDocument.Parse((string)result);
        return document.RootElement.TryGetProperty("public_id", out _) ? "public_id" : "unexpected";
    }

    private sealed record TokenRow(
        Guid Id,
        byte[] Hash,
        bool Revoked,
        int Generation,
        int? KeyVersion,
        bool ExpiresAtInfinity,
        string Json)
    {
        public bool Equals(TokenRow? other) =>
            other is not null &&
            Id == other.Id &&
            Hash.AsSpan().SequenceEqual(other.Hash) &&
            Revoked == other.Revoked &&
            Generation == other.Generation &&
            KeyVersion == other.KeyVersion &&
            ExpiresAtInfinity == other.ExpiresAtInfinity &&
            Json == other.Json;

        public override int GetHashCode() => HashCode.Combine(Id, Revoked, Generation, Json);
    }

    private sealed record AuditRow(
        string Action,
        Guid OrganizationId,
        Guid ActorId,
        string EntityType,
        string RequestId,
        string Payload);
}
