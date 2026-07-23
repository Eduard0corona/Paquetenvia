using Locations.Application.Locations;
using System.Data.Common;
using Locations.Infrastructure.Geocoding;
using Locations.Infrastructure.Locations;
using Locations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;
using Paqueteria.Application.Auditing;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class LocationsPostGisContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Real_PostGIS_creation_persists_lng_lat_ciphertext_and_append_only_audit_atomically()
    {
        var scenario = await SeedAsync(includeExcludedZone: false);
        try
        {
            await using var dataSource = fixture.CreateAppDataSource(applicationName: "Paqueteria.GEO001.ContractTests");
            var state = new TenantDatabaseExecutionState();
            var capture = new CommandCaptureInterceptor();
            var options = new DbContextOptionsBuilder<LocationsDbContext>()
                .UseNpgsql(dataSource, postgres => postgres.UseNetTopologySuite())
                .AddInterceptors(capture, new TenantTransactionGuardInterceptor(state), new TenantSaveChangesGuardInterceptor(state))
                .Options;
            await using var context = new LocationsDbContext(options, state);
            var service = new PostgreSqlLocationService(
                new TenantTransactionContext<LocationsDbContext>(context, state),
                new ManualGeocodingProvider(),
                new DeterministicMockLocationPiiProtector(),
                new PostgreSqlAppendOnlyAuditWriter(state),
                new SystemClock());

            const string plaintext = "Avenida Universidad 1234, interior 5";
            var result = await service.CreateAsync(new CreateLocationCommand(
                scenario.UserId,
                scenario.OrganizationId,
                $"geo001-{Guid.NewGuid():N}",
                scenario.CityId,
                scenario.ServiceAreaId,
                null,
                plaintext,
                "Centro Chihuahua",
                "Persona Privada",
                "+526141234567",
                28.63,
                -106.07,
                "mock-v1",
                "geo001-contract"), default);

            Assert.Equal(ServiceabilityStatus.Serviceable, result.Status);
            Assert.NotNull(result.Location);
            Assert.Equal(28.63, result.Location.Lat, 6);
            Assert.Equal(-106.07, result.Location.Lng, 6);
            var insert = Assert.Single(capture.Commands,
                sql => sql.Contains("INSERT INTO locations.locations", StringComparison.Ordinal));
            Assert.DoesNotContain("RETURNING", insert, StringComparison.OrdinalIgnoreCase);

            await using var command = fixture.AdminDataSource.CreateCommand(
                """
                SELECT ST_X(l.point), ST_Y(l.point), ST_SRID(l.point),
                       position(encode(convert_to(@plaintext,'UTF8'),'hex') in encode(l.address_ciphertext,'hex')),
                       count(a.id)
                FROM locations.locations l
                LEFT JOIN platform.audit_logs a ON a.entity_id=l.id AND a.action='LOCATION_CREATED'
                WHERE l.id=@location_id
                GROUP BY l.id;
                """);
            command.Parameters.AddWithValue("plaintext", plaintext);
            command.Parameters.AddWithValue("location_id", result.Location.Id);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(-106.07, reader.GetDouble(0), 6);
            Assert.Equal(28.63, reader.GetDouble(1), 6);
            Assert.Equal(4326, reader.GetInt32(2));
            Assert.Equal(0, reader.GetInt32(3));
            Assert.Equal(1L, reader.GetInt64(4));
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task PostGIS_uses_ST_Covers_boundary_convention_and_excluded_zone_precedence()
    {
        var scenario = await SeedAsync(includeExcludedZone: true);
        try
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                """
                SELECT
                  ST_Covers(sa.polygon, ST_SetSRID(ST_MakePoint(-106.10,28.60),4326)) AS boundary_covered,
                  ST_Covers(sa.polygon, ST_SetSRID(ST_MakePoint(-106.07,28.63),4326)) AS interior_covered,
                  ST_Covers(sa.polygon, ST_SetSRID(ST_MakePoint(-105.90,28.63),4326)) AS outside_covered,
                  EXISTS (
                    SELECT 1 FROM locations.operating_zones oz
                    WHERE oz.service_area_id=sa.id AND oz.zone_type='EXCLUDED'
                      AND ST_Covers(oz.polygon, ST_SetSRID(ST_MakePoint(-106.065,28.635),4326))
                  ) AS excluded
                FROM locations.service_areas sa WHERE sa.id=@area_id;
                """);
            command.Parameters.AddWithValue("area_id", scenario.ServiceAreaId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.False(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));

            await using var dataSource = fixture.CreateAppDataSource(applicationName: "Paqueteria.GEO001.Serviceability");
            var state = new TenantDatabaseExecutionState();
            var options = new DbContextOptionsBuilder<LocationsDbContext>()
                .UseNpgsql(dataSource, postgres => postgres.UseNetTopologySuite())
                .AddInterceptors(new TenantTransactionGuardInterceptor(state), new TenantSaveChangesGuardInterceptor(state))
                .Options;
            await using var context = new LocationsDbContext(options, state);
            var evaluator = new PostgreSqlLocationService(
                new TenantTransactionContext<LocationsDbContext>(context, state),
                new ManualGeocodingProvider(),
                new DeterministicMockLocationPiiProtector(),
                new PostgreSqlAppendOnlyAuditWriter(state),
                new SystemClock());

            var serviceable = await evaluator.EvaluateAsync(Command(28.61, -106.09), default);
            var excluded = await evaluator.EvaluateAsync(Command(28.635, -106.065), default);
            var outside = await evaluator.EvaluateAsync(Command(28.63, -105.90), default);
            var wrongArea = await evaluator.EvaluateAsync(
                Command(28.61, -106.09) with { ServiceAreaId = Guid.NewGuid() }, default);

            Assert.Equal(ServiceabilityStatus.Serviceable, serviceable.Status);
            Assert.Equal(scenario.ZoneId, serviceable.OperatingZoneId);
            Assert.Equal(ServiceabilityStatus.ExcludedZone, excluded.Status);
            Assert.Equal(ServiceabilityStatus.OutsideServiceArea, outside.Status);
            Assert.Equal(ServiceabilityStatus.InaccessibleServiceArea, wrongArea.Status);

            EvaluateServiceabilityCommand Command(double latitude, double longitude) => new(
                scenario.UserId,
                scenario.OrganizationId,
                scenario.CityId,
                scenario.ServiceAreaId,
                null,
                latitude,
                longitude);
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Location_catalog_is_tenant_isolated_and_cross_tenant_rows_are_uniformly_absent()
    {
        var scenario = await SeedAsync(includeExcludedZone: false);
        var foreignOrganization = Guid.NewGuid();
        try
        {
            await ExecuteAdminAsync(
                "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@id,'Foreign GEO','Foreign GEO','BUSINESS')",
                new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = foreignOrganization });

            await using var visible = await TenantTransaction.BeginAsync(
                fixture.AppDataSource, "paqueteria_app", scenario.UserId, [scenario.OrganizationId]);
            await using var visibleCommand = new NpgsqlCommand(
                "SELECT count(*) FROM locations.service_areas WHERE id=@id", visible.Connection, visible.Transaction);
            visibleCommand.Parameters.AddWithValue("id", scenario.ServiceAreaId);
            Assert.Equal(1L, await visibleCommand.ExecuteScalarAsync());
            await visible.RollbackAsync();

            await using var hidden = await TenantTransaction.BeginAsync(
                fixture.AppDataSource, "paqueteria_app", scenario.UserId, [foreignOrganization]);
            await using var hiddenCommand = new NpgsqlCommand(
                "SELECT count(*) FROM locations.service_areas WHERE id=@id", hidden.Connection, hidden.Transaction);
            hiddenCommand.Parameters.AddWithValue("id", scenario.ServiceAreaId);
            Assert.Equal(0L, await hiddenCommand.ExecuteScalarAsync());
            await hidden.RollbackAsync();
        }
        finally
        {
            await ExecuteAdminAsync("DELETE FROM organizations.organizations WHERE id=@id",
                new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = foreignOrganization });
            await CleanupAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Locations_adoption_history_is_separate_owned_and_non_destructive()
    {
        await using (var connection = new NpgsqlConnection(fixture.DeploymentConnectionString))
        {
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LocationsDbContext>()
                .UseNpgsql(connection, postgres =>
                {
                    postgres.UseNetTopologySuite();
                    postgres.MigrationsAssembly(typeof(LocationsDbContext).Assembly.FullName);
                    postgres.MigrationsHistoryTable("__ef_migrations_history_locations", "platform");
                }).Options;
            await using var context = new LocationsDbContext(options, new TenantDatabaseExecutionState());
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }

        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT h."MigrationId", pg_get_userbyid(c.relowner)
            FROM platform.__ef_migrations_history_locations h
            JOIN pg_class c ON c.relname='__ef_migrations_history_locations'
            JOIN pg_namespace n ON n.oid=c.relnamespace AND n.nspname='platform';
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("20260722_AdoptCanonicalLocationsBaseline", reader.GetString(0));
        Assert.Equal("paqueteria_migrator", reader.GetString(1));

        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src", "Modules", "Locations", "Locations.Infrastructure", "Persistence", "Migrations",
            "20260722_AdoptCanonicalLocationsBaseline.cs"));
        Assert.DoesNotContain("CreateTable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AlterTable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DropTable", source, StringComparison.Ordinal);
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_idempotent_creation_produces_one_location_and_one_audit()
    {
        var scenario = await SeedAsync(includeExcludedZone: false);
        try
        {
            await using var first = CreateRuntimeScope();
            await using var second = CreateRuntimeScope();
            var key = $"geo001-concurrent-{Guid.NewGuid():N}";
            var command = CreateCommand(scenario, key);

            var results = await Task.WhenAll(
                first.Service.CreateAsync(command, default),
                second.Service.CreateAsync(command, default));

            Assert.All(results, result => Assert.Equal(ServiceabilityStatus.Serviceable, result.Status));
            Assert.Equal(results[0].Location!.Id, results[1].Location!.Id);
            await using var count = fixture.AdminDataSource.CreateCommand(
                """
                SELECT count(*) FILTER (WHERE l.id=@id), count(*) FILTER (WHERE a.entity_id=@id)
                FROM locations.locations l
                FULL JOIN platform.audit_logs a ON a.entity_id=l.id
                WHERE l.id=@id OR a.entity_id=@id;
                """);
            count.Parameters.AddWithValue("id", results[0].Location!.Id);
            await using var reader = await count.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Audit_failure_rolls_back_location_without_leaving_plaintext_or_partial_state()
    {
        var scenario = await SeedAsync(includeExcludedZone: false);
        try
        {
            await using var scope = CreateRuntimeScope(new FailingAuditWriter());
            var command = CreateCommand(scenario, $"geo001-rollback-{Guid.NewGuid():N}");
            await Assert.ThrowsAsync<LocationServiceUnavailableException>(() => scope.Service.CreateAsync(command, default));

            await using var count = fixture.AdminDataSource.CreateCommand(
                "SELECT count(*) FROM locations.locations WHERE owner_org_id=@org_id");
            count.Parameters.AddWithValue("org_id", scenario.OrganizationId);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    private async Task<Scenario> SeedAsync(bool includeExcludedZone)
    {
        var scenario = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var excludedSql = includeExcludedZone
            ? "INSERT INTO locations.operating_zones(id,owner_org_id,service_area_id,name,zone_type,polygon,status) VALUES (@excluded_id,@org_id,@area_id,'Excluded synthetic','EXCLUDED',ST_Multi(ST_GeomFromText('POLYGON((-106.08 28.62,-106.05 28.62,-106.05 28.65,-106.08 28.65,-106.08 28.62))',4326)),'ACTIVE');"
            : string.Empty;
        await ExecuteAdminAsync(
            $$"""
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@org_id,'GEO-001 Synthetic','GEO-001 Synthetic','BUSINESS');
            INSERT INTO identity.users(id,identity_subject) VALUES (@user_id,@subject);
            INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default)
              VALUES (@membership_id,@user_id,@org_id,'BUSINESS_ADMIN','ACTIVE',true);
            INSERT INTO locations.cities(id,country_code,state_code,name,timezone,status)
              VALUES (@city_id,'MX','CHH',@city_name,'America/Chihuahua','ACTIVE');
            INSERT INTO locations.service_areas(id,owner_org_id,city_id,name,polygon,status)
              VALUES (@area_id,@org_id,@city_id,'Synthetic coverage',ST_Multi(ST_GeomFromText('POLYGON((-106.10 28.60,-106.04 28.60,-106.04 28.66,-106.10 28.66,-106.10 28.60))',4326)),'ACTIVE');
            INSERT INTO locations.operating_zones(id,owner_org_id,service_area_id,name,zone_type,polygon,status)
              VALUES (@zone_id,@org_id,@area_id,'Core synthetic','CORE',ST_Multi(ST_GeomFromText('POLYGON((-106.10 28.60,-106.04 28.60,-106.04 28.66,-106.10 28.66,-106.10 28.60))',4326)),'ACTIVE');
            {{excludedSql}}
            """,
            P("org_id", scenario.OrganizationId), P("user_id", scenario.UserId),
            P("membership_id", scenario.MembershipId), P("city_id", scenario.CityId),
            P("area_id", scenario.ServiceAreaId), P("zone_id", scenario.ZoneId),
            P("excluded_id", Guid.NewGuid()),
            new NpgsqlParameter<string>("subject", NpgsqlDbType.Text) { TypedValue = $"geo001|{scenario.UserId:N}" },
            new NpgsqlParameter<string>("city_name", NpgsqlDbType.Text) { TypedValue = $"Synthetic-{scenario.CityId:N}" });
        return scenario;
    }

    private RuntimeScope CreateRuntimeScope(IAppendOnlyAuditWriter? auditWriter = null)
    {
        var dataSource = fixture.CreateAppDataSource(applicationName: "Paqueteria.GEO001.Runtime");
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<LocationsDbContext>()
            .UseNpgsql(dataSource, postgres => postgres.UseNetTopologySuite())
            .AddInterceptors(new TenantTransactionGuardInterceptor(state), new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new LocationsDbContext(options, state);
        var service = new PostgreSqlLocationService(
            new TenantTransactionContext<LocationsDbContext>(context, state),
            new ManualGeocodingProvider(),
            new DeterministicMockLocationPiiProtector(),
            auditWriter ?? new PostgreSqlAppendOnlyAuditWriter(state),
            new SystemClock());
        return new RuntimeScope(dataSource, context, service);
    }

    private static CreateLocationCommand CreateCommand(Scenario scenario, string idempotencyKey) => new(
        scenario.UserId,
        scenario.OrganizationId,
        idempotencyKey,
        scenario.CityId,
        scenario.ServiceAreaId,
        null,
        "Synthetic private address",
        "Synthetic summary",
        "Synthetic contact",
        "+526141234567",
        28.61,
        -106.09,
        "mock-v1",
        "geo001-contract");

    private async Task CleanupAsync(Scenario scenario)
    {
        await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var context = new NpgsqlCommand(
                "SELECT set_config('app.current_user_id', @user_id::uuid::text, true), set_config('app.current_org_ids', @organization_ids::uuid[]::text, true)",
                connection,
                transaction))
            {
                context.Parameters.Add(P("user_id", scenario.UserId));
                context.Parameters.Add(new NpgsqlParameter<Guid[]>("organization_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                {
                    TypedValue = [scenario.OrganizationId],
                });
                await context.ExecuteNonQueryAsync();
            }
            await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_migrator", connection, transaction))
            {
                await role.ExecuteNonQueryAsync();
            }
            await using (var audit = new NpgsqlCommand(
                "DELETE FROM platform.audit_logs WHERE org_id=@org_id", connection, transaction))
            {
                audit.Parameters.Add(P("org_id", scenario.OrganizationId));
                await audit.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        await ExecuteAdminAsync(
            """
            DELETE FROM locations.locations WHERE owner_org_id=@org_id;
            DELETE FROM locations.operating_zones WHERE owner_org_id=@org_id;
            DELETE FROM locations.service_areas WHERE owner_org_id=@org_id;
            DELETE FROM locations.cities WHERE id=@city_id;
            DELETE FROM organizations.organization_memberships WHERE id=@membership_id;
            DELETE FROM identity.users WHERE id=@user_id;
            DELETE FROM organizations.organizations WHERE id=@org_id;
            """,
            P("org_id", scenario.OrganizationId), P("city_id", scenario.CityId),
            P("membership_id", scenario.MembershipId), P("user_id", scenario.UserId));
    }

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlParameter<Guid> P(string name, Guid value) =>
        new(name, NpgsqlDbType.Uuid) { TypedValue = value };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Paqueteria.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed record Scenario(
        Guid OrganizationId,
        Guid UserId,
        Guid MembershipId,
        Guid CityId,
        Guid ServiceAreaId,
        Guid ZoneId);

    private sealed class CommandCaptureInterceptor : DbCommandInterceptor
    {
        internal List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailingAuditWriter : IAppendOnlyAuditWriter
    {
        public Task WriteAsync(
            DbConnection connection,
            DbTransaction transaction,
            AuditEntry entry,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Synthetic redacted audit failure.");
    }

    private sealed class RuntimeScope(
        NpgsqlDataSource dataSource,
        LocationsDbContext context,
        PostgreSqlLocationService service) : IAsyncDisposable
    {
        internal PostgreSqlLocationService Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync();
            await dataSource.DisposeAsync();
        }
    }
}
