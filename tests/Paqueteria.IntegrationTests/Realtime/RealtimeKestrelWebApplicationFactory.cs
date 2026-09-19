using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Drivers.Application.Locations;
using Drivers.Infrastructure.Locations;
using Dispatch.Application.Stops;
using Dispatch.Infrastructure.Stops;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Realtime.Application.Authorization;
using Realtime.Application.Dispatching;
using Realtime.Application.Observability;
using Realtime.Endpoints;
using Realtime.Infrastructure.Authorization;

namespace Paqueteria.IntegrationTests.Realtime;

internal sealed class RealtimeKestrelWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string? _workerConnectionString;
    private readonly IRealtimeOutboxFailureInjector? _failureInjector;
    private readonly ILoggerProvider? _logProvider;
    private readonly RealtimeAuthorizationRecorder _recorder;
    private readonly string _allowedOrigin;
    private readonly bool _enableDispatch;
    private readonly bool _enableDriverApiCors;
    private readonly IReadOnlyDictionary<string, string?> _configurationOverrides;

    internal RealtimeKestrelWebApplicationFactory(
        string connectionString,
        RealtimeAuthorizationRecorder recorder,
        int port = 0,
        string? workerConnectionString = null,
        IRealtimeOutboxFailureInjector? failureInjector = null,
        ILoggerProvider? logProvider = null,
        string allowedOrigin = "http://127.0.0.1",
        bool enableDispatch = false,
        bool enableDriverApiCors = false,
        IReadOnlyDictionary<string, string?>? configurationOverrides = null,
        bool useHttps = false)
    {
        _connectionString = connectionString;
        _workerConnectionString = workerConnectionString;
        _failureInjector = failureInjector;
        _logProvider = logProvider;
        _recorder = recorder;
        _allowedOrigin = allowedOrigin;
        _enableDispatch = enableDispatch;
        _enableDriverApiCors = enableDriverApiCors;
        _configurationOverrides =
            configurationOverrides ?? new Dictionary<string, string?>();
        if (useHttps)
        {
            UseKestrel(options => options.Listen(
                IPAddress.Loopback,
                port,
                listen => listen.UseHttps(EphemeralLoopbackCertificate.Value)));
        }
        else
        {
            UseKestrel(port);
        }
    }

    /// <summary>
    /// Self-signed loopback certificate created in memory for the current test
    /// process. It is never written to disk, never versioned and never added to
    /// a host trust store; only the Playwright browser context that talks to
    /// the loopback listeners using it ignores their TLS error.
    /// </summary>
    private static readonly Lazy<X509Certificate2> EphemeralLoopbackCertificate =
        new(CreateEphemeralLoopbackCertificate);

    /// <summary>
    /// The process-local loopback certificate, shared with other loopback TLS
    /// listeners of the same test (for example the signed-upload tunnel) so a
    /// single browser context exception covers every origin under test.
    /// </summary>
    internal static X509Certificate2 LoopbackCertificate =>
        EphemeralLoopbackCertificate.Value;

    private static X509Certificate2 CreateEphemeralLoopbackCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddDnsName("localhost");
        alternativeNames.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(alternativeNames.Build());
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        // Re-import so Kestrel can use the private key on every host platform.
        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx),
            password: null);
    }

    internal Uri Start()
    {
        using var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var addresses = Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.SingleOrDefault()
            ?? throw new InvalidOperationException("Kestrel did not publish a single base address.");
        return new Uri(address, UriKind.Absolute);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        if (_logProvider is not null)
        {
            builder.ConfigureLogging(logging => logging.AddProvider(_logProvider));
        }

        builder.ConfigureAppConfiguration(configuration =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "PostgreSql",
                ["IdentityBootstrap:CommandTimeoutSeconds"] = "5",
                ["PublicTracking:Provider"] = "PostgreSql",
                ["PublicTracking:CommandTimeoutSeconds"] = "5",
                ["Orders:Provider"] = "PostgreSql",
                ["Orders:CommandTimeoutSeconds"] = "5",
                ["Tenancy:Provider"] = "PostgreSql",
                ["Tenancy:CommandTimeoutSeconds"] = "5",
                ["OperationsDashboard:Provider"] = "PostgreSql",
                ["OperationsDashboard:CommandTimeoutSeconds"] = "5",
                ["Realtime:Provider"] = "SignalR",
                ["Realtime:Backplane"] = "InProcess",
                ["Realtime:AllowedOrigins:0"] = _allowedOrigin,
                ["Realtime:ConnectionPermitLimit"] = "100",
                ["Realtime:ConnectionWindowSeconds"] = "300",
                ["Realtime:AuthorizationCommandTimeoutSeconds"] = "5",
                ["Realtime:AuthorizationRetryCount"] = "1",
                ["Realtime:MaximumDriverAssignmentGroups"] = "100",
                ["Realtime:ReconnectDelaysMilliseconds:0"] = "0",
                ["Realtime:ReconnectDelaysMilliseconds:1"] = "100",
                ["ConnectionStrings:Paqueteria"] = _connectionString,
                ["Realtime:OutboxDispatcher:Provider"] =
                    _workerConnectionString is null ? "Disabled" : "PostgreSql",
                ["Realtime:OutboxDispatcher:WorkerId"] = "rtm002-integration",
                ["Realtime:OutboxDispatcher:Business:PollIntervalMilliseconds"] = "50",
                ["Realtime:OutboxDispatcher:Business:LeaseSeconds"] = "15",
                ["Realtime:OutboxDispatcher:Location:PollIntervalMilliseconds"] = "50",
                ["Realtime:OutboxDispatcher:Location:LeaseSeconds"] = "15",
                ["Realtime:OutboxDispatcher:StaleRequeueIntervalSeconds"] = "1",
                ["ConnectionStrings:PaqueteriaWorker"] = _workerConnectionString,
            };
            foreach (var pair in _configurationOverrides)
            {
                settings[pair.Key] = pair.Value;
            }
            configuration.AddInMemoryCollection(settings);
        });
        builder.ConfigureServices(services =>
        {
            if (_enableDriverApiCors)
            {
                services.PostConfigure<CorsOptions>(options =>
                {
                    options.AddDefaultPolicy(policy =>
                        policy
                            .WithOrigins(_allowedOrigin)
                            .WithMethods("GET", "POST")
                            .WithHeaders(
                                "Authorization",
                                "Content-Type",
                                "Idempotency-Key",
                                "X-Organization-Id",
                                "X-SignalR-User-Agent")
                            .AllowCredentials());
                    options.AddPolicy(
                        RealtimeEndpointDefaults.CorsPolicy,
                        policy =>
                            policy
                                .WithOrigins(_allowedOrigin)
                                .WithMethods("GET", "POST")
                                .WithHeaders(
                                    "Authorization",
                                    "Content-Type",
                                    "X-Requested-With",
                                    "X-SignalR-User-Agent")
                                .AllowCredentials());
                });
            }

            services.AddSingleton(_recorder);
            services.RemoveAll<IRealtimeConnectionAuthorizer>();
            services.RemoveAll<IRealtimeTelemetry>();
            services.AddSingleton<IRealtimeTelemetry>(_recorder);
            if (_failureInjector is not null)
            {
                services.RemoveAll<IRealtimeOutboxFailureInjector>();
                services.AddSingleton(_failureInjector);
            }

            services.RemoveAll<IDriverLocationIngestionService>();
            services.AddScoped<IDriverLocationIngestionService>(provider =>
                provider.GetRequiredService<PostgreSqlDriverLocationIngestionService>());
            if (_enableDispatch)
            {
                services.RemoveAll<IDriverStopsQuery>();
                services.AddScoped<IDriverStopsQuery>(provider =>
                    provider.GetRequiredService<PostgreSqlDriverStopsQuery>());
            }
            services.AddScoped<IRealtimeConnectionAuthorizer>(provider =>
                new RecordingRealtimeConnectionAuthorizer(
                    provider.GetRequiredService<PostgreSqlRealtimeConnectionAuthorizer>(),
                    provider.GetRequiredService<RealtimeAuthorizationRecorder>()));
        });
    }
}

