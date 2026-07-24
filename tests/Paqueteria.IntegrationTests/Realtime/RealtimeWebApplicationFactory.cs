using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Realtime.Application.Authorization;

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
        });
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
