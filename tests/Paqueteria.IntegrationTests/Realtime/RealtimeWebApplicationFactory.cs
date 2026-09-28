using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Realtime.Application.Authorization;
using Realtime.Application.Observability;

namespace Paqueteria.IntegrationTests.Realtime;

public sealed class RealtimeWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly int _connectionPermitLimit;
    private readonly string _provider;
    private readonly string _backplane;
    private readonly string _allowedOrigin;

    public RealtimeWebApplicationFactory()
        : this(100, "SignalR", "InProcess", AllowedOrigin)
    {
    }

    internal RealtimeWebApplicationFactory(int connectionPermitLimit)
        : this(connectionPermitLimit, "SignalR", "InProcess", AllowedOrigin)
    {
    }

    internal RealtimeWebApplicationFactory(
        int connectionPermitLimit,
        string provider,
        string backplane,
        string allowedOrigin)
    {
        _connectionPermitLimit = connectionPermitLimit;
        _provider = provider;
        _backplane = backplane;
        _allowedOrigin = allowedOrigin;
    }

    public const string AllowedOrigin = "https://web.synthetic.local";
    public const string ValidTrackingTokenA = "tracking-token-a";
    public const string ValidTrackingTokenB = "tracking-token-b";
    public const string PublicOrderIdA = "ORD_abcdefghijklmnopqrstuv";
    public const string PublicOrderIdB = "ORD_ABCDEFGHIJKLMNOPQRSTUV";
    public static readonly Guid OrganizationA =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OrganizationB =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid DriverA =
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    public static readonly Guid DriverB =
        Guid.Parse("dddddddd-dddd-dddd-dddd-ddddddddddd2");
    public static readonly Guid AssignmentA =
        Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    public static readonly Guid AssignmentB =
        Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeee2");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "Mock",
                ["PublicTracking:Provider"] = "Disabled",
                ["Tenancy:Provider"] = "Disabled",
                ["Realtime:Provider"] = _provider,
                ["Realtime:Backplane"] = _backplane,
                ["Realtime:AllowedOrigins:0"] = _allowedOrigin,
                ["Realtime:ConnectionPermitLimit"] = _connectionPermitLimit.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["Realtime:ConnectionWindowSeconds"] = "300",
                ["Realtime:AuthorizationCommandTimeoutSeconds"] = "5",
                ["Realtime:AuthorizationRetryCount"] = "1",
                ["Realtime:MaximumDriverAssignmentGroups"] = "100",
                ["Realtime:ReconnectDelaysMilliseconds:0"] = "0",
                ["Realtime:ReconnectDelaysMilliseconds:1"] = "10",
                ["ConnectionStrings:Paqueteria"] =
                    "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1",
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRealtimeConnectionAuthorizer>();
            services.AddSingleton<SyntheticRealtimeAuthorizationState>();
            services.AddSingleton<IRealtimeConnectionAuthorizer, SyntheticRealtimeConnectionAuthorizer>();

            // Wrap (never replace) the production telemetry so the hubs keep their real
            // metrics while tests observe ConnectionAccepted, which every hub emits only
            // after its Groups.AddToGroupAsync calls completed.
            var telemetry = services.Last(descriptor =>
                descriptor.ServiceType == typeof(IRealtimeTelemetry));
            var createInner = telemetry.ImplementationFactory
                ?? throw new InvalidOperationException(
                    "The production realtime telemetry registration changed shape.");
            services.Remove(telemetry);
            services.AddSingleton<RealtimeConnectionAcceptances>();
            services.AddSingleton<IRealtimeTelemetry>(provider =>
                new AcceptanceObservingRealtimeTelemetry(
                    (IRealtimeTelemetry)createInner(provider),
                    provider.GetRequiredService<RealtimeConnectionAcceptances>()));
        });
    }

    /// <summary>
    /// Server-side readiness signal for hub connections of this host.
    /// </summary>
    internal RealtimeConnectionAcceptances ConnectionAcceptances =>
        Services.GetRequiredService<RealtimeConnectionAcceptances>();

    /// <summary>
    /// Counts <c>ConnectionAccepted</c> per hub. The client's <c>StartAsync</c> completes
    /// when the handshake response arrives, which SignalR sends before
    /// <c>Hub.OnConnectedAsync</c> runs; the hubs call <c>ConnectionAccepted</c> strictly
    /// after awaiting their group registrations, so a completed wait means a publish to
    /// those groups reaches the connection.
    /// </summary>
    internal sealed class RealtimeConnectionAcceptances
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, long> _accepted = new(StringComparer.Ordinal);
        private readonly List<(Dictionary<string, long> Targets, TaskCompletionSource Completion)> _waiters = [];

        /// <summary>
        /// Captures the current per-hub counts and returns a task that completes once the
        /// given number of further connections were accepted on each hub. Call it before
        /// <c>StartAsync</c> so no acceptance can be missed.
        /// </summary>
        internal Task ExpectAsync(params (string Hub, int Count)[] expectations)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                var targets = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var (hub, count) in expectations)
                {
                    targets[hub] = targets.GetValueOrDefault(hub, _accepted.GetValueOrDefault(hub)) + count;
                }

                _waiters.Add((targets, completion));
                CompleteSatisfiedWaiters();
            }

            return completion.Task;
        }

        internal void RecordAccepted(string hub)
        {
            lock (_gate)
            {
                _accepted[hub] = _accepted.GetValueOrDefault(hub) + 1;
                CompleteSatisfiedWaiters();
            }
        }

        private void CompleteSatisfiedWaiters()
        {
            for (var index = _waiters.Count - 1; index >= 0; index--)
            {
                var (targets, completion) = _waiters[index];
                if (targets.All(target => _accepted.GetValueOrDefault(target.Key) >= target.Value))
                {
                    _waiters.RemoveAt(index);
                    completion.TrySetResult();
                }
            }
        }
    }

    internal sealed class AcceptanceObservingRealtimeTelemetry(
        IRealtimeTelemetry inner,
        RealtimeConnectionAcceptances acceptances) : IRealtimeTelemetry
    {
        internal IRealtimeTelemetry Inner => inner;

        public IDisposable MeasureAuthorization(string hub, string authKind) =>
            inner.MeasureAuthorization(hub, authKind);

        public IDisposable MeasurePublication(string eventType) =>
            inner.MeasurePublication(eventType);

        public void ConnectionAccepted(string hub, string authKind)
        {
            inner.ConnectionAccepted(hub, authKind);
            acceptances.RecordAccepted(hub);
        }

        public void ConnectionRejected(string hub, string authKind) =>
            inner.ConnectionRejected(hub, authKind);

        public void ConnectionClosed(string hub) => inner.ConnectionClosed(hub);

        public void PublicationSucceeded(string eventType) => inner.PublicationSucceeded(eventType);

        public void PublicationFailed(string eventType) => inner.PublicationFailed(eventType);
    }

    internal sealed class SyntheticRealtimeAuthorizationState
    {
        private int _platformAdminActivations;

        internal int PlatformAdminActivations => Volatile.Read(ref _platformAdminActivations);

        internal void RecordPlatformAdminActivation() =>
            Interlocked.Increment(ref _platformAdminActivations);
    }

    private sealed class SyntheticRealtimeConnectionAuthorizer(
        SyntheticRealtimeAuthorizationState state) : IRealtimeConnectionAuthorizer
    {
        private static readonly Guid DispatcherA =
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa10");
        private static readonly Guid DispatcherB =
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4");
        private static readonly Guid PlatformAdmin =
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2");
        private static readonly Guid DriverUser =
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11");

        public ValueTask<ConnectionAuthorizationResult<OperationsConnectionAuthorization>>
            AuthorizeOperationsAsync(
                PrivateRealtimeConnectionRequest request,
                CancellationToken cancellationToken)
        {
            var role = request switch
            {
                { UserId: var user, OrganizationId: var org }
                    when user == DispatcherA && org == OrganizationA =>
                    Paqueteria.Domain.Tenancy.OrganizationRole.Dispatcher,
                { UserId: var user, OrganizationId: var org }
                    when user == DispatcherB && org == OrganizationB =>
                    Paqueteria.Domain.Tenancy.OrganizationRole.Dispatcher,
                { UserId: var user, OrganizationId: var org, MfaSatisfied: true }
                    when user == PlatformAdmin && org == OrganizationA =>
                    Paqueteria.Domain.Tenancy.OrganizationRole.PlatformAdmin,
                _ => (Paqueteria.Domain.Tenancy.OrganizationRole?)null,
            };
            if (role is null)
            {
                return ValueTask.FromResult(
                    ConnectionAuthorizationResult<OperationsConnectionAuthorization>.Rejected);
            }

            if (role == Paqueteria.Domain.Tenancy.OrganizationRole.PlatformAdmin)
            {
                state.RecordPlatformAdminActivation();
            }

            return ValueTask.FromResult(
                ConnectionAuthorizationResult<OperationsConnectionAuthorization>.Authorized(
                    new OperationsConnectionAuthorization(request.OrganizationId, role.Value)));
        }

        public ValueTask<ConnectionAuthorizationResult<DriverConnectionAuthorization>>
            AuthorizeDriverAsync(
                PrivateRealtimeConnectionRequest request,
                CancellationToken cancellationToken)
        {
            var authorization = request switch
            {
                { UserId: var user, OrganizationId: var org }
                    when user == DriverUser && org == OrganizationA =>
                    new DriverConnectionAuthorization(OrganizationA, DriverA, [AssignmentA]),
                { UserId: var user, OrganizationId: var org }
                    when user == DispatcherB && org == OrganizationB =>
                    new DriverConnectionAuthorization(OrganizationB, DriverB, [AssignmentB]),
                _ => null,
            };
            return ValueTask.FromResult(authorization is null
                ? ConnectionAuthorizationResult<DriverConnectionAuthorization>.Rejected
                : ConnectionAuthorizationResult<DriverConnectionAuthorization>.Authorized(authorization));
        }

        public ValueTask<ConnectionAuthorizationResult<TrackingConnectionAuthorization>>
            AuthorizeTrackingAsync(
                string exactToken,
                CancellationToken cancellationToken)
        {
            var publicId = exactToken switch
            {
                ValidTrackingTokenA => PublicOrderIdA,
                ValidTrackingTokenB => PublicOrderIdB,
                _ => null,
            };
            return ValueTask.FromResult(publicId is null
                ? ConnectionAuthorizationResult<TrackingConnectionAuthorization>.Rejected
                : ConnectionAuthorizationResult<TrackingConnectionAuthorization>.Authorized(
                    new TrackingConnectionAuthorization(publicId)));
        }
    }
}
