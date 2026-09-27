using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Paqueteria.Domain.Tenancy;
using Realtime.Application.Authorization;
using Realtime.Application.Observability;
using Realtime.Endpoints.Hubs;

namespace Paqueteria.UnitTests.Realtime;

/// <summary>
/// SignalR activates a new hub instance for every invocation, including OnConnectedAsync and
/// OnDisconnectedAsync of the same connection. The accepted state that drives
/// <c>realtime.connections.active</c> must therefore live with the connection, not the hub.
/// </summary>
public sealed class RealtimeHubConnectionMetricTests
{
    private const string TrackingItemKey = "Realtime.TrackingAuthorization";
    private const string PrivateRequestItemKey = "Realtime.PrivateConnectionRequest";
    private const string PublicOrderId = "ORD_SYNTHETIC0000000000001";

    [Fact]
    public async Task Tracking_active_connections_return_to_zero_when_a_new_hub_instance_handles_disconnect()
    {
        var telemetry = new CountingTelemetry();
        var connection = new FakeHubCallerContext("tracking-1");
        connection.HttpContext.Items[TrackingItemKey] = new TrackingConnectionAuthorization(PublicOrderId);

        await Activate(new TrackingHub(telemetry), connection).OnConnectedAsync();
        Assert.Equal(1, telemetry.Active("tracking"));

        await Activate(new TrackingHub(telemetry), connection).OnDisconnectedAsync(null);
        Assert.Equal(0, telemetry.Active("tracking"));
    }

    [Fact]
    public async Task Operations_active_connections_return_to_zero_when_a_new_hub_instance_handles_disconnect()
    {
        var telemetry = new CountingTelemetry();
        var authorizer = new AllowingAuthorizer();
        var connection = PrivateConnection("operations-1");

        await Activate(new OperationsHub(authorizer, telemetry), connection).OnConnectedAsync();
        Assert.Equal(1, telemetry.Active("operations"));

        await Activate(new OperationsHub(authorizer, telemetry), connection).OnDisconnectedAsync(null);
        Assert.Equal(0, telemetry.Active("operations"));
    }

    [Fact]
    public async Task Driver_active_connections_return_to_zero_when_a_new_hub_instance_handles_disconnect()
    {
        var telemetry = new CountingTelemetry();
        var authorizer = new AllowingAuthorizer();
        var connection = PrivateConnection("driver-1");

        await Activate(new DriverHub(authorizer, telemetry), connection).OnConnectedAsync();
        Assert.Equal(1, telemetry.Active("driver"));

        await Activate(new DriverHub(authorizer, telemetry), connection).OnDisconnectedAsync(null);
        Assert.Equal(0, telemetry.Active("driver"));
    }

    [Fact]
    public async Task Rejected_connection_never_decrements_the_active_gauge()
    {
        var telemetry = new CountingTelemetry();
        var connection = new FakeHubCallerContext("tracking-rejected");

        await Assert.ThrowsAsync<HubException>(
            () => Activate(new TrackingHub(telemetry), connection).OnConnectedAsync());
        await Activate(new TrackingHub(telemetry), connection).OnDisconnectedAsync(null);

        Assert.Equal(0, telemetry.Active("tracking"));
        Assert.Equal(0, telemetry.Closed);
    }

    [Fact]
    public async Task Connections_are_counted_independently_and_disconnect_is_idempotent()
    {
        var telemetry = new CountingTelemetry();
        var first = new FakeHubCallerContext("tracking-a");
        var second = new FakeHubCallerContext("tracking-b");
        first.HttpContext.Items[TrackingItemKey] = new TrackingConnectionAuthorization(PublicOrderId);
        second.HttpContext.Items[TrackingItemKey] = new TrackingConnectionAuthorization(PublicOrderId);

        await Activate(new TrackingHub(telemetry), first).OnConnectedAsync();
        await Activate(new TrackingHub(telemetry), second).OnConnectedAsync();
        await Activate(new TrackingHub(telemetry), first).OnDisconnectedAsync(null);
        await Activate(new TrackingHub(telemetry), first).OnDisconnectedAsync(null);

        Assert.Equal(1, telemetry.Active("tracking"));
    }

    private static FakeHubCallerContext PrivateConnection(string connectionId)
    {
        var connection = new FakeHubCallerContext(connectionId);
        connection.HttpContext.Items[PrivateRequestItemKey] = new PrivateRealtimeConnectionRequest(
            Guid.Parse("0b9d1f3e-1111-4111-8111-111111111111"),
            Guid.Parse("0b9d1f3e-2222-4222-8222-222222222222"),
            MfaSatisfied: true,
            RequestId: null);
        return connection;
    }

    private static T Activate<T>(T hub, HubCallerContext context)
        where T : Hub
    {
        hub.Context = context;
        hub.Groups = new NoopGroupManager();
        return hub;
    }

    private sealed class FakeHubCallerContext : HubCallerContext
    {
        private readonly FeatureCollection _features = new();

        internal FakeHubCallerContext(string connectionId)
        {
            ConnectionId = connectionId;
            HttpContext = new DefaultHttpContext();
            _features.Set<IHttpContextFeature>(new HttpContextFeature(HttpContext));
        }

        internal HttpContext HttpContext { get; }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => HttpContext.User;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features => _features;
        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }

        private sealed class HttpContextFeature(HttpContext httpContext) : IHttpContextFeature
        {
            public HttpContext? HttpContext { get; set; } = httpContext;
        }
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class AllowingAuthorizer : IRealtimeConnectionAuthorizer
    {
        public ValueTask<ConnectionAuthorizationResult<OperationsConnectionAuthorization>> AuthorizeOperationsAsync(
            PrivateRealtimeConnectionRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ConnectionAuthorizationResult<OperationsConnectionAuthorization>.Authorized(
                new OperationsConnectionAuthorization(request.OrganizationId, OrganizationRole.Dispatcher)));

        public ValueTask<ConnectionAuthorizationResult<DriverConnectionAuthorization>> AuthorizeDriverAsync(
            PrivateRealtimeConnectionRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ConnectionAuthorizationResult<DriverConnectionAuthorization>.Authorized(
                new DriverConnectionAuthorization(request.OrganizationId, request.UserId, [Guid.NewGuid()])));

        public ValueTask<ConnectionAuthorizationResult<TrackingConnectionAuthorization>> AuthorizeTrackingAsync(
            string exactToken,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ConnectionAuthorizationResult<TrackingConnectionAuthorization>.Rejected);
    }

    private sealed class CountingTelemetry : IRealtimeTelemetry
    {
        private readonly Dictionary<string, int> _active = new(StringComparer.Ordinal);

        internal int Closed { get; private set; }

        internal int Active(string hub) => _active.GetValueOrDefault(hub);

        public IDisposable MeasureAuthorization(string hub, string authKind) => Nothing.Instance;
        public IDisposable MeasurePublication(string eventType) => Nothing.Instance;

        public void ConnectionAccepted(string hub, string authKind) =>
            _active[hub] = Active(hub) + 1;

        public void ConnectionRejected(string hub, string authKind)
        {
        }

        public void ConnectionClosed(string hub)
        {
            Closed++;
            _active[hub] = Active(hub) - 1;
        }

        public void PublicationSucceeded(string eventType)
        {
        }

        public void PublicationFailed(string eventType)
        {
        }

        private sealed class Nothing : IDisposable
        {
            internal static Nothing Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