internal sealed class RealtimeAuthorizationRecorder : IRealtimeTelemetry
{
    private readonly SemaphoreSlim _operations = new(0);
    private readonly SemaphoreSlim _operationsAccepted = new(0);
    private readonly SemaphoreSlim _tracking = new(0);
    private readonly SemaphoreSlim _trackingAccepted = new(0);
    private readonly SemaphoreSlim _driverAccepted = new(0);
    private readonly ConcurrentQueue<Guid> _pendingDriverAuthorizations = new();
    private readonly ConcurrentDictionary<Guid, byte> _acceptedDriverIds = new();
    private int _operationsCount;
    private int _trackingCount;
    private Exception? _trackingException;

    internal int OperationsCount => Volatile.Read(ref _operationsCount);
    internal int TrackingCount => Volatile.Read(ref _trackingCount);
    internal Exception? TrackingException => Volatile.Read(ref _trackingException);

    internal void RecordOperations()
    {
        Interlocked.Increment(ref _operationsCount);
        _operations.Release();
    }

    internal void RecordTracking()
    {
        Interlocked.Increment(ref _trackingCount);
        _tracking.Release();
    }

    internal Task<bool> WaitForNextOperationsAsync(TimeSpan timeout) =>
        _operations.WaitAsync(timeout);

    internal Task<bool> WaitForNextOperationsAcceptedAsync(TimeSpan timeout) =>
        _operationsAccepted.WaitAsync(timeout);

    internal Task<bool> WaitForNextTrackingAsync(TimeSpan timeout) =>
        _tracking.WaitAsync(timeout);

    internal Task<bool> WaitForNextTrackingAcceptedAsync(TimeSpan timeout) =>
        _trackingAccepted.WaitAsync(timeout);

