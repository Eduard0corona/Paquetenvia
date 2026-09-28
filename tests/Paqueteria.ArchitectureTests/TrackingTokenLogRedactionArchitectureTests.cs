using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orders.Endpoints;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// Invariant 5: the public tracking token travels in the URL path, so the request path must be redacted before
/// ASP.NET Core hosting diagnostics read it. These guards keep that true whatever log sink is added later.
/// </summary>
public sealed class TrackingTokenLogRedactionArchitectureTests
{
    // Sinks that write the request path, or every scope (including the hosting RequestPath scope), somewhere.
    private static readonly Regex RequestPathSink = new(
        @"AddHttpLogging|UseHttpLogging|AddW3CLogging|UseW3CLogging|IncludeScopes|AddOpenTelemetry|"
        + @"AzureMonitor|ApplicationInsights|AddSimpleConsole|AddSystemdConsole|"
        + @"AddEventSourceLogger|AddEventLog|AddDebug\(|Serilog|NLog|AddSeq\(",
        RegexOptions.CultureInvariant);

    // Files that register a request-path log sink, each reviewed as served only behind the tracking redaction.
    // Empty today. A new sink fails Request_path_log_sinks_are_reviewed_and_behind_the_tracking_redaction until it
    // is reviewed and listed here with its reason; a stale entry fails it too.
    private static readonly IReadOnlyDictionary<string, string> ReviewedRequestPathSinks =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Theory]
    [InlineData("/api/v1/tracking/AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA",
        "/api/v1/tracking/redacted", "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA")]
    [InlineData("/api/v1/tracking/token-value/", "/api/v1/tracking/redacted/", "token-value")]
    [InlineData("/API/V1/Tracking/token-value", "/API/V1/Tracking/redacted", "token-value")]
    [InlineData("/api/v1/tracking/first/second", "/api/v1/tracking/redacted/redacted", null)]
    [InlineData("/api/v1/tracking//token-value", "/api/v1/tracking//redacted", null)]
    public void Every_path_under_the_tracking_prefix_is_redacted(
        string path,
        string expectedRedacted,
        string? expectedToken)
    {
        Assert.True(PublicTrackingPathRedaction.TryRedact(new PathString(path), out var redacted, out var token));
        Assert.Equal(expectedRedacted, redacted.Value);
        Assert.Equal(expectedToken, token);
        Assert.True(PublicTrackingEndpointDefaults.IsLookupPath(redacted));
    }

    [Theory]
    [InlineData("/api/v1/tracking")]
    [InlineData("/api/v1/tracking/")]
    [InlineData("/api/v1/trackingx/token-value")]
    [InlineData("/api/v1/orders/tracking/token-value")]
    [InlineData("/health/live")]
    public void Other_paths_are_left_untouched(string path)
    {
        Assert.False(PublicTrackingPathRedaction.TryRedact(new PathString(path), out var redacted, out var token));
        Assert.Equal(path, redacted.Value);
        Assert.Null(token);
    }

    [Fact]
    public void Orders_endpoints_replace_the_host_http_context_factory_with_the_redacting_one()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextFactory, DefaultHttpContextFactory>();
        services.AddOrdersEndpoints(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IHttpContextFactory));
        Assert.Equal("PublicTrackingRedactingHttpContextFactory", descriptor.ImplementationType?.Name);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        // HostingApplication reuses a connection's pooled HttpContext (Kestrel IHostContextContainer) through
        // DefaultHttpContextFactory.Initialize, bypassing Create, only when the registered factory IS a
        // DefaultHttpContextFactory. Any other factory is called through Create on every request, which is what
        // keeps the redaction on reused keep-alive and HTTP/2 connections (integration-tested on real Kestrel).
        Assert.False(descriptor.ImplementationType!.IsAssignableTo(typeof(DefaultHttpContextFactory)));
    }

    [Theory]
    [InlineData("/hubs/driver", "/hubs/driver")]
    [InlineData("/api/v1/tracking/token-value", "/api/v1/tracking/redacted")]
    public void Redacting_factory_keeps_the_default_context_lifecycle(string path, string expectedPath)
    {
        // Same contract as DefaultHttpContextFactory: a fresh context over the server features, the accessor set
        // on Create and cleared on Dispose, and a lazily created request services scope from the root provider.
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddHttpContextAccessor();
        services.AddPublicTrackingPathRedaction();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpContextFactory>();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();

        var features = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>(
            new Microsoft.AspNetCore.Http.Features.HttpRequestFeature { Path = path });
        features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(
            new Microsoft.AspNetCore.Http.Features.HttpResponseFeature());
        features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(
            new StreamResponseBodyFeature(Stream.Null));

        var context = factory.Create(features);
        Assert.IsType<DefaultHttpContext>(context);
        Assert.Same(features, context.Features);
        Assert.Same(context, accessor.HttpContext);
        Assert.Equal(expectedPath, context.Request.Path.Value);
        Assert.NotNull(context.RequestServices);

        var second = factory.Create(new Microsoft.AspNetCore.Http.Features.FeatureCollection(features));
        Assert.NotSame(context, second);
        factory.Dispose(second);

        factory.Dispose(context);
        Assert.Null(accessor.HttpContext);
    }

    [Fact]
    public void Lookup_endpoint_never_binds_the_token_from_the_route()
    {
        var findAsync = typeof(PublicTrackingEndpoints).GetMethod(
            "FindAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(findAsync);
        Assert.DoesNotContain(findAsync.GetParameters(), parameter => parameter.ParameterType == typeof(string));
        Assert.Contains(findAsync.GetParameters(), parameter => parameter.ParameterType == typeof(HttpContext));
    }

    [Fact]
    public void Api_host_registers_the_redaction_and_nothing_else_replaces_the_http_context_factory() =>
        AssertRedactionIsTheOnlyHttpContextFactory("the API host serves the public tracking lookup");

    [Fact]
    public void Request_path_log_sinks_are_reviewed_and_behind_the_tracking_redaction()
    {
        // Every sink matched here writes the request path, the query or the hosting RequestPath scope somewhere
        // (HTTP/W3C logging, scope-including formatters, OpenTelemetry or Application Insights exporters, other
        // providers). Each one must be reviewed and listed, and the redaction must stay wired for all of them.
        var sinks = SourceFiles()
            .Concat(Directory.EnumerateFiles(TestRepository.GetPath("src"), "appsettings*.json", SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file))
            .Where(file => RequestPathSink.IsMatch(File.ReadAllText(file)))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var unreviewed = sinks.Where(sink => !ReviewedRequestPathSinks.ContainsKey(sink)).ToArray();
        Assert.True(
            unreviewed.Length == 0,
            "Request-path log sinks registered without review against the tracking token redaction: "
            + string.Join(", ", unreviewed)
            + ". Check that they only observe the redacted path (the sink must run in a host whose "
            + "IHttpContextFactory is the tracking redaction), then list them in ReviewedRequestPathSinks.");
        var stale = ReviewedRequestPathSinks.Keys
            .Where(sink => !sinks.Contains(sink, StringComparer.Ordinal))
            .ToArray();
        Assert.True(stale.Length == 0, "Stale ReviewedRequestPathSinks entries: " + string.Join(", ", stale));

        foreach (var sink in sinks)
        {
            AssertRedactionIsTheOnlyHttpContextFactory($"{sink} registers a request-path log sink");
        }
    }

    [Fact]
    public void Api_json_console_does_not_include_scopes()
    {
        // Defence in depth: the redaction makes scopes safe, and the API console formatter still keeps them off.
        var program = File.ReadAllText(TestRepository.GetPath("src/Paqueteria.Api/Program.cs"));
        Assert.Contains("builder.Logging.ClearProviders();", program, StringComparison.Ordinal);
        Assert.Contains("builder.Logging.AddJsonConsole();", program, StringComparison.Ordinal);
        foreach (var settings in Directory.EnumerateFiles(
                     TestRepository.GetPath("src/Paqueteria.Api"),
                     "appsettings*.json",
                     SearchOption.TopDirectoryOnly))
        {
            Assert.DoesNotContain("IncludeScopes", File.ReadAllText(settings), StringComparison.Ordinal);
        }
    }

    private static void AssertRedactionIsTheOnlyHttpContextFactory(string because)
    {
        var program = File.ReadAllText(TestRepository.GetPath("src/Paqueteria.Api/Program.cs"));
        Assert.True(
            program.Contains("builder.Services.AddOrdersEndpoints(", StringComparison.Ordinal),
            $"The API host must register the Orders endpoints and with them the tracking redaction; {because}.");
        var dependencyInjection = File.ReadAllText(TestRepository.GetPath(
            "src/Modules/Orders/Orders.Endpoints/DependencyInjection.cs"));
        Assert.True(
            dependencyInjection.Contains("services.AddPublicTrackingPathRedaction();", StringComparison.Ordinal),
            $"AddOrdersEndpoints must register the tracking path redaction; {because}.");

        var redactionFile = TestRepository.Normalize(
            TestRepository.GetPath("src/Modules/Orders/Orders.Endpoints/PublicTrackingPathRedaction.cs"));
        var others = SourceFiles()
            .Where(file => TestRepository.Normalize(file) != redactionFile)
            .Where(file => File.ReadAllText(file).Contains("IHttpContextFactory", StringComparison.Ordinal))
            .Select(Relative)
            .ToArray();
        Assert.True(
            others.Length == 0,
            $"Only the tracking redaction may replace IHttpContextFactory ({because}); also found in: "
            + string.Join(", ", others));
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(TestRepository.GetPath("src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file));

    private static bool IsBuildOutput(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
            || file.Contains($"{separator}obj{separator}", StringComparison.Ordinal);
    }

    private static string Relative(string file) =>
        Path.GetRelativePath(TestRepository.Root, file).Replace(Path.DirectorySeparatorChar, '/');
}
