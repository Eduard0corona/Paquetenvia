using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Drivers.Endpoints;
using Drivers.Infrastructure;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orders.Application.Tracking;
using Orders.Endpoints;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.Infrastructure.Security.RateLimiting;
using Realtime.Application.Authorization;
using Realtime.Endpoints;

namespace Paqueteria.IntegrationTests.Security;

/// <summary>
/// SEC-004 (phase 4 PR 2): one registry of the rate-limit policies, one rejection writer and one 429 shape, over the
/// assembled API. Every 429 is AI-05 <c>TooManyRequests</c> as <c>application/problem+json</c> with the request's
/// <c>traceId</c>, <c>Retry-After</c> in whole seconds and <c>Cache-Control: no-store</c>; the public tracking
/// surfaces keep their uniform body without <c>traceId</c>, and <c>publicTracking</c> keeps its privacy headers and
/// its counter and, by the owner's decision of 2026-10-13, now also says how long to wait (<c>Retry-After</c>).
/// Partitions isolate subjects and networks, and no log line or metric carries a partition key, subject or address.
/// </summary>
public sealed class Sec004RateLimitPlumbingTests
{
    private const string UniformBody = """{"type":"about:blank","title":"Too Many Requests","status":429}""";
    private const string GpsPath = "/api/v1/driver/me/location-updates";
    private const string TrackingToken = "sec004-synthetic-tracking-token";
    private const string NetworkA = "203.0.113.41";
    private const string NetworkB = "203.0.113.42";
    private const string Ipv6A = "2001:db8:41:1::7";
    private const string Ipv6SameSlash64 = "2001:db8:41:1:ffff::9";
    private const string Ipv6B = "2001:db8:41:2::7";
    private const string DriverSubject = "mock-subject-active-driver";
    private const string DispatcherSubject = "mock-subject-active-dispatcher";

    [Fact]
    public async Task Gps_ingestion_429_is_the_TooManyRequests_problem_with_trace_id_and_retry_after()
    {
        await using var host = new Sec004Factory(gpsLimit: 1);
        using var client = host.CreateClient();

        using var first = await client.SendAsync(Gps(MockIdentityProfiles.ActiveDriver, NetworkA));
        using var second = await client.SendAsync(Gps(MockIdentityProfiles.ActiveDriver, NetworkA));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        await AssertProblemAsync(second, maximumRetryAfter: 60);
    }

    [Theory]
    [InlineData("operations", MockIdentityProfiles.ActiveDispatcher)]
    [InlineData("driver", MockIdentityProfiles.ActiveDriver)]
    public async Task Private_hub_negotiate_429_is_the_TooManyRequests_problem_with_trace_id_and_retry_after(
        string hub,
        string profile)
    {
        await using var host = new Sec004Factory(connectionLimit: 1);
        using var client = host.CreateClient();

        using var first = await client.SendAsync(Negotiate(hub, profile, NetworkA));
        using var second = await client.SendAsync(Negotiate(hub, profile, NetworkA));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        await AssertProblemAsync(second, maximumRetryAfter: 300);
    }

