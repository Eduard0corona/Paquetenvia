using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Locations.Application.Geocoding;
using Locations.Infrastructure;
using Locations.Infrastructure.Geocoding;
using Locations.Infrastructure.Geocoding.GoogleMaps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Paqueteria.UnitTests.Locations;

/// <summary>
/// GATE-003-PROVIDER-GOOGLE: the Google Maps geocoding adapter against a fake
/// <see cref="HttpMessageHandler"/>. No test reaches the real Google API.
/// </summary>
public sealed class GoogleMapsGeocodingProviderTests
{
    private const string ApiKey = "AIzaSyTEST-not-a-real-key-000000000000";
    private const string Address = "Calle Privada 123, Colonia Sensible, Mazatlán";
    private const double PinLatitude = 23.2494;
    private const double PinLongitude = -106.4111;

    private static readonly GeocodingRequest Request = new(Address, "  Centro   Mazatlán ", PinLatitude, PinLongitude);

    [Fact]
    public async Task A_single_precise_match_replaces_the_pin_and_keeps_the_client_summary()
    {
        var handler = new FakeGoogleHandler(_ => Json(Ok("ROOFTOP", 23.2301234, -106.4209876)));
        using var provider = Create(handler);

        var result = await provider.GeocodeAsync(Request, default);

        Assert.Equal(new GeocodingResult("Centro Mazatlán", 23.2301234, -106.4209876, "GOOGLE_MAPS", false), result);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("https://maps.googleapis.com/maps/api/geocode/json", sent.Uri.GetLeftPart(UriPartial.Path));
        var query = ParseQuery(sent.Uri);
        Assert.Equal(Address, query["address"]);
        Assert.Equal("country:MX", query["components"]);
        Assert.Equal("mx", query["region"]);
        Assert.Equal(ApiKey, query["key"]);
    }

    [Theory]
    [InlineData("ROOFTOP", false, true)]
    [InlineData("RANGE_INTERPOLATED", false, false)]
    [InlineData("GEOMETRIC_CENTER", false, false)]
    [InlineData("APPROXIMATE", false, false)]
    [InlineData("rooftop", false, false)]
    [InlineData("ROOFTOP", true, false)]
    public async Task Only_a_non_partial_rooftop_match_moves_the_pin(string locationType, bool partial, bool resolved)
    {
        // GATE-003-MAPS-PILOT-RULES-2026-10-02: owner literal "Solo ROOFTOP".
        using var provider = Create(new FakeGoogleHandler(_ => Json(Ok(locationType, 23.1, -106.3, partial))));

        var result = await provider.GeocodeAsync(Request, default);

        Assert.Equal(resolved, !result.UsedManualCoordinates);
        Assert.Equal(resolved ? 23.1 : PinLatitude, result.Latitude);
        Assert.Equal(resolved ? -106.3 : PinLongitude, result.Longitude);
        Assert.Equal(resolved ? "GOOGLE_MAPS" : "MANUAL", result.ProviderMode);
    }

    [Fact]
    public async Task A_rooftop_match_without_partial_match_field_is_exact()
    {
        const string body =
            """{"status":"OK","results":[{"geometry":{"location":{"lat":23.1,"lng":-106.3},"location_type":"ROOFTOP"},"address_components":[{"long_name":"México","short_name":"MX","types":["country","political"]}]}]}""";
        using var provider = Create(new FakeGoogleHandler(_ => Json(body)));

        var result = await provider.GeocodeAsync(Request, default);

        Assert.Equal(new GeocodingResult("Centro Mazatlán", 23.1, -106.3, "GOOGLE_MAPS", false), result);
    }

