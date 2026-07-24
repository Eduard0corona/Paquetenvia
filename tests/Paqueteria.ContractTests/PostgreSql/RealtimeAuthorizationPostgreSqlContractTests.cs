using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Tracking;
using Organizations.Application.Auditing;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Domain.Tenancy;
using Realtime.Application.Authorization;
using Realtime.Application.Configuration;
using Realtime.Infrastructure.Authorization;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class RealtimeAuthorizationPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [PostgreSqlContractFact]
    public async Task Operations_authorization_rechecks_current_tenant_membership_role_MFA_and_pool_state()
    {
        var scenario = await SeedOperationsAsync();
        var audit = new RecordingAudit();
        try
        {
            await using var dataSource = fixture.CreateAppDataSource(
                maxPoolSize: 1,
                applicationName: "Paqueteria.RTM001.Operations");
            var authorizer = CreateAuthorizer(dataSource, audit);
            var request = Request(scenario.UserId, scenario.OrganizationId, mfaSatisfied: false);

            var dispatcher = await authorizer.AuthorizeOperationsAsync(request, default);
            Assert.True(dispatcher.IsAuthorized);
            Assert.Equal(OrganizationRole.Dispatcher, dispatcher.Authorization?.Role);
            Assert.Empty(audit.Records);

            var crossTenant = await authorizer.AuthorizeOperationsAsync(
                request with { OrganizationId = Guid.NewGuid() },
                default);
            Assert.False(crossTenant.IsAuthorized);

            await ExecuteAdminAsync(
                "UPDATE organizations.organization_memberships SET status='SUSPENDED' WHERE id=@id",
                Uuid("id", scenario.MembershipId));
            Assert.False((await authorizer.AuthorizeOperationsAsync(request, default)).IsAuthorized);

            await ExecuteAdminAsync(
                """
                UPDATE organizations.organization_memberships
                SET status='ACTIVE',role='PLATFORM_ADMIN'
                WHERE id=@id
                """,
                Uuid("id", scenario.MembershipId));
            Assert.False((await authorizer.AuthorizeOperationsAsync(request, default)).IsAuthorized);

            var platformAdmin = await authorizer.AuthorizeOperationsAsync(
                request with { MfaSatisfied = true },
                default);
            Assert.True(platformAdmin.IsAuthorized);
            Assert.Equal(OrganizationRole.PlatformAdmin, platformAdmin.Authorization?.Role);
            Assert.Single(audit.Records);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await authorizer.AuthorizeOperationsAsync(request, cancellation.Token));

            Assert.True((await authorizer.AuthorizeOperationsAsync(
                request with { MfaSatisfied = true },
                default)).IsAuthorized);
        }
        finally
        {
            await CleanupOperationsAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Driver_authorization_rechecks_own_active_profile_and_fails_closed_cross_tenant()
    {
        var scenario = await SeedDriverAsync();
        try
        {
            await using var dataSource = fixture.CreateAppDataSource(
                maxPoolSize: 1,
                applicationName: "Paqueteria.RTM001.Driver");
            var authorizer = CreateAuthorizer(dataSource, new RecordingAudit());
            var request = Request(scenario.UserId, scenario.OrganizationId, mfaSatisfied: false);

            var authorized = await authorizer.AuthorizeDriverAsync(request, default);
            Assert.True(authorized.IsAuthorized);
            Assert.Equal(scenario.DriverId, authorized.Authorization?.DriverId);
            Assert.Empty(authorized.Authorization?.AssignmentIds ?? []);

            Assert.False((await authorizer.AuthorizeDriverAsync(
                request with { UserId = Guid.NewGuid() },
                default)).IsAuthorized);
            Assert.False((await authorizer.AuthorizeDriverAsync(
                request with { OrganizationId = Guid.NewGuid() },
                default)).IsAuthorized);

            await ExecuteAdminAsync(
                "UPDATE drivers.driver_profiles SET status='INACTIVE' WHERE id=@id",
                Uuid("id", scenario.DriverId));
            Assert.False((await authorizer.AuthorizeDriverAsync(request, default)).IsAuthorized);

            await ExecuteAdminAsync(
                "UPDATE drivers.driver_profiles SET status='ACTIVE' WHERE id=@id",
                Uuid("id", scenario.DriverId));
            Assert.True((await authorizer.AuthorizeDriverAsync(request, default)).IsAuthorized);
        }
        finally
        {
            await CleanupDriverAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Transient_retry_reopens_transaction_and_reapplies_tenant_context()
    {
        var userId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        await using var dataSource = fixture.CreateAppDataSource(
            maxPoolSize: 1,
            applicationName: "Paqueteria.RTM001.Retry");
        var authorizer = CreateAuthorizer(dataSource, new RecordingAudit());
        var attempts = 0;

        var observedContext = await authorizer.ExecuteWithRetryAsync(
            Request(userId, organizationId, mfaSatisfied: false),
            async (connection, transaction, cancellationToken) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new SyntheticTransientNpgsqlException();
                }

                await using var command = new NpgsqlCommand(
                    """
                    SELECT current_setting('app.current_user_id'),
                           current_setting('app.current_org_ids'),
                           current_user
                    """,
                    connection,
                    transaction);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                Assert.True(await reader.ReadAsync(cancellationToken));
                return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
            },
            default);

        Assert.Equal(2, attempts);
        Assert.Equal(userId.ToString("D"), observedContext.Item1);
        Assert.Equal($"{{{organizationId:D}}}", observedContext.Item2);
        Assert.Equal("paqueteria_app", observedContext.Item3);
    }

    private PostgreSqlRealtimeConnectionAuthorizer CreateAuthorizer(
        NpgsqlDataSource dataSource,
        IPlatformAdminTenantActivationAudit audit) =>
        new(
            dataSource,
            new RejectingTrackingReader(),
            audit,
            Options.Create(new RealtimeOptions
            {
                Provider = RealtimeProviderKind.SignalR,
                Backplane = RealtimeBackplaneKind.InProcess,
                AllowedOrigins = ["https://web.synthetic.local"],
                AuthorizationRetryCount = 2,
            }),
            NullLogger<PostgreSqlRealtimeConnectionAuthorizer>.Instance);

    private static PrivateRealtimeConnectionRequest Request(
        Guid userId,
        Guid organizationId,
        bool mfaSatisfied) =>
        new(userId, organizationId, mfaSatisfied, "rtm-001-contract");

    private async Task<OperationsScenario> SeedOperationsAsync()
    {
        var scenario = new OperationsScenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations
              (id,legal_name,display_name,organization_type)
            VALUES (@org,'RTM Operations','RTM Operations','BUSINESS');
            INSERT INTO identity.users(id,identity_subject,status,created_at)
            VALUES (@user,@subject,'ACTIVE',@created);
            INSERT INTO organizations.organization_memberships
              (id,user_id,organization_id,role,status,is_default,granted_at)
            VALUES (@membership,@user,@org,'DISPATCHER','ACTIVE',true,@created);
            """,
            Uuid("org", scenario.OrganizationId),
            Uuid("user", scenario.UserId),
            Text("subject", $"rtm-operations-{scenario.UserId:N}"),
            Timestamp("created", CreatedAt),
            Uuid("membership", scenario.MembershipId));
        return scenario;
    }

    private async Task<DriverScenario> SeedDriverAsync()
    {
        var scenario = new DriverScenario(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
        await ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations
              (id,legal_name,display_name,organization_type)
            VALUES (@org,'RTM Driver','RTM Driver','BUSINESS');
            INSERT INTO identity.users(id,identity_subject,status,created_at)
            VALUES (@user,@subject,'ACTIVE',@created);
            INSERT INTO organizations.organization_memberships
              (id,user_id,organization_id,role,status,is_default,granted_at)
            VALUES (@membership,@user,@org,'DRIVER','ACTIVE',true,@created);
            INSERT INTO locations.cities(id,country_code,state_code,name,timezone,status)
            VALUES (@city,'MX','CHH',@city_name,'America/Chihuahua','ACTIVE');
            INSERT INTO drivers.driver_profiles
              (id,user_id,org_id,home_city_id,driver_type,vehicle_type,status,created_at)
            VALUES (@driver,@user,@org,@city,'OWN','MOTORCYCLE','ACTIVE',@created);
            """,
            Uuid("org", scenario.OrganizationId),
            Uuid("user", scenario.UserId),
            Text("subject", $"rtm-driver-{scenario.UserId:N}"),
            Timestamp("created", CreatedAt),
            Uuid("membership", scenario.MembershipId),
            Uuid("city", scenario.CityId),
            Text("city_name", $"RTM-{scenario.CityId:N}"),
            Uuid("driver", scenario.DriverId));
        return scenario;
    }

    private async Task CleanupOperationsAsync(OperationsScenario scenario) =>
        await ExecuteAdminAsync(
            """
            DELETE FROM organizations.organization_memberships WHERE id=@membership;
            DELETE FROM identity.users WHERE id=@user;
            DELETE FROM organizations.organizations WHERE id=@org;
            """,
            Uuid("membership", scenario.MembershipId),
            Uuid("user", scenario.UserId),
            Uuid("org", scenario.OrganizationId));

    private async Task CleanupDriverAsync(DriverScenario scenario) =>
        await ExecuteAdminAsync(
            """
            DELETE FROM drivers.driver_profiles WHERE id=@driver;
            DELETE FROM organizations.organization_memberships WHERE id=@membership;
            DELETE FROM identity.users WHERE id=@user;
            DELETE FROM locations.cities WHERE id=@city;
            DELETE FROM organizations.organizations WHERE id=@org;
            """,
            Uuid("driver", scenario.DriverId),
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

    private sealed record OperationsScenario(Guid OrganizationId, Guid UserId, Guid MembershipId);

    private sealed record DriverScenario(
        Guid OrganizationId,
        Guid UserId,
        Guid MembershipId,
        Guid CityId,
        Guid DriverId);

    private sealed class RecordingAudit : IPlatformAdminTenantActivationAudit
    {
        internal List<(Guid UserId, Guid OrganizationId)> Records { get; } = [];

        public Task RecordAsync(
            Guid actorUserId,
            Guid organizationId,
            string? requestId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Add((actorUserId, organizationId));
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingTrackingReader : IPublicTrackingProjectionReader
    {
        public ValueTask<PublicTrackingLookupResult> FindAsync(
            string token,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(PublicTrackingLookupResult.NotFound);
    }

    private sealed class SyntheticTransientNpgsqlException : NpgsqlException
    {
        public override bool IsTransient => true;
    }
}