    [Fact]
    public async Task Tracking_hub_negotiate_429_is_the_uniform_public_body_with_retry_after()
    {
        await using var host = new Sec004Factory(connectionLimit: 1);
        using var client = host.CreateClient();

        using var first = await client.SendAsync(Negotiate("tracking", profile: null, NetworkA));
        using var second = await client.SendAsync(Negotiate("tracking", profile: null, NetworkA));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.ToString());
        Assert.Equal(UniformBody, await second.Content.ReadAsStringAsync());
        AssertRetryAfter(second, maximumSeconds: 300);
        Assert.Equal("no-store", second.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Public_tracking_429_keeps_its_uniform_body_headers_and_counter()
    {
        await using var host = new Sec004Factory(trackingLimit: 1);
        using var client = host.CreateClient();
        using var metrics = new RateLimitMetrics(host);

        using var first = await client.SendAsync(Tracking(NetworkA));
        using var second = await client.SendAsync(Tracking(NetworkA));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        // #244: exactly application/problem+json and the same bytes as before SEC-004, without traceId.
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.ToString());
        Assert.Equal(UniformBody, await second.Content.ReadAsStringAsync());
        // Owner decision (2026-10-13), "Sí, agregarlo (Recommended)": it says how long to wait, like every other 429.
        // The fixed window reports its full window, so the value is the configured window: constant, about no token.
        Assert.Equal("300", Assert.Single(second.Headers.GetValues("Retry-After")));
        AssertRetryAfter(second, maximumSeconds: 300);
        Assert.DoesNotContain("traceId", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // Its privacy headers are unchanged.
        Assert.Equal("no-store, private", second.Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", second.Headers.Pragma.ToString());
        Assert.Equal("no-referrer", Assert.Single(second.Headers.GetValues("Referrer-Policy")));
        // Its own counter (public_tracking.rate_limit.rejected) still counts, once, and so does the policy counter.
        Assert.Equal(1, host.PublicTrackingRejections);
        Assert.Equal([PublicTrackingEndpointDefaults.RateLimitPolicy], metrics.Policies());
    }

    [Fact]
    public async Task Subjects_do_not_share_a_budget_and_a_subject_keeps_its_budget_across_networks()
    {
        await using var host = new Sec004Factory(gpsLimit: 1, connectionLimit: 1);
        using var client = host.CreateClient();

        using var driver = await client.SendAsync(Gps(MockIdentityProfiles.ActiveDriver, NetworkA));
        using var otherDriver = await client.SendAsync(Gps(MockIdentityProfiles.SecondaryDriver, NetworkA));
        using var driverElsewhere = await client.SendAsync(Gps(MockIdentityProfiles.ActiveDriver, NetworkB));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, driver.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherDriver.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, driverElsewhere.StatusCode);

        using var dispatcher = await client.SendAsync(Negotiate("operations", MockIdentityProfiles.ActiveDispatcher, NetworkA));
        using var viewer = await client.SendAsync(Negotiate("operations", MockIdentityProfiles.ActiveViewer, NetworkA));
        using var dispatcherAgain = await client.SendAsync(Negotiate("operations", MockIdentityProfiles.ActiveDispatcher, NetworkB));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, dispatcher.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, viewer.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, dispatcherAgain.StatusCode);
    }

    [Fact]
    public async Task Anonymous_networks_do_not_share_a_budget()
    {
        await using var host = new Sec004Factory(connectionLimit: 1, trackingLimit: 1);
        using var client = host.CreateClient();

        // The tracking hub (anonymous): one budget per IPv4 address and per IPv6 /64.
        using var a = await client.SendAsync(Negotiate("tracking", profile: null, NetworkA));
        using var b = await client.SendAsync(Negotiate("tracking", profile: null, NetworkB));
        using var aAgain = await client.SendAsync(Negotiate("tracking", profile: null, NetworkA));
        using var v6 = await client.SendAsync(Negotiate("tracking", profile: null, Ipv6A));
        using var v6Other = await client.SendAsync(Negotiate("tracking", profile: null, Ipv6B));
        using var v6Same = await client.SendAsync(Negotiate("tracking", profile: null, Ipv6SameSlash64));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, a.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, b.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, aAgain.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, v6.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, v6Other.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, v6Same.StatusCode);

