using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Database.Pooling;
using Paqueteria.IntegrationTests.Security;
using Realtime.Application.Configuration;
using Realtime.Infrastructure.Dispatching;

namespace Paqueteria.IntegrationTests.Realtime;

/// <summary>
/// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the in-process dispatchers are ready only with their own login, as
/// <c>paqueteria_realtime</c> (NOBYPASSRLS) with the six lease-token functions executable, and never with a login that
/// can also assume any other role or that has a privileged attribute (the provisioned shape is
/// <c>LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS</c>, member of that role only).
/// </summary>
[Collection(RealtimePostgreSqlKestrelCollection.Name)]
public sealed class RealtimeOutboxReadinessPostgreSqlTests(
    PostgreSqlSecurityWebApplicationFactory database)
{
    [Fact]
    public async Task Complete_realtime_role_and_six_permissions_are_healthy_without_outbox_effects()
    {
        var before = await database.ReadOutboxStateSnapshotAsync();

        var result = await CheckAsync(database.RealtimeConnectionString);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(before, await database.ReadOutboxStateSnapshotAsync());
    }

    [Theory]
    [InlineData("security.claim_realtime_outbox(text,integer,interval)")]
    [InlineData("security.settle_outbox(uuid,uuid,text,text,timestamptz)")]
    [InlineData("security.requeue_stale_realtime_outbox(interval,integer,integer)")]
    [InlineData("security.claim_location_outbox(text,integer,interval)")]
    [InlineData("security.settle_location_outbox(uuid,uuid,text,text,timestamptz)")]
    [InlineData("security.requeue_stale_location_outbox(interval,integer,integer)")]
    public async Task Missing_any_required_execute_permission_is_unhealthy_without_outbox_effects(
        string signature)
    {
        var before = await database.ReadOutboxStateSnapshotAsync();
        await database.SetRealtimeOutboxFunctionExecuteAsync(signature, granted: false);
        try
        {
            var result = await CheckAsync(database.RealtimeConnectionString);

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal(before, await database.ReadOutboxStateSnapshotAsync());
        }
        finally
        {
            await database.SetRealtimeOutboxFunctionExecuteAsync(signature, granted: true);
        }
    }

    [Fact]
    public async Task Realtime_role_with_bypassrls_is_unhealthy_without_outbox_effects()
    {
        var before = await database.ReadOutboxStateSnapshotAsync();
        await database.SetRealtimeBypassRlsAsync(enabled: true);
        try
        {
            var result = await CheckAsync(database.RealtimeConnectionString);

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal(before, await database.ReadOutboxStateSnapshotAsync());
        }
        finally
        {
            await database.SetRealtimeBypassRlsAsync(enabled: false);
        }
    }

    [Fact]
    public async Task Login_that_cannot_assume_the_realtime_role_is_unhealthy_without_effects()
    {
        var before = await database.ReadOutboxStateSnapshotAsync();
        await database.SetRealtimeRoleMembershipAsync(granted: false);
        try
        {
            var result = await CheckAsync(database.RealtimeConnectionString);

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal(before, await database.ReadOutboxStateSnapshotAsync());
        }
        finally
        {
            await database.SetRealtimeRoleMembershipAsync(granted: true);
        }
    }

    [Fact]
    public async Task The_worker_credential_is_never_ready_for_the_realtime_dispatcher()
    {
        // The Worker login is not a member of paqueteria_realtime, so SET LOCAL ROLE fails before any statement.
        var before = await database.ReadOutboxStateSnapshotAsync();

        var result = await CheckAsync(database.WorkerConnectionString);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(before, await database.ReadOutboxStateSnapshotAsync());
    }

    /// <summary>
    /// Every other role AI-18 declares: the two runtime roles, the migrator, bootstrap, maintenance, every executor and
    /// the master data loader. A login that is also a member of any of them could SET ROLE into it from the API process.
    /// </summary>
    [Theory]
    [InlineData("paqueteria_worker")]
    [InlineData("paqueteria_app")]
    [InlineData("paqueteria_migrator")]
    [InlineData("paqueteria_bootstrap")]
    [InlineData("paqueteria_outbox_executor")]
    [InlineData("paqueteria_maintenance")]
    [InlineData("paqueteria_lifecycle_executor")]
    [InlineData("paqueteria_cleanup_executor")]
    [InlineData("paqueteria_registration_executor")]
    [InlineData("paqueteria_session_executor")]
    [InlineData("paqueteria_master_data_executor")]
    [InlineData("paqueteria_master_data_loader")]
    [InlineData("paqueteria_operator_outbox_executor")]
    [InlineData("paqueteria_auto_close_executor")]
    [InlineData("paqueteria_notification_receipt_executor")]
    public async Task A_realtime_login_that_can_also_assume_another_role_is_unhealthy(string role)
    {
        var before = await database.ReadOutboxStateSnapshotAsync();
        await database.SetRealtimeLoginRoleMembershipAsync(role, granted: true);
        try
        {
            var result = await CheckAsync(database.RealtimeConnectionString);

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal(before, await database.ReadOutboxStateSnapshotAsync());
        }
        finally
        {
            await database.SetRealtimeLoginRoleMembershipAsync(role, granted: false);
        }

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(database.RealtimeConnectionString)).Status);
    }

    [Fact]
    public async Task A_realtime_role_that_is_a_member_of_another_role_is_unhealthy()
    {
        // The login reaches paqueteria_worker through paqueteria_realtime; the membership check follows the chain.
        var before = await database.ReadOutboxStateSnapshotAsync();
        await database.SetRealtimeLoginRoleMembershipAsync(
            "paqueteria_worker",
            granted: true,
            member: "paqueteria_realtime");
        try
        {
            var result = await CheckAsync(database.RealtimeConnectionString);

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal(before, await database.ReadOutboxStateSnapshotAsync());
        }
        finally
        {
            await database.SetRealtimeLoginRoleMembershipAsync(
                "paqueteria_worker",
                granted: false,
                member: "paqueteria_realtime");
        }

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(database.RealtimeConnectionString)).Status);
    }

    [Theory]
    [InlineData("BYPASSRLS", "NOBYPASSRLS")]
    [InlineData("CREATEROLE", "NOCREATEROLE")]
    [InlineData("INHERIT", "NOINHERIT")]
    [InlineData("SUPERUSER", "NOSUPERUSER")]
    [InlineData("CREATEDB", "NOCREATEDB")]
    [InlineData("REPLICATION", "NOREPLICATION")]
    public async Task A_realtime_login_with_a_privileged_attribute_is_unhealthy(string attribute, string reset)
    {
        var before = await database.ReadOutboxStateSnapshotAsync();
        await database.SetRealtimeLoginAttributeAsync(attribute);
        try
        {
            var result = await CheckAsync(database.RealtimeConnectionString);

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal(before, await database.ReadOutboxStateSnapshotAsync());
        }
        finally
        {
            await database.SetRealtimeLoginAttributeAsync(reset);
        }

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(database.RealtimeConnectionString)).Status);
    }

    [Fact]
    public async Task Disabled_provider_is_degraded_without_opening_or_changing_outbox()
    {
        var before = await database.ReadOutboxStateSnapshotAsync();
        await using var realtimePool = RuntimeDataSource.Create(
            RuntimeDatabaseRole.Realtime, string.Empty, maximumPoolSize: 4);
        var connections = new RealtimeDispatcherConnectionFactory(realtimePool);
        var health = new RealtimeOutboxHealthCheck(
            connections,
            Options.Create(new RealtimeOptions()),
            Options.Create(new OutboxDispatcherOptions
            {
                Provider = OutboxDispatcherProviderKind.Disabled,
            }));

        var result = await health.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(before, await database.ReadOutboxStateSnapshotAsync());
    }

    private static async Task<HealthCheckResult> CheckAsync(string connectionString)
    {
        await using var realtimePool = RuntimeDataSource.Create(
            RuntimeDatabaseRole.Realtime, connectionString, maximumPoolSize: 4);
        var connections = new RealtimeDispatcherConnectionFactory(realtimePool);
        var health = new RealtimeOutboxHealthCheck(
            connections,
            Options.Create(new RealtimeOptions
            {
                Provider = RealtimeProviderKind.SignalR,
                Backplane = RealtimeBackplaneKind.InProcess,
            }),
            Options.Create(new OutboxDispatcherOptions
            {
                Provider = OutboxDispatcherProviderKind.PostgreSql,
            }));
        return await health.CheckHealthAsync(new HealthCheckContext());
    }
}
