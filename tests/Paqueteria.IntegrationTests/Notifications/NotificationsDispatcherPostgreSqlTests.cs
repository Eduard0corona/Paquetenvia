using System.Security.Cryptography;
using Identity.Application.Notifications;
using Identity.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Notifications.Infrastructure;
using Notifications.Infrastructure.Persistence;
using Npgsql;
using Organizations.Application.Notifications;
using Organizations.Infrastructure.Notifications;
using Paqueteria.Infrastructure.Database.Baseline;
using Testcontainers.PostgreSql;

namespace Paqueteria.IntegrationTests.Notifications;

[Trait("Category", "PostgreSqlIntegration")]
public sealed class NotificationsDispatcherPostgreSqlTests : IAsyncLifetime
{
    private const string Image = "postgis/postgis:18-3.6@sha256:b410052c6f0d7d37b83cac1369df144e1c843971155dea3317961001704d0a9d";
    private readonly string _adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _workerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private PostgreSqlContainer _container = null!;
    private string _adminConnectionString = string.Empty;
    private string _workerConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase("paqueteria_ntf001")
            .WithUsername("postgres")
            .WithPassword(_adminPassword)
            .WithCleanUp(true)
            .Build();
        await _container.StartAsync();
        _adminConnectionString = _container.GetConnectionString();
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, _adminConnectionString);
        await ApplyNotificationsMigrationAsync();

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($$"""
            CREATE ROLE paqueteria_ntf001_worker LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE
              NOREPLICATION NOBYPASSRLS PASSWORD '{{_workerPassword}}';
            GRANT paqueteria_worker TO paqueteria_ntf001_worker;
            """, connection);
        await command.ExecuteNonQueryAsync();
        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Username = "paqueteria_ntf001_worker",
            Password = _workerPassword,
            Pooling = true,
            MaxPoolSize = 12,
            ApplicationName = "Paqueteria.NTF001.IntegrationTests",
        };
        _workerConnectionString = builder.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task Orders_created_reaches_synthetic_provider_and_emits_realtime_status_rows()
    {
        var owner = Guid.NewGuid();
        var user = Guid.NewGuid();
        var order = Guid.NewGuid();
        var source = Guid.NewGuid();
        await SeedAsync(owner, user, order, source);
        await AssertAudienceFunctionsAsync(owner, user);
        using var host = CreateHost();
        await host.StartAsync();
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var state = await ReadStateAsync(source, owner, user);
                if (state is { SourceStatus: "PROCESSED", NotificationStatus: "SENT", NotificationAttempts: 1, NotificationVersion: 2,
                    SendStatus: "PROCESSED", HistoryCount: 2, StatusOutboxCount: 2 })
                {
                    Assert.Equal("SOURCE_EXPANDED", state.SourceCode);
                    Assert.Equal("SYNTHETIC_ACCEPTED", state.ProviderCode);
                    Assert.Equal(1, await CountSourceNotificationsAsync(source));
                    return;
                }

                await Task.Delay(100);
            }

            Assert.Fail($"The Notifications lifecycle did not reach terminal success: {await ReadDiagnosticsAsync(source)}");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Zero_audience_is_terminal_success_and_does_not_call_identity_or_provider()
    {
        var owner = Guid.NewGuid();
        var user = Guid.NewGuid();
        var source = Guid.NewGuid();
        await SeedAsync(owner, user, Guid.NewGuid(), source);
        var dispatchers = new CountingDispatcherReader([]);
        var active = new CountingActiveReader([]);
        using var host = CreateHost(configure: services =>
        {
            services.RemoveAll<IOwnerOrganizationDispatcherReader>();
            services.RemoveAll<IActiveNotificationRecipientReader>();
            services.AddSingleton<IOwnerOrganizationDispatcherReader>(dispatchers);
            services.AddSingleton<IActiveNotificationRecipientReader>(active);
        });
        await host.StartAsync();
        try
        {
            await WaitForSourceAsync(source, "PROCESSED", "NO_ELIGIBLE_RECIPIENT");
            Assert.Equal(1, dispatchers.Calls);
            Assert.Equal(0, active.Calls);
            Assert.Equal(0, await CountSourceNotificationsAsync(source));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(true, "AUDIENCE_LIMIT_EXCEEDED")]
    [InlineData(false, "AUDIENCE_CONTRACT_VIOLATION")]
    public async Task Invalid_audience_contracts_fail_closed_without_partial_notifications(
        bool exceedsLimit,
        string expectedCode)
    {
        var owner = Guid.NewGuid();
        var requested = Guid.NewGuid();
        var source = Guid.NewGuid();
        await SeedAsync(owner, requested, Guid.NewGuid(), source);
        var candidates = exceedsLimit
            ? Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray()
            : [requested];
        var dispatchers = new CountingDispatcherReader(candidates);
        var active = new CountingActiveReader(exceedsLimit ? [] : [Guid.NewGuid()]);
        using var host = CreateHost(configure: services =>
        {
            services.RemoveAll<IOwnerOrganizationDispatcherReader>();
            services.RemoveAll<IActiveNotificationRecipientReader>();
            services.AddSingleton<IOwnerOrganizationDispatcherReader>(dispatchers);
            services.AddSingleton<IActiveNotificationRecipientReader>(active);
        });
        await host.StartAsync();
        try
        {
            await WaitForSourceAsync(source, "DEAD", expectedCode);
            Assert.Equal(1, dispatchers.Calls);
            Assert.Equal(exceedsLimit ? 0 : 1, active.Calls);
            Assert.Equal(0, await CountSourceNotificationsAsync(source));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Retry_does_not_resolve_audience_again_and_max_finalization_does_not_call_provider()
    {
        var owner = Guid.NewGuid();
        var user = Guid.NewGuid();
        var source = Guid.NewGuid();
        await SeedAsync(owner, user, Guid.NewGuid(), source);
        var dispatchers = new CountingDispatcherReader([user]);
        var active = new CountingActiveReader([user]);
        using var host = CreateHost(SyntheticInAppOutcome.TransientFailure, maximumAttempts: 1, configure: services =>
        {
            services.RemoveAll<IOwnerOrganizationDispatcherReader>();
            services.RemoveAll<IActiveNotificationRecipientReader>();
            services.AddSingleton<IOwnerOrganizationDispatcherReader>(dispatchers);
            services.AddSingleton<IActiveNotificationRecipientReader>(active);
        });
        await host.StartAsync();
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var state = await ReadStateAsync(source, owner, user);
                if (state is { SourceStatus: "PROCESSED", NotificationStatus: "FAILED", NotificationAttempts: 1,
                    NotificationVersion: 3, SendStatus: "DEAD", HistoryCount: 3, StatusOutboxCount: 3 })
                {
                    Assert.Equal("MAX_ATTEMPTS_EXHAUSTED", state.ProviderCode);
                    Assert.Equal(1, dispatchers.Calls);
                    Assert.Equal(1, active.Calls);
                    return;
                }

                await Task.Delay(50);
            }

            Assert.Fail($"Maximum-attempt finalization did not complete: {await ReadDiagnosticsAsync(source)}");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private IHost CreateHost(
        SyntheticInAppOutcome outcome = SyntheticInAppOutcome.Success,
        int maximumAttempts = 3,
        Action<IServiceCollection>? configure = null) => Host.CreateDefaultBuilder()
        .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:PaqueteriaWorker"] = _workerConnectionString,
                ["Notifications:Provider"] = "PostgreSql",
                ["Notifications:WorkerId"] = "ntf001-integration",
                ["Notifications:BatchSize"] = "10",
                ["Notifications:MaximumConcurrency"] = "2",
                ["Notifications:PollIntervalMilliseconds"] = "25",
                ["Notifications:LeaseSeconds"] = "30",
                ["Notifications:MaximumAttempts"] = maximumAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Notifications:RetryBaseSeconds"] = "1",
                ["Notifications:RetryMaximumSeconds"] = "4",
                ["Notifications:StaleRecoveryIntervalSeconds"] = "1",
                ["Notifications:SyntheticOutcome"] = outcome.ToString(),
            }))
        .ConfigureServices((context, services) =>
        {
            services.AddOrganizationsNotificationAudienceReader(context.Configuration);
            services.AddIdentityNotificationAudienceReader(context.Configuration);
            services.AddNotificationsInfrastructure(context.Configuration);
            configure?.Invoke(services);
        })
        .Build();

    private async Task ApplyNotificationsMigrationAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(NotificationsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_notifications", "platform");
            }).Options;
        await using var context = new NotificationsDbContext(options);
        await context.Database.MigrateAsync();
    }

    private async Task SeedAsync(Guid owner, Guid user, Guid order, Guid source)
    {
        var suspendedMembershipUser = Guid.NewGuid();
        var wrongRoleUser = Guid.NewGuid();
        var inactiveUser = Guid.NewGuid();
        var foreignUser = Guid.NewGuid();
        var foreignOwner = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
              VALUES(@owner,'NTF-001 Synthetic','NTF-001 Synthetic','BUSINESS');
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
              VALUES(@foreign_owner,'NTF-001 Foreign','NTF-001 Foreign','BUSINESS');
            INSERT INTO identity.users(id,identity_subject,status) VALUES
              (@user,'ntf001-synthetic-user','ACTIVE'),
              (@suspended_membership_user,'ntf001-suspended-membership','ACTIVE'),
              (@wrong_role_user,'ntf001-wrong-role','ACTIVE'),
              (@inactive_user,'ntf001-inactive-user','SUSPENDED'),
              (@foreign_user,'ntf001-foreign-user','ACTIVE');
            INSERT INTO organizations.organization_memberships(
              id,user_id,organization_id,role,status,is_default) VALUES
              (gen_random_uuid(),@user,@owner,'DISPATCHER','ACTIVE',true),
              (gen_random_uuid(),@suspended_membership_user,@owner,'DISPATCHER','SUSPENDED',false),
              (gen_random_uuid(),@wrong_role_user,@owner,'VIEWER','ACTIVE',true),
              (gen_random_uuid(),@inactive_user,@owner,'DISPATCHER','ACTIVE',true),
              (gen_random_uuid(),@foreign_user,@foreign_owner,'DISPATCHER','ACTIVE',true);
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
              payload,priority,status,attempts,available_at,created_at)
            VALUES(@source,@owner,jsonb_build_object('organization_ids',jsonb_build_array(@owner::text)),
              'orders.created','Order',@order,1,
              jsonb_build_object('order_id',@order,'public_id','ORD_abcdefghijklmnopqrstuv','status','DRAFT'),
              50,'PENDING',0,clock_timestamp(),clock_timestamp());
            """,
            connection);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("user", user);
        command.Parameters.AddWithValue("suspended_membership_user", suspendedMembershipUser);
        command.Parameters.AddWithValue("wrong_role_user", wrongRoleUser);
        command.Parameters.AddWithValue("inactive_user", inactiveUser);
        command.Parameters.AddWithValue("foreign_user", foreignUser);
        command.Parameters.AddWithValue("foreign_owner", foreignOwner);
        command.Parameters.AddWithValue("order", order);
        command.Parameters.AddWithValue("source", source);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountSourceNotificationsAsync(Guid source)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::integer FROM notifications.notifications WHERE source_event_id=@source",
            connection);
        command.Parameters.AddWithValue("source", source);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task WaitForSourceAsync(Guid source, string status, string code)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT status,last_error FROM platform.outbox_events WHERE id=@source",
                connection);
            command.Parameters.AddWithValue("source", source);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync() && reader.GetString(0) == status && reader.GetString(1) == code)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Source did not reach {status}:{code}: {await ReadDiagnosticsAsync(source)}");
    }

    private async Task AssertAudienceFunctionsAsync(Guid owner, Guid user)
    {
        await using var connection = new NpgsqlConnection(_workerConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT d.user_id
            FROM security.read_owner_dispatcher_ids(@owner,501) d
            JOIN security.read_active_notification_users(ARRAY[d.user_id]::uuid[]) a ON a.user_id=d.user_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("owner", owner);
        Assert.Equal(user, await command.ExecuteScalarAsync());
        await transaction.CommitAsync();
    }

    private async Task<LifecycleState?> ReadStateAsync(Guid source, Guid owner, Guid user)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT s.status,s.last_error,n.status,n.attempts,n.version,n.last_provider_attempt_code,
              (SELECT status FROM platform.outbox_events
               WHERE topic='notifications.send-requested' AND aggregate_id=n.id),
              (SELECT count(*)::integer FROM notifications.notification_status_events h
               WHERE h.notification_id=n.id),
              (SELECT count(*)::integer FROM platform.outbox_events o
               WHERE o.topic='notifications.status-changed' AND o.aggregate_id=n.id)
            FROM platform.outbox_events s
            LEFT JOIN notifications.notifications n
              ON n.source_event_id=s.id AND n.owner_org_id=@owner AND n.recipient_user_id=@user
            WHERE s.id=@source;
            """,
            connection);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("user", user);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.IsDBNull(2))
        {
            return null;
        }

        return new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.GetInt32(8));
    }

    private async Task<string> ReadDiagnosticsAsync(Guid source)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT concat_ws(':',status,coalesce(last_error,'NULL'),attempts::text,
              (SELECT count(*)::text FROM notifications.notifications WHERE source_event_id=@source),
              (SELECT count(*)::text FROM platform.outbox_events WHERE topic='notifications.send-requested'),
              (SELECT count(*)::text FROM platform.outbox_events WHERE topic='notifications.status-changed'))
            FROM platform.outbox_events WHERE id=@source
            """,
            connection);
        command.Parameters.AddWithValue("source", source);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            ?? "source-missing";
    }

    private sealed record LifecycleState(
        string SourceStatus,
        string? SourceCode,
        string NotificationStatus,
        int NotificationAttempts,
        int NotificationVersion,
        string? ProviderCode,
        string SendStatus,
        int HistoryCount,
        int StatusOutboxCount);

    private sealed class CountingDispatcherReader(IReadOnlyList<Guid> values)
        : IOwnerOrganizationDispatcherReader
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<Guid>> ReadActiveDispatcherUserIdsAsync(
            Guid ownerOrganizationId,
            int limit,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(values);
        }
    }

    private sealed class CountingActiveReader(IReadOnlyList<Guid> values)
        : IActiveNotificationRecipientReader
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<Guid>> ReadActiveUserIdsAsync(
            IReadOnlyCollection<Guid> requestedUserIds,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(values);
        }
    }
}
