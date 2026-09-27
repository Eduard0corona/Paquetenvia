extern alias WorkerHost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Paqueteria.IntegrationTests.Realtime;
using Paqueteria.IntegrationTests.Security;
using Realtime.Infrastructure.Dispatching;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.Dispatch;

/// <summary>
/// D8 / D8-OUTBOX-LANE-DISPATCH in the real Worker composition: the Worker drains the DISPATCH lane
/// end-to-end against PostgreSQL as <c>paqueteria_worker</c>, closes the named assignment once, emits
/// the AI12-ASSIGNMENT-TERMINAL-STATES <c>AssignmentChanged</c> row Realtime can prove, and settles.
/// </summary>
[Collection(RealtimePostgreSqlKestrelCollection.Name)]
[Trait("Category", "PostgreSqlIntegration")]
public sealed class AssignmentLifecycleWorkerPostgreSqlTests(PostgreSqlSecurityWebApplicationFactory database)
{
    private const string CheckName = "dispatch_assignment_lifecycle";
    private static readonly Guid OrderId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid OwnerId = PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId;

    [Fact]
    public async Task Worker_is_registered_disabled_by_default_and_ready()
    {
        await using var worker = new DispatchLifecycleWorkerFactory(new()
        {
            ["ConnectionStrings:PaqueteriaWorker"] = database.WorkerConnectionString,
        });

        Assert.Single(
            worker.Services.GetServices<IHostedService>(),
            service => service.GetType().Name == "AssignmentLifecycleDispatcher");
        var report = await worker.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(candidate => candidate.Name == CheckName);
        Assert.Equal(HealthStatus.Healthy, report.Entries[CheckName].Status);
        Assert.Equal("Dispatch lifecycle consumer is disabled.", report.Entries[CheckName].Description);
    }

