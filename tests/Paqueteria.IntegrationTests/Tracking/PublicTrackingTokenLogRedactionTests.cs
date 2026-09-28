using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
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
        var unknown = RandomToken();
        var lookups = new (string Token, string Path, HttpStatusCode Status)[]
        {
            (PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken,
                $"/api/v1/tracking/{PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken}",
                HttpStatusCode.OK),
            (unknown, $"/api/v1/tracking/{unknown}", HttpStatusCode.NotFound),
            // Malformed: padded, wrong length and characters outside Base64URL, and a routing-case variant.
            (unknown + "=", $"/api/v1/tracking/{unknown}=", HttpStatusCode.NotFound),
            ("malformedTRKsecret.value~1", "/api/v1/tracking/malformedTRKsecret.value~1", HttpStatusCode.NotFound),
            ("upperCasePathTRKsecret", "/API/V1/TRACKING/upperCasePathTRKsecret", HttpStatusCode.NotFound),
        };

        var logs = new CapturingLoggerProvider();
        using var activities = new CapturingActivityListener("Microsoft.AspNetCore");
        await using var host = factory.WithWebHostBuilder(builder =>
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
        });
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

        var logEntries = logs.Entries;
        var activityEntries = activities.Entries;
        // The capture is real: hosting opened a request scope and started a request activity for every lookup.
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

        // Privacy headers apply to every path routing sends to the lookup, whatever its casing.
        Assert.All(cacheControl, value => Assert.Equal("no-store, private", value));
    }

    [Fact]
    public void Host_uses_the_tracking_path_redacting_http_context_factory()
    {
        var httpContextFactory = factory.Services.GetRequiredService<IHttpContextFactory>();
        Assert.Equal(
            "PublicTrackingRedactingHttpContextFactory",
            httpContextFactory.GetType().Name);
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
