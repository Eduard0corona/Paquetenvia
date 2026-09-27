using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Paqueteria.IntegrationTests.Security;

/// <summary>
/// HTTP hardening of the API host: explicit REST CORS policy, trusted forwarded headers,
/// HSTS outside Development, client-network rate-limit partitions and request body limits.
/// </summary>
public sealed class HttpHardeningTests
{
    private const string AllowedOrigin = "https://web.synthetic.test";
    private const string ForeignOrigin = "https://attacker.synthetic.test";
    private const string ProxyAddress = "10.20.0.5";

    [Theory]
    [InlineData("/api/v1/orders", "GET")]
    [InlineData("/api/v1/quotes", "POST")]
    [InlineData("/api/v1/routes/7b0cf3c4-7ad7-4b8d-9b1a-3a4d8e5f6a70/stops/order", "PUT")]
    [InlineData("/api/v1/routes/7b0cf3c4-7ad7-4b8d-9b1a-3a4d8e5f6a70/stops/9c0cf3c4-7ad7-4b8d-9b1a-3a4d8e5f6a71", "DELETE")]
    public async Task Rest_preflight_from_a_configured_origin_is_allowed(string path, string method)
    {
        await using var factory = new HardeningFactory(new()
        {
            ["Http:Cors:AllowedOrigins:0"] = AllowedOrigin,
        });
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight(path, method, AllowedOrigin));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(AllowedOrigin, Single(response, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Single(response, "Access-Control-Allow-Credentials"));
        Assert.Contains(method, Single(response, "Access-Control-Allow-Methods"), StringComparison.Ordinal);
        var allowedHeaders = Single(response, "Access-Control-Allow-Headers");
        Assert.Contains("Authorization", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Idempotency-Key", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Organization-Id", allowedHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/api/v1/orders", "GET")]
    [InlineData("/api/v1/quotes", "POST")]
    [InlineData("/api/v1/routes/7b0cf3c4-7ad7-4b8d-9b1a-3a4d8e5f6a70/stops/order", "PUT")]
    public async Task Rest_preflight_from_a_foreign_origin_is_denied(string path, string method)
    {
        await using var factory = new HardeningFactory(new()
        {
            ["Http:Cors:AllowedOrigins:0"] = AllowedOrigin,
        });
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight(path, method, ForeignOrigin));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Rest_cors_denies_every_origin_by_default_because_the_web_is_same_origin()
    {
        await using var factory = new HardeningFactory([]);
        using var client = factory.CreateClient();

        using var preflight = await client.SendAsync(Preflight("/api/v1/quotes", "POST", AllowedOrigin));
        using var simple = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        simple.Headers.Add("Origin", AllowedOrigin);
        using var simpleResponse = await client.SendAsync(simple);

        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(HttpStatusCode.OK, simpleResponse.StatusCode);
        Assert.False(simpleResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Rest_preflight_rejects_an_unlisted_method_and_header()
    {
        await using var factory = new HardeningFactory(new()
        {
            ["Http:Cors:AllowedOrigins:0"] = AllowedOrigin,
        });
        using var client = factory.CreateClient();

        using var patch = await client.SendAsync(Preflight("/api/v1/orders", "PATCH", AllowedOrigin));
        using var header = await client.SendAsync(
            Preflight("/api/v1/orders", "GET", AllowedOrigin, "X-Unlisted-Header"));

        // A browser only proceeds when the method/header is echoed back as allowed.
        Assert.DoesNotContain("PATCH", Granted(patch, "Access-Control-Allow-Methods"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Unlisted-Header", Granted(header, "Access-Control-Allow-Headers"), StringComparison.OrdinalIgnoreCase);

        static string Granted(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : string.Empty;
    }

    [Fact]
    public async Task Public_tracking_and_realtime_keep_their_own_cors_policies()
    {
        await using var factory = new HardeningFactory(new()
        {
            ["Http:Cors:AllowedOrigins:0"] = AllowedOrigin,
        });
        using var client = factory.CreateClient();

        using var tracking = await client.SendAsync(
            Preflight("/api/v1/tracking/abc", "GET", AllowedOrigin, "Accept"));

        // The public tracking policy has no configured origins in this host, so the REST
        // default policy must not leak into it.
        Assert.False(tracking.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Untrusted_peer_cannot_spoof_its_client_address_with_forwarded_headers()
    {
        await using var factory = new HardeningFactory(TrackingLimitOfOne());
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(Tracking("198.51.100.1", peer: ProxyAddress));
        using var second = await client.SendAsync(Tracking("198.51.100.2", peer: ProxyAddress));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task Clients_behind_a_trusted_proxy_get_their_own_rate_limit_partition()
    {
        var settings = TrackingLimitOfOne();
        settings["Http:ForwardedHeaders:KnownProxies:0"] = ProxyAddress;
        await using var factory = new HardeningFactory(settings);
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(Tracking("198.51.100.1", peer: ProxyAddress));
        using var second = await client.SendAsync(Tracking("198.51.100.2", peer: ProxyAddress));
        using var repeated = await client.SendAsync(Tracking("198.51.100.1", peer: ProxyAddress));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, repeated.StatusCode);
    }

    [Fact]
    public async Task Trusted_network_is_configurable_as_cidr()
    {
        var settings = TrackingLimitOfOne();
        settings["Http:ForwardedHeaders:KnownNetworks:0"] = "10.20.0.0/16";
        await using var factory = new HardeningFactory(settings);
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(Tracking("198.51.100.1", peer: "10.20.9.9"));
        using var second = await client.SendAsync(Tracking("198.51.100.2", peer: "10.20.9.9"));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task Ipv6_clients_share_a_rate_limit_partition_per_64_prefix()
    {
        var settings = TrackingLimitOfOne();
        settings["Http:ForwardedHeaders:KnownProxies:0"] = ProxyAddress;
        await using var factory = new HardeningFactory(settings);
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(Tracking("2001:db8:1:2::1", peer: ProxyAddress));
        using var sameSubnet = await client.SendAsync(Tracking("2001:db8:1:2::ffff", peer: ProxyAddress));
        using var otherSubnet = await client.SendAsync(Tracking("2001:db8:1:3::1", peer: ProxyAddress));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, sameSubnet.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherSubnet.StatusCode);
    }

    [Fact]
    public async Task Hsts_is_sent_on_https_outside_development()
    {
        await using var factory = new HardeningFactory([], environment: "Production");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://api.synthetic.test"),
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync("/health/live");

        var hsts = Single(response, "Strict-Transport-Security");
        Assert.Contains("max-age=31536000", hsts, StringComparison.Ordinal);
        Assert.Contains("includeSubDomains", hsts, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hsts_is_not_sent_in_development()
    {
        await using var factory = new HardeningFactory([], environment: "Development");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://api.synthetic.test"),
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync("/health/live");

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Forwarded_proto_from_a_trusted_proxy_enables_hsts_behind_tls_termination()
    {
        await using var trusted = new HardeningFactory(new()
        {
            ["Http:ForwardedHeaders:KnownProxies:0"] = ProxyAddress,
        }, environment: "Production");
        await using var untrusted = new HardeningFactory([], environment: "Production");

        var plainHttp = new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://api.synthetic.test"),
            AllowAutoRedirect = false,
        };
        using var trustedResponse = await trusted.CreateClient(plainHttp).SendAsync(Proto(ProxyAddress));
        using var untrustedResponse = await untrusted.CreateClient(plainHttp).SendAsync(Proto(ProxyAddress));

        Assert.True(trustedResponse.Headers.Contains("Strict-Transport-Security"));
        Assert.False(untrustedResponse.Headers.Contains("Strict-Transport-Security"));

        static HttpRequestMessage Proto(string peer)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            request.Headers.Add(PeerAddressStartupFilter.Header, peer);
            request.Headers.Add("X-Forwarded-Proto", "https");
            return request;
        }
    }

    [Fact]
    public async Task Json_body_over_the_default_limit_is_rejected_before_authentication()
    {
        await using var factory = new HardeningFactory([]);
        using var client = factory.CreateClient();
        using var content = new ByteArrayContent(new byte[(1024 * 1024) + 1]);
        content.Headers.ContentType = new("application/json");

        using var response = await client.PostAsync("/api/v1/quotes", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Json_body_within_the_default_limit_reaches_the_endpoint_pipeline()
    {
        await using var factory = new HardeningFactory([]);
        using var client = factory.CreateClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/v1/quotes", content);

        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Csv_multipart_keeps_its_own_one_mebibyte_file_limit()
    {
        await using var factory = new HardeningFactory([]);
        using var client = factory.CreateClient();
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[1024 * 1024]), "file", "orders.csv");

        using var response = await client.PostAsync("/api/v1/orders/csv/preview", content);

        // A full 1 MiB CSV plus its multipart envelope exceeds the 1 MiB JSON default, but
        // CSV-001 owns its own limit, so the request is not rejected by the host-wide cap.
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Body_limit_is_configurable()
    {
        await using var factory = new HardeningFactory(new()
        {
            ["Http:RequestBody:MaximumBytes"] = "4096",
        });
        using var client = factory.CreateClient();
        using var content = new ByteArrayContent(new byte[4097]);
        content.Headers.ContentType = new("application/json");

        using var response = await client.PostAsync("/api/v1/quotes", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private static Dictionary<string, string?> TrackingLimitOfOne() => new()
    {
        ["PublicTracking:LookupPermitLimit"] = "1",
        ["PublicTracking:LookupWindowSeconds"] = "300",
    };

    private static HttpRequestMessage Tracking(string forwardedFor, string peer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tracking/opaque-synthetic-token");
        request.Headers.Add(PeerAddressStartupFilter.Header, peer);
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return request;
    }

    private static HttpRequestMessage Preflight(
        string path,
        string method,
        string origin,
        string requestHeaders = "authorization,content-type,idempotency-key,x-organization-id")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", requestHeaders);
        return request;
    }

    private static string Single(HttpResponseMessage response, string header) =>
        Assert.Single(response.Headers.GetValues(header));

    private sealed class HardeningFactory(
        Dictionary<string, string?> settings,
        string environment = "Testing") : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter, PeerAddressStartupFilter>());
        }
    }

    /// <summary>
    /// TestServer has no socket peer. This filter runs before the host pipeline and gives the
    /// request the transport peer address the test names, exactly as Kestrel would.
    /// </summary>
    private sealed class PeerAddressStartupFilter : IStartupFilter
    {
        internal const string Header = "X-Test-Transport-Peer";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress =
                        context.Request.Headers.TryGetValue(Header, out var peer)
                            ? IPAddress.Parse(peer.ToString())
                            : IPAddress.Loopback;
                    context.Request.Headers.Remove(Header);
                    await nextMiddleware(context);
                });
                next(app);
            };
    }
}