    [Fact]
    public void Enabling_without_the_worker_connection_string_fails_at_startup()
    {
        using var worker = new DispatchLifecycleWorkerFactory(new()
        {
            ["Dispatch:AssignmentLifecycle:Provider"] = "PostgreSql",
            ["ConnectionStrings:PaqueteriaWorker"] = string.Empty,
        });

        var exception = Assert.ThrowsAny<Exception>(() => worker.Services);
        Assert.Contains("ConnectionStrings:PaqueteriaWorker", Flatten(exception), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_drains_the_dispatch_lane_end_to_end()
    {
        var scenario = await database.CreateAssignmentEvidenceScenarioAsync(assignmentStatus: "ACCEPTED");
        var closing = await CommitClosingEventAsync(scenario.AggregateVersion + 1);
        var reaction = Guid.NewGuid();
        var duplicate = Guid.NewGuid();
        await InsertReactionAsync(reaction, scenario.AssignmentId, closing);
        await InsertReactionAsync(duplicate, scenario.AssignmentId, closing);
        try
        {
            await using var worker = new DispatchLifecycleWorkerFactory(new()
            {
                ["ConnectionStrings:PaqueteriaWorker"] = database.WorkerConnectionString,
                ["Dispatch:AssignmentLifecycle:Provider"] = "PostgreSql",
                ["Dispatch:AssignmentLifecycle:WorkerId"] = "d8-integration",
                ["Dispatch:AssignmentLifecycle:PollIntervalMilliseconds"] = "25",
                ["Dispatch:AssignmentLifecycle:StaleRecoveryIntervalSeconds"] = "1",
            });
            var report = await worker.Services.GetRequiredService<HealthCheckService>()
                .CheckHealthAsync(candidate => candidate.Name == CheckName);
            Assert.Equal(HealthStatus.Healthy, report.Entries[CheckName].Status);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            (string Reaction, string Duplicate, string Assignment) state = default;
            while (DateTimeOffset.UtcNow < deadline)
            {
                state = (
                    await ScalarAsync<string>("SELECT status FROM platform.outbox_events WHERE id=@id", ("id", reaction)),
                    await ScalarAsync<string>("SELECT status FROM platform.outbox_events WHERE id=@id", ("id", duplicate)),
                    await ScalarAsync<string>("SELECT status FROM dispatch.assignments WHERE id=@id", ("id", scenario.AssignmentId)));
                if (state == ("PROCESSED", "PROCESSED", "CANCELLED"))
                {
                    break;
                }

                await Task.Delay(50);
            }

            Assert.Equal(("PROCESSED", "PROCESSED", "CANCELLED"), state);
            Assert.Equal(1L, await ScalarAsync<long>(
                """
                SELECT count(*) FROM platform.outbox_events
                WHERE topic='dispatch.assignment-changed' AND aggregate_id=@order AND aggregate_version=@version
                  AND payload->>'assignment_id'=@assignment AND payload->>'assignment_status'='CANCELLED'
                """,
                ("order", OrderId),
                ("version", closing.Version),
                ("assignment", scenario.AssignmentId.ToString("D"))));
            Assert.Equal(1L, await ScalarAsync<long>(
                """
                SELECT count(*) FROM platform.audit_logs
                WHERE entity_id=@assignment AND action='ASSIGNMENT_CLOSED' AND actor_id IS NULL
                """,
                ("assignment", scenario.AssignmentId)));

            await using var connections = new RealtimeWorkerConnectionFactory(database.WorkerConnectionString);
            var evidence = await new PostgreSqlRealtimeOutboxEvidenceReader(connections)
                .ReadClosedAssignmentAsync(OwnerId, scenario.AssignmentId, closing.Version, default);
            Assert.NotNull(evidence);
            Assert.Equal("CANCELLED", evidence.Status);
            Assert.Equal(closing.OccurredAt, evidence.OccurredAt);
            Assert.True(evidence.DriverAudienceAuthorized);
        }
        finally
        {
            await database.RetireAssignmentEvidenceScenarioAsync(scenario.AssignmentId);
        }
    }

    private sealed record ClosingEvent(Guid Id, int Version, DateTimeOffset OccurredAt);

    private async Task<ClosingEvent> CommitClosingEventAsync(int version)
    {
        var id = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 9, 27, 7, version % 60, 0, TimeSpan.Zero);
        await ExecuteAsync(
            """
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,aggregate_version,event_type,public_event_code,payload,occurred_at)
            VALUES (@id,@order,@owner,@version,'ORDER_STATUS_CHANGED',NULL,
              jsonb_build_object('previous_status','ASSIGNED','new_status','CANCELLED'),@occurred)
            """,
            ("id", id),
            ("order", OrderId),
            ("owner", OwnerId),
            ("version", version),
            ("occurred", occurredAt));
        return new(id, version, occurredAt);
    }

    private Task InsertReactionAsync(Guid id, Guid assignmentId, ClosingEvent closing) =>
        ExecuteAsync(
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,payload,
              priority,status,attempts,available_at,created_at)
            VALUES (@id,@owner,jsonb_build_object('organization_ids',jsonb_build_array(@owner::text)),
              'dispatch.order-status-reaction-requested','Order',@order,@version,
              jsonb_build_object(
                'schema_version','order-status-reaction-v1','order_event_id',@event::text,
                'order_id',@order::text,'previous_status','ASSIGNED','new_status','CANCELLED',
                'occurred_at',@occurred,'assignment_id',@assignment::text),
              50,'PENDING',0,clock_timestamp(),@occurred)
            """,
            ("id", id),
            ("owner", OwnerId),
            ("order", OrderId),
            ("version", closing.Version),
            ("event", closing.Id),
            ("occurred", closing.OccurredAt),
            ("assignment", assignmentId));

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static string Flatten(Exception exception) =>
        exception is AggregateException aggregate
            ? string.Join(" | ", aggregate.Flatten().InnerExceptions.Select(Flatten))
            : exception.InnerException is null
                ? exception.Message
                : $"{exception.Message} | {Flatten(exception.InnerException)}";

    private sealed class DispatchLifecycleWorkerFactory(Dictionary<string, string?> settings)
        : WebApplicationFactory<WorkerProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
        }
    }
}
