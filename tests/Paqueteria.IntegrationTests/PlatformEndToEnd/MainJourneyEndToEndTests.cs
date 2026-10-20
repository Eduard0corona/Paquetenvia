using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Paqueteria.Infrastructure.Database.Pooling;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.IntegrationTests.Custody;
using Paqueteria.IntegrationTests.Driver;
using Paqueteria.IntegrationTests.Observability;
using Realtime.Application.Configuration;
using Xunit.Abstractions;

namespace Paqueteria.IntegrationTests.PlatformEndToEnd;

/// <summary>
/// OPS-005, owner answer Q-PR10 "Recorrido principal (Recommended)" (decision OPS-005-JOURNEYS-2026-10-13): quote,
/// create and confirm the order, assign a driver, pick up, deliver with a photo and see the order delivered on public
/// tracking, through the real screens of the production web build, against the assembled API and Worker on
/// PostgreSQL/PostGIS and the S3-compatible storage. During that synthetic workflow it also measures four platform
/// checks of AI-08 OPS-005:
/// <list type="bullet">
/// <item>one browser action ("Asignar") has one trace id, in its API log lines and in its two audit rows;</item>
/// <item>the outbox event that action commits after an idle period reaches its SignalR client within AI-15
/// <c>event_delivery_p95_seconds</c> (2 s);</item>
/// <item>the PostgreSQL backends of each runtime role stay within the SCL-007 budget the whole time;</item>
/// <item>with the telemetry export off nothing leaves the process.</item>
/// </list>
/// The 429 check and the error leg of the trace check are in <see cref="RateLimitEndToEndTests"/>.
/// </summary>
[Collection(PlatformEndToEndCollection.Name)]
public sealed class MainJourneyEndToEndTests(PlatformEndToEndFixture fixture, ITestOutputHelper output)
{
    private const string AssignmentCostMxn = "45.00";

    /// <summary>
    /// Longer than both realtime ramps (business 250 + 500 ms, location 500 ms) plus their claims, so each lane is in
    /// its longest wait (1 s) when the browser action commits; the idle period of the SCL-007 delivery test.
    /// </summary>
    private static readonly TimeSpan IdlePeriod = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan DeliveryBudget =
        TimeSpan.FromMilliseconds(OutboxDispatcherOptionsValidator.EventDeliveryBudgetMilliseconds);

    private static readonly string[] ExpectedStatusChanges =
    [
        "CONFIRMED",
        "READY_FOR_PICKUP",
        "ASSIGNED",
        "AT_PICKUP",
        "PICKED_UP",
        "IN_TRANSIT",
        "DELIVERING",
        "DELIVERED",
    ];