    internal void RecordTrackingException(Exception exception) =>
        Volatile.Write(ref _trackingException, exception);

    /// <summary>
    /// Records a driver authorization that DriverHub accepted; the hub emits
    /// <c>ConnectionAccepted("driver", ...)</c> for it only after the driver and
    /// assignment group registrations of that same connection completed.
    /// </summary>
    internal void RecordDriverAuthorized(Guid driverId) =>
        _pendingDriverAuthorizations.Enqueue(driverId);

    /// <summary>
    /// Completes once DriverHub has accepted a connection for
    /// <paramref name="driverId"/>, i.e. after its server-side group
    /// registration finished. An acceptance that happened before the wait
    /// started is still observed; rejected connections never satisfy it.
    /// </summary>
    internal async Task<bool> WaitForDriverAcceptedAsync(Guid driverId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!_acceptedDriverIds.ContainsKey(driverId))
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero ||
                !await _driverAccepted.WaitAsync(remaining))
            {
                return false;
            }
        }

        return true;
    }

    public IDisposable MeasureAuthorization(string hub, string authKind) =>
        EmptyMeasurement.Instance;

    public IDisposable MeasurePublication(string eventType) =>
        EmptyMeasurement.Instance;

    public void ConnectionAccepted(string hub, string authKind)
    {
        if (hub == "operations")
        {
            _operationsAccepted.Release();
        }
        else if (hub == "tracking")
        {
            _trackingAccepted.Release();
        }
        else if (hub == "driver")
        {
            if (_pendingDriverAuthorizations.TryDequeue(out var driverId))
            {
                _acceptedDriverIds.TryAdd(driverId, 0);
            }

            _driverAccepted.Release();
        }
    }

    public void ConnectionRejected(string hub, string authKind)
    {
    }

    public void ConnectionClosed(string hub)
    {
    }

    public void PublicationSucceeded(string eventType)
    {
    }

    public void PublicationFailed(string eventType)
    {
    }

    private sealed class EmptyMeasurement : IDisposable
    {
        internal static EmptyMeasurement Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

internal sealed class OneShotRealtimeOutboxFailureInjector(
    RealtimeOutboxLane targetLane) : IRealtimeOutboxFailureInjector
{
    private readonly TaskCompletionSource _interrupted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _injected;
    private string? _targetOutboxId;

    internal void Arm(Guid outboxId)
    {
        if (outboxId == Guid.Empty ||
            Interlocked.CompareExchange(
                ref _targetOutboxId,
                outboxId.ToString("D"),
                null) is not null)
        {
            throw new InvalidOperationException("The failure injector can only be armed once.");
        }
    }

    internal Task WaitForInterruptionAsync(TimeSpan timeout) =>
        _interrupted.Task.WaitAsync(timeout);

    public ValueTask OnCheckpointAsync(
        RealtimeOutboxLane lane,
        Guid outboxId,
        RealtimeOutboxCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (lane == targetLane &&
            string.Equals(
                outboxId.ToString("D"),
                Volatile.Read(ref _targetOutboxId),
                StringComparison.Ordinal) &&
            checkpoint == RealtimeOutboxCheckpoint.AfterAllAudiencesPublishedBeforeSettle &&
            Interlocked.CompareExchange(ref _injected, 1, 0) == 0)
        {
            _interrupted.TrySetResult();
            throw new RealtimeOutboxInjectedFailureException();
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingRealtimeConnectionAuthorizer(
    PostgreSqlRealtimeConnectionAuthorizer inner,
    RealtimeAuthorizationRecorder recorder) : IRealtimeConnectionAuthorizer
{
    public ValueTask<ConnectionAuthorizationResult<OperationsConnectionAuthorization>>
        AuthorizeOperationsAsync(
            PrivateRealtimeConnectionRequest request,
            CancellationToken cancellationToken)
    {
        recorder.RecordOperations();
        return inner.AuthorizeOperationsAsync(request, cancellationToken);
    }

    public async ValueTask<ConnectionAuthorizationResult<DriverConnectionAuthorization>>
        AuthorizeDriverAsync(
            PrivateRealtimeConnectionRequest request,
            CancellationToken cancellationToken)
    {
        var result = await inner.AuthorizeDriverAsync(request, cancellationToken);
        if (result.IsAuthorized && result.Authorization is not null)
        {
            recorder.RecordDriverAuthorized(result.Authorization.DriverId);
        }

        return result;
    }

    public async ValueTask<ConnectionAuthorizationResult<TrackingConnectionAuthorization>>
        AuthorizeTrackingAsync(
            string exactToken,
            CancellationToken cancellationToken)
    {
        recorder.RecordTracking();
        try
        {
            return await inner.AuthorizeTrackingAsync(exactToken, cancellationToken);
        }
        catch (Exception exception)
        {
            recorder.RecordTrackingException(exception);
            throw;
        }
    }
}