        // Public tracking (always per network): its budget is its own, not the hub's.
        using var lookupA = await client.SendAsync(Tracking(NetworkA));
        using var lookupB = await client.SendAsync(Tracking(NetworkB));
        using var lookupAAgain = await client.SendAsync(Tracking(NetworkA));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, lookupA.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, lookupB.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, lookupAAgain.StatusCode);
    }

    [Fact]
    public async Task Rejections_are_counted_once_by_policy_name_only()
    {
        await using var host = new Sec004Factory(gpsLimit: 1, connectionLimit: 1, trackingLimit: 1);
        using var client = host.CreateClient();
        using var metrics = new RateLimitMetrics(host);

        await RejectOncePerPolicyAsync(client);

        Assert.Equal(
            new[]
            {
                DriverLocationEndpointOptions.RateLimitPolicy,
                PublicTrackingEndpointDefaults.RateLimitPolicy,
                RealtimeEndpointDefaults.RateLimitPolicy,
            },
            metrics.Policies().Order(StringComparer.Ordinal));
        Assert.All(metrics.Measurements, measurement =>
        {
            Assert.Equal(1, measurement.Value);
            Assert.Equal([RateLimitTelemetry.PolicyTag], measurement.Tags.Select(tag => tag.Key));
        });
    }

    [Fact]
    public async Task No_log_line_trace_or_metric_carries_the_partition_key_the_subject_or_the_address()
    {
        var logs = new CapturingLoggerProvider();
        // Recording listeners (all data), stricter than the API's own propagation-only listener (OBS-003).
        using var hostingActivities = new CapturingActivityListener("Microsoft.AspNetCore");
        using var applicationActivities = new CapturingActivityListener(TraceCorrelation.ActivitySourceName);
        await using var host = new Sec004Factory(gpsLimit: 1, connectionLimit: 1, trackingLimit: 1, logs: logs);
        using var client = host.CreateClient();
        using var allMetrics = new AllMetrics();

        await RejectOncePerPolicyAsync(client);
        using (var first = await client.SendAsync(Negotiate("tracking", profile: null, Ipv6A)))
        using (var rejected = await client.SendAsync(Negotiate("tracking", profile: null, Ipv6SameSlash64)))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }

        // The capture is real: the rate limiter and hosting wrote lines for these requests.
        Assert.Contains(logs.Entries, entry => entry.Contains("Request starting", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, entry => entry.Contains("Microsoft.AspNetCore.RateLimiting", StringComparison.Ordinal));
        Assert.Contains(allMetrics.Entries, entry => entry.Contains(RateLimitTelemetry.RejectedInstrument, StringComparison.Ordinal));
        Assert.Contains(hostingActivities.Entries, entry => entry.StartsWith("stop ", StringComparison.Ordinal) &&
            entry.Contains("HttpRequestIn", StringComparison.Ordinal));
        var activities = hostingActivities.Entries.Concat(applicationActivities.Entries).ToArray();

        var secrets = new List<string> { DriverSubject, DispatcherSubject };
        foreach (var address in new[] { NetworkA, NetworkB, Ipv6A, Ipv6SameSlash64 })
        {
            var parsed = IPAddress.Parse(address);
            secrets.Add(parsed.ToString());
            secrets.Add(ClientNetworkPartition.Describe(parsed));
            secrets.Add(ClientNetworkPartition.Key(parsed));
        }

        secrets.Add("2001:db8:41:1::");
        secrets.Add(SubjectPartition.Key(DriverSubject));
        secrets.Add(SubjectPartition.Key(DispatcherSubject));
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(logs.Entries, entry => entry.Contains(secret, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(allMetrics.Entries, entry => entry.Contains(secret, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(activities, entry => entry.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Every_limited_endpoint_names_a_registered_policy_and_every_policy_is_used()
    {
        using var host = new DeployedConfigurationFactory();
        var registry = host.Services.GetRequiredService<RateLimitPolicyRegistry>();
        var limited = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(endpoint => (endpoint.DisplayName, Policy: endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName))
            .Where(item => item.Policy is not null)
            .ToArray();

        Assert.NotEmpty(limited);
        Assert.All(limited, item => Assert.NotNull(registry.Find(item.Policy)));
        // GET /auth/login is mapped only with Authentication:Provider=AuthCenter; Sec004HeavyEndpointLimitsTests checks
        // that it names the sign-in policy there.
        Assert.Equal(
            registry.Policies.Select(policy => policy.Name)
                .Where(name => name != Identity.Endpoints.AuthCenter.AuthCenterDefaults.SignInRateLimitPolicy)
                .Order(StringComparer.Ordinal),
            limited.Select(item => item.Policy!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

        // Only the public tracking surfaces answer the uniform body.
        var uniform = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<PublicTrackingRateLimitRejection>() is not null)
            .Select(endpoint => ((RouteEndpoint)endpoint).RoutePattern.RawText!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains(PublicTrackingEndpointDefaults.Path, uniform);
        Assert.All(uniform, pattern => Assert.True(
            pattern == PublicTrackingEndpointDefaults.Path ||
                pattern.StartsWith(RealtimeEndpointDefaults.TrackingPath, StringComparison.Ordinal),
            pattern));
        Assert.Contains(uniform, pattern => pattern.StartsWith(RealtimeEndpointDefaults.TrackingPath + "/negotiate", StringComparison.Ordinal));
    }

    [Fact]
    public void The_composed_limiter_options_are_exactly_the_registry()
    {
        // Whatever any module registers (Configure<RateLimiterOptions>, an alias using or a custom limiter), the
        // composed options must be the registry's: its writer alone, its policies alone and no global limiter.
        using var host = new DeployedConfigurationFactory();
        var options = host.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        var registry = host.Services.GetRequiredService<RateLimitPolicyRegistry>();

        var onRejected = Assert.Single(Assert.IsType<Func<OnRejectedContext, CancellationToken, ValueTask>>(options.OnRejected)
            .GetInvocationList());
        Assert.Equal(typeof(RateLimitRejectionWriter).GetMethod(nameof(RateLimitRejectionWriter.WriteAsync)), onRejected.Method);
        Assert.Null(options.GlobalLimiter);
        Assert.Equal(StatusCodes.Status429TooManyRequests, options.RejectionStatusCode);
        Assert.Equal(
            registry.Policies.Select(policy => policy.Name).Order(StringComparer.Ordinal),
            RegisteredPolicyNames(options).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Existing_limits_keep_their_numbers()
    {
        // The API's own configuration (appsettings.json, no override), as deployed.
        using var host = new DeployedConfigurationFactory();
        var registry = host.Services.GetRequiredService<RateLimitPolicyRegistry>();

        // The policies PR 2 brought together; Sec004HeavyEndpointLimitsTests pins the decided list of PR 3.
        var existing = new[]
        {
            DriverLocationEndpointOptions.RateLimitPolicy,
            PublicTrackingEndpointDefaults.RateLimitPolicy,
            RealtimeEndpointDefaults.RateLimitPolicy,
        };
        var actual = registry.Policies
            .Where(policy => existing.Contains(policy.Name, StringComparer.Ordinal))
            .Select(policy => (policy.Name, policy.Partition, Limit: policy.Limit(host.Services)))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(15, registry.Policies.Count);
        Assert.All(registry.Policies.Where(policy => existing.Contains(policy.Name, StringComparer.Ordinal)), policy =>
        {
            // They keep the framework's own fixed window: no shared window, no in-flight rule.
            Assert.Null(policy.SharesWindowOf);
            Assert.Null(policy.InFlightLimit);
            Assert.False(registry.UsesBudgets(policy));
        });
        Assert.Equal(
            new[]
            {
                (DriverLocationEndpointOptions.RateLimitPolicy, RateLimitPartitionKind.SubjectOrClientNetwork,
                    new FixedWindowLimit(30, TimeSpan.FromSeconds(60))),
                (PublicTrackingEndpointDefaults.RateLimitPolicy, RateLimitPartitionKind.ClientNetwork,
                    new FixedWindowLimit(60, TimeSpan.FromSeconds(60))),
                (RealtimeEndpointDefaults.RateLimitPolicy, RateLimitPartitionKind.SubjectOrClientNetwork,
                    new FixedWindowLimit(20, TimeSpan.FromSeconds(60))),
            },
            actual);
        Assert.Equal("Drivers.LocationIngestion", DriverLocationEndpointOptions.RateLimitPolicy);
        Assert.Equal("PublicTrackingLookup", PublicTrackingEndpointDefaults.RateLimitPolicy);
        Assert.Equal("Realtime.Connections", RealtimeEndpointDefaults.RateLimitPolicy);

        // Recipient calls keep their database-backed limit (not a limiter policy): 3 per order in 15 min, 20 per hour.
        var calls = host.Services.GetRequiredService<IOptions<DriversOptions>>().Value.RecipientCalls;
        Assert.Equal((3, 15, 20), (calls.MaximumPerOrder, calls.OrderWindowMinutes, calls.MaximumPerDriverPerHour));
    }

    /// <summary>
    /// <c>RateLimiterOptions</c> keeps its named policies in two internal maps (<c>AddPolicy</c> with a partition
    /// function, and <c>AddPolicy&lt;TPolicy&gt;</c>); reading them is the only way to list what was registered. If the
    /// framework renames them, this fails loudly rather than passing.
    /// </summary>
    private static IEnumerable<string> RegisteredPolicyNames(RateLimiterOptions options) =>
        new[] { "PolicyMap", "UnactivatedPolicyMap" }.SelectMany(name =>
        {
            var property = typeof(RateLimiterOptions).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"RateLimiterOptions.{name} is gone; update this test.");
            return ((IDictionary)property.GetValue(options)!).Keys.Cast<string>();
        });

    private static async Task RejectOncePerPolicyAsync(HttpClient client)
    {
        foreach (var make in new Func<HttpRequestMessage>[]
                 {
                     () => Gps(MockIdentityProfiles.ActiveDriver, NetworkA),
                     () => Negotiate("operations", MockIdentityProfiles.ActiveDispatcher, NetworkB),
                     () => Tracking(NetworkA),
                 })
        {
            using var first = await client.SendAsync(make());
            using var rejected = await client.SendAsync(make());
            Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, int maximumRetryAfter)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.ToString());
        AssertRetryAfter(response, maximumRetryAfter);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = problem.RootElement;
        Assert.Equal(
            ["status", "title", "traceId", "type"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("about:blank", root.GetProperty("type").GetString());
        Assert.Equal("Too Many Requests", root.GetProperty("title").GetString());
        Assert.Equal(429, root.GetProperty("status").GetInt32());
        Assert.True(TraceCorrelation.IsTraceId(root.GetProperty("traceId").GetString()));
    }

    private static void AssertRetryAfter(HttpResponseMessage response, int maximumSeconds)
    {
        var value = Assert.Single(response.Headers.GetValues("Retry-After"));
        Assert.Matches("^[1-9][0-9]*$", value);
        Assert.InRange(long.Parse(value, CultureInfo.InvariantCulture), 1, maximumSeconds);
    }

    private static HttpRequestMessage Gps(string profile, string peer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GpsPath)
        {
            Content = new StringContent(
                $$"""{"positions":[{"client_event_id":"{{Guid.NewGuid():D}}","lat":24.8091,"lng":-107.394,"accuracy_m":5,"captured_at":"2026-10-12T12:00:00Z"}]}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        request.Headers.Add(PeerAddress.Header, peer);
        return request;
    }

    private static HttpRequestMessage Negotiate(string hub, string? profile, string peer)
    {
        var query = hub == "tracking" ? $"&access_token={TrackingToken}" : string.Empty;
        var request = new HttpRequestMessage(HttpMethod.Post, $"/hubs/{hub}/negotiate?negotiateVersion=1{query}");
        if (profile is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        }

        request.Headers.Add(PeerAddress.Header, peer);
        return request;
    }

    private static HttpRequestMessage Tracking(string peer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tracking/{TrackingToken}");
        request.Headers.Add(PeerAddress.Header, peer);
        return request;
    }

    /// <summary>
    /// The assembled API with the limits under test, a synthetic transport peer per request, a realtime authorizer
    /// that refuses every connection (only the limiter is under test) and an observed public tracking counter.
    /// </summary>
    private sealed class Sec004Factory(
        int gpsLimit = 100,
        int connectionLimit = 100,
        int trackingLimit = 100,
        CapturingLoggerProvider? logs = null) : WebApplicationFactory<Program>
    {
        private int _publicTrackingRejections;

        internal int PublicTrackingRejections => Volatile.Read(ref _publicTrackingRejections);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:Provider"] = "Mock",
                    ["IdentityBootstrap:Provider"] = "Mock",
                    ["Realtime:Provider"] = "SignalR",
                    ["Realtime:AllowedOrigins:0"] = "https://web.synthetic.local",
                    ["Realtime:ConnectionPermitLimit"] = connectionLimit.ToString(CultureInfo.InvariantCulture),
                    ["Realtime:ConnectionWindowSeconds"] = "300",
                    ["PublicTracking:LookupPermitLimit"] = trackingLimit.ToString(CultureInfo.InvariantCulture),
                    ["PublicTracking:LookupWindowSeconds"] = "300",
                    ["Drivers:LocationIngestion:BatchPermitLimit"] = gpsLimit.ToString(CultureInfo.InvariantCulture),
                    ["Drivers:LocationIngestion:WindowSeconds"] = "60",
                    ["ConnectionStrings:Paqueteria"] =
                        "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1",
                    ["Logging:LogLevel:Default"] = "Trace",
                    ["Logging:LogLevel:Microsoft"] = "Trace",
                    ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                }));
            if (logs is not null)
            {
                builder.ConfigureLogging(logging => logging
                    .AddProvider(logs)
                    .SetMinimumLevel(LogLevel.Trace)
                    .AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
            }

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter, PeerAddress>();
                services.AddSingleton<IRealtimeConnectionAuthorizer, RefusingRealtimeAuthorizer>();

                var telemetry = services.Last(descriptor => descriptor.ServiceType == typeof(IPublicTrackingTelemetry));
                var createInner = telemetry.ImplementationFactory
                    ?? throw new InvalidOperationException("The public tracking telemetry registration changed shape.");
                services.Remove(telemetry);
                services.AddSingleton<IPublicTrackingTelemetry>(provider =>
                    new ObservedPublicTrackingTelemetry(
                        (IPublicTrackingTelemetry)createInner(provider),
                        () => Interlocked.Increment(ref _publicTrackingRejections)));
            });
        }
    }

    /// <summary>The API with its own appsettings.json and nothing overridden.</summary>
    private sealed class DeployedConfigurationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
    }

    /// <summary>TestServer has no socket peer: this gives each request the transport address it names.</summary>
    private sealed class PeerAddress : IStartupFilter
    {
        internal const string Header = "X-Test-Transport-Peer";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue(Header, out var peer))
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
                        context.Request.Headers.Remove(Header);
                    }

                    await nextMiddleware(context);
                });
                next(app);
            };
    }

    private sealed class RefusingRealtimeAuthorizer : IRealtimeConnectionAuthorizer
    {
        public ValueTask<ConnectionAuthorizationResult<OperationsConnectionAuthorization>> AuthorizeOperationsAsync(
            PrivateRealtimeConnectionRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ConnectionAuthorizationResult<OperationsConnectionAuthorization>.Rejected);

        public ValueTask<ConnectionAuthorizationResult<DriverConnectionAuthorization>> AuthorizeDriverAsync(
            PrivateRealtimeConnectionRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ConnectionAuthorizationResult<DriverConnectionAuthorization>.Rejected);

        public ValueTask<ConnectionAuthorizationResult<TrackingConnectionAuthorization>> AuthorizeTrackingAsync(
            string exactToken,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ConnectionAuthorizationResult<TrackingConnectionAuthorization>.Rejected);
    }

    private sealed class ObservedPublicTrackingTelemetry(IPublicTrackingTelemetry inner, Action rejected)
        : IPublicTrackingTelemetry
    {
        public void LookupCompleted(string outcome) => inner.LookupCompleted(outcome);

        public void LookupFailed(string category) => inner.LookupFailed(category);

        public void RateLimitRejected()
        {
            rejected();
            inner.RateLimitRejected();
        }
    }

    /// <summary>The rate-limit counter of one host only (its meters come from that host's IMeterFactory).</summary>
    private sealed class RateLimitMetrics : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(long Value, KeyValuePair<string, object?>[] Tags)> _measurements = new();

        internal RateLimitMetrics(WebApplicationFactory<Program> host)
        {
            var scope = host.Services.GetRequiredService<IMeterFactory>();
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RateLimitTelemetry.MeterName &&
                    ReferenceEquals(instrument.Meter.Scope, scope) &&
                    instrument.Name == RateLimitTelemetry.RejectedInstrument)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
                _measurements.Enqueue((value, tags.ToArray())));
            _listener.Start();
        }

        internal IReadOnlyCollection<(long Value, KeyValuePair<string, object?>[] Tags)> Measurements =>
            _measurements.ToArray();

        internal IReadOnlyList<string> Policies() =>
            Measurements.Select(measurement => (string)measurement.Tags.Single().Value!).ToArray();

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Every measurement of every instrument in the process while it lives, as text.</summary>
    private sealed class AllMetrics : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<string> _entries = new();

        internal AllMetrics()
        {
            _listener.InstrumentPublished = (instrument, listener) => listener.EnableMeasurementEvents(instrument);
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.Start();
        }

        internal IReadOnlyCollection<string> Entries => _entries.ToArray();

        public void Dispose() => _listener.Dispose();

        private void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var text = new StringBuilder($"{instrument.Meter.Name} {instrument.Name} {value}");
            foreach (var tag in tags)
            {
                text.Append(CultureInfo.InvariantCulture, $" {tag.Key}={tag.Value}");
            }

            _entries.Enqueue(text.ToString());
        }
    }
}
