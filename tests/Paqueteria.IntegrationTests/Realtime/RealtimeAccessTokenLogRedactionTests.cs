using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Realtime;

/// <summary>
/// Invariant 5: browsers cannot set headers on WebSockets, so SignalR carries the tracking token and private access
/// tokens in the access_token query. Hosting logs the query before any middleware runs; the token must reach no
/// log message, scope or activity for negotiate, long-polling and WebSocket requests, on TestServer and on reused
/// Kestrel connections.
/// </summary>
public sealed class RealtimeAccessTokenLogRedactionTests(RealtimeWebApplicationFactory factory)
    : IClassFixture<RealtimeWebApplicationFactory>
{
    private const char RecordSeparator = '\u001e';
    private const string Handshake = "{\"protocol\":\"json\",\"version\":1}\u001e";
    private static readonly string OrganizationQuery =
        $"organization_id={RealtimeWebApplicationFactory.OrganizationA:D}";

    [Fact]
    public async Task Hub_access_tokens_reach_no_log_message_scope_or_activity_on_negotiate_and_connect()
    {
        var logs = new CapturingLoggerProvider();
        using var activities = new CapturingActivityListener("Microsoft.AspNetCore");
        await using var host = factory.WithWebHostBuilder(builder => ConfigureCapture(builder, logs));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var unknown = RandomToken();

        await ConnectLongPollingAsync(client, "/hubs/tracking", RealtimeWebApplicationFactory.ValidTrackingTokenA, null);
        await ConnectLongPollingAsync(client, "/HUBS/Tracking", RealtimeWebApplicationFactory.ValidTrackingTokenB, null);
        await ConnectLongPollingAsync(client, "/hubs/operations", MockIdentityProfiles.ActiveDispatcher, OrganizationQuery);
        await ConnectLongPollingAsync(client, "/hubs/driver", MockIdentityProfiles.ActiveDriver, OrganizationQuery);
        using (var rejected = await NegotiateAsync(client, "/hubs/tracking", unknown, null))
        {
            Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        }

        AssertNoLeak(
            [
                RealtimeWebApplicationFactory.ValidTrackingTokenA,
                RealtimeWebApplicationFactory.ValidTrackingTokenB,
                MockIdentityProfiles.ActiveDispatcher,
                MockIdentityProfiles.ActiveDriver,
                unknown,
            ],
            logs.Entries,
            activities.Entries);
    }

    [Fact]
    public async Task Hub_access_tokens_are_redacted_on_reused_kestrel_connections_and_websockets()
    {
        var logs = new CapturingLoggerProvider();
        using var activities = new CapturingActivityListener("Microsoft.AspNetCore");
        await using var host = factory.WithWebHostBuilder(builder => ConfigureCapture(builder, logs));
        host.UseKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));
        host.StartServer();
        var address = new Uri(
            host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(),
            UriKind.Absolute);

        // One persistent HTTP/1.1 connection carries every negotiate and long-polling request.
        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 1,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,
        };
        using var client = new HttpClient(handler) { BaseAddress = address };
        for (var round = 0; round < 2; round++)
        {
            await ConnectLongPollingAsync(client, "/hubs/tracking", RealtimeWebApplicationFactory.ValidTrackingTokenA, null);
            await ConnectLongPollingAsync(client, "/hubs/operations", MockIdentityProfiles.ActiveDispatcher, OrganizationQuery);
        }

        // WebSocket transport: the token is only in the upgrade request's query.
        await ConnectWebSocketAsync(client, address, "/hubs/tracking", RealtimeWebApplicationFactory.ValidTrackingTokenB, null);
        await ConnectWebSocketAsync(client, address, "/hubs/driver", MockIdentityProfiles.ActiveDriver, OrganizationQuery);

        var logEntries = logs.Entries;
        AssertNoLeak(
            [
                RealtimeWebApplicationFactory.ValidTrackingTokenA,
                RealtimeWebApplicationFactory.ValidTrackingTokenB,
                MockIdentityProfiles.ActiveDispatcher,
                MockIdentityProfiles.ActiveDriver,
            ],
            logEntries,
            activities.Entries);

        // The HTTP requests really shared one Kestrel connection, so its pooled hosting context was reused.
        var hubScopeConnections = logEntries
            .Where(entry => entry.StartsWith("Microsoft.AspNetCore.Hosting.Diagnostics scope", StringComparison.Ordinal)
                && entry.Contains("/hubs/", StringComparison.OrdinalIgnoreCase))
            .Select(entry => RequestIdPattern.Match(entry))
            .Where(match => match.Success)
            .Select(match => match.Groups["connection"].Value)
            .ToArray();
        Assert.True(hubScopeConnections.Length >= 16, $"Expected at least 16 hub requests, saw {hubScopeConnections.Length}.");
        Assert.True(
            hubScopeConnections.GroupBy(value => value, StringComparer.Ordinal).Max(group => group.Count()) >= 16,
            "The negotiate and long-polling requests did not share one Kestrel connection.");
    }

    private static readonly System.Text.RegularExpressions.Regex RequestIdPattern = new(
        @"RequestId=(?<connection>[^:\s]+):[0-9A-F]+",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static async Task ConnectLongPollingAsync(
        HttpClient client,
        string hub,
        string token,
        string? extraQuery)
    {
        var connectionToken = await NegotiateConnectionTokenAsync(client, hub, token, extraQuery);
        var target = HubTarget(hub, token, extraQuery, connectionToken);

        // The first long poll only starts the transport and returns empty, as the SignalR client expects.
        using (var start = await client.GetAsync(target))
        {
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        }

        using (var send = await client.PostAsync(target, new StringContent(Handshake, Encoding.UTF8, "text/plain")))
        {
            Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        }

        using (var poll = await client.GetAsync(target))
        {
            Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
            var payload = await poll.Content.ReadAsStringAsync();
            Assert.StartsWith("{}", payload, StringComparison.Ordinal);
            Assert.Contains(RecordSeparator, payload);
        }

        using var close = await client.DeleteAsync(target);
        Assert.True(close.IsSuccessStatusCode, $"Closing {hub} returned {(int)close.StatusCode}.");
    }

    private static async Task ConnectWebSocketAsync(
        HttpClient client,
        Uri address,
        string hub,
        string token,
        string? extraQuery)
    {
        var connectionToken = await NegotiateConnectionTokenAsync(client, hub, token, extraQuery);
        var target = new UriBuilder(address)
        {
            Scheme = "ws",
            Path = hub,
            Query = HubTarget(hub, token, extraQuery, connectionToken).Split('?', 2)[1],
        }.Uri;
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(target, timeout.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes(Handshake), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[1024];
        var received = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.StartsWith("{}", Encoding.UTF8.GetString(buffer, 0, received.Count), StringComparison.Ordinal);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    private static async Task<string> NegotiateConnectionTokenAsync(
        HttpClient client,
        string hub,
        string token,
        string? extraQuery)
    {
        using var response = await NegotiateAsync(client, hub, token, extraQuery);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var negotiate = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return negotiate.RootElement.GetProperty("connectionToken").GetString()!;
    }

    private static Task<HttpResponseMessage> NegotiateAsync(
        HttpClient client,
        string hub,
        string token,
        string? extraQuery) =>
        client.PostAsync(
            $"{hub}/negotiate?negotiateVersion=1&{(extraQuery is null ? string.Empty : extraQuery + "&")}"
            + $"access_token={Uri.EscapeDataString(token)}",
            content: null);

    private static string HubTarget(string hub, string token, string? extraQuery, string connectionToken) =>
        $"{hub}?id={Uri.EscapeDataString(connectionToken)}&{(extraQuery is null ? string.Empty : extraQuery + "&")}"
        + $"access_token={Uri.EscapeDataString(token)}";

    private static void ConfigureCapture(IWebHostBuilder builder, CapturingLoggerProvider logs)
    {
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Logging:Console:FormatterName"] = "json",
                ["Logging:Console:FormatterOptions:IncludeScopes"] = "true",
            }));
        builder.ConfigureLogging(logging => logging
            .AddProvider(logs)
            .SetMinimumLevel(LogLevel.Trace)
            .AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
    }

    private static void AssertNoLeak(
        IReadOnlyCollection<string> tokens,
        IReadOnlyCollection<string> logEntries,
        IReadOnlyCollection<string> activityEntries)
    {
        // The capture is real: hosting logged the hub requests and started activities for them.
        Assert.Contains(logEntries, entry => entry.Contains("Request starting", StringComparison.Ordinal)
            && entry.Contains("/hubs/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(activityEntries, entry => entry.StartsWith("stop ", StringComparison.Ordinal)
            && entry.Contains("HttpRequestIn", StringComparison.Ordinal));

        foreach (var token in tokens)
        {
            AssertAbsent(token, logEntries, "log");
            AssertAbsent(token, activityEntries, "activity");
        }

        // Only the value is replaced; the parameter and the rest of the query stay visible to diagnostics.
        Assert.Contains(logEntries, entry => entry.Contains("Request starting", StringComparison.Ordinal)
            && entry.Contains("access_token=redacted", StringComparison.Ordinal));
    }

    private static void AssertAbsent(string token, IReadOnlyCollection<string> entries, string kind)
    {
        var leaks = entries
            .Where(entry => entry.Contains(token, StringComparison.OrdinalIgnoreCase)
                || entry.Contains(Uri.EscapeDataString(token), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(
            leaks.Length == 0,
            $"The access token reached {leaks.Length} {kind} entr(ies):{Environment.NewLine}"
            + string.Join(Environment.NewLine, leaks.Take(10)));
    }

    private static string RandomToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