    [Theory]
    [InlineData("""[{"long_name":"United States","short_name":"US","types":["country","political"]}]""")]
    [InlineData("""[{"long_name":"Mazatlán","short_name":"MX","types":["locality","political"]}]""")]
    [InlineData("""[{"long_name":"México","short_name":"mx","types":["country","political"]}]""")]
    [InlineData("""[{"long_name":"México","types":["country","political"]}]""")]
    [InlineData("""[{"long_name":"México","short_name":"MX"}]""")]
    [InlineData("""[]""")]
    [InlineData("""{"short_name":"MX"}""")]
    [InlineData(null)]
    public async Task A_rooftop_match_outside_Mexico_or_without_a_Mexican_country_component_keeps_the_pin(string? components)
    {
        var handler = new FakeGoogleHandler(_ => Json(Ok("ROOFTOP", 23.1, -106.3, components: components)));
        var logger = new CapturingLogger();
        using var provider = Create(handler, options => options.CircuitBreakerFailureThreshold = 1, logger: logger);

        AssertManualPin(await provider.GeocodeAsync(Request, default));
        Assert.Single(handler.Requests);
        Assert.Equal(GoogleMapsCircuitBreaker.State.Closed, provider.CircuitBreaker.CurrentState);
        Assert.Contains(logger.Entries, entry => entry.Contains("(outside_country)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_request_targets_only_the_geocoding_path_and_is_restricted_to_Mexico()
    {
        var handler = new FakeGoogleHandler(_ => Json("""{"status":"ZERO_RESULTS","results":[]}"""));
        using var provider = Create(handler, options =>
        {
            options.BaseUri = "https://maps.example.test/";
            options.Region = string.Empty;
        });

        foreach (var address in new[] { Address, "  Av. del Mar 1, Mazatlán  ", "Directions/json?x=1", "../../distancematrix/json" })
        {
            AssertManualPin(await provider.GeocodeAsync(Request with { AddressText = address }, default));
            var uri = provider.BuildRequestUri(address);
            Assert.Equal("/" + GoogleMapsGeocodingProvider.GeocodePath, uri.AbsolutePath);
            Assert.Equal("country:MX", ParseQuery(uri)["components"]);
        }

        Assert.Equal(4, handler.Requests.Count);
        Assert.All(handler.Requests, sent =>
        {
            Assert.Equal(HttpMethod.Get, sent.Method);
            Assert.Equal("/maps/api/geocode/json", sent.Uri.AbsolutePath);
            Assert.Equal("country:MX", ParseQuery(sent.Uri)["components"]);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("US")]
    [InlineData("mx")]
    [InlineData("MX|country:US")]
    public void The_country_restriction_cannot_be_changed_or_emptied(string country)
    {
        Assert.True(GoogleMapsGeocodingOptions.IsValid(new GoogleMapsGeocodingOptions { ApiKey = ApiKey }));
        Assert.False(GoogleMapsGeocodingOptions.IsValid(new GoogleMapsGeocodingOptions { ApiKey = ApiKey, ComponentsCountry = country }));
        Assert.Throws<InvalidOperationException>(() => Create(new FakeGoogleHandler(_ => Json("{}")), options => options.ComponentsCountry = country));

        using var module = BuildModule(new()
        {
            ["Locations:GeocodingProvider"] = "GoogleMaps",
            ["Locations:GoogleMaps:ApiKey"] = ApiKey,
            ["Locations:GoogleMaps:ComponentsCountry"] = country,
        });
        Assert.Throws<OptionsValidationException>(() => module.GetRequiredService<IOptions<LocationsOptions>>().Value);
    }

    [Theory]
    [InlineData("""{"status":"ZERO_RESULTS","results":[]}""")]
    [InlineData("""{"status":"INVALID_REQUEST","results":[],"error_message":"x"}""")]
    [InlineData("""{"status":"OK","results":[{"geometry":{"location":{"lat":1,"lng":2},"location_type":"ROOFTOP"}},{"geometry":{"location":{"lat":3,"lng":4},"location_type":"ROOFTOP"}}]}""")]
    public async Task No_match_invalid_or_ambiguous_answers_keep_the_pin_without_retry_or_circuit_charge(string body)
    {
        var handler = new FakeGoogleHandler(_ => Json(body));
        using var provider = Create(handler, options => options.CircuitBreakerFailureThreshold = 1);

        var result = await provider.GeocodeAsync(Request, default);

        AssertManualPin(result);
        Assert.Single(handler.Requests);
        Assert.Equal(GoogleMapsCircuitBreaker.State.Closed, provider.CircuitBreaker.CurrentState);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_and_then_succeeds()
    {
        var calls = 0;
        var handler = new FakeGoogleHandler(_ => Interlocked.Increment(ref calls) switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            2 => Json("""{"status":"OVER_QUERY_LIMIT","results":[]}"""),
            _ => Json(Ok("ROOFTOP", 23.2, -106.4)),
        });
        using var provider = Create(handler);

        var result = await provider.GeocodeAsync(Request, default);

        Assert.False(result.UsedManualCoordinates);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Persistent_provider_failure_falls_back_to_the_pin_after_the_bounded_retries()
    {
        var handler = new FakeGoogleHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var provider = Create(handler, options => options.MaxRetries = 2);

        AssertManualPin(await provider.GeocodeAsync(Request, default));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("""{"status":"REQUEST_DENIED","results":[],"error_message":"The provided API key is invalid."}""", HttpStatusCode.OK)]
    [InlineData("""{"status":"OVER_DAILY_LIMIT","results":[]}""", HttpStatusCode.OK)]
    [InlineData("{}", HttpStatusCode.Forbidden)]
    [InlineData("not json", HttpStatusCode.OK)]
    [InlineData("""{"status":"OK","results":[{"geometry":{"location":{"lat":123,"lng":2},"location_type":"ROOFTOP"}}]}""", HttpStatusCode.OK)]
    public async Task Denied_or_malformed_answers_are_not_retried_but_charge_the_circuit(string body, HttpStatusCode status)
    {
        var handler = new FakeGoogleHandler(_ => Json(body, status));
        using var provider = Create(handler, options => options.CircuitBreakerFailureThreshold = 1);

        AssertManualPin(await provider.GeocodeAsync(Request, default));
        Assert.Single(handler.Requests);
        Assert.Equal(GoogleMapsCircuitBreaker.State.Open, provider.CircuitBreaker.CurrentState);
    }

    [Fact]
    public async Task An_attempt_that_exceeds_its_timeout_falls_back_to_the_pin()
    {
        var handler = new FakeGoogleHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        using var provider = Create(handler, options =>
        {
            options.AttemptTimeoutMilliseconds = 200;
            options.MaxRetries = 1;
        });

        AssertManualPin(await provider.GeocodeAsync(Request, default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Network_errors_and_oversized_bodies_fall_back_to_the_pin()
    {
        using var network = Create(
            new FakeGoogleHandler(_ => throw new HttpRequestException("connection refused")),
            options => options.MaxRetries = 0);
        AssertManualPin(await network.GeocodeAsync(Request, default));

        var huge = "{\"status\":\"OK\",\"results\":[],\"pad\":\"" + new string('x', GoogleMapsGeocodingProvider.MaxResponseBytes) + "\"}";
        using var oversized = Create(new FakeGoogleHandler(_ => Json(huge)), options => options.MaxRetries = 0);
        AssertManualPin(await oversized.GeocodeAsync(Request, default));
    }

    [Fact]
    public async Task The_circuit_opens_skips_the_provider_and_recovers_after_one_half_open_probe()
    {
        var time = new ManualTimeProvider();
        var healthy = false;
        var handler = new FakeGoogleHandler(_ => healthy
            ? Json(Ok("ROOFTOP", 23.2, -106.4))
            : new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var provider = Create(handler, options =>
        {
            options.MaxRetries = 0;
            options.CircuitBreakerFailureThreshold = 2;
            options.CircuitBreakerBreakSeconds = 30;
        }, time);

        AssertManualPin(await provider.GeocodeAsync(Request, default));
        AssertManualPin(await provider.GeocodeAsync(Request, default));
        Assert.Equal(GoogleMapsCircuitBreaker.State.Open, provider.CircuitBreaker.CurrentState);

        AssertManualPin(await provider.GeocodeAsync(Request, default));
        Assert.Equal(2, handler.Requests.Count);

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(GoogleMapsCircuitBreaker.State.HalfOpen, provider.CircuitBreaker.CurrentState);
        AssertManualPin(await provider.GeocodeAsync(Request, default));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(GoogleMapsCircuitBreaker.State.Open, provider.CircuitBreaker.CurrentState);

        time.Advance(TimeSpan.FromSeconds(31));
        healthy = true;
        Assert.False((await provider.GeocodeAsync(Request, default)).UsedManualCoordinates);
        Assert.Equal(GoogleMapsCircuitBreaker.State.Closed, provider.CircuitBreaker.CurrentState);
    }

    [Fact]
    public async Task A_saturated_bulkhead_falls_back_at_once_without_calling_the_provider()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeGoogleHandler(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return Json(Ok("ROOFTOP", 23.2, -106.4));
        });
        using var provider = Create(handler, options => options.MaxConcurrentRequests = 1);

        var first = provider.GeocodeAsync(Request, default);
        await entered.Task;
        AssertManualPin(await provider.GeocodeAsync(Request, default));
        release.SetResult();

        Assert.False((await first).UsedManualCoordinates);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_does_not_charge_the_circuit()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new FakeGoogleHandler(async (_, token) =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        using var provider = Create(handler, options => options.CircuitBreakerFailureThreshold = 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GeocodeAsync(Request, cancellation.Token));
        Assert.Equal(GoogleMapsCircuitBreaker.State.Closed, provider.CircuitBreaker.CurrentState);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task An_invalid_pin_is_rejected_before_any_provider_call()
    {
        var handler = new FakeGoogleHandler(_ => Json(Ok("ROOFTOP", 23.2, -106.4)));
        using var provider = Create(handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            provider.GeocodeAsync(new GeocodingRequest(Address, "Centro", double.NaN, -106.4), default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Logs_never_contain_the_key_the_address_or_the_request_uri()
    {
        var logger = new CapturingLogger();
        var responses = new Queue<Func<HttpResponseMessage>>(
        [
            () => Json("""{"status":"REQUEST_DENIED","results":[],"error_message":"key AIzaSyTEST rejected"}"""),
            () => Json("""{"status":"ZERO_RESULTS","results":[]}"""),
            () => throw new HttpRequestException("GET https://maps.googleapis.com/?key=" + ApiKey),
        ]);
        using var provider = Create(
            new FakeGoogleHandler(_ => responses.Dequeue()()),
            options => options.MaxRetries = 0,
            logger: logger);

        for (var i = 0; i < 3; i++)
        {
            AssertManualPin(await provider.GeocodeAsync(Request, default));
        }

        Assert.Equal(3, logger.Entries.Count);
        foreach (var entry in logger.Entries)
        {
            Assert.DoesNotContain(ApiKey, entry, StringComparison.Ordinal);
            Assert.DoesNotContain("AIzaSy", entry, StringComparison.Ordinal);
            Assert.DoesNotContain("Privada", entry, StringComparison.Ordinal);
            Assert.DoesNotContain("Sensible", entry, StringComparison.Ordinal);
            Assert.DoesNotContain("googleapis", entry, StringComparison.Ordinal);
        }

        Assert.Contains(logger.Entries, entry => entry.Contains("(denied)", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Contains("(zero_results)", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Contains("(network_error)", StringComparison.Ordinal));
    }

    [Fact]
    public void Configuration_selects_the_adapter_only_with_a_valid_key_and_never_echoes_it()
    {
        using (var provider = BuildModule(new() { ["Locations:GeocodingProvider"] = "GoogleMaps", ["Locations:GoogleMaps:ApiKey"] = ApiKey }))
        {
            Assert.IsType<GoogleMapsGeocodingProvider>(provider.GetRequiredService<IGeocodingProvider>());
        }

        using (var provider = BuildModule(new() { ["Locations:GeocodingProvider"] = "Manual", ["Locations:GoogleMaps:ApiKey"] = ApiKey }))
        {
            Assert.IsType<ManualGeocodingProvider>(provider.GetRequiredService<IGeocodingProvider>());
        }

        const string badKey = "bad key with spaces";
        foreach (var settings in new Dictionary<string, string?>[]
                 {
                     new() { ["Locations:GeocodingProvider"] = "GoogleMaps" },
                     new() { ["Locations:GeocodingProvider"] = "GoogleMaps", ["Locations:GoogleMaps:ApiKey"] = badKey },
                     new() { ["Locations:GeocodingProvider"] = "GoogleMaps", ["Locations:GoogleMaps:ApiKey"] = ApiKey, ["Locations:GoogleMaps:BaseUri"] = "http://maps.googleapis.com/" },
                     new() { ["Locations:GeocodingProvider"] = "GoogleMaps", ["Locations:GoogleMaps:ApiKey"] = ApiKey, ["Locations:GoogleMaps:MaxRetries"] = "10" },
                     new() { ["Locations:GeocodingProvider"] = "GoogleMaps", ["Locations:GoogleMaps:ApiKey"] = ApiKey, ["Locations:GoogleMaps:AttemptTimeoutMilliseconds"] = "60000" },
                 })
        {
            using var provider = BuildModule(settings);
            var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<LocationsOptions>>().Value);
            Assert.DoesNotContain(badKey, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(ApiKey, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_named_client_has_no_redirects_and_no_global_timeout()
    {
        using var provider = BuildModule(new() { ["Locations:GeocodingProvider"] = "GoogleMaps", ["Locations:GoogleMaps:ApiKey"] = ApiKey });
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(GoogleMapsGeocodingProvider.HttpClientName);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);

        var factoryOptions = provider.GetRequiredService<IOptionsMonitor<Microsoft.Extensions.Http.HttpClientFactoryOptions>>()
            .Get(GoogleMapsGeocodingProvider.HttpClientName);
        var builder = provider.GetRequiredService<Microsoft.Extensions.Http.HttpMessageHandlerBuilder>();
        builder.Name = GoogleMapsGeocodingProvider.HttpClientName;
        foreach (var action in factoryOptions.HttpMessageHandlerBuilderActions)
        {
            action(builder);
        }

        var primary = Assert.IsType<SocketsHttpHandler>(builder.PrimaryHandler);
        Assert.False(primary.AllowAutoRedirect);
        Assert.False(primary.UseCookies);
    }

    [Fact]
    public async Task Through_the_real_client_factory_no_log_line_carries_the_key_the_address_or_the_uri()
    {
        var sink = new CapturingLoggerProvider();
        var handler = new FakeGoogleHandler(_ => Json(Ok("ROOFTOP", 23.2, -106.4)));
        using var provider = BuildModule(
            new() { ["Locations:GeocodingProvider"] = "GoogleMaps", ["Locations:GoogleMaps:ApiKey"] = ApiKey },
            services =>
            {
                services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(sink));
                services.AddHttpClient(GoogleMapsGeocodingProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
            });

        var result = await provider.GetRequiredService<GoogleMapsGeocodingProvider>().GeocodeAsync(Request, default);

        Assert.False(result.UsedManualCoordinates);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(sink.Entries, entry =>
            entry.Contains(ApiKey, StringComparison.Ordinal) ||
            entry.Contains("Privada", StringComparison.Ordinal) ||
            entry.Contains("googleapis", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertManualPin(GeocodingResult result) =>
        Assert.Equal(new GeocodingResult("Centro Mazatlán", PinLatitude, PinLongitude, "MANUAL", true), result);

    private static GoogleMapsGeocodingProvider Create(
        FakeGoogleHandler handler,
        Action<GoogleMapsGeocodingOptions>? configure = null,
        TimeProvider? time = null,
        CapturingLogger? logger = null)
    {
        var options = new LocationsOptions
        {
            GeocodingProvider = GeocodingProviderKind.GoogleMaps,
            GoogleMaps = new GoogleMapsGeocodingOptions { ApiKey = ApiKey, RetryBaseDelayMilliseconds = 0 },
        };
        configure?.Invoke(options.GoogleMaps);
        return new GoogleMapsGeocodingProvider(
            new FakeHttpClientFactory(handler),
            Options.Create(options),
            time ?? new ManualTimeProvider(),
            (ILogger<GoogleMapsGeocodingProvider>?)logger ?? new CapturingLogger(),
            jitter: _ => TimeSpan.Zero);
    }

    private static ServiceProvider BuildModule(Dictionary<string, string?> settings, Action<IServiceCollection>? configure = null)
    {
        settings["ConnectionStrings:Paqueteria"] = "Host=localhost;Database=not-used;Username=not-used;Password=not-used";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLocationsInfrastructure(configuration, new FixedEnvironment("Production"));
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private const string MexicoComponents =
        """[{"long_name":"Mazatlán","short_name":"Mazatlán","types":["locality","political"]},""" +
        """{"long_name":"México","short_name":"MX","types":["country","political"]}]""";

    private static string Ok(
        string locationType,
        double latitude,
        double longitude,
        bool partial = false,
        string? components = MexicoComponents)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return "{\"status\":\"OK\",\"results\":[{\"formatted_address\":\"Calle Privada 123, Colonia Sensible\"," +
            (components is null ? string.Empty : "\"address_components\":" + components + ",") +
            "\"partial_match\":" + (partial ? "true" : "false") + "," +
            "\"geometry\":{\"location\":{\"lat\":" + latitude.ToString("R", culture) + ",\"lng\":" + longitude.ToString("R", culture) + "}," +
            "\"location_type\":\"" + locationType + "\"}}]}";
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);

    private sealed record SentRequest(HttpMethod Method, Uri Uri);

    private sealed class FakeGoogleHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public FakeGoogleHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        public FakeGoogleHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
            _respond = respond;

        public ConcurrentQueue<SentRequest> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new SentRequest(request.Method, request.RequestUri!));
            return _respond(request, cancellationToken);
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(GoogleMapsGeocodingProvider.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CapturingLogger : ILogger<GoogleMapsGeocodingProvider>
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue($"{logLevel}|{eventId}|{formatter(state, exception)}|{exception}");
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue($"{category}|{logLevel}|{formatter(state, exception)}|{state}|{exception}");
        }
    }

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Gate003";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