    [Fact]
    [Trait("Category", PlatformEndToEndCategory.Name)]
    public async Task Main_journey_runs_through_the_real_screens_within_the_platform_budgets()
    {
        var outbound = OutboundHttpRequests.Instance.Mark();
        var ingestionBefore = fixture.Ingestion.Requests.Count;
        await using var connections = new ConnectionBudgetSampler(fixture.Database.AdminConnectionString, fixture.RuntimeLogins);
        connections.Start();
        await using var hosts = PlatformHosts.Start(fixture);
        await using var hub = new OperationsHubClient(hosts.ApiAddress, PlatformEndToEndFixture.OrganizationId);
        await hub.StartAsync();
        await using var browser = await PlatformBrowser.LaunchAsync();
        try
        {
            // 1. Dispatcher: "Nueva orden" quotes, creates and confirms the order.
            var dispatcher = await browser.DispatcherAsync();
            var order = await CreateAndConfirmOrderAsync(dispatcher);
            output.WriteLine($"order created and confirmed in the wizard: {order.PublicId}");

            // 2. Dispatcher, order detail: "Liberar para recolección", then "Asignar repartidor" after an idle period.
            await ReleaseForPickupAsync(dispatcher, order);
            var assignment = await AssignDriverAfterIdleAsync(dispatcher, order, hub);

            // 3. Dispatcher, order detail: "Ver enlace" gives the order's public tracking link.
            var trackingLink = await ReadTrackingLinkAsync(dispatcher);

            // 4. Driver PWA: pickup and delivery, each photo through the signed upload, never through the API.
            var driver = await browser.DriverAsync();
            var uploads = await DriverUploadObserver.AttachAsync(driver);
            await PickUpAndDeliverAsync(driver, order);

            // 5. Recipient: the public tracking page of that link shows the order delivered.
            var recipient = await browser.RecipientAsync();
            await recipient.GotoAsync(trackingLink.AbsoluteUri);
            await recipient.Locator(".trackingPublicId").GetByText(order.PublicId, new() { Exact = true }).WaitForAsync();
            await recipient.Locator(".trackingStatus").GetByText("Entregado", new() { Exact = true }).WaitForAsync();
            output.WriteLine("public tracking shows: Entregado");

            await AssertJourneyEvidenceAsync(order, uploads, hosts);
            await AssertOneTraceForTheAssignmentAsync(order, assignment, hosts);
            await AssertDeliveryAfterIdleAsync(order, assignment);
            AssertConnectionBudget(await connections.StopAsync(), hosts);
            AssertNothingLeftTheProcess(hosts, outbound, ingestionBefore);
        }
        catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
        {
            throw new InvalidOperationException(
                $"The journey failed: {exception.Message}{Environment.NewLine}" +
                $"browser errors: {string.Join(" | ", browser.Errors.Take(20))}{Environment.NewLine}" +
                $"pages:{Environment.NewLine}{await browser.DescribeAsync()}",
                exception);
        }
    }

