using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Identity.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Notifications.Application.Dispatching;
using Notifications.Infrastructure;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Persistence;
using Npgsql;
using Organizations.Infrastructure.Notifications;
using Paqueteria.Infrastructure.Database.Baseline;
using Testcontainers.PostgreSql;

namespace Paqueteria.IntegrationTests.Notifications;

/// <summary>
/// SCL-001 acceptance: two Workers do not duplicate effects. Worker A produces the external effect
/// and dies before it can settle; Worker B recovers the same logical operation through the real
/// processor path and reuses the production idempotency key, so the sink keeps exactly one effect
/// and the durable state still ends successfully settled.
/// </summary>
[Trait("Category", "PostgreSqlIntegration")]
public sealed class Scl001TwoWorkerNoDuplicateEffectTests : IAsyncLifetime
{
    private const string Image = "postgis/postgis:18-3.6@sha256:b410052c6f0d7d37b83cac1369df144e1c843971155dea3317961001704d0a9d";
    private const string ReplicaA = "scl001-replica-a";
    private const string ReplicaB = "scl001-replica-b";

    private readonly string _adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _workerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private PostgreSqlContainer _container = null!;
    private string _adminConnectionString = string.Empty;
    private string _workerConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase("paqueteria_scl001")
            .WithUsername("postgres")
            .WithPassword(_adminPassword)
            .WithCleanUp(true)
            .Build();
        await _container.StartAsync();
        _adminConnectionString = _container.GetConnectionString();
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, _adminConnectionString);
        await ApplyNotificationsMigrationAsync();

        await ExecuteAsync($$"""
            CREATE ROLE paqueteria_scl001_worker LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE
              NOREPLICATION NOBYPASSRLS PASSWORD '{{_workerPassword}}';
            GRANT paqueteria_worker TO paqueteria_scl001_worker;
            """);
        _workerConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Username = "paqueteria_scl001_worker",
            Password = _workerPassword,
            Pooling = true,
            MaxPoolSize = 12,
            ApplicationName = "Paqueteria.SCL001.IntegrationTests",
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task Two_workers_do_not_duplicate_an_external_effect_across_a_failed_settlement()
    {
        var owner = Guid.NewGuid();
        var user = Guid.NewGuid();
        var source = Guid.NewGuid();
        await SeedAsync(owner, user, Guid.NewGuid(), source);
        var sink = new ExternalEffectSink();

        // 1-3. Worker A claims the notification, the provider records the external effect and the
        // replica is lost before it can settle the claim.
        using (var workerA = CreateHost(ReplicaA, sink, crashAfterEffect: true))
        {
            await workerA.StartAsync();
            try
            {
                await WaitAsync(
                    () => Task.FromResult(sink.SendCalls >= 1),
                    "Worker A never reached the provider.");
            }
            finally
            {
                await workerA.StopAsync();
            }
        }

        var sendRequested = await ReadSendRequestedIdAsync(owner);
        Assert.Equal(1, sink.SendCalls);
        Assert.Equal(1, sink.EffectCount);
        Assert.Equal("PROCESSING", await ScalarAsync<string>(
            "SELECT status FROM platform.outbox_events WHERE id=@id",
            Parameter("id", sendRequested)));
        Assert.StartsWith(ReplicaA, await ScalarAsync<string>(
            "SELECT locked_by FROM platform.outbox_events WHERE id=@id",
            Parameter("id", sendRequested)), StringComparison.Ordinal);
        Assert.Equal("PENDING", await ScalarAsync<string>(
            "SELECT status FROM notifications.notifications WHERE source_event_id=@source",
            Parameter("source", source)));

        // 4. The lease of the lost replica expires, so the operation becomes recoverable.
        await ExecuteAsync(
            """
            UPDATE platform.outbox_events
            SET lease_expires_at=clock_timestamp()-interval '1 second'
            WHERE id=@id
            """,
            Parameter("id", sendRequested));

        // 5-8. Worker B processes the same logical operation and settles it.
        using var workerB = CreateHost(ReplicaB, sink, crashAfterEffect: false);
        await workerB.StartAsync();
        try
        {
            await WaitAsync(
                async () => await ScalarAsync<string>(
                    "SELECT status FROM notifications.notifications WHERE source_event_id=@source",
                    Parameter("source", source)) == "SENT",
                "Worker B never settled the recovered operation.");
        }
        finally
        {
            await workerB.StopAsync();
        }

        // 6-7. The same provider idempotency key was reused, so exactly one effect exists.
        Assert.Equal(2, sink.SendCalls);
        Assert.Equal(1, sink.EffectCount);
        Assert.Equal(new[] { ReplicaA, ReplicaB }, sink.Attempts.Select(attempt => attempt.Replica).ToArray());
        Assert.Single(sink.Attempts.Select(attempt => attempt.IdempotencyKey).Distinct(StringComparer.Ordinal));
        Assert.Equal(
            sink.Attempts[0].IdempotencyKey,
            NotificationIdempotencyKey.Create(
                await ScalarAsync<Guid>(
                    "SELECT id FROM notifications.notifications WHERE source_event_id=@source",
                    Parameter("source", source)),
                NotificationTemplate.Key,
                NotificationTemplate.Version,
                NotificationTemplate.Channel));

        // 8. The durable state is successfully settled and no second delivery was recorded.
        Assert.Equal("PROCESSED", await ScalarAsync<string>(
            "SELECT status FROM platform.outbox_events WHERE id=@id",
            Parameter("id", sendRequested)));
        Assert.True(await ScalarAsync<bool>(
            """
            SELECT last_error IS NULL AND processed_at IS NOT NULL
              AND locked_by IS NULL AND lease_token IS NULL
            FROM platform.outbox_events WHERE id=@id
            """,
            Parameter("id", sendRequested)));
        Assert.Equal(NotificationErrorCodes.ProviderAccepted, await ScalarAsync<string>(
            """
            SELECT last_provider_attempt_code
            FROM notifications.notifications WHERE source_event_id=@source
            """,
            Parameter("source", source)));

        // Two claims, but a single applied provider outcome.
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT attempts FROM notifications.notifications WHERE source_event_id=@source",
            Parameter("source", source)));
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT count(*)::integer FROM notifications.notifications WHERE source_event_id=@source",
            Parameter("source", source)));
        Assert.Equal(1, await ScalarAsync<int>(
            """
            SELECT count(*)::integer
            FROM notifications.notification_status_events h
            JOIN notifications.notifications n ON n.id=h.notification_id
            WHERE n.source_event_id=@source AND h.status='SENT'
            """,
            Parameter("source", source)));
    }

    private IHost CreateHost(string workerId, ExternalEffectSink sink, bool crashAfterEffect) =>
        Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:PaqueteriaWorker"] = _workerConnectionString,
                    ["Notifications:Provider"] = "PostgreSql",
                    ["Notifications:WorkerId"] = workerId,
                    ["Notifications:BatchSize"] = "10",
                    ["Notifications:MaximumConcurrency"] = "1",
                    ["Notifications:PollIntervalMilliseconds"] = "25",
                    ["Notifications:LeaseSeconds"] = "30",
                    ["Notifications:MaximumAttempts"] = "5",
                    ["Notifications:RetryBaseSeconds"] = "1",
                    ["Notifications:RetryMaximumSeconds"] = "4",
                    ["Notifications:StaleRecoveryIntervalSeconds"] = "1",
                }))
            .ConfigureServices((context, services) =>
            {
                services.AddOrganizationsNotificationAudienceReader(context.Configuration);
                services.AddIdentityNotificationAudienceReader(context.Configuration);
                services.AddNotificationsInfrastructure(context.Configuration);
                services.RemoveAll<ISyntheticInAppProvider>();
                services.AddSingleton<ISyntheticInAppProvider>(
                    new InstrumentedInAppProvider(sink, workerId, crashAfterEffect));
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

    private async Task SeedAsync(Guid owner, Guid user, Guid order, Guid source) => await ExecuteAsync(
        """
        INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
          VALUES(@owner,'SCL-001 Synthetic','SCL-001 Synthetic','BUSINESS');
        INSERT INTO identity.users(id,identity_subject,status)
          VALUES(@user,'scl001-synthetic-user','ACTIVE');
        INSERT INTO organizations.organization_memberships(
          id,user_id,organization_id,role,status,is_default)
          VALUES(gen_random_uuid(),@user,@owner,'DISPATCHER','ACTIVE',true);
        INSERT INTO platform.outbox_events(
          id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
          payload,priority,status,attempts,available_at,created_at)
        VALUES(@source,@owner,jsonb_build_object('organization_ids',jsonb_build_array(@owner::text)),
          'orders.created','Order',@order,1,
          jsonb_build_object('order_id',@order,'public_id','ORD_abcdefghijklmnopqrstuv','status','DRAFT'),
          50,'PENDING',0,clock_timestamp(),clock_timestamp());
        """,
        Parameter("owner", owner),
        Parameter("user", user),
        Parameter("order", order),
        Parameter("source", source));

    private async Task<Guid> ReadSendRequestedIdAsync(Guid owner) => await ScalarAsync<Guid>(
        """
        SELECT id FROM platform.outbox_events
        WHERE topic='notifications.send-requested' AND owner_org_id=@owner
        """,
        Parameter("owner", owner));

    private static async Task WaitAsync(Func<Task<bool>> condition, string failure)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(failure);
    }

    private async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    private static NpgsqlParameter Parameter(string name, object value) => new(name, value);

    private sealed record EffectAttempt(string Replica, string IdempotencyKey);

    /// <summary>
    /// Counts real external effects rather than claims: a second call carrying an idempotency key
    /// the sink already holds returns the stored receipt without producing a new effect.
    /// </summary>
    private sealed class ExternalEffectSink
    {
        private readonly ConcurrentDictionary<string, string> _effects = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<EffectAttempt> _attempts = new();

        public int SendCalls => _attempts.Count;

        public int EffectCount => _effects.Count;

        public IReadOnlyList<EffectAttempt> Attempts => _attempts.ToArray();

        public string Record(string replica, string idempotencyKey, string receipt)
        {
            _attempts.Enqueue(new EffectAttempt(replica, idempotencyKey));
            return _effects.GetOrAdd(idempotencyKey, receipt);
        }
    }

    private sealed class InstrumentedInAppProvider(
        ExternalEffectSink sink,
        string replica,
        bool crashAfterEffect) : ISyntheticInAppProvider
    {
        public ValueTask<SyntheticInAppResult> SendAsync(
            SyntheticInAppRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The production idempotency-key contract is preserved verbatim.
            if (request.Channel != NotificationTemplate.Channel ||
                request.TemplateKey != NotificationTemplate.Key ||
                request.TemplateVersion != NotificationTemplate.Version ||
                request.IdempotencyKey != NotificationIdempotencyKey.Create(
                    request.NotificationId,
                    request.TemplateKey,
                    request.TemplateVersion,
                    request.Channel))
            {
                throw new NotificationMessageException(NotificationErrorCodes.InvalidPayload);
            }

            var rendered = NotificationTemplateRenderer.Render(request.Body, request.Variables);
            var receipt = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"{request.IdempotencyKey}|{rendered}"))))
                .ToLowerInvariant();
            var stored = sink.Record(replica, request.IdempotencyKey, receipt);
            if (crashAfterEffect)
            {
                // The external effect is durable; this replica dies before it can settle.
                throw new InvalidOperationException("SCL001_REPLICA_LOST_BEFORE_SETTLEMENT");
            }

            return ValueTask.FromResult(new SyntheticInAppResult(
                "SUCCESS",
                NotificationErrorCodes.ProviderAccepted,
                stored));
        }
    }
}
