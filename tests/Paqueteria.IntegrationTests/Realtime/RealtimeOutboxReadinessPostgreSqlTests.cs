using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Paqueteria.IntegrationTests.Security;
using Realtime.Application.Configuration;
using Realtime.Infrastructure.Dispatching;

namespace Paqueteria.IntegrationTests.Realtime;

[Collection(RealtimePostgreSqlKestrelCollection.Name)]
public sealed class RealtimeOutboxReadinessPostgreSqlTests(
    PostgreSqlSecurityWebApplicationFactory database)
{
    [Fact]
    public async Task Complete_worker_role_and_six_permissions_are_healthy_without_outbox_effects()
    {
        var before = await database.ReadOutboxStateSnapshotAsync();

        var result = await CheckAsync();

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
        await database.SetOutboxFunctionExecuteAsync(signature, granted: false);
        try
        {
            var result = await CheckAsync();

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal(before, await database.ReadOutboxStateSnapshotAsync());
        }
        finally
        {
            await database.SetOutboxFunctionExecuteAsync(signature, granted: true);
        }
    }

    [Fact]
    public async Task Worker_role_with_bypassrls_is_unhealthy_without_outbox_effects()
    {
        var before = await database.ReadOutboxStateSnapshotAsync();
        await database.SetWorkerBypassRlsAsync(enabled: true);
        try
        {
            var result = await CheckAsync();

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal(before, await database.ReadOutboxStateSnapshotAsync());
        }
        finally
        {
            await database.SetWorkerBypassRlsAsync(enabled: false);
        }
    }

    [Fact]
    public async Task Login_that_cannot_assume_the_effective_worker_role_is_unhealthy_without_effects()
    {
        var before = await database.ReadOutboxStateSnapshotAsync();
        await database.SetWorkerRoleMembershipAsync(granted: false);
        try
        {
            var result = await CheckAsync();

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal(before, await database.ReadOutboxStateSnapshotAsync());
        }
        finally
        {
            await database.SetWorkerRoleMembershipAsync(granted: true);
        }
    }

    [Fact]
    public async Task Disabled_provider_is_degraded_without_opening_or_changing_outbox()
    {
        var before = await database.ReadOutboxStateSnapshotAsync();
        await using var connections = new RealtimeWorkerConnectionFactory(string.Empty);
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

    private async Task<HealthCheckResult> CheckAsync()
    {
        await using var connections = new RealtimeWorkerConnectionFactory(
            database.WorkerConnectionString);
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
