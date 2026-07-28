using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Custody.Application.ProofUploads;
using Dispatch.Application.Stops;
using Identity.Infrastructure.Mock;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Tracking;
using Paqueteria.IntegrationTests.Realtime;
using Paqueteria.IntegrationTests.Security;
using Realtime.Application.Dispatching;

namespace Paqueteria.IntegrationTests.Operations;

internal sealed class Ops001ScenarioRunner(Ops001DeliverySimulationFixture fixture)
{
    private readonly SemaphoreSlim proofProcessingGate = new(2, 2);
    private const int ExpectedVersion = 9;
    private const int ExpectedDomainEventsPerOrder = 9;
    private const int ExpectedRealtimeStatusEventsPerOrder = 8;
    private const int ExpectedAuditsPerOrder = 17;
    private const int ExpectedRealtimeOutboxPerOrder = 17;
    private const int ExpectedTotalOutboxPerOrder = 18;
    private const string PoisonSentinel = "OPS001_SECRET_SENTINEL";
    private const string TransitionReason = "OPS-001 synthetic workflow";
    private static readonly byte[] SyntheticPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static readonly byte[] SyntheticPngSha256 = SHA256.HashData(SyntheticPng);

    internal async Task<Ops001SimulationReport> RunAsync(
        Ops001ScenarioData data,
        bool publishReport,
        CancellationToken cancellationToken,
        Ops001RunTestCheckpoint? testCheckpoint = null)
    {
        var stopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        await fixture.BootstrapAsync(data, cancellationToken);

        IHost? proofWorker = null;
        RealtimeKestrelWebApplicationFactory? restoredHost = null;
        Ops001RealtimeObserver? restoredObserver = null;
        var interruptedOutboxId = Guid.Empty;
        var realtimeDeliveries = new List<Ops001RealtimeDelivery>();
        var logCollector = new Ops001LogCollector();
        try
        {
            ScenarioOrder[] orders;
            await using (var setupHost = fixture.CreateApiHost(data))
            {
                var setupAddress = setupHost.Start();
                using var setupClient = new HttpClient { BaseAddress = setupAddress };
                orders = await RunBoundedAsync(
                    Enumerable.Range(1, Ops001ScenarioData.OrderCount),
                    degreeOfParallelism: 4,
                    orderNumber => CreateReadyAssignedOrderAsync(
                        setupClient,
                        setupHost.Services,
                        data,
                        orderNumber,
                        cancellationToken),
                    cancellationToken);

                Assert.Equal(
                    Ops001ScenarioData.OrderCount,
                    orders.Select(order => order.OrderId).Distinct().Count());
                Assert.Equal(
                    Ops001ScenarioData.OrderCount,
                    orders.Select(order => order.QuoteId).Distinct().Count());
                Assert.Equal(
                    Ops001ScenarioData.DriverCount,
                    orders.Select(order => order.DriverId).Distinct().Count());
                Assert.All(
                    data.DriverIds,
                    driver => Assert.Equal(
                        Ops001ScenarioData.OrdersPerDriver,
                        orders.Count(order => order.DriverId == driver)));

                await HoldScenarioOutboxAsync(data.OrganizationId, cancellationToken);
                interruptedOutboxId = await ReadStatusOutboxIdAsync(
                    orders[0].OrderId,
                    aggregateVersion: 4,
                    cancellationToken);
            }

            var failureInjector = new OneShotRealtimeOutboxFailureInjector(
                RealtimeOutboxLane.Business);
            failureInjector.Arm(interruptedOutboxId);
            var initialAuthorization = new RealtimeAuthorizationRecorder();
            PostgreSqlSecurityWebApplicationFactory.OutboxDeliveryState interrupted;
            await using (var initialHost = fixture.CreateApiHost(
                             data,
                             fixture.Database.WorkerConnectionString,
                             failureInjector,
                             authorizationRecorder: initialAuthorization))
            {
                var initialAddress = initialHost.Start();
                await using var initialObserver =
                    new Ops001RealtimeObserver(initialAddress, data.OrganizationId);
                await initialObserver.StartAsync(cancellationToken);
                Assert.True(await initialAuthorization.WaitForNextOperationsAcceptedAsync(
                    TimeSpan.FromSeconds(5)));
                await MakeOutboxAvailableAsync(interruptedOutboxId, cancellationToken);
                await failureInjector.WaitForInterruptionAsync(TimeSpan.FromSeconds(20));
                await initialObserver.WaitForEventAsync(
                    interruptedOutboxId,
                    TimeSpan.FromSeconds(10),
                    cancellationToken);
                interrupted = await WaitForOutboxStateAsync(
                    interruptedOutboxId,
                    state => state.Status == "PROCESSING",
                    TimeSpan.FromSeconds(10),
                    cancellationToken);
                Assert.Equal(1, interrupted.Attempts);
                Assert.NotNull(interrupted.LeaseToken);
                realtimeDeliveries.AddRange(initialObserver.Deliveries);
            }

            var staleLeaseToken = Assert.IsType<Guid>(interrupted.LeaseToken);
            testCheckpoint?.SetTargetOutbox(interruptedOutboxId);

            var pausingInjector = new Ops001PausingOutboxInjector(interruptedOutboxId);
            restoredHost = fixture.CreateApiHost(
                data,
                fixture.Database.WorkerConnectionString,
                pausingInjector,
                logCollector);
            var restoredAddress = restoredHost.Start();
            restoredObserver = new Ops001RealtimeObserver(
                restoredAddress,
                data.OrganizationId);
            await restoredObserver.StartAsync(cancellationToken);
            testCheckpoint?.OwnedHostStarted();
            testCheckpoint?.OwnedObserverStarted();
            if (testCheckpoint is not null)
            {
                await testCheckpoint.PauseAfterOwnedResourcesStartedAsync(cancellationToken);
            }

            await ExpireLeaseAsync(interruptedOutboxId, cancellationToken);

            await pausingInjector.WaitUntilPausedAsync(
                TimeSpan.FromSeconds(30),
                cancellationToken);
            var reclaimed = await WaitForOutboxStateAsync(
                interruptedOutboxId,
                state => state.Status == "PROCESSING" && state.Attempts == 2,
                TimeSpan.FromSeconds(10),
                cancellationToken);
            var currentLeaseToken = Assert.IsType<Guid>(reclaimed.LeaseToken);
            Assert.NotEqual(staleLeaseToken, currentLeaseToken);
            Assert.False(await SettleOutboxAsync(
                interruptedOutboxId,
                staleLeaseToken,
                "PROCESSED",
                cancellationToken));
            var afterStaleSettle = await ReadOutboxStateAsync(
                interruptedOutboxId,
                cancellationToken);
            Assert.NotNull(afterStaleSettle);
            Assert.Equal("PROCESSING", afterStaleSettle.Status);
            Assert.Equal(currentLeaseToken, afterStaleSettle.LeaseToken);
            pausingInjector.Release();

            var recovered = await WaitForOutboxStateAsync(
                interruptedOutboxId,
                state => state.Status == "PROCESSED",
                TimeSpan.FromSeconds(20),
                cancellationToken);
            Assert.Equal(2, recovered.Attempts);
            Assert.Null(recovered.LeaseToken);

            await MakeRealtimeOutboxAvailableAsync(
                data.OrganizationId,
                cancellationToken);

            await RunBoundedAsync(
                orders,
                4,
                order => TransitionAsync(
                    restoredAddress,
                    data,
                    order,
                    "AT_PICKUP",
                    expectedVersion: 4,
                    cancellationToken),
                cancellationToken);

            proofWorker = fixture.CreateProofWorker();
            testCheckpoint?.OwnedProofWorkerStarted();

            await RunBoundedAsync(
                orders,
                4,
                order => CompleteProofAsync(
                    restoredHost.Services,
                    proofWorker.Services,
                    data,
                    order,
                    "PICKUP_PHOTO",
                    cancellationToken),
                cancellationToken);

            await RunBoundedAsync(
                orders,
                4,
                async order =>
                {
                    await TransitionAsync(
                        restoredAddress,
                        data,
                        order,
                        "PICKED_UP",
                        expectedVersion: 5,
                        cancellationToken);
                    await TransitionAsync(
                        restoredAddress,
                        data,
                        order,
                        "IN_TRANSIT",
                        expectedVersion: 6,
                        cancellationToken);
                    await TransitionAsync(
                        restoredAddress,
                        data,
                        order,
                        "DELIVERING",
                        expectedVersion: 7,
                        cancellationToken);
                },
                cancellationToken);

            await RunBoundedAsync(
                orders,
                4,
                order => CompleteProofAsync(
                    restoredHost.Services,
                    proofWorker.Services,
                    data,
                    order,
                    "DELIVERY_PHOTO",
                    cancellationToken),
                cancellationToken);

            var poisonId = await InsertPoisonAsync(
                data.OrganizationId,
                orders[0].OrderId,
                cancellationToken);
            await RunBoundedAsync(
                orders,
                4,
                order => TransitionAsync(
                    restoredAddress,
                    data,
                    order,
                    "DELIVERED",
                    expectedVersion: 8,
                    cancellationToken),
                cancellationToken);

            var newerOutboxId = await ReadStatusOutboxIdAsync(
                orders[0].OrderId,
                aggregateVersion: 9,
                cancellationToken);
            var poison = await WaitForOutboxStateAsync(
                poisonId,
                state => state.Status == "DEAD",
                TimeSpan.FromSeconds(20),
                cancellationToken);
            Assert.Equal(RealtimeOutboxErrorCodes.UnknownTopic, poison.LastError);
            Assert.Equal(1, poison.Attempts);
            var newer = await WaitForOutboxStateAsync(
                newerOutboxId,
                state => state.Status == "PROCESSED",
                TimeSpan.FromSeconds(30),
                cancellationToken);
            Assert.Equal("PROCESSED", newer.Status);

            await WaitForRealtimeOutboxCompletionAsync(
                data.OrganizationId,
                ExpectedRealtimeOutboxPerOrder * Ops001ScenarioData.OrderCount,
                TimeSpan.FromMinutes(2),
                cancellationToken);
            var realtimeExpectations = await ReadRealtimeExpectationsAsync(
                data.OrganizationId,
                orders.Select(order => order.OrderId).ToArray(),
                cancellationToken);
            Assert.Equal(
                ExpectedRealtimeStatusEventsPerOrder * Ops001ScenarioData.OrderCount,
                realtimeExpectations.Count);
            Assert.All(
                orders,
                order => Assert.Equal(
                    Enumerable.Range(2, ExpectedRealtimeStatusEventsPerOrder),
                    realtimeExpectations
                        .Where(value => value.OrderId == order.OrderId)
                        .Select(value => checked((int)value.AggregateVersion))
                        .Order()));
            await WaitForExpectedEventsAsync(
                realtimeDeliveries,
                restoredObserver,
                realtimeExpectations,
                TimeSpan.FromMinutes(1),
                cancellationToken);
            realtimeDeliveries.AddRange(restoredObserver.Deliveries);
            var realtimeCorrelation = Ops001RealtimeCorrelation.Correlate(
                realtimeExpectations,
                realtimeDeliveries);
            Assert.True(
                realtimeCorrelation.RawDeliveriesFor(interruptedOutboxId) >= 2,
                "The interrupted event did not have at least two raw deliveries.");
            Assert.Single(
                realtimeDeliveries
                    .Where(value => value.EventId == interruptedOutboxId)
                    .Select(value => value.EventId)
                    .Distinct());

            await AssertPublicTrackingAsync(
                restoredAddress,
                orders,
                cancellationToken);
            await AssertDashboardAsync(
                restoredAddress,
                data,
                orders,
                cancellationToken);
            await AssertDriverStopsClearedAsync(
                restoredHost.Services,
                data,
                cancellationToken);
            await AssertNoSimulationEndpointAsync(
                restoredAddress,
                data,
                cancellationToken);

            await restoredObserver.DisposeAsync();
            testCheckpoint?.OwnedObserverDisposed();
            restoredObserver = null;
            await restoredHost.DisposeAsync();
            testCheckpoint?.OwnedHostDisposed();
            restoredHost = null;

            await CompleteOrderCreatedOutboxAsync(
                data.OrganizationId,
                cancellationToken);

            stopwatch.Stop();
            var report = await BuildReportAsync(
                data,
                orders,
                realtimeCorrelation,
                poisonId,
                newerOutboxId,
                interruptedOutboxId,
                stopwatch.Elapsed,
                cancellationToken);
            report.EnsureAccepted();
            await AssertNoSensitiveEvidenceAsync(
                data.OrganizationId,
                orders,
                logCollector.Messages,
                report,
                cancellationToken);
            if (publishReport)
            {
                await WriteReportAsync(
                    report,
                    testCheckpoint?.ReportPath,
                    cancellationToken);
            }

            return report;
        }
        finally
        {
            if (restoredObserver is not null)
            {
                await restoredObserver.DisposeAsync();
                testCheckpoint?.OwnedObserverDisposed();
            }

            if (restoredHost is not null)
            {
                await restoredHost.DisposeAsync();
                testCheckpoint?.OwnedHostDisposed();
            }

            if (proofWorker is not null)
            {
                proofWorker.Dispose();
                testCheckpoint?.OwnedProofWorkerDisposed();
            }

            if (interruptedOutboxId != Guid.Empty)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await ReleaseOwnedProcessingLeaseAsync(
                    interruptedOutboxId,
                    cleanup.Token);
                var finalState = await ReadOutboxStateAsync(
                    interruptedOutboxId,
                    cleanup.Token);
                testCheckpoint?.CleanupCompleted(finalState?.Status);
            }
        }
    }

    private async Task<ScenarioOrder> CreateReadyAssignedOrderAsync(
        HttpClient client,
        IServiceProvider services,
        Ops001ScenarioData data,
        int orderNumber,
        CancellationToken cancellationToken)
    {
        var quoteBody = new
        {
            client_account_id = (Guid?)null,
            origin = new
            {
                address_text = data.SyntheticAddress("origin", orderNumber),
                contact_name = data.SyntheticContact("sender", orderNumber),
                phone = data.SyntheticPhone(orderNumber),
                lat = 24.78 + orderNumber * 0.001,
                lng = -107.42 + orderNumber * 0.001,
                references = "OPS-001 SYNTHETIC FIXTURE",
            },
            destination = new
            {
                address_text = data.SyntheticAddress("destination", orderNumber),
                contact_name = data.SyntheticContact("receiver", orderNumber),
                phone = data.SyntheticPhone(orderNumber),
                lat = 24.79 + orderNumber * 0.001,
                lng = -107.41 + orderNumber * 0.001,
                references = "OPS-001 SYNTHETIC FIXTURE",
            },
            service_type = "SAME_DAY",
            consolidated_route = false,
            packages = new[]
            {
                new
                {
                    description = $"OPS-001 SYNTHETIC PARCEL {orderNumber:D2}",
                    weight_grams = 1000,
                    declared_value_cents = 0L,
                    length_mm = 100,
                    width_mm = 100,
                    height_mm = 100,
                },
            },
        };
        var quoteKey = data.IdempotencyKey("create-quote", orderNumber);
        var quoteId = await PostForIdAsync(
            client,
            "/api/v1/quotes",
            data.OrganizationId,
            quoteKey,
            quoteBody,
            HttpStatusCode.Created,
            cancellationToken);
        if (orderNumber == 1)
        {
            Assert.Equal(
                quoteId,
                await PostForIdAsync(
                    client,
                    "/api/v1/quotes",
                    data.OrganizationId,
                    quoteKey,
                    quoteBody,
                    HttpStatusCode.Created,
                    cancellationToken));
        }

        var orderBody = new
        {
            quote_id = quoteId,
            payer_type = "SENDER",
            acceptance = new
            {
                terms_version = "ops001-synthetic-terms-v1",
                privacy_version = "ops001-synthetic-privacy-v1",
                accepted_at = "2026-07-27T12:00:00.1234560Z",
                acceptance_channel = "API",
            },
        };
        var orderKey = data.IdempotencyKey("create-order", orderNumber);
        var orderId = await PostForIdAsync(
            client,
            "/api/v1/orders",
            data.OrganizationId,
            orderKey,
            orderBody,
            HttpStatusCode.Created,
            cancellationToken);
        if (orderNumber == 1)
        {
            Assert.Equal(
                orderId,
                await PostForIdAsync(
                    client,
                    "/api/v1/orders",
                    data.OrganizationId,
                    orderKey,
                    orderBody,
                    HttpStatusCode.Created,
                    cancellationToken));
        }

        string trackingToken;
        await using (var scope = services.CreateAsyncScope())
        {
            var grant = await scope.ServiceProvider
                .GetRequiredService<IPublicTrackingTokenService>()
                .IssueAsync(
                    new IssuePublicTrackingTokenCommand(
                        data.DispatcherUserId,
                        data.OrganizationId,
                        orderId,
                        $"ops001-r{data.RunNumber:D2}-tracking-{orderNumber:D2}"),
                    cancellationToken);
            trackingToken = grant.Token;
        }

        var order = new ScenarioOrder(
            orderNumber,
            quoteId,
            orderId,
            data.DriverForOrder(orderNumber),
            data.DriverUserForOrder(orderNumber),
            trackingToken);
        await TransitionAsync(
            client.BaseAddress!,
            data,
            order,
            "CONFIRMED",
            expectedVersion: 1,
            cancellationToken,
            metadata: new { restricted_goods_acknowledged = true });
        await TransitionAsync(
            client.BaseAddress!,
            data,
            order,
            "READY_FOR_PICKUP",
            expectedVersion: 2,
            cancellationToken);

        var assignmentBody = new
        {
            driver_id = order.DriverId,
            assignment_type = "OWN",
            cost_cents = 1000L,
            route_id = (Guid?)null,
        };
        var assignmentKey = data.IdempotencyKey("assign-own", orderNumber);
        order.AssignmentId = await PostForIdAsync(
            client,
            $"/api/v1/orders/{order.OrderId:D}/assignments",
            data.OrganizationId,
            assignmentKey,
            assignmentBody,
            HttpStatusCode.Created,
            cancellationToken);
        if (orderNumber == 1)
        {
            Assert.Equal(
                order.AssignmentId,
                await PostForIdAsync(
                    client,
                    $"/api/v1/orders/{order.OrderId:D}/assignments",
                    data.OrganizationId,
                    assignmentKey,
                    assignmentBody,
                    HttpStatusCode.Created,
                    cancellationToken));
        }

        order.Version = 4;
        return order;
    }

    private async Task TransitionAsync(
        Uri baseAddress,
        Ops001ScenarioData data,
        ScenarioOrder order,
        string targetStatus,
        int expectedVersion,
        CancellationToken cancellationToken,
        object? metadata = null)
    {
        var operation = $"transition-{targetStatus.ToLowerInvariant().Replace('_', '-')}";
        using var client = new HttpClient { BaseAddress = baseAddress };
        using var request = AuthorizedRequest(
            HttpMethod.Post,
            $"/api/v1/orders/{order.OrderId:D}/transitions",
            data.OrganizationId);
        request.Headers.Add(
            "Idempotency-Key",
            data.IdempotencyKey(operation, order.Number));
        request.Content = JsonContent.Create(new
        {
            target_status = targetStatus,
            reason = TransitionReason,
            expected_version = expectedVersion,
            metadata = metadata ?? new { },
        });
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureStatusAsync(response, HttpStatusCode.OK, cancellationToken);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(targetStatus, json.RootElement.GetProperty("status").GetString());
        Assert.Equal(expectedVersion + 1, json.RootElement.GetProperty("version").GetInt32());
        order.Version = expectedVersion + 1;
    }

    private async Task CompleteProofAsync(
        IServiceProvider apiServices,
        IServiceProvider workerServices,
        Ops001ScenarioData data,
        ScenarioOrder order,
        string proofType,
        CancellationToken cancellationToken)
    {
        await proofProcessingGate.WaitAsync(cancellationToken);
        try
        {
            await CompleteProofUnderGateAsync(
                apiServices,
                workerServices,
                data,
                order,
                proofType,
                cancellationToken);
        }
        finally
        {
            proofProcessingGate.Release();
        }
    }

    private async Task CompleteProofUnderGateAsync(
        IServiceProvider apiServices,
        IServiceProvider workerServices,
        Ops001ScenarioData data,
        ScenarioOrder order,
        string proofType,
        CancellationToken cancellationToken)
    {
        var operation = proofType == "PICKUP_PHOTO" ? "pickup" : "delivery";
        ProofUploadSessionResult upload;
        await using (var scope = apiServices.CreateAsyncScope())
        {
            upload = await scope.ServiceProvider
                .GetRequiredService<IProofUploadSessionService>()
                .CreateAsync(
                    new CreateProofUploadSessionCommand(
                        order.DriverUserId,
                        data.OrganizationId,
                        false,
                        data.IdempotencyKey($"{operation}-upload", order.Number),
                        order.OrderId,
                        proofType,
                        "image/png",
                        SyntheticPng.Length,
                        SyntheticPngSha256,
                        $"ops001-r{data.RunNumber:D2}-{operation}-upload"),
                    cancellationToken);
        }

        using (var put = await PutProofAsync(upload, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        await ProcessProofUntilReadyAsync(
            workerServices,
            upload.Id,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        var capturedAt = UtcMicrosecond(DateTimeOffset.UtcNow.AddMinutes(-1));
        var finalization = new FinalizeProofCommand(
            order.DriverUserId,
            data.OrganizationId,
            false,
            data.IdempotencyKey($"{operation}-finalize", order.Number),
            order.OrderId,
            upload.Id,
            proofType,
            SyntheticPngSha256,
            capturedAt,
            null,
            null,
            null,
            $"ops001-r{data.RunNumber:D2}-{operation}-finalize");
        ProofResult proof;
        await using (var scope = apiServices.CreateAsyncScope())
        {
            proof = await scope.ServiceProvider
                .GetRequiredService<IProofFinalizationService>()
                .FinalizeAsync(finalization, cancellationToken);
        }

        Assert.Equal(upload.Id, proof.UploadSessionId);
        Assert.Equal(proofType, proof.ProofType);
        if (order.Number == 1 && proofType == "DELIVERY_PHOTO")
        {
            await using var replayScope = apiServices.CreateAsyncScope();
            var replay = await replayScope.ServiceProvider
                .GetRequiredService<IProofFinalizationService>()
                .FinalizeAsync(finalization, cancellationToken);
            Assert.Equal(proof.Id, replay.Id);
        }
    }

    private static async Task<HttpResponseMessage> PutProofAsync(
        ProofUploadSessionResult upload,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Put, upload.UploadUrl)
        {
            Content = new ByteArrayContent(SyntheticPng),
        };
        foreach (var header in upload.RequiredHeaders)
        {
            if (header.Key == "Content-Type")
            {
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(header.Value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private async Task AssertPublicTrackingAsync(
        Uri baseAddress,
        IReadOnlyCollection<ScenarioOrder> orders,
        CancellationToken cancellationToken)
    {
        Assert.Equal(orders.Count, orders.Select(order => order.TrackingToken).Distinct().Count());
        using var client = new HttpClient { BaseAddress = baseAddress };
        foreach (var order in orders)
        {
            using var response = await client.GetAsync(
                $"/api/v1/tracking/{Uri.EscapeDataString(order.TrackingToken)}",
                cancellationToken);
            await EnsureStatusAsync(response, HttpStatusCode.OK, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(
                "DELIVERED",
                json.RootElement.GetProperty("public_status").GetString());
            Assert.Equal(
                ExpectedVersion,
                json.RootElement.GetProperty("aggregate_version").GetInt32());
            var timeline = json.RootElement.GetProperty("timeline")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(
                [
                    "ORDER_CREATED",
                    "PICKUP_SCHEDULED",
                    "PICKED_UP",
                    "IN_TRANSIT",
                    "OUT_FOR_DELIVERY",
                    "DELIVERED",
                ],
                timeline.Select(item =>
                    item.GetProperty("code").GetString()
                    ?? throw new Ops001AcceptanceException(
                        "A public timeline code is missing."))
                    .ToArray());
            var occurredAt = timeline
                .Select(item => item.GetProperty("occurred_at").GetDateTimeOffset())
                .ToArray();
            Assert.All(occurredAt, value => Assert.Equal(TimeSpan.Zero, value.Offset));
            Assert.Equal(occurredAt.Order().ToArray(), occurredAt);
            Assert.DoesNotContain("driver", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("assignment", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("payload", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task AssertDashboardAsync(
        Uri baseAddress,
        Ops001ScenarioData data,
        IReadOnlyCollection<ScenarioOrder> orders,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = baseAddress };
        using var request = AuthorizedRequest(
            HttpMethod.Get,
            "/api/v1/operations/dashboard?status=DELIVERED",
            data.OrganizationId);
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureStatusAsync(response, HttpStatusCode.OK, cancellationToken);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(Ops001ScenarioData.OrderCount, items.Length);
        Assert.Null(json.RootElement.GetProperty("next_cursor").GetString());
        var expected = orders.ToDictionary(order => order.OrderId);
        foreach (var item in items)
        {
            var orderId = item.GetProperty("order_id").GetGuid();
            var scenario = Assert.IsType<ScenarioOrder>(expected.GetValueOrDefault(orderId));
            Assert.Equal("DELIVERED", item.GetProperty("status").GetString());
            Assert.Equal(ExpectedVersion, item.GetProperty("aggregate_version").GetInt32());
            var assignment = item.GetProperty("assignment");
            Assert.Equal("OWN", assignment.GetProperty("assignment_type").GetString());
            Assert.Equal(scenario.DriverId, assignment.GetProperty("driver_id").GetGuid());
            Assert.False(item.GetProperty("unassigned_alert").GetBoolean());
        }

        using var decoyRequest = AuthorizedRequest(
            HttpMethod.Get,
            "/api/v1/operations/dashboard?status=DELIVERED",
            data.DecoyOrganizationId);
        using var decoyResponse = await client.SendAsync(decoyRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, decoyResponse.StatusCode);
    }

    private static async Task AssertDriverStopsClearedAsync(
        IServiceProvider services,
        Ops001ScenarioData data,
        CancellationToken cancellationToken)
    {
        foreach (var driverUserId in data.DriverUserIds)
        {
            await using var scope = services.CreateAsyncScope();
            var stops = await scope.ServiceProvider
                .GetRequiredService<IDriverStopsQuery>()
                .ListCurrentDriverStopsAsync(
                    driverUserId,
                    data.OrganizationId,
                    cancellationToken);
            Assert.Empty(stops);
        }
    }

    private static async Task AssertNoSimulationEndpointAsync(
        Uri baseAddress,
        Ops001ScenarioData data,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = baseAddress };
        using var request = AuthorizedRequest(
            HttpMethod.Post,
            "/api/v1/operations/simulation",
            data.OrganizationId);
        request.Content = JsonContent.Create(new { scenario = "OPS-001" });
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<Ops001SimulationReport> BuildReportAsync(
        Ops001ScenarioData data,
        IReadOnlyCollection<ScenarioOrder> orders,
        Ops001RealtimeCorrelation realtimeCorrelation,
        Guid poisonId,
        Guid newerOutboxId,
        Guid interruptedOutboxId,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AdminConnectionString);
        await connection.OpenAsync(cancellationToken);

        var orderIds = orders.Select(order => order.OrderId).ToArray();
        await AssertPerOrderIntegrityAsync(
            connection,
            data.OrganizationId,
            orders,
            cancellationToken);
        var ordersCreated = await ScalarAsync<int>(
            connection,
            """
            SELECT count(*)::integer
            FROM orders.orders
            WHERE owner_org_id=@org AND id=ANY(@orders)
            """,
            cancellationToken,
            P("org", data.OrganizationId),
            PA("orders", orderIds));
        var ordersDelivered = await ScalarAsync<int>(
            connection,
            """
            SELECT count(*)::integer
            FROM orders.orders
            WHERE owner_org_id=@org AND id=ANY(@orders)
              AND status='DELIVERED' AND version=@version
            """,
            cancellationToken,
            P("org", data.OrganizationId),
            PA("orders", orderIds),
            PI("version", ExpectedVersion));
        var assignments = await ScalarAsync<int>(
            connection,
            """
            SELECT count(*)::integer
            FROM dispatch.assignments
            WHERE owner_org_id=@org AND order_id=ANY(@orders)
              AND assignment_type='OWN' AND status IN ('ACCEPTED','ACTIVE','COMPLETED')
            """,
            cancellationToken,
            P("org", data.OrganizationId),
            PA("orders", orderIds));
        var pickupProofs = await CountProofsAsync(
            connection,
            data.OrganizationId,
            orderIds,
            "PICKUP_PHOTO",
            cancellationToken);
        var deliveryProofs = await CountProofsAsync(
            connection,
            data.OrganizationId,
            orderIds,
            "DELIVERY_PHOTO",
            cancellationToken);
        var persistedVersions = await ReadPersistedVersionsAsync(
            connection,
            data.OrganizationId,
            orderIds,
            cancellationToken);
        var domainEventsPersisted = persistedVersions.Count;
        var expectedMaximumVersions = orderIds.ToDictionary(id => id, _ => ExpectedVersion);
        var missingVersions = Ops001SimulationReport.CountMissingVersions(
            persistedVersions,
            expectedMaximumVersions);
        var auditExpectations = await ReadAuditExpectationsAsync(
            connection,
            data.OrganizationId,
            orderIds,
            cancellationToken);
        Assert.Equal(
            ExpectedAuditsPerOrder * Ops001ScenarioData.OrderCount,
            auditExpectations.Count);
        var auditObservations = await ReadAuditObservationsAsync(
            connection,
            data.OrganizationId,
            orderIds,
            auditExpectations,
            cancellationToken);
        var auditCorrelation = Ops001AuditCorrelation.Correlate(
            auditExpectations,
            auditObservations);
        if (auditCorrelation.Missing != 0 ||
            auditCorrelation.Duplicated != 0 ||
            auditCorrelation.Mismatched != 0)
        {
            var missingByAction = auditExpectations
                .Where(expected => !auditObservations.Contains(expected))
                .GroupBy(value => value.Action)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}:{group.Count()}");
            var mismatchedByAction = auditObservations
                .Where(observed => !auditExpectations.Contains(observed))
                .GroupBy(value => value.Action)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}:{group.Count()}");
            throw new Ops001AcceptanceException(
                "Audit evidence did not correlate exactly. " +
                $"missing={auditCorrelation.Missing}; " +
                $"duplicated={auditCorrelation.Duplicated}; " +
                $"mismatched={auditCorrelation.Mismatched}; " +
                $"missing_by_action=[{string.Join(',', missingByAction)}]; " +
                $"mismatched_by_action=[{string.Join(',', mismatchedByAction)}].");
        }
        var outboxProcessed = await ScalarAsync<int>(
            connection,
            """
            SELECT count(*)::integer
            FROM platform.outbox_events
            WHERE owner_org_id=@org AND status='PROCESSED'
            """,
            cancellationToken,
            P("org", data.OrganizationId));
        var outboxDead = await ScalarAsync<int>(
            connection,
            """
            SELECT count(*)::integer
            FROM platform.outbox_events
            WHERE owner_org_id=@org AND status='DEAD'
            """,
            cancellationToken,
            P("org", data.OrganizationId));
        var activeOutbox = await ScalarAsync<int>(
            connection,
            """
            SELECT count(*)::integer
            FROM platform.outbox_events
            WHERE owner_org_id=@org AND status IN ('PENDING','RETRY','PROCESSING')
            """,
            cancellationToken,
            P("org", data.OrganizationId));
        Assert.Equal(0, activeOutbox);
        var interrupted = await ReadOutboxStateAsync(
            interruptedOutboxId,
            cancellationToken);
        Assert.NotNull(interrupted);
        Assert.Equal("PROCESSED", interrupted.Status);
        Assert.Equal(2, interrupted.Attempts);
        var poison = await ReadOutboxStateAsync(poisonId, cancellationToken);
        Assert.NotNull(poison);
        var newer = await ReadOutboxStateAsync(newerOutboxId, cancellationToken);
        Assert.NotNull(newer);

        return new Ops001SimulationReport(
            OrdersPlanned: Ops001ScenarioData.OrderCount,
            OrdersCreated: ordersCreated,
            OrdersDelivered: ordersDelivered,
            AssignmentsCreated: assignments,
            PickupProofsExpected: Ops001ScenarioData.OrderCount,
            PickupProofsCompleted: pickupProofs,
            DeliveryProofsExpected: Ops001ScenarioData.OrderCount,
            DeliveryProofsCompleted: deliveryProofs,
            DomainEventsExpected:
                ExpectedDomainEventsPerOrder * Ops001ScenarioData.OrderCount,
            DomainEventsPersisted: domainEventsPersisted,
            RealtimeEventsExpected: realtimeCorrelation.Expected,
            RealtimeEventsMatched: realtimeCorrelation.Matched,
            RealtimeEventsMissing: realtimeCorrelation.Missing,
            RealtimeEventsUnexpected: realtimeCorrelation.Unexpected,
            RealtimeEventsMismatched: realtimeCorrelation.Mismatched,
            RealtimeRawDeliveries: realtimeCorrelation.RawDeliveries,
            RealtimeDuplicateDeliveries: realtimeCorrelation.DuplicateDeliveries,
            RealtimeObservationPercent: realtimeCorrelation.ObservationPercent,
            AuditsExpected: auditCorrelation.Expected,
            AuditsExactlyMatched: auditCorrelation.ExactlyMatched,
            AuditsMissing: auditCorrelation.Missing,
            AuditsDuplicated: auditCorrelation.Duplicated,
            AuditsMismatched: auditCorrelation.Mismatched,
            OutboxProcessed: outboxProcessed,
            OutboxDeadExpected: 1,
            OutboxDeadActual: outboxDead,
            StaleRecoveries: interrupted.Attempts - 1,
            StaleLeaseRejections: 1,
            MissingAggregateVersions: missingVersions,
            SecondaryTenantRows: await CountSecondaryTenantRowsAsync(
                connection,
                data,
                orderIds,
                cancellationToken),
            NewerMessageProcessedAfterPoison:
                poison.Status == "DEAD" &&
                newer.Status == "PROCESSED" &&
                await WasCreatedAfterAsync(
                    connection,
                    newerOutboxId,
                    poisonId,
                    cancellationToken),
            Duration: duration.ToString("c", CultureInfo.InvariantCulture));
    }

    private async Task AssertNoSensitiveEvidenceAsync(
        Guid organizationId,
        IReadOnlyCollection<ScenarioOrder> orders,
        IReadOnlyCollection<string> logMessages,
        Ops001SimulationReport report,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        var auditText = await ScalarAsync<string>(
            connection,
            """
            SELECT COALESCE(
              string_agg(payload_redacted::text,E'\n' ORDER BY occurred_at),
              '')
            FROM platform.audit_logs
            WHERE org_id=@org
            """,
            cancellationToken,
            P("org", organizationId));
        var logText = string.Join(Environment.NewLine, logMessages);
        var sensitiveValues = orders.Select(order => order.TrackingToken)
            .Append(Convert.ToHexString(SyntheticPngSha256).ToLowerInvariant())
            .Append("quarantine/")
            .Append("proofs/")
            .Append(PoisonSentinel)
            .ToArray();
        foreach (var value in sensitiveValues)
        {
            Assert.DoesNotContain(value, auditText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(value, logText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(value, report.ToJson(), StringComparison.OrdinalIgnoreCase);
        }

        foreach (var token in orders.Select(order => order.TrackingToken))
        {
            var hashCount = await ScalarAsync<int>(
                connection,
                """
                SELECT count(*)::integer
                FROM orders.public_tracking_tokens
                WHERE owner_org_id=@org AND token_hash=@hash
                """,
                cancellationToken,
                P("org", organizationId),
                new NpgsqlParameter<byte[]>("hash", NpgsqlDbType.Bytea)
                {
                    TypedValue = SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(token)),
                });
            Assert.Equal(1, hashCount);
        }
    }

    private async Task HoldScenarioOutboxAsync(
        Guid organizationId,
        CancellationToken cancellationToken) =>
        await ExecuteAdminAsync(
            """
            UPDATE platform.outbox_events
            SET available_at=clock_timestamp()+interval '2 hours'
            WHERE owner_org_id=@org AND status IN ('PENDING','RETRY')
            """,
            cancellationToken,
            P("org", organizationId));

    private async Task MakeRealtimeOutboxAvailableAsync(
        Guid organizationId,
        CancellationToken cancellationToken) =>
        await ExecuteAdminAsync(
            """
            UPDATE platform.outbox_events
            SET available_at=clock_timestamp()
            WHERE owner_org_id=@org
              AND topic IN (
                'orders.status-changed',
                'orders.timeline-event-added',
                'dispatch.assignment-changed')
              AND status IN ('PENDING','RETRY')
            """,
            cancellationToken,
            P("org", organizationId));

    private async Task MakeOutboxAvailableAsync(
        Guid outboxId,
        CancellationToken cancellationToken) =>
        await ExecuteAdminAsync(
            """
            UPDATE platform.outbox_events
            SET available_at=clock_timestamp()
            WHERE id=@id AND status IN ('PENDING','RETRY')
            """,
            cancellationToken,
            P("id", outboxId));

    private async Task ExpireLeaseAsync(
        Guid outboxId,
        CancellationToken cancellationToken) =>
        await ExecuteAdminAsync(
            """
            UPDATE platform.outbox_events
            SET lease_expires_at=clock_timestamp()-interval '1 second'
            WHERE id=@id AND status='PROCESSING'
            """,
            cancellationToken,
            P("id", outboxId));

    private async Task ReleaseOwnedProcessingLeaseAsync(
        Guid outboxId,
        CancellationToken cancellationToken) =>
        await ExecuteAdminAsync(
            """
            UPDATE platform.outbox_events
            SET status='RETRY',
                available_at=clock_timestamp(),
                locked_at=NULL,
                locked_by=NULL,
                lease_token=NULL,
                lease_expires_at=NULL
            WHERE id=@id AND status='PROCESSING'
            """,
            cancellationToken,
            P("id", outboxId));

    private async Task<Guid> InsertPoisonAsync(
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var poisonId = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,
              aggregate_version,payload,priority,status,attempts,available_at,created_at)
            VALUES (
              @id,@org,
              jsonb_build_object(
                'organization_ids',jsonb_build_array(@org::text)),
              'ops001.poison','Order',@order,NULL,
              jsonb_build_object('sentinel',@sentinel),
              100,'PENDING',0,clock_timestamp(),clock_timestamp())
            """,
            cancellationToken,
            P("id", poisonId),
            P("org", organizationId),
            P("order", orderId),
            new NpgsqlParameter<string>("sentinel", NpgsqlDbType.Text)
            {
                TypedValue = PoisonSentinel,
            });
        return poisonId;
    }

    private async Task<bool> SettleOutboxAsync(
        Guid outboxId,
        Guid leaseToken,
        string status,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(
            fixture.Database.WorkerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetWorkerRoleAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT security.settle_outbox(
              @id,@lease_token,@status,NULL,NULL);
            """,
            connection,
            transaction);
        command.Parameters.Add(P("id", outboxId));
        command.Parameters.Add(P("lease_token", leaseToken));
        command.Parameters.Add(new NpgsqlParameter<string>("status", NpgsqlDbType.Text)
        {
            TypedValue = status,
        });
        var result = await command.ExecuteScalarAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result is true;
    }

    private async Task CompleteOrderCreatedOutboxAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await ExecuteAdminAsync(
            """
            UPDATE platform.outbox_events
            SET available_at=clock_timestamp()
            WHERE owner_org_id=@org AND topic='orders.created'
              AND status IN ('PENDING','RETRY')
            """,
            cancellationToken,
            P("org", organizationId));

        var completed = 0;
        while (completed < Ops001ScenarioData.OrderCount)
        {
            await using var connection = new NpgsqlConnection(
                fixture.Database.WorkerConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                cancellationToken);
            await SetWorkerRoleAsync(connection, transaction, cancellationToken);
            var claimed = new List<(Guid Id, Guid LeaseToken, string Topic)>();
            await using (var claim = new NpgsqlCommand(
                             """
                             SELECT id,lease_token,topic
                             FROM security.claim_outbox(
                               'ops001-non-realtime-consumer',20,interval '30 seconds')
                             """,
                             connection,
                             transaction))
            await using (var reader = await claim.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    claimed.Add((
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        reader.GetString(2)));
                }
            }

            Assert.NotEmpty(claimed);
            Assert.All(
                claimed,
                item => Assert.Equal("orders.created", item.Topic));
            foreach (var message in claimed)
            {
                await using var settle = new NpgsqlCommand(
                    """
                    SELECT security.settle_outbox(
                      @id,@lease_token,'PROCESSED',NULL,NULL)
                    """,
                    connection,
                    transaction);
                settle.Parameters.Add(P("id", message.Id));
                settle.Parameters.Add(P("lease_token", message.LeaseToken));
                Assert.True(await settle.ExecuteScalarAsync(cancellationToken) is true);
                completed++;
            }

            await transaction.CommitAsync(cancellationToken);
        }

        Assert.Equal(Ops001ScenarioData.OrderCount, completed);
    }

    private async Task<Guid> ReadStatusOutboxIdAsync(
        Guid orderId,
        int aggregateVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await ScalarAsync<Guid>(
            connection,
            """
            SELECT id
            FROM platform.outbox_events
            WHERE aggregate_id=@order
              AND aggregate_version=@version
              AND topic='orders.status-changed'
            """,
            cancellationToken,
            P("order", orderId),
            PI("version", aggregateVersion));
    }

    private async Task<PostgreSqlSecurityWebApplicationFactory.OutboxDeliveryState?>
        ReadOutboxStateAsync(
            Guid outboxId,
            CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT status,attempts,lease_token,lease_expires_at,last_error
            FROM platform.outbox_events
            WHERE id=@id
            """,
            connection);
        command.Parameters.Add(P("id", outboxId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetString(4))
            : null;
    }

    private async Task<PostgreSqlSecurityWebApplicationFactory.OutboxDeliveryState>
        WaitForOutboxStateAsync(
            Guid outboxId,
            Func<PostgreSqlSecurityWebApplicationFactory.OutboxDeliveryState, bool> predicate,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (!deadline.IsCancellationRequested)
        {
            var state = await ReadOutboxStateAsync(outboxId, deadline.Token);
            if (state is not null && predicate(state))
            {
                return state;
            }

            await Task.Delay(50, deadline.Token);
        }

        throw new TimeoutException("An outbox row did not reach the required state.");
    }

    private async Task WaitForRealtimeOutboxCompletionAsync(
        Guid organizationId,
        int expected,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (!deadline.IsCancellationRequested)
        {
            await using var connection = new NpgsqlConnection(
                fixture.Database.AdminConnectionString);
            await connection.OpenAsync(deadline.Token);
            var processed = await ScalarAsync<int>(
                connection,
                """
                SELECT count(*)::integer
                FROM platform.outbox_events
                WHERE owner_org_id=@org
                  AND topic IN (
                    'orders.status-changed',
                    'orders.timeline-event-added',
                    'dispatch.assignment-changed')
                  AND status='PROCESSED'
                """,
                deadline.Token,
                P("org", organizationId));
            if (processed == expected)
            {
                return;
            }

            await Task.Delay(100, deadline.Token);
        }

        throw new TimeoutException("Realtime outbox processing did not complete.");
    }

    private async Task<IReadOnlyList<Ops001RealtimeExpectation>>
        ReadRealtimeExpectationsAsync(
            Guid organizationId,
            Guid[] orderIds,
            CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(
            fixture.Database.AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT id,aggregate_id,aggregate_version
            FROM platform.outbox_events
            WHERE owner_org_id=@org
              AND aggregate_id=ANY(@orders)
              AND aggregate_version BETWEEN 2 AND 9
              AND topic='orders.status-changed'
            ORDER BY aggregate_id,aggregate_version
            """,
            connection);
        command.Parameters.Add(P("org", organizationId));
        command.Parameters.Add(PA("orders", orderIds));
        var result = new List<Ops001RealtimeExpectation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt32(2),
                global::Realtime.Application.Events.RealtimeEventTypes.OrderStatusChanged));
        }

        return result;
    }

    private static async Task WaitForExpectedEventsAsync(
        IReadOnlyCollection<Ops001RealtimeDelivery> initial,
        Ops001RealtimeObserver observer,
        IReadOnlyCollection<Ops001RealtimeExpectation> expected,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var correlation = Ops001RealtimeCorrelation.Correlate(
                    expected,
                    initial.Concat(observer.Deliveries));
                if (correlation.Matched == expected.Count)
                {
                    return;
                }

                await Task.Delay(50, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Realtime observations did not match every expected outbox event.");
        }

        throw new TimeoutException(
            "Realtime observations did not match every expected outbox event.");
    }

    private async Task ProcessProofUntilReadyAsync(
        IServiceProvider services,
        Guid sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                await using var connection = new NpgsqlConnection(
                    fixture.Database.AdminConnectionString);
                await connection.OpenAsync(deadline.Token);
                var status = await ScalarAsync<string>(
                    connection,
                    "SELECT status FROM custody.proof_upload_sessions WHERE id=@id",
                    deadline.Token,
                    P("id", sessionId));
                if (status == "READY")
                {
                    return;
                }

                if (status is "REJECTED" or "EXPIRED")
                {
                    throw new Ops001AcceptanceException(
                        "A required synthetic proof was rejected.");
                }

                try
                {
                    await using var scope = services.CreateAsyncScope();
                    await scope.ServiceProvider
                        .GetRequiredService<IProofValidationProcessor>()
                        .ProcessAvailableAsync(deadline.Token);
                }
                catch (NullReferenceException)
                {
                    // The S3-compatible adapter can expose a null object
                    // collection while the quarantine listing is empty.
                }

                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("A proof upload did not become READY.");
        }

        throw new TimeoutException("A proof upload did not become READY.");
    }

    private async Task ExecuteAdminAsync(
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetWorkerRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SET LOCAL ROLE paqueteria_worker;",
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid> PostForIdAsync(
        HttpClient client,
        string path,
        Guid organizationId,
        string idempotencyKey,
        object body,
        HttpStatusCode expectedStatus,
        CancellationToken cancellationToken)
    {
        using var request = AuthorizedRequest(HttpMethod.Post, path, organizationId);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureStatusAsync(response, expectedStatus, cancellationToken);
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static HttpRequestMessage AuthorizedRequest(
        HttpMethod method,
        string path,
        Guid organizationId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            MockIdentityProfiles.ActiveDispatcher);
        request.Headers.Add("X-Organization-Id", organizationId.ToString("D"));
        return request;
    }

    private static async Task EnsureStatusAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == expected)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new Ops001AcceptanceException(
            $"Expected HTTP {(int)expected} but received {(int)response.StatusCode}. " +
            $"Safe response length: {body.Length}.");
    }

    private static async Task<T[]> RunBoundedAsync<TInput, T>(
        IEnumerable<TInput> inputs,
        int degreeOfParallelism,
        Func<TInput, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(degreeOfParallelism);
        var tasks = inputs.Select(async input =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await action(input);
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }

    private static async Task RunBoundedAsync<TInput>(
        IEnumerable<TInput> inputs,
        int degreeOfParallelism,
        Func<TInput, Task> action,
        CancellationToken cancellationToken) =>
        await RunBoundedAsync(
            inputs,
            degreeOfParallelism,
            async input =>
            {
                await action(input);
                return true;
            },
            cancellationToken);

    private static async Task<int> CountProofsAsync(
        NpgsqlConnection connection,
        Guid organizationId,
        Guid[] orderIds,
        string proofType,
        CancellationToken cancellationToken) =>
        await ScalarAsync<int>(
            connection,
            """
            SELECT count(*)::integer
            FROM custody.proofs
            WHERE owner_org_id=@org AND order_id=ANY(@orders)
              AND proof_type=@proof_type
            """,
            cancellationToken,
            P("org", organizationId),
            PA("orders", orderIds),
            new NpgsqlParameter<string>("proof_type", NpgsqlDbType.Text)
            {
                TypedValue = proofType,
            });

    private static async Task AssertPerOrderIntegrityAsync(
        NpgsqlConnection connection,
        Guid organizationId,
        IEnumerable<ScenarioOrder> orders,
        CancellationToken cancellationToken)
    {
        string[] expectedStatuses =
        [
            "DRAFT",
            "CONFIRMED",
            "READY_FOR_PICKUP",
            "ASSIGNED",
            "AT_PICKUP",
            "PICKED_UP",
            "IN_TRANSIT",
            "DELIVERING",
            "DELIVERED",
        ];
        foreach (var order in orders)
        {
            await using (var counts = new NpgsqlCommand(
                             """
                             SELECT
                               (
                                 SELECT count(*)::integer
                                 FROM dispatch.assignments a
                                 WHERE a.owner_org_id=@org
                                   AND a.order_id=@order
                                   AND a.id=@assignment
                                   AND a.driver_id=@driver
                                   AND a.assignment_type='OWN'
                                   AND a.status IN ('ACCEPTED','ACTIVE','COMPLETED')
                               ),
                               (
                                 SELECT count(*)::integer
                                 FROM dispatch.assignments a
                                 WHERE a.owner_org_id=@org AND a.order_id=@order
                               ),
                               (
                                 SELECT count(*)::integer
                                 FROM custody.proofs p
                                 WHERE p.owner_org_id=@org AND p.order_id=@order
                                   AND p.proof_type='PICKUP_PHOTO'
                               ),
                               (
                                 SELECT count(*)::integer
                                 FROM custody.proofs p
                                 WHERE p.owner_org_id=@org AND p.order_id=@order
                                   AND p.proof_type='DELIVERY_PHOTO'
                               ),
                               (
                                 SELECT count(*)::integer
                                 FROM custody.proofs p
                                 WHERE p.owner_org_id=@org AND p.order_id=@order
                               )
                             """,
                             connection))
            {
                counts.Parameters.Add(P("org", organizationId));
                counts.Parameters.Add(P("order", order.OrderId));
                counts.Parameters.Add(P("assignment", order.AssignmentId));
                counts.Parameters.Add(P("driver", order.DriverId));
                await using var reader = await counts.ExecuteReaderAsync(cancellationToken);
                Assert.True(await reader.ReadAsync(cancellationToken));
                Assert.Equal(1, reader.GetInt32(0));
                Assert.Equal(1, reader.GetInt32(1));
                Assert.Equal(1, reader.GetInt32(2));
                Assert.Equal(1, reader.GetInt32(3));
                Assert.Equal(2, reader.GetInt32(4));
            }

            await using var events = new NpgsqlCommand(
                """
                SELECT aggregate_version,event_type,
                       COALESCE(payload->>'new_status',payload->>'status'),
                       occurred_at
                FROM orders.order_events
                WHERE owner_org_id=@org AND order_id=@order
                ORDER BY aggregate_version
                """,
                connection);
            events.Parameters.Add(P("org", organizationId));
            events.Parameters.Add(P("order", order.OrderId));
            var actualStatuses = new List<string>();
            var versions = new List<int>();
            var occurredAt = new List<DateTimeOffset>();
            await using var eventReader = await events.ExecuteReaderAsync(cancellationToken);
            while (await eventReader.ReadAsync(cancellationToken))
            {
                versions.Add(eventReader.GetInt32(0));
                Assert.Equal(
                    versions.Count == 1 ? "ORDER_CREATED" : "ORDER_STATUS_CHANGED",
                    eventReader.GetString(1));
                actualStatuses.Add(eventReader.GetString(2));
                occurredAt.Add(eventReader.GetFieldValue<DateTimeOffset>(3));
            }

            Assert.Equal(Enumerable.Range(1, ExpectedVersion), versions);
            Assert.Equal(expectedStatuses, actualStatuses);
            Assert.Equal(occurredAt.Order().ToArray(), occurredAt);
        }
    }

    private static async Task<List<(Guid OrderId, int Version)>> ReadPersistedVersionsAsync(
        NpgsqlConnection connection,
        Guid organizationId,
        Guid[] orderIds,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT order_id,aggregate_version
            FROM orders.order_events
            WHERE owner_org_id=@org AND order_id=ANY(@orders)
            ORDER BY order_id,aggregate_version
            """,
            connection);
        command.Parameters.Add(P("org", organizationId));
        command.Parameters.Add(PA("orders", orderIds));
        var result = new List<(Guid, int)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add((reader.GetGuid(0), reader.GetInt32(1)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<Ops001AuditEvidence>>
        ReadAuditExpectationsAsync(
        NpgsqlConnection connection,
        Guid organizationId,
        Guid[] orderIds,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT e.id,e.owner_org_id,e.order_id,e.order_id,'Order',
                   CASE WHEN e.aggregate_version=1
                        THEN 'ORDER_CREATED'
                        ELSE 'ORDER_STATUS_CHANGED' END,
                   CASE WHEN e.aggregate_version=1
                        THEN NULL
                        ELSE e.aggregate_version END,
                   e.occurred_at
            FROM orders.order_events e
            WHERE e.owner_org_id=@org AND e.order_id=ANY(@orders)
              AND e.aggregate_version BETWEEN 1 AND 9
            UNION ALL
            SELECT t.order_id,t.owner_org_id,t.order_id,t.order_id,
                   'PublicTrackingToken','TRACKING_TOKEN_ISSUED',
                   NULL,t.created_at
            FROM orders.public_tracking_tokens t
            WHERE t.owner_org_id=@org AND t.order_id=ANY(@orders)
            UNION ALL
            SELECT a.id,a.owner_org_id,a.order_id,a.id,
                   'Assignment','ASSIGNMENT_CREATED',
                   NULL,a.created_at
            FROM dispatch.assignments a
            WHERE a.owner_org_id=@org AND a.order_id=ANY(@orders)
            UNION ALL
            SELECT s.id,s.owner_org_id,s.order_id,s.id,
                   'proof_upload_session',
                   'custody.proof_upload_session.created',
                   NULL,s.created_at
            FROM custody.proof_upload_sessions s
            WHERE s.owner_org_id=@org AND s.order_id=ANY(@orders)
            UNION ALL
            SELECT s.id,s.owner_org_id,s.order_id,s.id,
                   'proof_upload_session',
                   'custody.proof_upload_session.ready',
                   NULL,NULL::timestamptz
            FROM custody.proof_upload_sessions s
            WHERE s.owner_org_id=@org AND s.order_id=ANY(@orders)
            UNION ALL
            SELECT p.id,p.owner_org_id,p.order_id,p.id,
                   'proof','custody.proof.finalized',
                   NULL,p.created_at
            FROM custody.proofs p
            WHERE p.owner_org_id=@org AND p.order_id=ANY(@orders)
            """,
            connection);
        command.Parameters.Add(P("org", organizationId));
        command.Parameters.Add(PA("orders", orderIds));
        var result = new List<Ops001AuditEvidence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<Ops001AuditEvidence>>
        ReadAuditObservationsAsync(
            NpgsqlConnection connection,
            Guid organizationId,
            Guid[] orderIds,
            IReadOnlyCollection<Ops001AuditEvidence> expectations,
            CancellationToken cancellationToken)
    {
        var entityIds = expectations
            .Select(value => value.EntityId)
            .Distinct()
            .ToArray();
        var orderTexts = orderIds.Select(value => value.ToString("D")).ToArray();
        await using var command = new NpgsqlCommand(
            """
            SELECT id,org_id,action,entity_type,entity_id,
                   payload_redacted::text,occurred_at
            FROM platform.audit_logs
            WHERE action IN (
              'ORDER_CREATED',
              'ORDER_STATUS_CHANGED',
              'TRACKING_TOKEN_ISSUED',
              'ASSIGNMENT_CREATED',
              'custody.proof_upload_session.created',
              'custody.proof_upload_session.ready',
              'custody.proof.finalized')
              AND (
                entity_id=ANY(@entities)
                OR payload_redacted->>'order_id'=ANY(@order_texts))
            ORDER BY occurred_at,id
            """,
            connection);
        command.Parameters.Add(PA("entities", entityIds));
        command.Parameters.Add(new NpgsqlParameter<string[]>(
            "order_texts",
            NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = orderTexts,
        });

        var expectedByEntity = expectations
            .GroupBy(value => value.EntityId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var result = new List<Ops001AuditEvidence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var auditId = reader.GetGuid(0);
            var auditOrganizationId = reader.GetGuid(1);
            var action = reader.GetString(2);
            var entityType = reader.GetString(3);
            var entityId = reader.GetGuid(4);
            using var payload = JsonDocument.Parse(reader.GetString(5));
            var payloadRoot = payload.RootElement;
            var orderId =
                TryReadGuid(payloadRoot, "order_id") ??
                expectedByEntity.GetValueOrDefault(entityId)?.FirstOrDefault()?.OrderId ??
                Guid.Empty;
            var aggregateVersion = action == "ORDER_STATUS_CHANGED"
                ? TryReadInt32(payloadRoot, "new_version")
                : null;
            var operationId = ResolveAuditOperationId(
                auditId,
                action,
                entityId,
                orderId,
                aggregateVersion,
                payloadRoot,
                expectations);
            DateTimeOffset? occurredAt =
                action == "custody.proof_upload_session.ready"
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(6);
            result.Add(new(
                operationId,
                auditOrganizationId,
                orderId,
                entityId,
                entityType,
                action,
                aggregateVersion,
                occurredAt));
        }

        return result;
    }

    private static Guid ResolveAuditOperationId(
        Guid auditId,
        string action,
        Guid entityId,
        Guid orderId,
        int? aggregateVersion,
        JsonElement payload,
        IReadOnlyCollection<Ops001AuditEvidence> expectations)
    {
        if (action is "custody.proof_upload_session.created"
            or "custody.proof_upload_session.ready"
            or "custody.proof.finalized")
        {
            return entityId;
        }

        if (action == "ASSIGNMENT_CREATED")
        {
            return TryReadGuid(payload, "assignment_id") ?? entityId;
        }

        if (action == "TRACKING_TOKEN_ISSUED")
        {
            return orderId == Guid.Empty ? auditId : orderId;
        }

        return expectations.FirstOrDefault(value =>
                   value.OrderId == orderId &&
                   value.Action == action &&
                   value.AggregateVersion == aggregateVersion)
               ?.OperationId ??
            auditId;
    }

    private static Guid? TryReadGuid(JsonElement payload, string propertyName) =>
        payload.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        property.TryGetGuid(out var value)
            ? value
            : null;

    private static int? TryReadInt32(JsonElement payload, string propertyName) =>
        payload.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt32(out var value)
            ? value
            : null;

    private static async Task<int> CountSecondaryTenantRowsAsync(
        NpgsqlConnection connection,
        Ops001ScenarioData data,
        Guid[] orderIds,
        CancellationToken cancellationToken) =>
        await ScalarAsync<int>(
            connection,
            """
            SELECT (
              SELECT count(*) FROM orders.orders
              WHERE owner_org_id=@decoy_org AND id=ANY(@orders)
            )::integer + (
              SELECT count(*) FROM platform.audit_logs
              WHERE org_id=@decoy_org AND entity_id=ANY(@orders)
            )::integer
            """,
            cancellationToken,
            P("decoy_org", data.DecoyOrganizationId),
            PA("orders", orderIds));

    private static async Task<bool> WasCreatedAfterAsync(
        NpgsqlConnection connection,
        Guid newerId,
        Guid earlierId,
        CancellationToken cancellationToken) =>
        await ScalarAsync<bool>(
            connection,
            """
            SELECT newer.created_at >= earlier.created_at
            FROM platform.outbox_events newer
            CROSS JOIN platform.outbox_events earlier
            WHERE newer.id=@newer AND earlier.id=@earlier
            """,
            cancellationToken,
            P("newer", newerId),
            P("earlier", earlierId));

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is T typed
            ? typed
            : (T)Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task WriteReportAsync(
        Ops001SimulationReport report,
        string? pathOverride,
        CancellationToken cancellationToken)
    {
        var path = pathOverride;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Environment.GetEnvironmentVariable("OPS001_REPORT_PATH");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(
                AppContext.BaseDirectory,
                "TestResults",
                "ops001-delivery-simulation.json");
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The report directory is missing."));
        await File.WriteAllTextAsync(fullPath, report.ToJson(), cancellationToken);
    }

    private static DateTimeOffset UtcMicrosecond(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    private static NpgsqlParameter P(string name, Guid value) =>
        new(name, NpgsqlDbType.Uuid) { Value = value };

    private static NpgsqlParameter PI(string name, int value) =>
        new(name, NpgsqlDbType.Integer) { Value = value };

    private static NpgsqlParameter PA(string name, Guid[] values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = values };

    private sealed class ScenarioOrder(
        int number,
        Guid quoteId,
        Guid orderId,
        Guid driverId,
        Guid driverUserId,
        string trackingToken)
    {
        internal int Number { get; } = number;
        internal Guid QuoteId { get; } = quoteId;
        internal Guid OrderId { get; } = orderId;
        internal Guid DriverId { get; } = driverId;
        internal Guid DriverUserId { get; } = driverUserId;
        internal string TrackingToken { get; } = trackingToken;
        internal Guid AssignmentId { get; set; }
        internal int Version { get; set; } = 1;
    }
}
