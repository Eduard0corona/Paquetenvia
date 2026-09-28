using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Tracking;

/// <summary>
/// Invariant 5: the public tracking token is a bearer-like secret carried in the URL path. It must reach no log
/// message, no log scope and no activity, whatever logging provider, formatter or exporter is configured.
/// </summary>
[Collection(PublicTrackingPostgreSqlCollection.Name)]
[Trait("Category", "PublicTrackingPostgreSql")]
public sealed class PublicTrackingTokenLogRedactionTests(
    PostgreSqlSecurityWebApplicationFactory factory)
{
    [Fact]
    public async Task Lookup_token_reaches_no_log_message_scope_or_activity_at_trace_level()
    {
        var lookups = Lookups();
        var logs = new CapturingLoggerProvider();
        using var activities = new CapturingActivityListener("Microsoft.AspNetCore");
        await using var host = factory.WithWebHostBuilder(builder => ConfigureCapture(builder, logs));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var cacheControl = new List<string?>();
        foreach (var lookup in lookups)
        {
            using var response = await client.GetAsync(lookup.Path);
            Assert.Equal(lookup.Status, response.StatusCode);
            cacheControl.Add(response.Headers.CacheControl?.ToString());
        }

        AssertNoLeak(lookups, logs.Entries, activities.Entries);

        // Privacy headers apply to every path routing sends to the lookup, whatever its casing.
        Assert.All(cacheControl, value => Assert.Equal("no-store, private", value));
    }

    [Fact]
    public async Task Lookup_token_is_redacted_on_every_request_of_reused_kestrel_connections()
    {
        // Kestrel keeps one hosting context per connection (IHostContextContainer); TestServer does not. Several
        // lookups over one persistent HTTP/1.1 connection and one HTTP/2 connection prove every request is covered.
        var lookups = Lookups();
        var logs = new CapturingLoggerProvider();
        using var activities = new CapturingActivityListener("Microsoft.AspNetCore");
        await using var host = factory.WithWebHostBuilder(builder => ConfigureCapture(builder, logs));
        host.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);
        });
        host.StartServer();
        var addresses = host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses
            .Select(address => new Uri(address, UriKind.Absolute))
            .ToArray();
        Assert.Equal(2, addresses.Length);

        var exercised = new List<Version>();
        foreach (var address in addresses)
        {
            using var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 1,
                PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                AllowAutoRedirect = false,
            };
            using var client = new HttpClient(handler) { BaseAddress = address };
            var version = await ProbeVersionAsync(client);
            exercised.Add(version);
            for (var round = 0; round < 2; round++)
            {
                foreach (var lookup in lookups)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, lookup.Path)
                    {
                        Version = version,
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    };
                    using var response = await client.SendAsync(request);
                    Assert.Equal(lookup.Status, response.StatusCode);
                    Assert.Equal(version, response.Version);
                    Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
                }
            }
        }

        Assert.Equal([HttpVersion.Version11, HttpVersion.Version20], exercised.Order().ToArray());
        var logEntries = logs.Entries;
        AssertNoLeak(lookups, logEntries, activities.Entries);

        // The lookups really shared connections: one Kestrel connection per protocol served all of them, so the
        // hosting context of the first request was reused by every later one.
        var trackingScopes = logEntries
            .Where(entry => entry.StartsWith("Microsoft.AspNetCore.Hosting.Diagnostics scope", StringComparison.Ordinal)
                && entry.Contains("/tracking/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(2 * 2 * lookups.Length, trackingScopes.Length);
        var connections = trackingScopes
            .Select(entry => RequestIdPattern.Match(entry))
            .Where(match => match.Success)
            .Select(match => match.Groups["connection"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, connections.Length);
    }

    [Fact]
    public void Host_uses_the_tracking_path_redacting_http_context_factory()
    {
        var httpContextFactory = factory.Services.GetRequiredService<IHttpContextFactory>();
        Assert.Equal(
            "PublicTrackingRedactingHttpContextFactory",
            httpContextFactory.GetType().Name);
    }

    private static readonly System.Text.RegularExpressions.Regex RequestIdPattern = new(
        @"RequestId=(?<connection>[^:\s]+):[0-9A-F]+",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static (string Token, string Path, HttpStatusCode Status)[] Lookups()
    {
        var unknown = RandomToken();
        return
        [
            (PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken,
                $"/api/v1/tracking/{PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken}",
                HttpStatusCode.OK),
            (unknown, $"/api/v1/tracking/{unknown}", HttpStatusCode.NotFound),
            // Malformed: padded, wrong length and characters outside Base64URL, and a routing-case variant.
            (unknown + "=", $"/api/v1/tracking/{unknown}=", HttpStatusCode.NotFound),
            ("malformedTRKsecret.value~1", "/api/v1/tracking/malformedTRKsecret.value~1", HttpStatusCode.NotFound),
            ("upperCasePathTRKsecret", "/API/V1/TRACKING/upperCasePathTRKsecret", HttpStatusCode.NotFound),
        ];
    }

    private static void ConfigureCapture(IWebHostBuilder builder, CapturingLoggerProvider logs)
    {
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                // A scope-including formatter must be harmless too.
                ["Logging:Console:FormatterName"] = "json",
                ["Logging:Console:FormatterOptions:IncludeScopes"] = "true",
            }));
        builder.ConfigureLogging(logging => logging
            .AddProvider(logs)
            .SetMinimumLevel(LogLevel.Trace)
            .AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
    }

    private static void AssertNoLeak(
        IReadOnlyCollection<(string Token, string Path, HttpStatusCode Status)> lookups,
        IReadOnlyCollection<string> logEntries,
        IReadOnlyCollection<string> activityEntries)
    {
        // The capture is real: hosting opened a request scope and started a request activity for the lookups.
        Assert.Contains(logEntries, entry => entry.Contains("scope", StringComparison.Ordinal)
            && entry.Contains("RequestPath", StringComparison.Ordinal)
            && entry.Contains("/tracking/redacted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logEntries, entry => entry.Contains("Request starting", StringComparison.Ordinal));
        Assert.Contains(activityEntries, entry => entry.StartsWith("stop ", StringComparison.Ordinal)
            && entry.Contains("HttpRequestIn", StringComparison.Ordinal));

        foreach (var lookup in lookups)
        {
            AssertAbsent(lookup.Token, logEntries, "log");
            AssertAbsent(lookup.Token, activityEntries, "activity");
        }
    }

    private static async Task<Version> ProbeVersionAsync(HttpClient client)
    {
        foreach (var version in new[] { HttpVersion.Version20, HttpVersion.Version11 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live")
            {
                Version = version,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            try
            {
                using var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode && response.Version == version)
                {
                    return version;
                }
            }
            catch (HttpRequestException)
            {
            }
        }

        throw new InvalidOperationException("The Kestrel listener answered neither HTTP/2 nor HTTP/1.1.");
    }

    private static void AssertAbsent(string token, IReadOnlyCollection<string> entries, string kind)
    {
        var leaks = entries
            .Where(entry => entry.Contains(token, StringComparison.OrdinalIgnoreCase)
                || entry.Contains(Uri.EscapeDataString(token), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(
            leaks.Length == 0,
            $"The tracking token reached {leaks.Length} {kind} entr(ies):{Environment.NewLine}"
            + string.Join(Environment.NewLine, leaks.Take(10)));
    }

    private static string RandomToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> entries = new();

        internal IReadOnlyCollection<string> Entries => entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category,
            ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                entries.Enqueue($"{category} scope {state} {Describe(state)}");
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(
                    $"{category} {logLevel} {eventId} {formatter(state, exception)} {Describe(state)} {exception}");

            private static string Describe<TState>(TState state) =>
                state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(" ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                    : string.Empty;
        }
    }

    private sealed class CapturingActivityListener : IDisposable
    {
        private readonly ConcurrentQueue<string> entries = new();
        private readonly ActivityListener listener;

        internal CapturingActivityListener(string sourceName)
        {
            listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == sourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                {
                    entries.Enqueue($"sample {options.Name} {Describe(options.Tags)}");
                    return ActivitySamplingResult.AllDataAndRecorded;
                },
                ActivityStarted = activity => entries.Enqueue($"start {Describe(activity)}"),
                ActivityStopped = activity => entries.Enqueue($"stop {Describe(activity)}"),
            };
            ActivitySource.AddActivityListener(listener);
        }

        internal IReadOnlyCollection<string> Entries => entries.ToArray();

        public void Dispose() => listener.Dispose();

        private static string Describe(Activity activity) =>
            $"{activity.OperationName} display={activity.DisplayName} status={activity.StatusDescription} "
            + $"tags={Describe(activity.TagObjects)} "
            + $"baggage={string.Join(" ", activity.Baggage.Select(pair => $"{pair.Key}={pair.Value}"))} "
            + $"events={string.Join(" ", activity.Events.Select(item => $"{item.Name}:{Describe(item.Tags)}"))}";

        private static string Describe(IEnumerable<KeyValuePair<string, object?>>? tags) =>
            tags is null ? string.Empty : string.Join(" ", tags.Select(tag => $"{tag.Key}={tag.Value}"));
    }
}
