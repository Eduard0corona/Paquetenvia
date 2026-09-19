using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Realtime.Application.Authorization;
using Realtime.Application.Clients;
using Realtime.Application.Events;
using Realtime.Application.Observability;
using Realtime.Endpoints.Hubs;

DiagnosticProbe.Emit("host_process_startup", new
{
    command_line_argument_count = args.Length,
});
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSingleton<TestRealtimeState>();
builder.Services.AddSingleton<IRealtimeConnectionAuthorizer, TestRealtimeConnectionAuthorizer>();
builder.Services.AddSingleton<IRealtimeTelemetry, TestRealtimeTelemetry>();
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.PayloadSerializerOptions.DefaultIgnoreCondition =
            JsonIgnoreCondition.WhenWritingNull;
    });

var app = builder.Build();
app.Lifetime.ApplicationStarted.Register(() =>
    DiagnosticProbe.Emit("host_application_started", new
    {
        urls = app.Urls.Order(StringComparer.Ordinal).ToArray(),
    }));
app.Lifetime.ApplicationStopping.Register(() =>
    DiagnosticProbe.Emit("host_application_stopping"));
app.Lifetime.ApplicationStopped.Register(() =>
    DiagnosticProbe.Emit("host_application_stopped"));
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/hubs/operations", StringComparison.Ordinal))
    {
        await next();
        return;
    }

    var queryToken = context.Request.Query["access_token"].SingleOrDefault();
    string? headerToken = null;
    if (AuthenticationHeaderValue.TryParse(
            context.Request.Headers.Authorization.SingleOrDefault(),
            out var authorization) &&
        string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
    {
        headerToken = authorization.Parameter;
    }

    var token = queryToken ?? headerToken;
    if (!string.Equals(token, TestRealtimeState.ValidToken, StringComparison.Ordinal) ||
        !Guid.TryParseExact(
            context.Request.Query["organization_id"].SingleOrDefault(),
            "D",
            out var organizationId) ||
        organizationId == Guid.Empty)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    if (string.Equals(
            context.Request.Headers.Upgrade.SingleOrDefault(),
            "websocket",
            StringComparison.OrdinalIgnoreCase))
    {
        context.RequestServices.GetRequiredService<TestRealtimeState>()
            .RecordTransport("WebSockets");
    }

    context.Items["Realtime.PrivateConnectionRequest"] = new PrivateRealtimeConnectionRequest(
        TestRealtimeState.UserId,
        organizationId,
        false,
        context.TraceIdentifier);
    await next();
});

app.MapGet("/__test/health", () => Results.NoContent());
app.MapGet(
    "/__test/snapshot",
    (TestRealtimeState state) => Results.Json(new
    {
        aggregate_versions = new Dictionary<string, long>
        {
            [TestRealtimeState.AggregateId.ToString("D")] = state.SnapshotVersion,
        },
    }));
app.MapGet(
    "/__test/stats",
    (TestRealtimeState state) => Results.Json(new
    {
        authorization_count = state.AuthorizationCount,
        transport = state.Transport,
    }));
app.MapPost(
    "/__test/publish/{organizationId:guid}/{version:long}/{eventId:guid}",
    async (
        Guid organizationId,
        long version,
        Guid eventId,
        IHubContext<OperationsHub, IOperationsClient> hub,
        CancellationToken cancellationToken) =>
    {
        var group = $"org:{organizationId:D}".ToLowerInvariant();
        DiagnosticProbe.Emit("server_publish_request_received", new
        {
            organization_id = organizationId.ToString("D"),
            aggregate_version = version,
            event_id = eventId.ToString("D"),
            group,
        });
        var message = new RealtimeEnvelope<OrderStatusChangedPayload>(
            eventId,
            RealtimeEventTypes.OrderStatusChanged,
            TestRealtimeState.OccurredAt,
            TestRealtimeState.AggregateId,
            version,
            null,
            new OrderStatusChangedPayload(
                TestRealtimeState.AggregateId,
                "READY_FOR_PICKUP",
                "ASSIGNED",
                TestRealtimeState.OccurredAt));
        DiagnosticProbe.Emit("server_publish_dispatch_begin", new
        {
            organization_id = organizationId.ToString("D"),
            aggregate_version = version,
            event_id = eventId.ToString("D"),
            group,
        });
        await hub.Clients.Group(group)
            .OrderStatusChanged(message)
            .WaitAsync(cancellationToken);
        DiagnosticProbe.Emit("server_publish_dispatch_complete", new
        {
            organization_id = organizationId.ToString("D"),
            aggregate_version = version,
            event_id = eventId.ToString("D"),
            group,
        });
        DiagnosticProbe.Emit("server_publish_response_returned", new
        {
            response_status = StatusCodes.Status204NoContent,
            aggregate_version = version,
            event_id = eventId.ToString("D"),
        });
        return Results.NoContent();
    });
