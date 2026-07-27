using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Microsoft.Playwright;
using Npgsql;
using Paqueteria.IntegrationTests.Driver;
using Paqueteria.IntegrationTests.Realtime;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Operations;

[Collection(OperationsDashboardPostgreSqlCollection.Name)]
public sealed class OperationsDashboardPwaPlaywrightTests(
    PostgreSqlSecurityWebApplicationFactory database)
{
    private const string SecondTenantPublicOrderId = "ORD_obs_tenant_b";

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Real_pipeline_refreshes_isolates_reconnects_and_never_uses_external_maps()
    {
        await SeedSecondTenantOrderAsync(database.AdminConnectionString);
        var nextPort = DriverStopsNextServer.ReservePort();
        var nextOrigin = $"http://127.0.0.1:{nextPort}";
        var recorder = new RealtimeAuthorizationRecorder();
        RealtimeKestrelWebApplicationFactory? api =
            new(
                database.ApplicationConnectionString,
                recorder,
                workerConnectionString: database.WorkerConnectionString,
                allowedOrigin: nextOrigin,
                enableDriverApiCors: true,
                configurationOverrides: DashboardConfiguration());
        RealtimeKestrelWebApplicationFactory? restoredApi = null;
        try
        {
            var apiAddress = api.Start();
            using (var apiClient = new HttpClient { BaseAddress = apiAddress })
            using (var dashboardRequest = new HttpRequestMessage(
                       HttpMethod.Get,
                       "/api/v1/operations/dashboard"))
            {
                dashboardRequest.Headers.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        MockIdentityProfiles.ActiveMultiOrganization);
                dashboardRequest.Headers.Add(
                    "X-Organization-Id",
                    PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId
                        .ToString("D"));
                using var dashboardResponse =
                    await apiClient.SendAsync(dashboardRequest);
                var dashboardJson =
                    await dashboardResponse.Content.ReadAsStringAsync();
                Assert.True(
                    dashboardResponse.IsSuccessStatusCode,
                    $"Dashboard bootstrap failed: {(int)dashboardResponse.StatusCode} {dashboardJson}");
                Assert.Contains(
                    SecondTenantPublicOrderId,
                    dashboardJson,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    PostgreSqlSecurityWebApplicationFactory.ValidPublicOrderId,
                    dashboardJson,
                    StringComparison.Ordinal);
            }
            await using var web = await DriverStopsNextServer.StartAsync(
                apiAddress.GetLeftPart(UriPartial.Authority),
                nextPort);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = true });
            await using var context = await browser.NewContextAsync(
                new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = 390, Height = 844 },
                    TimezoneId = "America/New_York",
                });
            await InstallSessionAsync(
                context,
                PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId);
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(20_000);
            var requests = new ConcurrentQueue<IRequest>();
            var browserDiagnostics = new ConcurrentQueue<string>();
            page.Request += (_, request) => requests.Enqueue(request);
            page.Response += (_, browserResponse) =>
                browserDiagnostics.Enqueue(
                    $"{browserResponse.Status}:{browserResponse.Url}");
            page.Console += (_, message) =>
            {
                if (message.Type == "error")
                {
                    browserDiagnostics.Enqueue($"console:{message.Text}");
                }
            };
            page.PageError += (_, error) =>
                browserDiagnostics.Enqueue($"page:{error}");

            var response = await page.GotoAsync(
                new Uri(web.BaseAddress, "/ops/dashboard").AbsoluteUri);
            Assert.NotNull(response);
            await page.GetByRole(
                    AriaRole.Heading,
                    new() { Name = "Operaciones", Exact = true })
                .WaitForAsync();
            await Task.Delay(3_000);
            var initialBody = await page.Locator("body").InnerTextAsync();
            Assert.True(
                initialBody.Contains(
                    SecondTenantPublicOrderId,
                    StringComparison.Ordinal),
                $"Dashboard did not render the real order. body={initialBody}; " +
                $"browser={string.Join(" | ", browserDiagnostics)}");
            Assert.True(
                await recorder.WaitForNextOperationsAcceptedAsync(
                    TimeSpan.FromSeconds(10)));
            Assert.Contains(
                "no-cache",
                (await response.AllHeadersAsync())["cache-control"],
                StringComparison.Ordinal);
            Assert.Equal(
                "no-referrer",
                (await response.AllHeadersAsync())["referrer-policy"]);
            var contentSecurityPolicy =
                (await response.AllHeadersAsync())["content-security-policy"];
            Assert.Contains(
                $"connect-src 'self' {apiAddress.GetLeftPart(UriPartial.Authority)}",
                contentSecurityPolicy,
                StringComparison.Ordinal);
            Assert.DoesNotContain("*", contentSecurityPolicy, StringComparison.Ordinal);
            foreach (var viewport in new[]
                     {
                         new ViewportSize { Width = 320, Height = 568 },
                         new ViewportSize { Width = 390, Height = 844 },
                         new ViewportSize { Width = 430, Height = 932 },
                         new ViewportSize { Width = 768, Height = 1024 },
                         new ViewportSize { Width = 1440, Height = 900 },
                     })
            {
                await page.SetViewportSizeAsync(viewport.Width, viewport.Height);
                Assert.False(
                    await page.EvaluateAsync<bool>(
                        "() => document.documentElement.scrollWidth > document.documentElement.clientWidth"),
                    $"Dashboard overflowed at {viewport.Width}x{viewport.Height}.");
            }

            var statusFilter = page.GetByRole(
                AriaRole.Combobox,
                new() { Name = "Estado", Exact = true });
            await statusFilter.SelectOptionAsync("DELIVERING");
            await page.GetByText(
                    SecondTenantPublicOrderId,
                    new() { Exact = true })
                .WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Detached,
                });
            await statusFilter.SelectOptionAsync("");
            await page.GetByText(
                    SecondTenantPublicOrderId,
                    new() { Exact = true })
                .WaitForAsync();

            await SwitchOrganizationAsync(
                page,
                PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId);
            await page.GetByText(
                    SecondTenantPublicOrderId,
                    new() { Exact = true })
                .WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Detached,
                });
            await page.GetByText(
                    PostgreSqlSecurityWebApplicationFactory.ValidPublicOrderId,
                    new() { Exact = true })
                .WaitForAsync();
            Assert.True(
                await recorder.WaitForNextOperationsAcceptedAsync(
                    TimeSpan.FromSeconds(10)));

            var dashboardRequests = DashboardRequestCount(requests);
            var statusEvent = await database.EnqueueRealtimeStatusAsync();
            Assert.Equal(
                "PROCESSED",
                await WaitForOutboxAsync(database, statusEvent.OutboxId));
            await WaitForDashboardRequestAsync(requests, dashboardRequests);
            await page.Locator(".opsOrderCard .opsStatus")
                .GetByText("En reparto", new() { Exact = true })
                .WaitForAsync();

            await page.GetByRole(
                    AriaRole.Button,
                    new() { Name = "Posiciones", Exact = true })
                .ClickAsync();
            var locationRequests = DashboardRequestCount(requests);
            var locationOutboxId = await database.EnqueueRealtimeLocationAsync();
            Assert.Equal(
                "PROCESSED",
                await WaitForLocationOutboxAsync(database, locationOutboxId));
            await WaitForDashboardRequestAsync(requests, locationRequests);
            var body = await page.Locator("body").InnerTextAsync();
            Assert.DoesNotContain("24.80", body, StringComparison.Ordinal);
            Assert.DoesNotContain("-107.40", body, StringComparison.Ordinal);
            Assert.DoesNotContain(
                requests,
                request =>
                    new Uri(request.Url).Host is not ("127.0.0.1" or "localhost"));

            await page.GetByRole(
                    AriaRole.Button,
                    new() { Name = "Lista", Exact = true })
                .ClickAsync();
            var requestCountBeforeReconnect = DashboardRequestCount(requests);
            var apiPort = apiAddress.Port;
            await api.DisposeAsync();
            api = null;
            await page.GetByText("Reconectando", new() { Exact = true }).WaitForAsync();
            restoredApi = new RealtimeKestrelWebApplicationFactory(
                database.ApplicationConnectionString,
                recorder,
                apiPort,
                database.WorkerConnectionString,
                allowedOrigin: nextOrigin,
                enableDriverApiCors: true,
                configurationOverrides: DashboardConfiguration());
            Assert.Equal(apiAddress, restoredApi.Start());
            await page.GetByText("Conectada", new() { Exact = true }).WaitForAsync(
                new LocatorWaitForOptions { Timeout = 30_000 });
            await WaitForDashboardRequestAsync(requests, requestCountBeforeReconnect);

            var documentsBeforeDetail = requests.Count(
                request => request.ResourceType == "document");
            await page.GetByRole(
                    AriaRole.Link,
                    new() { Name = "Abrir orden", Exact = true })
                .ClickAsync();
            await page.GetByRole(
                    AriaRole.Heading,
                    new()
                    {
                        Name = PostgreSqlSecurityWebApplicationFactory.ValidPublicOrderId,
                        Exact = true,
                    })
                .WaitForAsync();
            await page.GetByRole(
                    AriaRole.Heading,
                    new() { Name = "Timeline", Exact = true })
                .WaitForAsync();
            Assert.Equal(
                documentsBeforeDetail,
                requests.Count(request => request.ResourceType == "document"));
            Assert.Equal(
                "America/New_York",
                await page.EvaluateAsync<string>(
                    "() => Intl.DateTimeFormat().resolvedOptions().timeZone"));
            Assert.Equal(
                "Horarios mostrados en hora de Mazatlán.",
                (await page.Locator(".opsTimezone").TextContentAsync())?.Trim());
            var timelineTime = page.Locator(".opsTimeline time").First;
            var timelineIso = await timelineTime.GetAttributeAsync("datetime");
            Assert.NotNull(timelineIso);
            Assert.Equal(
                await FormatInMazatlanAsync(page, timelineIso),
                (await timelineTime.TextContentAsync())?.Trim());
            Assert.True(await page.GetByRole(
                    AriaRole.Button,
                    new() { Name = "Asignar repartidor propio", Exact = true })
                .IsDisabledAsync());
            Assert.True(await page.GetByRole(
                    AriaRole.Button,
                    new() { Name = "Abrir incidencia", Exact = true })
                .IsDisabledAsync());

            var persistence = await page.EvaluateAsync<PersistenceEvidence>(
                """
                async () => {
                  const cached = [];
                  for (const key of await caches.keys()) {
                    const cache = await caches.open(key);
                    for (const request of await cache.keys()) cached.push(request.url);
                  }
                  return {
                    local: Object.keys(localStorage).filter(key => !key.startsWith("__next_")),
                    session: Object.keys(sessionStorage).filter(key => !key.startsWith("__next_")),
                    databases: indexedDB.databases
                      ? (await indexedDB.databases())
                          .map(value => value.name ?? "")
                          .filter(name => !name.startsWith("__next_"))
                      : [],
                    cached,
                  };
                }
                """);
            Assert.Empty(persistence.Local);
            Assert.Empty(persistence.Session);
            Assert.Empty(persistence.Databases);
            Assert.DoesNotContain(
                persistence.Cached,
                value =>
                    value.Contains("/ops/", StringComparison.Ordinal) ||
                    value.Contains(
                        "/api/v1/operations/dashboard",
                        StringComparison.Ordinal) ||
                    value.Contains("/hubs/operations", StringComparison.Ordinal));
        }
        finally
        {
            if (api is not null)
            {
                await api.DisposeAsync();
            }
            if (restoredApi is not null)
            {
                await restoredApi.DisposeAsync();
            }
        }
    }

    private static IReadOnlyDictionary<string, string?> DashboardConfiguration() =>
        new Dictionary<string, string?>
        {
            ["OperationsDashboard:Provider"] = "PostgreSql",
            ["OperationsDashboard:CommandTimeoutSeconds"] = "5",
            ["OperationsDashboard:DefaultPageSize"] = "50",
            ["OperationsDashboard:MaximumPageSize"] = "100",
            ["OperationsDashboard:MaximumDateRangeDays"] = "31",
        };

    private static Task InstallSessionAsync(
        IBrowserContext context,
        Guid organizationId) =>
        context.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(organizationId.ToString("D"))}},
              sessionNamespace: "obs-001-browser",
              getAccessToken: () =>
                window.__paquetenviaOperationsSession.organizationId ===
                  {{JsonSerializer.Serialize(PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId.ToString("D"))}}
                  ? {{JsonSerializer.Serialize(MockIdentityProfiles.ActiveMultiOrganization)}}
                  : {{JsonSerializer.Serialize(MockIdentityProfiles.ActivePlatformAdminMfa)}},
              requestOrganizationChange: organizationId => {
                window.__paquetenviaOperationsSession = {
                  ...window.__paquetenviaOperationsSession,
                  organizationId,
                };
                window.dispatchEvent(
                  new Event("paquetenvia:operations-session-changed"));
              },
            };
            """);

    private static async Task SeedSecondTenantOrderAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pricing.quotes(
              id,owner_org_id,city_id,origin_location_id,destination_location_id,
              service_type,pricing_tier,consolidated_route,subtotal_cents,
              discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,
              currency,pricing_policy_version,request_snapshot_redacted,
              package_snapshot,breakdown,input_hash,status,expires_at)
            VALUES (
              '81000000-0000-0000-0000-000000000001',
              '22222222-2222-2222-2222-222222222222',
              '33333333-3333-3333-3333-333333333333',
              '44444444-4444-4444-4444-444444444441',
              '44444444-4444-4444-4444-444444444442',
              'URGENT','OCCASIONAL',false,10000,0,0,10000,10000,'MXN',
              'obs-pwa-v1','{}','[]','{}',decode(repeat('81',32),'hex'),
              'USED',clock_timestamp()+interval '1 day')
            ON CONFLICT DO NOTHING;
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,operator_org_id,city_id,
              origin_location_id,destination_location_id,service_type,
              pricing_tier,consolidated_route,payer_type,status,subtotal_cents,
              discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,
              currency,pricing_policy_version,package_snapshot,
              cod_expected_cents,version,created_at,updated_at)
            VALUES (
              '82000000-0000-0000-0000-000000000001',
              'ORD_obs_tenant_b',
              '81000000-0000-0000-0000-000000000001',
              '22222222-2222-2222-2222-222222222222',
              '22222222-2222-2222-2222-222222222222',
              '33333333-3333-3333-3333-333333333333',
              '44444444-4444-4444-4444-444444444441',
              '44444444-4444-4444-4444-444444444442',
              'URGENT','OCCASIONAL',false,'SENDER','READY_FOR_PICKUP',
              10000,0,0,10000,10000,'MXN','obs-pwa-v1','[]',0,1,
              '2026-07-26T01:00:00Z','2026-07-27T01:00:00Z')
            ON CONFLICT DO NOTHING;
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static Task SwitchOrganizationAsync(IPage page, Guid organizationId) =>
        page.EvaluateAsync(
            """
            organizationId =>
              window.__paquetenviaOperationsSession
                .requestOrganizationChange(organizationId)
            """,
            organizationId.ToString("D"));

    private static int DashboardRequestCount(
        IEnumerable<IRequest> requests) =>
        requests.Count(
            request => request.Url.Contains(
                "/api/v1/operations/dashboard",
                StringComparison.Ordinal));

    private static async Task WaitForDashboardRequestAsync(
        IEnumerable<IRequest> requests,
        int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (DashboardRequestCount(requests) > count)
            {
                return;
            }
            await Task.Delay(50);
        }
        Assert.Fail("The dashboard did not refresh through REST.");
    }

    private static async Task<string?> WaitForOutboxAsync(
        PostgreSqlSecurityWebApplicationFactory database,
        Guid outboxId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            var status = await database.ReadOutboxStatusAsync(outboxId);
            if (status == "PROCESSED")
            {
                return status;
            }
            await Task.Delay(50, timeout.Token);
        }
        return await database.ReadOutboxStatusAsync(outboxId);
    }

    private static async Task<string?> WaitForLocationOutboxAsync(
        PostgreSqlSecurityWebApplicationFactory database,
        Guid outboxId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            var status = await database.ReadLocationOutboxStatusAsync(outboxId);
            if (status == "PROCESSED")
            {
                return status;
            }
            await Task.Delay(50, timeout.Token);
        }
        return await database.ReadLocationOutboxStatusAsync(outboxId);
    }

    private static Task<string> FormatInMazatlanAsync(
        IPage page,
        string value) =>
        page.EvaluateAsync<string>(
            """
            value => new Intl.DateTimeFormat("es-MX", {
              dateStyle: "medium",
              timeStyle: "short",
              timeZone: "America/Mazatlan",
              hourCycle: "h23",
            }).format(new Date(value))
            """,
            value);

    private sealed class PersistenceEvidence
    {
        public string[] Local { get; set; } = [];
        public string[] Session { get; set; } = [];
        public string[] Databases { get; set; } = [];
        public string[] Cached { get; set; } = [];
    }
}