    private async Task<JourneyOrder> CreateAndConfirmOrderAsync(IPage page)
    {
        await OrderWizardScreen.OpenAsync(page, fixture.WebAddress);
        await OrderWizardScreen.FillWhereAsync(page, shipment: 1);
        await OrderWizardScreen.NextAsync(page, 2);
        await OrderWizardScreen.FillWhatAsync(page, shipment: 1);
        await OrderWizardScreen.NextAsync(page, 3);
        await OrderWizardScreen.ChooseSameDayAsync(page);
        await OrderWizardScreen.QuoteButton(page).ClickAsync();
        await page.GetByText("Precio calculado:", new() { Exact = false }).WaitForAsync();
        await OrderWizardScreen.NextAsync(page, 4);
        await page.Locator("#order-payerType").SelectOptionAsync("SENDER");
        await page.Locator("#order-accepted").CheckAsync();
        await page.Locator("#order-restrictedGoodsAcknowledged").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Crear y confirmar orden", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Orden creada y confirmada", Exact = true }).WaitForAsync();
        var publicId = (await page.Locator(".opsWizardGuide").InnerTextAsync()).Trim();
        await page.GetByRole(AriaRole.Link, new() { Name = "Abrir orden", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(new Regex("/ops/orders/[0-9a-f-]{36}$", RegexOptions.CultureInvariant));
        var orderId = Guid.Parse(new Uri(page.Url).Segments[^1]);
        return new JourneyOrder(orderId, publicId);
    }

    private static async Task ReleaseForPickupAsync(IPage page, JourneyOrder order)
    {
        await page.GetByRole(AriaRole.Heading, new() { Name = "Siguiente paso", Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Liberar para recolección", Exact = true }).ClickAsync();
        await page.GetByLabel("Motivo: liberar para recolección").FillAsync("OPS-005 sintetico: lista para recolectar");
        await page.GetByRole(AriaRole.Button, new() { Name = "Continuar", Exact = true }).ClickAsync();
        await page.Locator("dialog[open]")
            .GetByRole(AriaRole.Button, new() { Name = "Liberar para recolección", Exact = true })
            .ClickAsync();
        // The detail reads the order again: READY_FOR_PICKUP without an assignment offers the driver picker.
        await page.GetByRole(AriaRole.Heading, new() { Name = "Asignar repartidor", Exact = true }).WaitForAsync();
        Assert.Contains(order.PublicId, await page.Locator("main").InnerTextAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Picks the eligible synthetic driver and the cost, opens the confirmation, waits until both realtime lanes have
    /// had no work for <see cref="IdlePeriod"/>, then confirms: that one click is the browser action measured by the
    /// trace and the delivery checks.
    /// </summary>
    private async Task<AssignmentAction> AssignDriverAfterIdleAsync(IPage page, JourneyOrder order, OperationsHubClient hub)
    {
        var picker = page.Locator("section.opsDriverAssignment");
        var driver = picker.Locator($"input[type=radio][value='{PlatformEndToEndFixture.DriverId:D}']");
        await driver.WaitForAsync();
        if (await driver.IsDisabledAsync())
        {
            Assert.Fail(
                "The synthetic driver is not assignable: " +
                await picker.Locator($"#driver-{PlatformEndToEndFixture.DriverId:D}-hint").InnerTextAsync());
        }

        await driver.CheckAsync();
        await picker.Locator("input[name=cost]").FillAsync(AssignmentCostMxn);
        await picker.GetByRole(AriaRole.Button, new() { Name = "Asignar repartidor", Exact = true }).ClickAsync();
        var confirm = page.Locator("dialog[open]").GetByRole(AriaRole.Button, new() { Name = "Asignar", Exact = true });
        await confirm.WaitForAsync();

        await WaitForIdleRealtimeLanesAsync();
        var posts = new ConcurrentQueue<string>();
        void OnRequest(object? sender, IRequest request)
        {
            // Writes only: the refetch the screen does after the answer is a read of the new state.
            if (request.Method is not ("GET" or "HEAD" or "OPTIONS") &&
                Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) &&
                uri.GetLeftPart(UriPartial.Authority) == fixture.ApiOrigin)
            {
                posts.Enqueue($"{request.Method} {uri.AbsolutePath}");
            }
        }

        page.Request += OnRequest;
        var committedAfter = await fixture.ScalarAsync<DateTime>("SELECT clock_timestamp()");
        var clickedAt = Stopwatch.GetTimestamp();
        await confirm.ClickAsync();
        var (eventId, arrivedAt) = await hub.WaitForStatusAsync(order.Id, "ASSIGNED", TimeSpan.FromSeconds(15));
        await page.GetByText("asignado.", new() { Exact = false }).First.WaitForAsync();
        page.Request -= OnRequest;

        var assignmentId = await fixture.ScalarAsync<Guid>(
            "SELECT id FROM dispatch.assignments WHERE order_id=@order",
            ("order", order.Id));
        return new AssignmentAction(
            assignmentId,
            posts.ToArray(),
            committedAfter,
            eventId,
            Stopwatch.GetElapsedTime(clickedAt, arrivedAt));
    }

    /// <summary>
    /// Both realtime lanes idle: nothing waits for them (business rows the REALTIME consumer claims, location rows),
    /// and the last one they handled was created and settled more than <see cref="IdlePeriod"/> ago.
    /// </summary>
    private async Task WaitForIdleRealtimeLanesAsync()
    {
        var idleSeconds = IdlePeriod.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture);
        var sql = $"""
            SELECT
              (SELECT count(*) FROM platform.outbox_events
               WHERE security.resolve_outbox_consumer(topic)='REALTIME' AND status IN ('PENDING','RETRY','PROCESSING'))
              + (SELECT count(*) FROM platform.location_outbox_events WHERE status IN ('PENDING','RETRY','PROCESSING')) = 0
              AND coalesce(greatest(
                (SELECT max(greatest(created_at, coalesce(processed_at, created_at))) FROM platform.outbox_events
                 WHERE security.resolve_outbox_consumer(topic)='REALTIME'),
                (SELECT max(greatest(created_at, coalesce(processed_at, created_at)))
                 FROM platform.location_outbox_events)), '-infinity'::timestamptz)
                < clock_timestamp() - interval '{idleSeconds} seconds'
            """;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (!await fixture.ScalarAsync<bool>(sql))
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The realtime lanes never became idle.");
            await Task.Delay(200);
        }
    }

    private async Task<Uri> ReadTrackingLinkAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Ver enlace", Exact = true }).ClickAsync();
        var field = page.Locator("#tracking-link-url");
        await field.WaitForAsync();
        var link = new Uri(await field.InputValueAsync(), UriKind.Absolute);
        Assert.Equal(fixture.WebOrigin, link.GetLeftPart(UriPartial.Authority));
        Assert.StartsWith("/track/", link.AbsolutePath, StringComparison.Ordinal);
        return link;
    }

    private async Task PickUpAndDeliverAsync(IPage page, JourneyOrder order)
    {
        await page.GotoAsync(new Uri(fixture.WebAddress, "/driver/stops").AbsoluteUri);
        await page.Locator("li").Filter(new() { HasText = order.PublicId })
            .GetByRole(AriaRole.Link, new() { Name = "Ver detalle", Exact = true })
            .ClickAsync();
        await page.WaitForURLAsync($"**/driver/stops/{order.Id:D}");
        await page.GetByText(order.PublicId, new() { Exact = true }).First.WaitForAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Llegué a recolección", Exact = true }).ClickAsync();
        // Synthetic photos the phone can decode (DRV-PROOF-PHOTO-REDUCE-2026-10-20 reduces each before it is queued):
        // a 1x1 PNG at the pickup and the EXIF GPS camera JPEG of the driver suites at the delivery.
        await UsePhotoAsync(
            page,
            "Confirmar recolección",
            "pickup-ops005-synthetic.png",
            "image/png",
            ProofPhotoEvidence.TealPixelPng);
        await page.GetByRole(AriaRole.Button, new() { Name = "Iniciar traslado", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Llegué al destino", Exact = true }).ClickAsync();
        await UsePhotoAsync(
            page,
            "Confirmar entrega",
            "delivery-ops005-synthetic.jpg",
            "image/jpeg",
            ProofPhotoEvidence.ExifGpsJpeg());

        // The PWA sends its queue by itself; the photos wait for the Worker's validation and are retried by the
        // queue's own backoff. "Sincronizar ahora" is the driver's button for sending it at once.
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        var nextSync = DateTimeOffset.UtcNow.AddSeconds(5);
        string? status = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            status = await fixture.ScalarAsync<string>("SELECT status FROM orders.orders WHERE id=@id", ("id", order.Id));
            if (status == "DELIVERED" && await QueuedOperationsAsync(page) == 0)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= nextSync)
            {
                var sync = page.GetByRole(AriaRole.Button, new() { Name = "Sincronizar ahora", Exact = true });
                if (await sync.IsVisibleAsync() && await sync.IsEnabledAsync())
                {
                    await sync.ClickAsync();
                }

                nextSync = DateTimeOffset.UtcNow.AddSeconds(5);
            }

            await Task.Delay(250);
        }

        Assert.Fail($"The driver's pickup and delivery did not reach DELIVERED; status={status}.");
    }

    /// <summary>
    /// UI-001 phase 3: the step names the action, the photo is shown in "Vista previa de la foto" and only
    /// "Usar esta foto" queues it.
    /// </summary>
    private static async Task UsePhotoAsync(IPage page, string action, string fileName, string mimeType, byte[] bytes)
    {
        await page.GetByText(action, new() { Exact = true }).First.WaitForAsync();
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = fileName,
            MimeType = mimeType,
            Buffer = bytes,
        });
        await page.GetByRole(AriaRole.Heading, new() { Name = "Vista previa de la foto", Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Usar esta foto", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Vista previa de la foto", Exact = true })
            .WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    private static Task<int> QueuedOperationsAsync(IPage page) =>
        page.EvaluateAsync<int>(
            """
            () => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1");
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const db = open.result;
                if (!db.objectStoreNames.contains("operations")) { db.close(); resolve(0); return; }
                const request = db.transaction("operations", "readonly").objectStore("operations").count();
                request.onsuccess = () => { db.close(); resolve(request.result); };
                request.onerror = () => { db.close(); reject(request.error); };
              };
            })
            """);

    /// <summary>The business outcome in the database, the photos in storage and invariant 13 on both sides.</summary>
    private async Task AssertJourneyEvidenceAsync(JourneyOrder order, DriverUploadObserver uploads, PlatformHosts hosts)
    {
        var row = Assert.Single(await fixture.RowsAsync(
            """
            SELECT o.status, o.total_cents, o.public_id,
              ARRAY(SELECT e.payload->>'new_status' FROM orders.order_events e
                    WHERE e.order_id=o.id AND e.event_type='ORDER_STATUS_CHANGED' ORDER BY e.aggregate_version),
              ARRAY(SELECT p.proof_type FROM custody.proofs p WHERE p.order_id=o.id ORDER BY p.captured_at),
              ARRAY(SELECT p.object_key FROM custody.proofs p WHERE p.order_id=o.id ORDER BY p.captured_at),
              ARRAY(SELECT p.content_type FROM custody.proofs p WHERE p.order_id=o.id ORDER BY p.captured_at),
              (SELECT count(*)::integer FROM custody.proof_upload_sessions s WHERE s.order_id=o.id AND s.status='CONSUMED'),
              (SELECT count(*)::integer FROM dispatch.assignments a
               WHERE a.order_id=o.id AND a.driver_id=@driver AND a.cost_cents=4500)
            FROM orders.orders o
            WHERE o.id=@order
            """,
            ("order", order.Id),
            ("driver", PlatformEndToEndFixture.DriverId)));
        Assert.Equal("DELIVERED", row[0]);
        // The synthetic SAME_DAY tariff, VAT included, in centavos; no floating point anywhere (invariant 4).
        Assert.Equal(PlatformEndToEndFixture.TariffAmountCents, Assert.IsType<long>(row[1]));
        Assert.Equal(order.PublicId, row[2]);
        Assert.Equal(ExpectedStatusChanges, Assert.IsType<string[]>(row[3]));
        Assert.Equal(new[] { "PICKUP_PHOTO", "DELIVERY_PHOTO" }, Assert.IsType<string[]>(row[4]));
        Assert.Equal(["image/jpeg", "image/jpeg"], Assert.IsType<string[]>(row[6]));
        Assert.Equal(2, row[7]);
        Assert.Equal(1, row[8]);

        // The photo bytes are in the bucket under each proof's final key: the JPEGs the phone reduced, without
        // metadata (DRV-PROOF-PHOTO-REDUCE-2026-10-20); the EXIF GPS delivery photo is kept as 800x1600.
        using var storage = fixture.Storage.CreateClient();
        var shapes = new List<(int Width, int Height)>();
        foreach (var key in Assert.IsType<string[]>(row[5]))
        {
            using var stored = await storage.GetObjectAsync(new GetObjectRequest
            {
                BucketName = CorsCapableS3Fixture.Bucket,
                Key = key,
            });
            using var content = new MemoryStream();
            await stored.ResponseStream.CopyToAsync(content);
            Assert.True(content.Length > 0, "A proof has no object in storage.");
            var header = ProofPhotoEvidence.AssertReducedJpeg(content.ToArray());
            shapes.Add((header.Width, header.Height));
        }
        Assert.Equal([(1, 1), (800, 1600)], shapes);

        // Invariant 13: the browser sent each photo to the signed storage origin, and the API received no multipart.
        var puts = uploads.Successful("PUT", fixture.StorageOrigin);
        Assert.True(puts.Count >= 2, $"Expected the two photos as signed PUTs to storage; saw {puts.Count}.");
        // The cross-origin upload is the browser's own: its CORS preflight to storage came from the web origin.
        var preflights = uploads.Successful("OPTIONS", fixture.StorageOrigin);
        Assert.NotEmpty(preflights);
        Assert.All(preflights, preflight => Assert.Equal(fixture.WebOrigin, preflight.BrowserOrigin));
        var requests = ApiLogLines.HostingRequests(hosts.ApiLogs.Lines).ToArray();
        Assert.Contains(requests, request => request.Method == "POST" && request.Path.EndsWith("/proofs", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, request => request.ContentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase));
        output.WriteLine($"photos: {puts.Count} signed PUTs to storage, 0 multipart requests to the API, 2 proofs");
    }

    /// <summary>
    /// OBS-003: the "Asignar" click sent one request to the API, and its W3C trace id is the <c>request_id</c> of both
    /// audit rows that request wrote (ASSIGNMENT_CREATED and ORDER_STATUS_CHANGED) and the <c>TraceId</c> of every log
    /// line of that request, and of no line of any other request.
    /// </summary>
    private async Task AssertOneTraceForTheAssignmentAsync(JourneyOrder order, AssignmentAction assignment, PlatformHosts hosts)
    {
        Assert.Equal(new[] { $"POST /api/v1/orders/{order.Id:D}/assignments" }, assignment.BrowserRequests);
        var traceId = await fixture.ScalarAsync<string>(
            "SELECT request_id FROM platform.audit_logs WHERE action='ASSIGNMENT_CREATED' AND entity_id=@assignment",
            ("assignment", assignment.AssignmentId));
        Assert.True(TraceCorrelation.IsTraceId(traceId), $"The audit request_id is not a W3C trace id: {traceId}");
        var audit = (await fixture.RowsAsync(
                "SELECT action, entity_type, entity_id FROM platform.audit_logs WHERE request_id=@trace ORDER BY action",
                ("trace", traceId)))
            .Select(row => $"{row[0]} {row[1]} {row[2]}")
            .ToArray();
        Assert.Equal(
            new[]
            {
                $"ASSIGNMENT_CREATED Assignment {assignment.AssignmentId:D}",
                $"ORDER_STATUS_CHANGED Order {order.Id:D}",
            },
            audit);

        Assert.NotEmpty(hosts.ApiLogs.LinesOf(traceId));
        var scope = hosts.ApiLogs.RequestScopeOf(traceId);
        var lines = hosts.ApiLogs.LinesOfRequestScope(scope);
        Assert.All(lines, line => Assert.Equal(traceId, JsonConsoleCapture.TraceIdOf(line)));
        var request = Assert.Single(ApiLogLines.HostingRequests(lines));
        Assert.Equal(("POST", $"/api/v1/orders/{order.Id:D}/assignments"), (request.Method, request.Path));
        output.WriteLine($"trace: one request, {lines.Count} log lines and 2 audit rows share one W3C trace id");
    }

    /// <summary>
    /// SCL-007/AI-15: the realtime notice of the assignment, committed after both lanes idled, reached the operations
    /// hub client within <c>event_delivery_p95_seconds</c>, measured from the click, before the request was even sent.
    /// </summary>
    private async Task AssertDeliveryAfterIdleAsync(JourneyOrder order, AssignmentAction assignment)
    {
        var outbox = Assert.Single(await fixture.RowsAsync(
            """
            SELECT id, created_at FROM platform.outbox_events
            WHERE topic='orders.status-changed' AND aggregate_id=@order AND payload->>'new_status'='ASSIGNED'
            """,
            ("order", order.Id)));
        Assert.Equal(assignment.EventId, outbox[0]);
        Assert.True(
            (DateTime)outbox[1]! >= assignment.CommittedAfter,
            "The measured notice was not committed by the measured click.");
        output.WriteLine(
            $"delivery after {IdlePeriod.TotalSeconds:F0} s idle: " +
            $"{assignment.DeliveryLatency.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms " +
            $"(budget {DeliveryBudget.TotalMilliseconds:F0} ms)");
        Assert.True(
            assignment.DeliveryLatency <= DeliveryBudget,
            $"The notice took {assignment.DeliveryLatency.TotalMilliseconds} ms after an idle period.");
    }

    /// <summary>SCL-007-POOL-PER-ROLE-2026-10-13: 10 + 4 + 10 = 24 at most, and each role's backends within its pool.</summary>
    private void AssertConnectionBudget(ConnectionBudgetPeaks peaks, PlatformHosts hosts)
    {
        var maximum = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["paqueteria_app"] = hosts.MaximumPoolSize(RuntimeDatabaseRole.Application),
            ["paqueteria_realtime"] = hosts.MaximumPoolSize(RuntimeDatabaseRole.Realtime),
            ["paqueteria_worker"] = hosts.MaximumPoolSize(RuntimeDatabaseRole.Worker),
        };
        Assert.Equal(
            new[] { 10, 4, 10 },
            new[] { maximum["paqueteria_app"], maximum["paqueteria_realtime"], maximum["paqueteria_worker"] });
        Assert.Equal(DatabasePoolOptions.RuntimeConnectionBudget, maximum.Values.Sum());
        output.WriteLine(
            $"connections: {peaks.Samples} samples; peaks " +
            string.Join(", ", peaks.PeakByRole.Select(pair => $"{pair.Key}={pair.Value}/{maximum[pair.Key]}")) +
            $"; total={peaks.PeakTotal}/{DatabasePoolOptions.RuntimeConnectionBudget}");
        Assert.True(peaks.Samples >= 20, $"Only {peaks.Samples} samples of pg_stat_activity.");
        foreach (var (role, peak) in peaks.PeakByRole)
        {
            Assert.True(peak >= 1, $"No backend of {role} was ever seen; the sampler did not observe the workflow.");
            Assert.True(peak <= maximum[role], $"{role} held {peak} backends, above its pool of {maximum[role]}.");
        }

        Assert.True(
            peaks.PeakTotal <= DatabasePoolOptions.RuntimeConnectionBudget,
            $"The runtime pools held {peaks.PeakTotal} backends, above the SCL-007 budget.");
    }

    /// <summary>
    /// OBS-003 export off, as the pilot runs: both hosts read the connection string and registered no exporter, the
    /// ingestion endpoint it names received nothing, and every real HTTP request of the process during the workflow
    /// stayed on loopback (the storage container, the API itself, the hub client).
    /// </summary>
    private void AssertNothingLeftTheProcess(PlatformHosts hosts, int outboundMark, int ingestionBefore)
    {
        foreach (var services in new[] { hosts.Api.Services, hosts.Worker.Services })
        {
            var configuration = services.GetRequiredService<IConfiguration>();
            Assert.Equal("false", configuration["TelemetryExport:Enabled"]);
            Assert.Equal(fixture.TelemetryConnectionString, configuration["TelemetryExport:ConnectionString"]);
        }

        hosts.AssertNoTelemetryExporter();
        Assert.Equal(ingestionBefore, fixture.Ingestion.Requests.Count);
        var uris = OutboundHttpRequests.Instance.Since(outboundMark);
        var storagePort = new Uri(fixture.Storage.Endpoint).Port;
        Assert.Contains(uris, uri => uri.IsLoopback && uri.Port == storagePort);
        Assert.All(uris, uri => Assert.True(uri.IsLoopback, $"A request left the process: {uri.GetLeftPart(UriPartial.Authority)}"));
        output.WriteLine($"export off: no exporter, 0 ingestion requests, {uris.Count} outgoing requests all on loopback");
    }

    private sealed record JourneyOrder(Guid Id, string PublicId);

    private sealed record AssignmentAction(
        Guid AssignmentId,
        IReadOnlyList<string> BrowserRequests,
        DateTime CommittedAfter,
        Guid EventId,
        TimeSpan DeliveryLatency);
}
