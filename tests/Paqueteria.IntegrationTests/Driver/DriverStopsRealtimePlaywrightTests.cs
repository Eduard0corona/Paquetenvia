using Identity.Infrastructure.Mock;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Realtime;
using Paqueteria.IntegrationTests.Security;
using Xunit.Abstractions;

namespace Paqueteria.IntegrationTests.Driver;

[Collection(DriverStopsRealtimeCollection.Name)]
public sealed class DriverStopsRealtimePlaywrightTests(
    PostgreSqlSecurityWebApplicationFactory database,
    ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Real_DriverHub_signal_debounces_and_resynchronizes_from_REST()
    {
        // DIAGNOSTIC ONLY (driver PWA cold CI correlation probe): the timeline
        // observes the existing sequence; no wait, timeout or retry changed.
        var timeline = new RealtimeDiagnosticTimeline(
            nameof(Real_DriverHub_signal_debounces_and_resynchronizes_from_REST));
        var nextPort = DriverStopsNextServer.ReservePort();
        var nextOrigin = $"http://127.0.0.1:{nextPort}";
        var recorder = new RealtimeAuthorizationRecorder { Diagnostics = timeline };
        var scenario = await database.CreateDriverAudienceOutboxScenarioAsync(
            available: false);
        timeline.Record(
            "test",
            $"scenario created driver={RealtimeDiagnosticTimeline.Short(scenario.DriverId)} outbox={RealtimeDiagnosticTimeline.Short(scenario.AssignmentOutboxId)}");
        await using var api = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            recorder,
            workerConnectionString: database.WorkerConnectionString,
            failureInjector: new RealtimeDiagnosticCheckpointObserver(
                timeline,
                scenario.AssignmentOutboxId),
            logProvider: new RealtimeDiagnosticLoggerProvider(timeline),
            allowedOrigin: nextOrigin,
            enableDispatch: true,
            enableDriverApiCors: true,
            configureLogging: logging =>
            {
                foreach (var category in RealtimeDiagnosticLoggerProvider.Categories)
                {
                    logging.AddFilter<RealtimeDiagnosticLoggerProvider>(
                        category,
                        LogLevel.Debug);
                }
            },
            configureDiagnosticServices: services =>
                RealtimeDiagnosticPublisher.Decorate(services, timeline, scenario.DriverId));
        var apiAddress = api.Start();
        timeline.Record("test", "api started");
        await using var web = await DriverStopsNextServer.StartAsync(
            apiAddress.GetLeftPart(UriPartial.Authority),
            nextPort);
        timeline.Record("test", "next started");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
        });
        var page = await context.NewPageAsync();
        timeline.Record("test", "browser page created");
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaDriverSession = {
              organizationId: "{{PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId:D}}",
              cacheNamespace: "opaque-real-driver-01",
              getAccessToken: () => "{{MockIdentityProfiles.ActiveDriver}}",
            };
            """);
        var restRequests = 0;
        page.Request += (_, request) =>
        {
            if (request.Url.EndsWith(
                    "/api/v1/driver/me/stops",
                    StringComparison.Ordinal))
            {
                var ordinal = Interlocked.Increment(ref restRequests);
                timeline.Record("browser", $"REST #{ordinal} request {request.Method} /api/v1/driver/me/stops");
            }
            else if (request.Url.Contains("/hubs/driver", StringComparison.Ordinal))
            {
                timeline.Record("browser", $"hub request {request.Method} {Describe(request.Url)}");
            }
        };
        page.Response += (_, response) =>
        {
            if (response.Url.EndsWith("/api/v1/driver/me/stops", StringComparison.Ordinal))
            {
                timeline.Record("browser", $"REST response {response.Status} /api/v1/driver/me/stops");
            }
            else if (response.Url.Contains("/hubs/driver", StringComparison.Ordinal))
            {
                timeline.Record("browser", $"hub response {response.Status} {Describe(response.Url)}");
            }
            else if (response.Status >= 400)
            {
                timeline.Record("browser", $"response {response.Status} {Describe(response.Url)}");
            }
        };
        page.RequestFailed += (_, request) =>
            timeline.Record("browser", $"request FAILED {request.Method} {Describe(request.Url)} {request.Failure}");
        page.Console += (_, message) =>
        {
            if (message.Type is "error" or "warning")
            {
                timeline.Record("browser", $"console.{message.Type} {message.Text}");
            }
        };
        page.PageError += (_, error) =>
            timeline.Record("browser", $"page error {error}");
        page.WebSocket += (_, socket) =>
        {
            if (!socket.Url.Contains("/hubs/", StringComparison.Ordinal))
            {
                // Next.js dev HMR socket: irrelevant to the probe.
                return;
            }

            timeline.Record("browser", $"websocket opened {Describe(socket.Url)}");
            socket.FrameSent += (_, frame) =>
                timeline.Record("browser", $"ws sent {DescribeFrame(frame)}");
            socket.FrameReceived += (_, frame) =>
                timeline.Record("browser", $"ws received {DescribeFrame(frame)}");
            socket.SocketError += (_, error) =>
                timeline.Record("browser", $"ws error {error}");
            socket.Close += (_, _) =>
                timeline.Record("browser", "ws closed");
        };

        try
        {
            timeline.Record("test", "navigation begin /driver/stops");
            await page.GotoAsync(new Uri(web.BaseAddress, "/driver/stops").AbsoluteUri);
            timeline.Record("test", "navigation end");
            await page.GetByText(
                    PostgreSqlSecurityWebApplicationFactory.ValidPublicOrderId,
                    new() { Exact = true })
                .WaitForAsync();
            timeline.Record("test", "initial DOM ready (order id visible)");
            Assert.Equal(1, Volatile.Read(ref restRequests));
            timeline.Record("test", "restRequests == 1 asserted");

            timeline.Record("test", "MakeBusinessOutboxAvailableAsync begin");
            await database.MakeBusinessOutboxAvailableAsync(scenario.AssignmentOutboxId);
            timeline.Record("test", "MakeBusinessOutboxAvailableAsync end");
            await page.WaitForFunctionAsync(
                "() => document.body.textContent.includes('Actualizado')");
            timeline.Record("test", "'Actualizado' wait completed");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            timeline.Record("test", "polling loop begin (20s test-owned budget)");
            while (Volatile.Read(ref restRequests) < 2 &&
                   !timeout.IsCancellationRequested)
            {
                await Task.Delay(50, timeout.Token);
            }

            timeline.Record(
                "test",
                $"polling loop end restRequests={Volatile.Read(ref restRequests)} cancelled={timeout.IsCancellationRequested}");
            Assert.True(
                Volatile.Read(ref restRequests) >= 2,
                "DriverHub did not trigger a REST resynchronization.");
            var body = await page.Locator("body").InnerTextAsync();
            Assert.Contains(
                PostgreSqlSecurityWebApplicationFactory.ValidPublicOrderId,
                body);
            Assert.DoesNotContain(scenario.AssignmentId.ToString("D"), body);
            Assert.DoesNotContain(scenario.DriverId.ToString("D"), body);
            timeline.Record("test", "assertions passed");
        }
        catch (Exception exception)
        {
            timeline.Record("test", $"FAILED {exception.GetType().Name}: {exception.Message}");
            throw;
        }
        finally
        {
            await RecordConnectionLabelAsync(page, timeline);
            timeline.Record(
                "test",
                $"summary restRequests={Volatile.Read(ref restRequests)} driver_accepted={recorder.DriverAcceptedCount}");
            Flush(timeline);
        }
    }

    private static async Task RecordConnectionLabelAsync(
        IPage page,
        RealtimeDiagnosticTimeline timeline)
    {
        try
        {
            var text = await page.Locator("body").InnerTextAsync(new() { Timeout = 2000 });
            var label = text.Contains("Sin conexión", StringComparison.Ordinal)
                ? "Sin conexión"
                : text.Contains("Reconectando", StringComparison.Ordinal)
                    ? "Reconectando"
                    : text.Contains("Actualizado", StringComparison.Ordinal)
                        ? "Actualizado"
                        : "(none)";
            timeline.Record("browser", $"final connection label={label}");
        }
        catch (Exception exception)
        {
            timeline.Record("browser", $"final connection label unavailable {exception.GetType().Name}");
        }
    }

    private void Flush(RealtimeDiagnosticTimeline timeline)
    {
        var lines = timeline.Snapshot();
        foreach (var line in lines)
        {
            output.WriteLine(line);
        }

        var directory = Environment.GetEnvironmentVariable("PAQUETENVIA_DIAG_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        File.WriteAllLines(
            Path.Combine(
                directory,
                $"driver-pwa-cold-probe-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}Z.log"),
            lines);
    }

    private static string Describe(string url)
    {
        var uri = new Uri(url);
        return uri.AbsolutePath + (uri.Query.Contains("negotiateVersion") ? "?negotiate" : string.Empty);
    }

    private static string DescribeFrame(IWebSocketFrame frame)
    {
        var text = frame.Text;
        if (string.IsNullOrEmpty(text))
        {
            return $"binary {frame.Binary?.Length ?? 0}B";
        }

        var targets = text
            .Split('', StringSplitOptions.RemoveEmptyEntries)
            .Select(static message =>
            {
                var index = message.IndexOf("\"target\":\"", StringComparison.Ordinal);
                if (index < 0)
                {
                    return message.Length <= 40 ? message : message[..40] + "...";
                }

                var start = index + "\"target\":\"".Length;
                var end = message.IndexOf('"', start);
                return "invocation target=" + message[start..end];
            });
        return string.Join(" | ", targets) + $" ({text.Length}B)";
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DriverStopsRealtimeCollection
    : ICollectionFixture<PostgreSqlSecurityWebApplicationFactory>
{
    public const string Name = "Driver stops realtime";
}
