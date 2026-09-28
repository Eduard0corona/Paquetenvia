using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orders.Endpoints;
using Paqueteria.Application.Security;
using Paqueteria.ArchitectureTests.Architecture;
using Paqueteria.Infrastructure.Security;
using Realtime.Endpoints.Connection;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// Invariant 5: the public tracking token travels in the URL path and hub tokens in the access_token query, so the
/// request target must be redacted before ASP.NET Core hosting diagnostics read it. These guards keep that true
/// whatever log sink is added later.
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

    [Theory]
    [InlineData("?access_token=secret-value", "?access_token=redacted", new[] { "secret-value" })]
    [InlineData("?id=abc&access_token=a%2Bb%3D&organization_id=o1", "?id=abc&access_token=redacted&organization_id=o1",
        new[] { "a+b=" })]
    [InlineData("?Access_Token=one&x=1&ACCESS_TOKEN=two", "?Access_Token=redacted&x=1&ACCESS_TOKEN=redacted",
        new[] { "one", "two" })]
    [InlineData("?access%5Ftoken=encoded-key", "?access%5Ftoken=redacted", new[] { "encoded-key" })]
    [InlineData("?access_token=", "?access_token=redacted", new[] { "" })]
    [InlineData("?id=abc", "?id=abc", new string[0])]
    [InlineData("", "", new string[0])]
    public void Hub_access_token_values_are_redacted_and_the_rest_of_the_query_is_kept(
        string query,
        string expectedQuery,
        string[] expectedTokens)
    {
        Assert.Equal(expectedQuery, RealtimeAccessTokenRedaction.RedactQuery(query, out var tokens));
        Assert.Equal(expectedTokens, tokens.ToArray());
    }

    [Theory]
    [InlineData("/hubs/tracking", "?access_token=t1&id=2", "?access_token=redacted&id=2")]
    [InlineData("/HUBS/Tracking/negotiate", "?negotiateVersion=1&access_token=t1", "?negotiateVersion=1&access_token=redacted")]
    [InlineData("/hubs/operations", "?id=x", "?id=x")]
    public void Hub_requests_always_get_the_access_token_feature(string path, string query, string expectedQuery)
    {
        using var provider = RedactorServices().BuildServiceProvider();
        var redactor = Assert.Single(
            provider.GetServices<IRequestTargetRedactor>(),
            item => item.GetType().Name == "RealtimeAccessTokenRedactor");
        var redaction = redactor.Redact(path, query);
        Assert.NotNull(redaction);
        Assert.Equal(path, redaction.Path);
        Assert.Equal(expectedQuery, redaction.QueryString);
        Assert.Equal(typeof(IRealtimeAccessTokenFeature), redaction.FeatureType);
        Assert.DoesNotContain("t1", redaction.Feature.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(redactor.Redact("/api/v1/orders", query));
    }

    [Fact]
    public void Host_registers_one_request_target_redacting_factory_composing_both_module_redactors()
    {
        var services = RedactorServices();
        services.AddSingleton<IHttpContextFactory, DefaultHttpContextFactory>();
        services.AddRequestTargetRedaction();

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(IHttpContextFactory));
        Assert.Equal(typeof(RequestTargetRedactingHttpContextFactory), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(
            ["PublicTrackingPathRedactor", "RealtimeAccessTokenRedactor"],
            services.Where(item => item.ServiceType == typeof(IRequestTargetRedactor))
                .Select(item => item.ImplementationType!.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());

        // HostingApplication reuses a connection's pooled HttpContext (Kestrel IHostContextContainer) through
        // DefaultHttpContextFactory.Initialize, bypassing Create, only when the registered factory IS a
        // DefaultHttpContextFactory. Any other factory is called through Create on every request, which is what
        // keeps the redaction on reused keep-alive and HTTP/2 connections (integration-tested on real Kestrel).
        Assert.False(descriptor.ImplementationType!.IsAssignableTo(typeof(DefaultHttpContextFactory)));
    }

    [Theory]
    [InlineData("/hubs/driver", "?access_token=t1&id=2", "/hubs/driver", "?access_token=redacted&id=2")]
    [InlineData("/api/v1/tracking/token-value", "", "/api/v1/tracking/redacted", "")]
    [InlineData("/health/live", "?a=1", "/health/live", "?a=1")]
    public void Redacting_factory_keeps_the_default_context_lifecycle(
        string path,
        string query,
        string expectedPath,
        string expectedQuery)
    {
        // Same contract as DefaultHttpContextFactory: a fresh context over the server features, the accessor set
        // on Create and cleared on Dispose, and a lazily created request services scope from the root provider.
        var services = RedactorServices();
        services.AddOptions();
        services.AddHttpContextAccessor();
        services.AddRequestTargetRedaction();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpContextFactory>();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();

        var request = new Microsoft.AspNetCore.Http.Features.HttpRequestFeature
        {
            Path = path,
            QueryString = query,
            RawTarget = path + query,
        };
        var features = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>(request);
        features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(
            new Microsoft.AspNetCore.Http.Features.HttpResponseFeature());
        features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(
            new StreamResponseBodyFeature(Stream.Null));

        var context = factory.Create(features);
        Assert.IsType<DefaultHttpContext>(context);
        Assert.Same(features, context.Features);
        Assert.Same(context, accessor.HttpContext);
        Assert.Equal(expectedPath, context.Request.Path.Value);
        Assert.Equal(expectedQuery, context.Request.QueryString.Value ?? string.Empty);
        Assert.Equal(expectedPath + expectedQuery, request.RawTarget);
        Assert.NotNull(context.RequestServices);

        var second = factory.Create(new Microsoft.AspNetCore.Http.Features.FeatureCollection(features));
        Assert.NotSame(context, second);
        factory.Dispose(second);

        factory.Dispose(context);
        Assert.Null(accessor.HttpContext);
    }

    [Fact]
    public void Realtime_middlewares_read_access_tokens_only_from_the_redaction_feature()
    {
        foreach (var file in new[]
                 {
                     "src/Modules/Realtime/Realtime.Endpoints/Connection/RealtimeConnectionGateMiddleware.cs",
                     "src/Modules/Realtime/Realtime.Endpoints/Connection/RealtimePrivateAccessTokenMiddleware.cs",
                 })
        {
            var source = File.ReadAllText(TestRepository.GetPath(file));
            Assert.Contains("GetOriginalAccessTokens()", source, StringComparison.Ordinal);
            // Hub paths are matched case-insensitively, like routing.
            Assert.DoesNotContain("StringComparison.Ordinal)", source, StringComparison.Ordinal);
        }

        var queryReads = SourceFiles()
            .Where(file => CodeWithoutComments(file).Contains("Query[\"access_token\"]", StringComparison.Ordinal))
            .Select(Relative)
            .ToArray();
        Assert.Empty(queryReads);
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
        AssertRedactionIsTheOnlyHttpContextFactory("the API host serves the tracking lookup and the hubs");

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
        foreach (var registration in new[]
                 {
                     "builder.Services.AddRequestTargetRedaction();",
                     "builder.Services.AddOrdersEndpoints(",
                     "builder.Services.AddRealtimeEndpoints(",
                 })
        {
            Assert.True(
                program.Contains(registration, StringComparison.Ordinal),
                $"The API host must call {registration}; {because}.");
        }

        foreach (var (file, registration) in new[]
                 {
                     ("src/Modules/Orders/Orders.Endpoints/DependencyInjection.cs",
                         "services.AddPublicTrackingPathRedaction();"),
                     ("src/Modules/Realtime/Realtime.Endpoints/DependencyInjection.cs",
                         "services.AddRealtimeAccessTokenRedaction();"),
                 })
        {
            Assert.True(
                File.ReadAllText(TestRepository.GetPath(file)).Contains(registration, StringComparison.Ordinal),
                $"{file} must call {registration}; {because}.");
        }

        var redactionFile = TestRepository.Normalize(TestRepository.GetPath(
            "src/BuildingBlocks/Paqueteria.Infrastructure/Security/RequestTargetRedactingHttpContextFactory.cs"));
        var others = SourceFiles()
            .Where(file => TestRepository.Normalize(file) != redactionFile)
            .Where(file => CodeWithoutComments(file).Contains("IHttpContextFactory", StringComparison.Ordinal))
            .Select(Relative)
            .ToArray();
        Assert.True(
            others.Length == 0,
            $"Only the request target redaction may replace IHttpContextFactory ({because}); also found in: "
            + string.Join(", ", others));
    }

    // Source lines that are not // or /// comments: documentation may name the types, code may not use them.
    private static string CodeWithoutComments(string file) => string.Join(
        '\n',
        File.ReadLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static ServiceCollection RedactorServices()
    {
        var services = new ServiceCollection();
        services.AddPublicTrackingPathRedaction();
        services.AddRealtimeAccessTokenRedaction();
        return services;
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
