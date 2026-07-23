using System.Data.Common;
using System.Text.Json;
using Locations.Application.Locations;
using Locations.Infrastructure.Geocoding;
using Locations.Infrastructure.Locations;
using Locations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application.Auditing;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;
using Pricing.Application.Quotes;
using Pricing.Infrastructure;
using Pricing.Infrastructure.Persistence;
using Pricing.Infrastructure.Persistence.Migrations;
using Pricing.Infrastructure.Quotes;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
public sealed class PricingPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Pricing_adoption_is_applied_migrator_owned_and_has_zero_pending_migrations()
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT h."MigrationId", pg_get_userbyid(c.relowner)
            FROM platform.__ef_migrations_history_pricing h
            JOIN pg_class c ON c.relname='__ef_migrations_history_pricing'
            JOIN pg_namespace n ON n.oid=c.relnamespace AND n.nspname='platform';
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(AdoptCanonicalPricingBaseline.MigrationId, reader.GetString(0));
        Assert.Equal("paqueteria_migrator", reader.GetString(1));
        Assert.False(await reader.ReadAsync());

        var options = new DbContextOptionsBuilder<PricingDbContext>()
            .UseNpgsql(fixture.AdminDataSource, postgres =>
            {
                postgres.MigrationsAssembly(typeof(PricingDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_pricing", "platform");
            }).Options;
        await using var context = new PricingDbContext(options, new TenantDatabaseExecutionState());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    [PostgreSqlContractFact]
    public async Task Quote_service_selects_active_tariff_persists_safe_snapshots_and_replays()
    {
        var data = SyntheticPricingData.Create();
        await SeedAsync(data);
        await using var appDataSource = fixture.CreateAppDataSource(maxPoolSize: 12, applicationName: "PRC001.Service.Contract");
        try
        {
            await using var scope = CreateScope(appDataSource);
            var command = CreateCommand(data, "prc001-contract-replay-0001");
            var created = await scope.Service.CreateAsync(command, default);
            var replay = await scope.Service.CreateAsync(command, default);

            Assert.Equal(created.Id, replay.Id);
            Assert.Equal(34_567, created.Net.AmountCents);
            Assert.Equal(0, created.Tax.AmountCents);
            Assert.Equal(34_567, created.Total.AmountCents);
            Assert.Equal("OCCASIONAL", created.PricingTier);
            Assert.Equal("PRC-001-v1", created.PricingPolicyVersion);
            Assert.Equal([data.ZoneRuleId], created.RuleIds);
            Assert.NotEqual(created.OriginLocationId, created.DestinationLocationId);
            Assert.Equal(data.CityId, created.CityId);
            Assert.Equal(data.AreaId, created.ServiceAreaId);
            Assert.DoesNotContain("Synthetic Sender", System.Text.Json.JsonSerializer.Serialize(created), StringComparison.Ordinal);

            var fetched = await scope.Service.GetAsync(data.ActorId, data.OrganizationId, created.Id, default);
            Assert.Equal(created.Id, fetched.Id);

            var changed = command with { ConsolidatedRoute = true };
            var conflict = await Assert.ThrowsAsync<QuoteValidationException>(() => scope.Service.CreateAsync(changed, default));
            Assert.Equal(QuoteValidationCode.IdempotencyConflict, conflict.Code);

            await using var persisted = fixture.AdminDataSource.CreateCommand(
                """
                SELECT q.id,q.origin_location_id,q.destination_location_id,q.rule_ids,q.request_snapshot_redacted,
                       q.package_snapshot,q.breakdown,q.input_hash,q.financial_override,q.created_at,q.expires_at,
                       i.response_status,i.resource_id,i.expires_at>=q.expires_at,
                       encode(q.pii_snapshot_ciphertext,'hex')
                FROM pricing.quotes q
                JOIN platform.idempotency_keys i ON i.resource_id=q.id
                WHERE q.id=@id;
                """);
            persisted.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = created.Id });
            await using var reader = await persisted.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(created.Id, reader.GetGuid(0));
            Assert.Equal(created.OriginLocationId, reader.GetGuid(1));
            Assert.Equal(created.DestinationLocationId, reader.GetGuid(2));
            Assert.Equal([data.ZoneRuleId], reader.GetFieldValue<Guid[]>(3));
            var safeJson = string.Join(' ', reader.GetString(4), reader.GetString(5), reader.GetString(6));
            Assert.DoesNotContain("Synthetic origin", safeJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Synthetic Sender", safeJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("+52667", safeJson, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(32, reader.GetFieldValue<byte[]>(7).Length);
            Assert.True(reader.IsDBNull(8));
            Assert.NotEqual(default, reader.GetFieldValue<DateTimeOffset>(9));
            Assert.True(reader.GetFieldValue<DateTimeOffset>(10) > reader.GetFieldValue<DateTimeOffset>(9));
            Assert.Equal(201, reader.GetInt32(11));
            Assert.Equal(created.Id, reader.GetGuid(12));
            Assert.True(reader.GetBoolean(13));
            Assert.True(reader.IsDBNull(14));
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [PostgreSqlContractFact]
    public async Task Failed_attempt_binds_hash_before_locations_rejects_changed_body_and_resumes_original()
    {
        var data = SyntheticPricingData.Create();
        const string key = "prc001-def001-reservation-01";
        await SeedAsync(data);
        await using (var disableTariffs = fixture.AdminDataSource.CreateCommand(
            "UPDATE pricing.tariff_rules SET status='INACTIVE' WHERE owner_org_id=@org"))
        {
            disableTariffs.Parameters.Add(P("org", data.OrganizationId));
            Assert.Equal(3, await disableTariffs.ExecuteNonQueryAsync());
        }

        await using var appDataSource = fixture.CreateAppDataSource(maxPoolSize: 12, applicationName: "PRC001.Def001.Contract");
        try
        {
            await using var scope = CreateScope(appDataSource);
            var original = CreateCommand(data, key);
            var originalHash = PostgreSqlQuoteService.ComputeInputHash(original);
            var failed = await Assert.ThrowsAsync<QuoteValidationException>(() => scope.Service.CreateAsync(original, default));
            Assert.Equal(QuoteValidationCode.NoTariffRule, failed.Code);
            Assert.Equal(2, scope.LocationResolver.CallCount);

            await AssertPendingReservationAsync(data, key, originalHash, expectedLocations: 2, expectedAudits: 2);

            var changed = original with
            {
                Origin = original.Origin with
                {
                    AddressText = "Different synthetic origin 900",
                    Lat = 24.82,
                },
                Packages = [original.Packages[0] with { Description = "Different synthetic parcel" }],
            };
            scope.LocationResolver.Reset();
            var conflict = await Assert.ThrowsAsync<QuoteValidationException>(() => scope.Service.CreateAsync(changed, default));
            Assert.Equal(QuoteValidationCode.IdempotencyConflict, conflict.Code);
            Assert.Equal(0, scope.LocationResolver.CallCount);
            await AssertPendingReservationAsync(data, key, originalHash, expectedLocations: 2, expectedAudits: 2);

            await using (var enableTariff = fixture.AdminDataSource.CreateCommand(
                "UPDATE pricing.tariff_rules SET status='ACTIVE' WHERE id=@rule"))
            {
                enableTariff.Parameters.Add(P("rule", data.CityRuleId));
                Assert.Equal(1, await enableTariff.ExecuteNonQueryAsync());
            }

            scope.LocationResolver.Reset();
            var resumed = await scope.Service.CreateAsync(original, default);
            Assert.Equal(2, scope.LocationResolver.CallCount);
            Assert.Equal([data.CityRuleId], resumed.RuleIds);

            await using (var completed = fixture.AdminDataSource.CreateCommand(
                """
                SELECT i.request_hash,i.response_status,i.response_body::text,i.resource_id,i.expires_at,
                       q.expires_at,
                       (SELECT count(*) FROM pricing.quotes WHERE owner_org_id=@org),
                       (SELECT count(*) FROM locations.locations WHERE owner_org_id=@org),
                       (SELECT count(*) FROM platform.idempotency_keys
                          WHERE owner_org_id=@org AND scope='PRC-001:CREATE_QUOTE' AND idempotency_key=@key),
                       (SELECT count(*) FROM platform.audit_logs
                          WHERE org_id=@org AND action='LOCATION_CREATED')
                FROM platform.idempotency_keys i
                JOIN pricing.quotes q ON q.id=i.resource_id
                WHERE i.owner_org_id=@org AND i.scope='PRC-001:CREATE_QUOTE' AND i.idempotency_key=@key;
                """))
            {
                completed.Parameters.Add(P("org", data.OrganizationId));
                completed.Parameters.Add(new NpgsqlParameter<string>("key", NpgsqlDbType.Text) { TypedValue = key });
                await using var reader = await completed.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(originalHash, reader.GetFieldValue<byte[]>(0));
                Assert.Equal(201, reader.GetInt32(1));
                using var response = JsonDocument.Parse(reader.GetString(2));
                Assert.Equal(resumed.Id, response.RootElement.GetProperty("id").GetGuid());
                Assert.Equal(resumed.Id, reader.GetGuid(3));
                Assert.Equal(reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(4));
                Assert.Equal(1, reader.GetInt64(6));
                Assert.Equal(2, reader.GetInt64(7));
                Assert.Equal(1, reader.GetInt64(8));
                Assert.Equal(2, reader.GetInt64(9));
                Assert.False(await reader.ReadAsync());
            }

            scope.LocationResolver.Reset();
            var replay = await scope.Service.CreateAsync(original, default);
            Assert.Equal(resumed.Id, replay.Id);
            Assert.Equal(0, scope.LocationResolver.CallCount);

            scope.LocationResolver.Reset();
            var completedConflict = await Assert.ThrowsAsync<QuoteValidationException>(() => scope.Service.CreateAsync(changed, default));
            Assert.Equal(QuoteValidationCode.IdempotencyConflict, completedConflict.Code);
            Assert.Equal(0, scope.LocationResolver.CallCount);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [PostgreSqlContractFact]
    public async Task Pending_reservation_conflicts_for_individual_package_service_or_coordinate_changes_before_resolver()
    {
        var data = SyntheticPricingData.Create();
        await SeedAsync(data);
        await using var appDataSource = fixture.CreateAppDataSource(maxPoolSize: 8, applicationName: "PRC001.Def001.Variants");
        try
        {
            var changes = new Func<CreateQuoteCommand, CreateQuoteCommand>[]
            {
                command => command with
                {
                    Packages = [command.Packages[0] with { Description = "Changed package only" }],
                },
                command => command with { ServiceType = "URGENT" },
                command => command with { Origin = command.Origin with { Lat = 24.82 } },
            };

            for (var index = 0; index < changes.Length; index++)
            {
                var key = $"prc001-def001-variant-{index:000}";
                await using var scope = CreateScope(appDataSource, new RejectingQuoteLocationResolver());
                var original = CreateCommand(data, key);
                var rejected = await Assert.ThrowsAsync<QuoteValidationException>(() => scope.Service.CreateAsync(original, default));
                Assert.Equal(QuoteValidationCode.OutsideCoverage, rejected.Code);
                Assert.Equal(2, scope.LocationResolver.CallCount);

                scope.LocationResolver.Reset();
                var conflict = await Assert.ThrowsAsync<QuoteValidationException>(() => scope.Service.CreateAsync(changes[index](original), default));
                Assert.Equal(QuoteValidationCode.IdempotencyConflict, conflict.Code);
                Assert.Equal(0, scope.LocationResolver.CallCount);
                await AssertPendingReservationAsync(
                    data,
                    key,
                    PostgreSqlQuoteService.ComputeInputHash(original),
                    expectedLocations: 0,
                    expectedAudits: 0);
            }
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [PostgreSqlContractFact]
    public async Task Cancellation_after_preflight_keeps_incomplete_non_sensitive_reservation()
    {
        var data = SyntheticPricingData.Create();
        const string key = "prc001-def001-cancel-0001";
        await SeedAsync(data);
        await using var appDataSource = fixture.CreateAppDataSource(maxPoolSize: 4, applicationName: "PRC001.Def001.Cancel");
        using var cancellation = new CancellationTokenSource();
        try
        {
            await using var scope = CreateScope(appDataSource, new CancelingQuoteLocationResolver(cancellation));
            var command = CreateCommand(data, key);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Service.CreateAsync(command, cancellation.Token));
            Assert.Equal(1, scope.LocationResolver.CallCount);
            await AssertPendingReservationAsync(
                data,
                key,
                PostgreSqlQuoteService.ComputeInputHash(command),
                expectedLocations: 0,
                expectedAudits: 0);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [PostgreSqlContractFact]
    public async Task Transient_preflight_retry_does_not_duplicate_reservation()
    {
        var data = SyntheticPricingData.Create();
        const string key = "prc001-def001-retry-0001";
        var interceptor = new FailFirstAdvisoryLockInterceptor();
        await SeedAsync(data);
        await using var appDataSource = fixture.CreateAppDataSource(maxPoolSize: 4, applicationName: "PRC001.Def001.Retry");
        try
        {
            await using var scope = CreateScope(
                appDataSource,
                new RejectingQuoteLocationResolver(),
                interceptor);
            var command = CreateCommand(data, key);
            var rejected = await Assert.ThrowsAsync<QuoteValidationException>(() => scope.Service.CreateAsync(command, default));
            Assert.Equal(QuoteValidationCode.OutsideCoverage, rejected.Code);
            Assert.Equal(2, interceptor.Attempts);
            await AssertPendingReservationAsync(
                data,
                key,
                PostgreSqlQuoteService.ComputeInputHash(command),
                expectedLocations: 0,
                expectedAudits: 0);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_same_key_creates_one_quote_and_two_locations()
    {
        var data = SyntheticPricingData.Create();
        await SeedAsync(data);
        await using var appDataSource = fixture.CreateAppDataSource(maxPoolSize: 16, applicationName: "PRC001.Concurrent.Contract");
        try
        {
            var tasks = Enumerable.Range(0, 8).Select(async _ =>
            {
                await using var scope = CreateScope(appDataSource);
                return await scope.Service.CreateAsync(CreateCommand(data, "prc001-contract-concurrent-01"), default);
            });
            var results = await Task.WhenAll(tasks);
            Assert.Single(results.Select(result => result.Id).Distinct());

            await using var counts = fixture.AdminDataSource.CreateCommand(
                """
                SELECT
                  (SELECT count(*) FROM pricing.quotes WHERE owner_org_id=@org),
                  (SELECT count(*) FROM locations.locations WHERE owner_org_id=@org),
                  (SELECT count(*) FROM platform.idempotency_keys WHERE owner_org_id=@org AND scope='PRC-001:CREATE_QUOTE');
                """);
            counts.Parameters.Add(new NpgsqlParameter<Guid>("org", NpgsqlDbType.Uuid) { TypedValue = data.OrganizationId });
            await using var reader = await counts.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal(2, reader.GetInt64(1));
            Assert.Equal(1, reader.GetInt64(2));
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_different_hashes_choose_one_winner_before_location_side_effects()
    {
        var data = SyntheticPricingData.Create();
        const string key = "prc001-def001-concurrent-hash";
        await SeedAsync(data);
        await using var appDataSource = fixture.CreateAppDataSource(maxPoolSize: 16, applicationName: "PRC001.Def001.HashRace");
        try
        {
            var first = CreateCommand(data, key);
            var second = first with
            {
                Packages = [first.Packages[0] with { Description = "Competing synthetic package" }],
            };
            var tasks = Enumerable.Range(0, 8).Select(async index =>
            {
                await using var scope = CreateScope(appDataSource);
                try
                {
                    return (Result: await scope.Service.CreateAsync(index % 2 == 0 ? first : second, default), Error: (QuoteValidationCode?)null);
                }
                catch (QuoteValidationException exception)
                {
                    return (Result: (QuoteResult?)null, Error: (QuoteValidationCode?)exception.Code);
                }
            });
            var outcomes = await Task.WhenAll(tasks);
            var successes = outcomes.Where(outcome => outcome.Result is not null).Select(outcome => outcome.Result!).ToArray();
            var conflicts = outcomes.Where(outcome => outcome.Error == QuoteValidationCode.IdempotencyConflict).ToArray();
            Assert.NotEmpty(successes);
            Assert.NotEmpty(conflicts);
            Assert.Single(successes.Select(result => result.Id).Distinct());
            Assert.Equal(8, successes.Length + conflicts.Length);

            await using var counts = fixture.AdminDataSource.CreateCommand(
                """
                SELECT
                  (SELECT count(*) FROM pricing.quotes WHERE owner_org_id=@org),
                  (SELECT count(*) FROM locations.locations WHERE owner_org_id=@org),
                  (SELECT count(*) FROM platform.idempotency_keys
                     WHERE owner_org_id=@org AND scope='PRC-001:CREATE_QUOTE' AND idempotency_key=@key),
                  request_hash
                FROM platform.idempotency_keys
                WHERE owner_org_id=@org AND scope='PRC-001:CREATE_QUOTE' AND idempotency_key=@key;
                """);
            counts.Parameters.Add(P("org", data.OrganizationId));
            counts.Parameters.Add(new NpgsqlParameter<string>("key", NpgsqlDbType.Text) { TypedValue = key });
            await using var reader = await counts.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal(2, reader.GetInt64(1));
            Assert.Equal(1, reader.GetInt64(2));
            var winningHash = reader.GetFieldValue<byte[]>(3);
            Assert.True(
                winningHash.SequenceEqual(PostgreSqlQuoteService.ComputeInputHash(first)) ||
                winningHash.SequenceEqual(PostgreSqlQuoteService.ComputeInputHash(second)));
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [PostgreSqlContractFact]
    public async Task Different_tenants_can_reuse_same_key_without_pooling_leak()
    {
        var first = SyntheticPricingData.Create();
        var second = SyntheticPricingData.Create();
        const string key = "prc001-def001-shared-tenant-key";
        await SeedAsync(first);
        await SeedAsync(second);
        await using var appDataSource = fixture.CreateAppDataSource(maxPoolSize: 1, applicationName: "PRC001.Def001.TenantPool");
        try
        {
            QuoteResult firstQuote;
            await using (var firstScope = CreateScope(appDataSource))
            {
                firstQuote = await firstScope.Service.CreateAsync(CreateCommand(first, key), default);
            }

            QuoteResult secondQuote;
            await using (var secondScope = CreateScope(appDataSource))
            {
                secondQuote = await secondScope.Service.CreateAsync(CreateCommand(second, key), default);
            }

            Assert.NotEqual(firstQuote.Id, secondQuote.Id);
            await using var counts = fixture.AdminDataSource.CreateCommand(
                """
                SELECT count(*),count(DISTINCT owner_org_id)
                FROM platform.idempotency_keys
                WHERE scope='PRC-001:CREATE_QUOTE' AND idempotency_key=@key
                  AND owner_org_id IN (@first,@second);
                """);
            counts.Parameters.Add(new NpgsqlParameter<string>("key", NpgsqlDbType.Text) { TypedValue = key });
            counts.Parameters.Add(P("first", first.OrganizationId));
            counts.Parameters.Add(P("second", second.OrganizationId));
            await using var reader = await counts.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2, reader.GetInt64(0));
            Assert.Equal(2, reader.GetInt64(1));
        }
        finally
        {
            await CleanupAsync(first);
            await CleanupAsync(second);
        }
    }

    [PostgreSqlContractFact]
    public async Task Private_client_tariff_is_read_only_exact_and_cross_tenant_safe()
    {
        var data = SyntheticPricingData.Create();
        var privateRuleId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var foreignOrganizationId = Guid.NewGuid();
        var foreignAccountId = Guid.NewGuid();
        await SeedAsync(data);
        await using (var seed = fixture.AdminDataSource.CreateCommand(
            """
            INSERT INTO pricing.tariff_rules(
              id,owner_org_id,city_id,service_area_id,operating_zone_id,pricing_tier,service_type,
              amount_cents,tax_mode,active_from,active_to,status)
              VALUES (@rule,@org,@city,@area,@zone,'CUSTOM','SAME_DAY',45678,'EXEMPT',@active_from,NULL,'ACTIVE');
            INSERT INTO clients.client_accounts(id,owner_org_id,name,status,private_tariff_id,created_at)
              VALUES (@account,@org,'Synthetic private account','ACTIVE',@rule,@created);
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
              VALUES (@foreign_org,'PRC-001 Foreign Synthetic Legal','PRC-001 Foreign Synthetic','BUSINESS');
            INSERT INTO clients.client_accounts(id,owner_org_id,name,status,private_tariff_id,created_at)
              VALUES (@foreign_account,@foreign_org,'Synthetic foreign account','ACTIVE',@rule,@created);
            """))
        {
            seed.Parameters.Add(P("rule", privateRuleId));
            seed.Parameters.Add(P("org", data.OrganizationId));
            seed.Parameters.Add(P("city", data.CityId));
            seed.Parameters.Add(P("area", data.AreaId));
            seed.Parameters.Add(P("zone", data.ZoneId));
            seed.Parameters.Add(P("account", accountId));
            seed.Parameters.Add(P("foreign_org", foreignOrganizationId));
            seed.Parameters.Add(P("foreign_account", foreignAccountId));
            seed.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created", NpgsqlDbType.TimestampTz) { TypedValue = DateTimeOffset.UtcNow.AddMinutes(-5) });
            seed.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("active_from", NpgsqlDbType.TimestampTz) { TypedValue = DateTimeOffset.UtcNow.AddDays(-1) });
            await seed.ExecuteNonQueryAsync();
        }

        await using var appDataSource = fixture.CreateAppDataSource(maxPoolSize: 8, applicationName: "PRC001.Private.Contract");
        try
        {
            await using var scope = CreateScope(appDataSource);
            var created = await scope.Service.CreateAsync(
                CreateCommand(data, "prc001-private-tariff-0001") with { ClientAccountId = accountId },
                default);
            Assert.Equal("CUSTOM", created.PricingTier);
            Assert.Equal(45_678, created.Total.AmountCents);
            Assert.Equal([privateRuleId], created.RuleIds);

            var crossTenant = CreateCommand(data, "prc001-private-cross-tenant") with { ClientAccountId = foreignAccountId };
            var rejected = await Assert.ThrowsAsync<QuoteValidationException>(() => scope.Service.CreateAsync(crossTenant, default));
            Assert.Equal(QuoteValidationCode.ClientAccountUnavailable, rejected.Code);
        }
        finally
        {
            await using (var dependents = fixture.AdminDataSource.CreateCommand(
                """
                DELETE FROM platform.idempotency_keys WHERE owner_org_id=@org;
                DELETE FROM pricing.quotes WHERE owner_org_id=@org;
                """))
            {
                dependents.Parameters.Add(P("org", data.OrganizationId));
                await dependents.ExecuteNonQueryAsync();
            }

            await using (var clients = fixture.AdminDataSource.CreateCommand(
                """
                DELETE FROM clients.client_accounts WHERE id IN (@account,@foreign_account);
                DELETE FROM organizations.organizations WHERE id=@foreign_org;
                """))
            {
                clients.Parameters.Add(P("account", accountId));
                clients.Parameters.Add(P("foreign_account", foreignAccountId));
                clients.Parameters.Add(P("foreign_org", foreignOrganizationId));
                await clients.ExecuteNonQueryAsync();
            }

            await CleanupAsync(data);
        }
    }

    [PostgreSqlContractFact]
    public async Task Pricing_and_idempotency_RLS_are_forced_fail_closed_and_cross_tenant_safe()
    {
        await using (var catalog = fixture.AdminDataSource.CreateCommand(
            """
            SELECT n.nspname,c.relname,c.relrowsecurity,c.relforcerowsecurity
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE (n.nspname,c.relname) IN (('pricing','tariff_rules'),('pricing','quotes'),('platform','idempotency_keys'))
            ORDER BY n.nspname,c.relname;
            """))
        await using (var reader = await catalog.ExecuteReaderAsync())
        {
            var rows = new List<(string Schema, string Table, bool Enabled, bool Forced)>();
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
            Assert.Equal(3, rows.Count);
            Assert.All(rows, row => Assert.True(row.Enabled && row.Forced, $"{row.Schema}.{row.Table}"));
        }

        await using var empty = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            Guid.NewGuid(),
            []);
        foreach (var table in new[] { "pricing.tariff_rules", "pricing.quotes", "platform.idempotency_keys" })
        {
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", empty.Connection, empty.Transaction);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }

        await using var role = fixture.AdminDataSource.CreateCommand(
            """
            SELECT rolname,rolbypassrls FROM pg_roles WHERE rolname IN ('paqueteria_app','paqueteria_worker') ORDER BY rolname;
            """);
        await using var roleReader = await role.ExecuteReaderAsync();
        while (await roleReader.ReadAsync()) Assert.False(roleReader.GetBoolean(1));
    }

    [PostgreSqlContractFact]
    public async Task Pricing_rows_reject_cross_tenant_reads_and_mutations()
    {
        var data = SyntheticPricingData.Create();
        var foreignOrganizationId = Guid.NewGuid();
        await SeedAsync(data);
        await using var appDataSource = fixture.CreateAppDataSource(maxPoolSize: 6, applicationName: "PRC001.Rls.Contract");
        try
        {
            QuoteResult created;
            await using (var scope = CreateScope(appDataSource))
            {
                created = await scope.Service.CreateAsync(CreateCommand(data, "prc001-cross-tenant-0001"), default);
            }

            await using (var organization = fixture.AdminDataSource.CreateCommand(
                "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@org,'PRC-001 RLS Foreign Legal','PRC-001 RLS Foreign','BUSINESS')"))
            {
                organization.Parameters.Add(P("org", foreignOrganizationId));
                await organization.ExecuteNonQueryAsync();
            }

            await using (var foreign = await TenantTransaction.BeginAsync(
                appDataSource,
                "paqueteria_app",
                data.ActorId,
                [foreignOrganizationId]))
            {
                foreach (var table in new[] { "pricing.tariff_rules", "pricing.quotes", "platform.idempotency_keys" })
                {
                    await using var select = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE owner_org_id=@org", foreign.Connection, foreign.Transaction);
                    select.Parameters.Add(P("org", data.OrganizationId));
                    Assert.Equal(0L, await select.ExecuteScalarAsync());

                    await using var update = new NpgsqlCommand($"UPDATE {table} SET owner_org_id=owner_org_id WHERE owner_org_id=@org", foreign.Connection, foreign.Transaction);
                    update.Parameters.Add(P("org", data.OrganizationId));
                    Assert.Equal(0, await update.ExecuteNonQueryAsync());

                    await using var delete = new NpgsqlCommand($"DELETE FROM {table} WHERE owner_org_id=@org", foreign.Connection, foreign.Transaction);
                    delete.Parameters.Add(P("org", data.OrganizationId));
                    Assert.Equal(0, await delete.ExecuteNonQueryAsync());
                }

                await foreign.CommitAsync();
            }

            await AssertCrossTenantInsertRejectedAsync(
                appDataSource,
                data.ActorId,
                foreignOrganizationId,
                """
                INSERT INTO pricing.tariff_rules(
                  id,owner_org_id,city_id,service_area_id,operating_zone_id,pricing_tier,service_type,
                  amount_cents,tax_mode,active_from,active_to,status)
                VALUES (@id,@owner,@city,NULL,NULL,'OCCASIONAL','SAME_DAY',1,'EXEMPT',@now,NULL,'ACTIVE')
                """,
                P("id", Guid.NewGuid()), P("owner", data.OrganizationId), P("city", data.CityId),
                new NpgsqlParameter<DateTimeOffset>("now", NpgsqlDbType.TimestampTz) { TypedValue = DateTimeOffset.UtcNow });

            await AssertCrossTenantInsertRejectedAsync(
                appDataSource,
                data.ActorId,
                foreignOrganizationId,
                """
                INSERT INTO platform.idempotency_keys(
                  owner_org_id,scope,idempotency_key,request_hash,response_status,response_body,resource_id,created_at,expires_at)
                VALUES (@owner,'PRC-001:CREATE_QUOTE','prc001-cross-insert-key',@hash,NULL,NULL,NULL,@now,@expires)
                """,
                P("owner", data.OrganizationId),
                new NpgsqlParameter<byte[]>("hash", NpgsqlDbType.Bytea) { TypedValue = new byte[32] },
                new NpgsqlParameter<DateTimeOffset>("now", NpgsqlDbType.TimestampTz) { TypedValue = DateTimeOffset.UtcNow },
                new NpgsqlParameter<DateTimeOffset>("expires", NpgsqlDbType.TimestampTz) { TypedValue = DateTimeOffset.UtcNow.AddMinutes(30) });

            await AssertCrossTenantInsertRejectedAsync(
                appDataSource,
                data.ActorId,
                foreignOrganizationId,
                """
                INSERT INTO pricing.quotes(
                  id,owner_org_id,client_account_id,city_id,service_area_id,origin_location_id,destination_location_id,
                  service_type,pricing_tier,consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,
                  minimum_total_cents_snapshot,currency,pricing_policy_version,rule_ids,request_snapshot_redacted,
                  package_snapshot,pii_snapshot_ciphertext,pii_key_version,breakdown,input_hash,financial_override,status,
                  expires_at,consumed_at,created_at)
                VALUES (@id,@owner,NULL,@city,@area,@origin,@destination,'SAME_DAY','OCCASIONAL',false,
                  34567,0,0,34567,34567,'MXN','PRC-001-v1',@rules,'{}'::jsonb,'[]'::jsonb,NULL,NULL,'[]'::jsonb,
                  @hash,NULL,'ACTIVE',@expires,NULL,@now)
                """,
                P("id", Guid.NewGuid()), P("owner", data.OrganizationId), P("city", data.CityId), P("area", data.AreaId),
                P("origin", created.OriginLocationId), P("destination", created.DestinationLocationId),
                new NpgsqlParameter<Guid[]>("rules", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = [data.ZoneRuleId] },
                new NpgsqlParameter<byte[]>("hash", NpgsqlDbType.Bytea) { TypedValue = new byte[32] },
                new NpgsqlParameter<DateTimeOffset>("expires", NpgsqlDbType.TimestampTz) { TypedValue = DateTimeOffset.UtcNow.AddMinutes(30) },
                new NpgsqlParameter<DateTimeOffset>("now", NpgsqlDbType.TimestampTz) { TypedValue = DateTimeOffset.UtcNow });
        }
        finally
        {
            await CleanupAsync(data);
            await using var foreign = fixture.AdminDataSource.CreateCommand("DELETE FROM organizations.organizations WHERE id=@org");
            foreign.Parameters.Add(P("org", foreignOrganizationId));
            await foreign.ExecuteNonQueryAsync();
        }
    }

    [PostgreSqlContractFact]
    public async Task Quote_and_idempotency_insert_roll_back_together()
    {
        var data = SyntheticPricingData.Create();
        var rolledBackQuoteId = Guid.NewGuid();
        const string rolledBackKey = "prc001-rollback-key-0001";
        await SeedAsync(data);
        await using var appDataSource = fixture.CreateAppDataSource(maxPoolSize: 4, applicationName: "PRC001.Rollback.Contract");
        try
        {
            QuoteResult source;
            await using (var scope = CreateScope(appDataSource))
            {
                source = await scope.Service.CreateAsync(CreateCommand(data, "prc001-rollback-source-01"), default);
            }

            await using (var transaction = await TenantTransaction.BeginAsync(
                appDataSource,
                "paqueteria_app",
                data.ActorId,
                [data.OrganizationId]))
            {
                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO pricing.quotes(
                      id,owner_org_id,client_account_id,city_id,service_area_id,origin_location_id,destination_location_id,
                      service_type,pricing_tier,consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,
                      minimum_total_cents_snapshot,currency,pricing_policy_version,rule_ids,request_snapshot_redacted,
                      package_snapshot,pii_snapshot_ciphertext,pii_key_version,breakdown,input_hash,financial_override,status,
                      expires_at,consumed_at,created_at)
                    SELECT @new_id,owner_org_id,client_account_id,city_id,service_area_id,origin_location_id,destination_location_id,
                      service_type,pricing_tier,consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,
                      minimum_total_cents_snapshot,currency,pricing_policy_version,rule_ids,request_snapshot_redacted,
                      package_snapshot,pii_snapshot_ciphertext,pii_key_version,breakdown,@hash,financial_override,status,
                      expires_at,consumed_at,@now
                    FROM pricing.quotes WHERE id=@source_id;
                    INSERT INTO platform.idempotency_keys(
                      owner_org_id,scope,idempotency_key,request_hash,response_status,response_body,resource_id,created_at,expires_at)
                    VALUES (@owner,'PRC-001:CREATE_QUOTE',@key,@hash,201,'{}'::jsonb,@new_id,@now,@expires);
                    """,
                    transaction.Connection,
                    transaction.Transaction);
                command.Parameters.Add(P("new_id", rolledBackQuoteId));
                command.Parameters.Add(P("source_id", source.Id));
                command.Parameters.Add(P("owner", data.OrganizationId));
                command.Parameters.Add(new NpgsqlParameter<string>("key", NpgsqlDbType.Text) { TypedValue = rolledBackKey });
                command.Parameters.Add(new NpgsqlParameter<byte[]>("hash", NpgsqlDbType.Bytea) { TypedValue = new byte[32] });
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", NpgsqlDbType.TimestampTz) { TypedValue = DateTimeOffset.UtcNow });
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("expires", NpgsqlDbType.TimestampTz) { TypedValue = DateTimeOffset.UtcNow.AddMinutes(30) });
                Assert.Equal(2, await command.ExecuteNonQueryAsync());
            }

            await using var count = fixture.AdminDataSource.CreateCommand(
                """
                SELECT
                  (SELECT count(*) FROM pricing.quotes WHERE id=@id),
                  (SELECT count(*) FROM platform.idempotency_keys WHERE owner_org_id=@org AND idempotency_key=@key)
                """);
            count.Parameters.Add(P("id", rolledBackQuoteId));
            count.Parameters.Add(P("org", data.OrganizationId));
            count.Parameters.Add(new NpgsqlParameter<string>("key", NpgsqlDbType.Text) { TypedValue = rolledBackKey });
            await using var reader = await count.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0, reader.GetInt64(0));
            Assert.Equal(0, reader.GetInt64(1));
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [PostgreSqlContractFact]
    public async Task Canonical_pricing_storage_uses_bigint_uuid_arrays_jsonb_bytea_and_coherence_checks()
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              (SELECT data_type FROM information_schema.columns WHERE table_schema='pricing' AND table_name='quotes' AND column_name='total_cents'),
              (SELECT udt_name FROM information_schema.columns WHERE table_schema='pricing' AND table_name='quotes' AND column_name='rule_ids'),
              (SELECT data_type FROM information_schema.columns WHERE table_schema='pricing' AND table_name='quotes' AND column_name='breakdown'),
              (SELECT data_type FROM information_schema.columns WHERE table_schema='pricing' AND table_name='quotes' AND column_name='input_hash'),
              (SELECT string_agg(pg_get_constraintdef(oid),' ') FROM pg_constraint WHERE conrelid='pricing.quotes'::regclass AND contype='c');
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("bigint", reader.GetString(0));
        Assert.Equal("_uuid", reader.GetString(1));
        Assert.Equal("jsonb", reader.GetString(2));
        Assert.Equal("bytea", reader.GetString(3));
        var checks = reader.GetString(4);
        Assert.Contains("total_cents", checks, StringComparison.Ordinal);
        Assert.Contains("minimum_total_cents_snapshot", checks, StringComparison.Ordinal);
        Assert.Contains("consolidated_route", checks, StringComparison.Ordinal);
        Assert.Contains("financial_override", checks, StringComparison.Ordinal);

        var insertSource = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "Pricing", "Pricing.Infrastructure", "Quotes", "PostgreSqlQuoteService.cs"));
        var insert = insertSource[insertSource.IndexOf("INSERT INTO pricing.quotes", StringComparison.Ordinal)..insertSource.IndexOf("AddQuoteParameters", StringComparison.Ordinal)];
        Assert.DoesNotContain("RETURNING", insert, StringComparison.OrdinalIgnoreCase);
    }

    private QuoteRuntimeScope CreateScope(
        NpgsqlDataSource dataSource,
        IQuoteLocationResolver? resolverOverride = null,
        DbCommandInterceptor? pricingInterceptor = null)
    {
        var locationState = new TenantDatabaseExecutionState();
        var locationOptions = new DbContextOptionsBuilder<LocationsDbContext>()
            .UseNpgsql(dataSource, postgres => postgres.UseNetTopologySuite())
            .AddInterceptors(new TenantTransactionGuardInterceptor(locationState), new TenantSaveChangesGuardInterceptor(locationState))
            .Options;
        var locations = new LocationsDbContext(locationOptions, locationState);
        var locationService = new PostgreSqlLocationService(
            new TenantTransactionContext<LocationsDbContext>(locations, locationState),
            new ManualGeocodingProvider(),
            new DeterministicMockLocationPiiProtector(),
            new PostgreSqlAppendOnlyAuditWriter(locationState),
            new SystemClock());
        var countingResolver = new CountingQuoteLocationResolver(resolverOverride ?? locationService);

        var pricingState = new TenantDatabaseExecutionState();
        var pricingOptionsBuilder = new DbContextOptionsBuilder<PricingDbContext>()
            .UseNpgsql(dataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(new TenantTransactionGuardInterceptor(pricingState), new TenantSaveChangesGuardInterceptor(pricingState));
        if (pricingInterceptor is not null)
        {
            pricingOptionsBuilder.AddInterceptors(pricingInterceptor);
        }

        var pricing = new PricingDbContext(pricingOptionsBuilder.Options, pricingState);
        var service = new PostgreSqlQuoteService(
            new TenantTransactionContext<PricingDbContext>(pricing, pricingState),
            countingResolver,
            new AuditPayloadRedactor(),
            Options.Create(new PricingOptions
            {
                Provider = PricingProviderKind.PostgreSql,
                QuoteLifetimeMinutes = 30,
                CommandTimeoutSeconds = 30,
                PricingPolicyVersion = "PRC-001-v1",
            }),
            new SystemClock(),
            NullLogger<PostgreSqlQuoteService>.Instance);
        return new QuoteRuntimeScope(locations, pricing, service, countingResolver);
    }

    private async Task SeedAsync(SyntheticPricingData data)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            INSERT INTO identity.users(id,identity_subject,status,created_at)
              VALUES (@actor,@subject,'ACTIVE',@created);
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
              VALUES (@org,'PRC-001 Synthetic Legal','PRC-001 Synthetic','BUSINESS');
            INSERT INTO locations.cities(id,country_code,state_code,name,timezone,status)
              VALUES (@city,'MX','SIN',@city_name,'America/Mazatlan','ACTIVE');
            INSERT INTO locations.service_areas(id,owner_org_id,city_id,name,polygon,status,created_at)
              VALUES (@area,@org,@city,'Synthetic PRC Area',
                ST_GeomFromText('MULTIPOLYGON(((-107.6 24.6,-107.2 24.6,-107.2 25.0,-107.6 25.0,-107.6 24.6)))',4326),
                'ACTIVE',@created);
            INSERT INTO locations.operating_zones(id,owner_org_id,service_area_id,name,zone_type,polygon,status,created_at)
              VALUES (@zone,@org,@area,'Synthetic PRC Core','CORE',
                ST_GeomFromText('MULTIPOLYGON(((-107.5 24.7,-107.3 24.7,-107.3 24.9,-107.5 24.9,-107.5 24.7)))',4326),
                'ACTIVE',@created);
            INSERT INTO pricing.tariff_rules(
              id,owner_org_id,city_id,service_area_id,operating_zone_id,pricing_tier,service_type,
              amount_cents,tax_mode,active_from,active_to,status)
              VALUES
                (@city_rule,@org,@city,NULL,NULL,'OCCASIONAL','SAME_DAY',12345,'EXEMPT',@active_from,NULL,'ACTIVE'),
                (@area_rule,@org,@city,@area,NULL,'OCCASIONAL','SAME_DAY',23456,'EXEMPT',@active_from,NULL,'ACTIVE'),
                (@zone_rule,@org,@city,@area,@zone,'OCCASIONAL','SAME_DAY',34567,'EXEMPT',@active_from,NULL,'ACTIVE');
            """);
        command.Parameters.Add(P("org", data.OrganizationId));
        command.Parameters.Add(P("actor", data.ActorId));
        command.Parameters.Add(new NpgsqlParameter<string>("subject", NpgsqlDbType.Text) { TypedValue = $"prc001|{data.ActorId:N}" });
        command.Parameters.Add(P("city", data.CityId));
        command.Parameters.Add(new NpgsqlParameter<string>("city_name", NpgsqlDbType.Text) { TypedValue = $"Synthetic PRC City {data.CityId:N}" });
        command.Parameters.Add(P("area", data.AreaId));
        command.Parameters.Add(P("zone", data.ZoneId));
        command.Parameters.Add(P("city_rule", data.CityRuleId));
        command.Parameters.Add(P("area_rule", data.AreaRuleId));
        command.Parameters.Add(P("zone_rule", data.ZoneRuleId));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created", NpgsqlDbType.TimestampTz) { TypedValue = DateTimeOffset.UtcNow.AddMinutes(-5) });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("active_from", NpgsqlDbType.TimestampTz) { TypedValue = DateTimeOffset.UtcNow.AddDays(-1) });
        await command.ExecuteNonQueryAsync();
    }

    private async Task CleanupAsync(SyntheticPricingData data)
    {
        await using (var command = fixture.AdminDataSource.CreateCommand(
            """
            DELETE FROM platform.idempotency_keys WHERE owner_org_id=@org;
            DELETE FROM pricing.quotes WHERE owner_org_id=@org;
            DELETE FROM locations.locations WHERE owner_org_id=@org;
            DELETE FROM pricing.tariff_rules WHERE owner_org_id=@org;
            DELETE FROM locations.operating_zones WHERE owner_org_id=@org;
            DELETE FROM locations.service_areas WHERE owner_org_id=@org;
            """))
        {
            command.Parameters.Add(P("org", data.OrganizationId));
            await command.ExecuteNonQueryAsync();
        }

        await using (var migrator = await TenantTransaction.BeginAsync(
            fixture.AdminDataSource,
            "paqueteria_migrator",
            data.ActorId,
            [data.OrganizationId]))
        {
            await using var audit = new NpgsqlCommand(
                "DELETE FROM platform.audit_logs WHERE org_id=@org",
                migrator.Connection,
                migrator.Transaction);
            audit.Parameters.Add(P("org", data.OrganizationId));
            await audit.ExecuteNonQueryAsync();
            await migrator.CommitAsync();
        }

        await using var principals = fixture.AdminDataSource.CreateCommand(
            """
            DELETE FROM locations.cities WHERE id=@city;
            DELETE FROM organizations.organizations WHERE id=@org;
            DELETE FROM identity.users WHERE id=@actor;
            """);
        principals.Parameters.Add(P("org", data.OrganizationId));
        principals.Parameters.Add(P("city", data.CityId));
        principals.Parameters.Add(P("actor", data.ActorId));
        await principals.ExecuteNonQueryAsync();
    }

    private async Task AssertPendingReservationAsync(
        SyntheticPricingData data,
        string key,
        byte[] expectedHash,
        long expectedLocations,
        long expectedAudits)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT i.scope,i.request_hash,i.response_status,i.response_body,i.resource_id,i.created_at,i.expires_at,
                   to_jsonb(i)::text,
                   (SELECT count(*) FROM pricing.quotes WHERE owner_org_id=@org),
                   (SELECT count(*) FROM locations.locations WHERE owner_org_id=@org),
                   (SELECT count(*) FROM platform.idempotency_keys
                      WHERE owner_org_id=@org AND scope='PRC-001:CREATE_QUOTE' AND idempotency_key=@key),
                   (SELECT count(*) FROM platform.audit_logs
                      WHERE org_id=@org AND action='LOCATION_CREATED')
            FROM platform.idempotency_keys i
            WHERE i.owner_org_id=@org AND i.scope='PRC-001:CREATE_QUOTE' AND i.idempotency_key=@key;
            """);
        command.Parameters.Add(P("org", data.OrganizationId));
        command.Parameters.Add(new NpgsqlParameter<string>("key", NpgsqlDbType.Text) { TypedValue = key });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(PostgreSqlQuoteService.IdempotencyScope, reader.GetString(0));
        Assert.Equal(expectedHash, reader.GetFieldValue<byte[]>(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(reader.IsDBNull(3));
        Assert.True(reader.IsDBNull(4));
        var createdAt = reader.GetFieldValue<DateTimeOffset>(5);
        var expiresAt = reader.GetFieldValue<DateTimeOffset>(6);
        Assert.True(expiresAt > createdAt.AddMinutes(30));
        var reservationJson = reader.GetString(7);
        Assert.DoesNotContain("Synthetic origin", reservationJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Synthetic Sender", reservationJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("+52667", reservationJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Synthetic gate", reservationJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Synthetic parcel", reservationJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, reader.GetInt64(8));
        Assert.Equal(expectedLocations, reader.GetInt64(9));
        Assert.Equal(1, reader.GetInt64(10));
        Assert.Equal(expectedAudits, reader.GetInt64(11));
        Assert.False(await reader.ReadAsync());
    }

    private static CreateQuoteCommand CreateCommand(SyntheticPricingData data, string key) => new(
        data.ActorId,
        data.OrganizationId,
        key,
        null,
        new QuoteAddressInput("Synthetic origin avenue 100", "Synthetic Sender", "+526671111111", 24.80, -107.40, "Synthetic gate"),
        new QuoteAddressInput("Synthetic destination avenue 200", "Synthetic Receiver", "+526672222222", 24.81, -107.41, null),
        "SAME_DAY",
        false,
        [new QuotePackageInput("Synthetic parcel", 1000, 5000, 100, 100, 100)],
        "prc001-contract-request");

    private static NpgsqlParameter P(string name, Guid value) =>
        new NpgsqlParameter<Guid>(name, NpgsqlDbType.Uuid) { TypedValue = value };

    private static async Task AssertCrossTenantInsertRejectedAsync(
        NpgsqlDataSource dataSource,
        Guid actorId,
        Guid activeOrganizationId,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var transaction = await TenantTransaction.BeginAsync(
            dataSource,
            "paqueteria_app",
            actorId,
            [activeOrganizationId]);
        await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction.Transaction);
        command.Parameters.AddRange(parameters);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join('/', segments));
    }

    private sealed record SyntheticPricingData(
        Guid ActorId,
        Guid OrganizationId,
        Guid CityId,
        Guid AreaId,
        Guid ZoneId,
        Guid CityRuleId,
        Guid AreaRuleId,
        Guid ZoneRuleId)
    {
        internal static SyntheticPricingData Create() => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
    }

    private sealed class QuoteRuntimeScope(
        LocationsDbContext locations,
        PricingDbContext pricing,
        PostgreSqlQuoteService service,
        CountingQuoteLocationResolver locationResolver) : IAsyncDisposable
    {
        internal PostgreSqlQuoteService Service { get; } = service;
        internal CountingQuoteLocationResolver LocationResolver { get; } = locationResolver;

        public async ValueTask DisposeAsync()
        {
            await pricing.DisposeAsync();
            await locations.DisposeAsync();
        }
    }

    private sealed class CountingQuoteLocationResolver(IQuoteLocationResolver inner) : IQuoteLocationResolver
    {
        private int callCount;

        internal int CallCount => Volatile.Read(ref callCount);

        internal void Reset() => Interlocked.Exchange(ref callCount, 0);

        public Task<ResolveQuoteLocationResult> ResolveAsync(
            ResolveQuoteLocationCommand command,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            return inner.ResolveAsync(command, cancellationToken);
        }
    }

    private sealed class RejectingQuoteLocationResolver : IQuoteLocationResolver
    {
        public Task<ResolveQuoteLocationResult> ResolveAsync(
            ResolveQuoteLocationCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ResolveQuoteLocationResult(
                QuoteLocationResolutionStatus.OutsideCoverage,
                null));
        }
    }

    private sealed class CancelingQuoteLocationResolver(CancellationTokenSource cancellation) : IQuoteLocationResolver
    {
        public Task<ResolveQuoteLocationResult> ResolveAsync(
            ResolveQuoteLocationCommand command,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled<ResolveQuoteLocationResult>(cancellation.Token);
        }
    }

    private sealed class FailFirstAdvisoryLockInterceptor : DbCommandInterceptor
    {
        private int attempts;

        internal int Attempts => Volatile.Read(ref attempts);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal) &&
                Interlocked.Increment(ref attempts) == 1)
            {
                throw new NpgsqlException("Synthetic PRC-001 preflight transient failure.", new TimeoutException());
            }

            return ValueTask.FromResult(result);
        }
    }
}