app.MapHub<OperationsHub>("/hubs/operations");
app.Run();

internal static class DiagnosticProbe
{
    private static readonly long ProcessStartedAt = Stopwatch.GetTimestamp();

    internal static void Emit(string eventName, object? details = null)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PAQUETERIA_REALTIME_DIAGNOSTICS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var testElapsed = long.TryParse(
            Environment.GetEnvironmentVariable("PAQUETERIA_DIAGNOSTIC_TEST_STARTED_UNIX_MS"),
            out var testStartedAt)
            ? now.ToUnixTimeMilliseconds() - testStartedAt
            : (long?)null;
        Console.Error.WriteLine(
            $"RTDIAG {JsonSerializer.Serialize(new
            {
                schema = "paquetenvia-realtime-correlation-v1",
                repetition = Environment.GetEnvironmentVariable("PAQUETERIA_DIAGNOSTIC_REPETITION"),
                testcase = Environment.GetEnvironmentVariable("PAQUETERIA_DIAGNOSTIC_TESTCASE"),
                process_role = "realtime-test-host",
                pid = Environment.ProcessId,
                correlation_id = Environment.GetEnvironmentVariable("PAQUETERIA_DIAGNOSTIC_CORRELATION_ID"),
                utc = now.ToString("O"),
                elapsed_ms = testElapsed,
                process_elapsed_ms = Stopwatch.GetElapsedTime(ProcessStartedAt).TotalMilliseconds,
                @event = eventName,
                details,
            })}");
    }
}

internal sealed class TestRealtimeState
{
    internal const string ValidToken = "synthetic-dispatcher-token";
    internal static readonly Guid UserId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa10");
    internal static readonly Guid OrganizationA =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid AggregateId =
        Guid.Parse("77777777-7777-7777-7777-777777777777");
    internal static readonly DateTimeOffset OccurredAt =
        new(2026, 7, 24, 19, 0, 0, TimeSpan.Zero);

    private int _authorizationCount;
    private string _transport = "Unknown";

    internal long SnapshotVersion { get; } =
        long.TryParse(
            Environment.GetEnvironmentVariable("PAQUETERIA_TEST_SNAPSHOT_VERSION"),
            out var version)
            ? version
            : 1;

    internal int AuthorizationCount => Volatile.Read(ref _authorizationCount);
    internal string Transport => Volatile.Read(ref _transport);

    internal void RecordAuthorization() =>
        Interlocked.Increment(ref _authorizationCount);

    internal void RecordTransport(string transport) =>
        Volatile.Write(ref _transport, transport);
}

internal sealed class TestRealtimeConnectionAuthorizer(TestRealtimeState state)
    : IRealtimeConnectionAuthorizer
{
    public ValueTask<ConnectionAuthorizationResult<OperationsConnectionAuthorization>>
        AuthorizeOperationsAsync(
            PrivateRealtimeConnectionRequest request,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        state.RecordAuthorization();
        var result = request.UserId == TestRealtimeState.UserId &&
            request.OrganizationId == TestRealtimeState.OrganizationA
            ? ConnectionAuthorizationResult<OperationsConnectionAuthorization>.Authorized(
                new OperationsConnectionAuthorization(
                    request.OrganizationId,
                    Paqueteria.Domain.Tenancy.OrganizationRole.Dispatcher))
            : ConnectionAuthorizationResult<OperationsConnectionAuthorization>.Rejected;
        return ValueTask.FromResult(result);
    }

    public ValueTask<ConnectionAuthorizationResult<DriverConnectionAuthorization>>
        AuthorizeDriverAsync(
            PrivateRealtimeConnectionRequest request,
            CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            ConnectionAuthorizationResult<DriverConnectionAuthorization>.Rejected);

    public ValueTask<ConnectionAuthorizationResult<TrackingConnectionAuthorization>>
        AuthorizeTrackingAsync(
            string exactToken,
            CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            ConnectionAuthorizationResult<TrackingConnectionAuthorization>.Rejected);
}

internal sealed class TestRealtimeTelemetry : IRealtimeTelemetry
{
    public IDisposable MeasureAuthorization(string hub, string authKind) => Measurement.Instance;
    public IDisposable MeasurePublication(string eventType) => Measurement.Instance;
    public void ConnectionAccepted(string hub, string authKind) { }
    public void ConnectionRejected(string hub, string authKind) { }
    public void ConnectionClosed(string hub) { }
    public void PublicationSucceeded(string eventType) { }
    public void PublicationFailed(string eventType) { }

    private sealed class Measurement : IDisposable
    {
        internal static Measurement Instance { get; } = new();
        public void Dispose() { }
    }
}
