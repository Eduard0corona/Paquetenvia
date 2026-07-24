using Drivers.Application.Eligibility;
using Drivers.Infrastructure;
using Drivers.Infrastructure.Eligibility;
using Drivers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class DriversEligibilityPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly DateTimeOffset EvaluatedAt =
        new(2026, 7, 23, 20, 0, 0, TimeSpan.Zero);

    [PostgreSqlContractFact]
    public async Task PostgreSql_service_evaluates_profile_membership_area_latest_document_and_capacity()
    {
        var scenario = await SeedAsync();
        try
        {
            await using var dataSource = fixture.CreateAppDataSource(
                maxPoolSize: 1,
                applicationName: "Paqueteria.DSP001.Eligibility");
            var service = CreateService(dataSource);
            var command = Command(scenario);

            var eligible = await service.EvaluateAsync(command, default);
            Assert.True(eligible.IsEligible);
            Assert.Equal(scenario.DriverId, eligible.DriverId);
            Assert.Equal("MOTORCYCLE", eligible.VehicleType);
            Assert.Empty(eligible.Rejections);

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => service.EvaluateAsync(command, cancellation.Token));
            }
            Assert.True((await service.EvaluateAsync(command, default)).IsEligible);

            await ExecuteAdminAsync(
                """
                INSERT INTO drivers.driver_documents
                  (id,driver_id,org_id,document_type,object_key,sha256,expires_at,status,created_at)
                VALUES
                  (@id,@driver,@org,'IDENTITY','synthetic/newer',decode(repeat('bb',32),'hex'),
                   @expires,'REVOKED',@created);
                """,
                Uuid("id", Guid.NewGuid()),
                Uuid("driver", scenario.DriverId),
                Uuid("org", scenario.OrganizationId),
                Timestamp("expires", EvaluatedAt.AddDays(30)),
                Timestamp("created", EvaluatedAt.AddMinutes(1)));

            var revokedLatest = await service.EvaluateAsync(command, default);
            Assert.Equal(
                [DriverEligibilityRejectionCodes.DocumentStatusNotValid],
                revokedLatest.Rejections.Select(rejection => rejection.Code));

            var foreign = await service.EvaluateAsync(
                command with { OrganizationId = Guid.NewGuid() },
                default);
            Assert.Equal(
                [DriverEligibilityRejectionCodes.DriverUnavailable],
                foreign.Rejections.Select(rejection => rejection.Code));
            Assert.Null(foreign.DriverId);
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Driver_RLS_is_forced_fail_closed_and_blocks_cross_tenant_mutations()
    {
        var scenario = await SeedAsync();
        var foreignOrganizationId = Guid.NewGuid();
        try
        {
            await ExecuteAdminAsync(
                """
                INSERT INTO organizations.organizations
                  (id,legal_name,display_name,organization_type)
                VALUES (@id,'Foreign DSP','Foreign DSP','BUSINESS');
                """,
                Uuid("id", foreignOrganizationId));

            await using (var empty = await TenantTransaction.BeginAsync(
                fixture.AppDataSource, "paqueteria_app", scenario.UserId, []))
            {
                await using var command = new NpgsqlCommand(
                    "SELECT count(*) FROM drivers.driver_profiles",
                    empty.Connection,
                    empty.Transaction);
                Assert.Equal(0L, await command.ExecuteScalarAsync());
                await empty.RollbackAsync();
            }

            await using (var foreign = await TenantTransaction.BeginAsync(
                fixture.AppDataSource, "paqueteria_app", scenario.UserId, [foreignOrganizationId]))
            {
                await using var select = new NpgsqlCommand(
                    "SELECT count(*) FROM drivers.driver_profiles WHERE id=@id",
                    foreign.Connection,
                    foreign.Transaction);
                select.Parameters.AddWithValue("id", scenario.DriverId);
                Assert.Equal(0L, await select.ExecuteScalarAsync());

                await using var update = new NpgsqlCommand(
                    "UPDATE drivers.driver_profiles SET status='INACTIVE' WHERE id=@id",
                    foreign.Connection,
                    foreign.Transaction);
                update.Parameters.AddWithValue("id", scenario.DriverId);
                Assert.Equal(0, await update.ExecuteNonQueryAsync());

                await using var delete = new NpgsqlCommand(
                    "DELETE FROM drivers.driver_documents WHERE driver_id=@id",
                    foreign.Connection,
                    foreign.Transaction);
                delete.Parameters.AddWithValue("id", scenario.DriverId);
                Assert.Equal(0, await delete.ExecuteNonQueryAsync());
                await foreign.RollbackAsync();
            }

            await using (var crossTenantInsert = await TenantTransaction.BeginAsync(
                fixture.AppDataSource, "paqueteria_app", scenario.UserId, [scenario.OrganizationId]))
            {
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO drivers.driver_documents
                      (id,driver_id,org_id,document_type,object_key,sha256,status,created_at)
                    VALUES (@id,@driver,@foreign,'OTHER','synthetic/forbidden',
                      decode(repeat('cc',32),'hex'),'VALID',@created);
                    """,
                    crossTenantInsert.Connection,
                    crossTenantInsert.Transaction);
                insert.Parameters.AddWithValue("id", Guid.NewGuid());
                insert.Parameters.AddWithValue("driver", scenario.DriverId);
                insert.Parameters.AddWithValue("foreign", foreignOrganizationId);
                insert.Parameters.AddWithValue("created", EvaluatedAt);
                var exception = await Assert.ThrowsAsync<PostgresException>(
                    () => insert.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
            }

            await using var security = fixture.AdminDataSource.CreateCommand(
                """
                SELECT bool_and(c.relrowsecurity AND c.relforcerowsecurity),
                       (SELECT NOT rolbypassrls FROM pg_roles WHERE rolname='paqueteria_app')
                FROM pg_class c
                JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='drivers'
                  AND c.relname IN ('driver_profiles','driver_service_areas','driver_documents');
                """);
            await using var reader = await security.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
        }
        finally
        {
            await ExecuteAdminAsync(
                "DELETE FROM organizations.organizations WHERE id=@id",
                Uuid("id", foreignOrganizationId));
            await CleanupAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Drivers_adoption_history_is_owned_applied_and_has_zero_pending_migrations()
    {
        await using (var connection = new NpgsqlConnection(fixture.DeploymentConnectionString))
        {
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DriversDbContext>()
                .UseNpgsql(connection, postgres =>
                {
                    postgres.MigrationsAssembly(typeof(DriversDbContext).Assembly.FullName);
                    postgres.MigrationsHistoryTable("__ef_migrations_history_drivers", "platform");
                }).Options;
            await using var context = new DriversDbContext(options, new TenantDatabaseExecutionState());
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }

        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT h."MigrationId",pg_get_userbyid(c.relowner)
            FROM platform.__ef_migrations_history_drivers h
            JOIN pg_class c ON c.relname='__ef_migrations_history_drivers'
            JOIN pg_namespace n ON n.oid=c.relnamespace AND n.nspname='platform';
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("20260723_AdoptCanonicalDriversBaseline", reader.GetString(0));
        Assert.Equal("paqueteria_migrator", reader.GetString(1));
        Assert.False(await reader.ReadAsync());
    }

    private PostgreSqlDriverEligibilityService CreateService(NpgsqlDataSource dataSource)
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<DriversDbContext>()
            .UseNpgsql(dataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new DriversDbContext(dbOptions, state);
        return new PostgreSqlDriverEligibilityService(
            new TenantTransactionContext<DriversDbContext>(context, state),
            Options.Create(OptionsValue()));
    }

    private static DriversOptions OptionsValue() => new()
    {
        Provider = DriversProviderKind.PostgreSql,
        Eligibility = new DriverEligibilityOptions
        {
            PolicyVersion = "dsp-contract-v1",
            RequiredDocumentTypesByVehicleType = new(StringComparer.Ordinal)
            {
                ["MOTORCYCLE"] = ["IDENTITY"],
            },
            VehicleCapacity = new(StringComparer.Ordinal)
            {
                ["MOTORCYCLE"] = new VehicleCapacityOptions
                {
                    MaximumPackageCount = 2,
                    MaximumTotalWeightGrams = 2_000,
                    MaximumSinglePackageWeightGrams = 1_000,
                    MaximumLengthMillimeters = 300,
                    MaximumWidthMillimeters = 200,
                    MaximumHeightMillimeters = 150,
                    RequireDimensions = true,
                },
            },
        },
    };

    private static EvaluateOwnDriverEligibilityCommand Command(DriverScenario scenario) => new(
        scenario.UserId,
        scenario.OrganizationId,
        scenario.DriverId,
        scenario.CityId,
        scenario.ServiceAreaId,
        new DriverCapacityRequirement(1, 500, 500, 100, 100, 100),
        EvaluatedAt);

    private async Task<DriverScenario> SeedAsync()
    {
        var scenario = new DriverScenario(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
        await ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations
              (id,legal_name,display_name,organization_type)
            VALUES (@org,'DSP Synthetic','DSP Synthetic','BUSINESS');
            INSERT INTO identity.users(id,identity_subject,status,created_at)
            VALUES (@user,@subject,'ACTIVE',@created);
            INSERT INTO organizations.organization_memberships
              (id,user_id,organization_id,role,status,is_default,granted_at)
            VALUES (@membership,@user,@org,'DRIVER','ACTIVE',true,@created);
            INSERT INTO locations.cities(id,country_code,state_code,name,timezone,status)
            VALUES (@city,'MX','CHH',@city_name,'America/Chihuahua','ACTIVE');
            INSERT INTO locations.service_areas
              (id,owner_org_id,city_id,name,polygon,status,created_at)
            VALUES (
              @area,@org,@city,'DSP Synthetic Area',
              ST_GeomFromText('MULTIPOLYGON(((-106.2 28.5,-106.0 28.5,-106.0 28.7,-106.2 28.7,-106.2 28.5)))',4326),
              'ACTIVE',@created);
            INSERT INTO drivers.driver_profiles
              (id,user_id,org_id,home_city_id,driver_type,vehicle_type,status,created_at)
            VALUES (@driver,@user,@org,@city,'OWN','MOTORCYCLE','ACTIVE',@created);
            INSERT INTO drivers.driver_service_areas(driver_id,service_area_id,org_id,status)
            VALUES (@driver,@area,@org,'ACTIVE');
            INSERT INTO drivers.driver_documents
              (id,driver_id,org_id,document_type,object_key,sha256,expires_at,status,created_at)
            VALUES (
              @document,@driver,@org,'IDENTITY','synthetic/identity',
              decode(repeat('aa',32),'hex'),@expires,'VALID',@created);
            """,
            Uuid("org", scenario.OrganizationId),
            Uuid("user", scenario.UserId),
            Text("subject", $"dsp-{scenario.UserId:N}"),
            Timestamp("created", EvaluatedAt.AddDays(-1)),
            Uuid("membership", scenario.MembershipId),
            Uuid("city", scenario.CityId),
            Text("city_name", $"DSP-{scenario.CityId:N}"),
            Uuid("area", scenario.ServiceAreaId),
            Uuid("driver", scenario.DriverId),
            Uuid("document", scenario.DocumentId),
            Timestamp("expires", EvaluatedAt.AddDays(1)));
        return scenario;
    }

    private async Task CleanupAsync(DriverScenario scenario) => await ExecuteAdminAsync(
        """
        DELETE FROM drivers.driver_documents WHERE driver_id=@driver;
        DELETE FROM drivers.driver_service_areas WHERE driver_id=@driver;
        DELETE FROM drivers.driver_profiles WHERE id=@driver;
        DELETE FROM locations.service_areas WHERE id=@area;
        DELETE FROM organizations.organization_memberships WHERE id=@membership;
        DELETE FROM identity.users WHERE id=@user;
        DELETE FROM locations.cities WHERE id=@city;
        DELETE FROM organizations.organizations WHERE id=@org;
        """,
        Uuid("driver", scenario.DriverId),
        Uuid("area", scenario.ServiceAreaId),
        Uuid("membership", scenario.MembershipId),
        Uuid("user", scenario.UserId),
        Uuid("city", scenario.CityId),
        Uuid("org", scenario.OrganizationId));

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlParameter<Guid> Uuid(string name, Guid value) =>
        new(name, NpgsqlDbType.Uuid) { TypedValue = value };

    private static NpgsqlParameter<string> Text(string name, string value) =>
        new(name, NpgsqlDbType.Text) { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(string name, DateTimeOffset value) =>
        new(name, NpgsqlDbType.TimestampTz) { TypedValue = value };

    private sealed record DriverScenario(
        Guid OrganizationId,
        Guid UserId,
        Guid MembershipId,
        Guid CityId,
        Guid ServiceAreaId,
        Guid DriverId)
    {
        public Guid DocumentId { get; } = Guid.NewGuid();
    }
}
