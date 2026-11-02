using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.IntegrationTests.PlatformEndToEnd;
using Xunit.Abstractions;

namespace Paqueteria.IntegrationTests.PilotRehearsal;

/// <summary>
/// REL-001 entry phase, the synthetic rehearsal (REL-001-PILOT-READINESS-2026-11-02): Z4 "Local/CI" - containers on
/// the runner and no Azure, with delivery photos through the storage double and the Synthetic scanner - and Z5 "Solo en
/// el ensayo" - the cobro y fallas journey on the real platform, run only by the on-demand pilot readiness workflow.
/// It drives the assembled API, Worker, PostgreSQL/PostGIS, storage and production web build of the OPS-005 harness:
/// <list type="bullet">
/// <item>at least 50 synthetic deliveries with both photos through the API, among them cash on delivery recorded,
/// reconciled and closed, a failed attempt decided by dispatch and redelivered, and orders created by a CSV import;</item>
/// <item>a settlement calculated, approved, paid and exported by finance;</item>
/// <item>AI-02 orders_with_complete_events_and_pod over every delivered order (Z6, entry half);</item>
/// <item>the AI-02 latencies of the five critical screens and of order creation in Chromium, 100 samples each (Z7, Z8),
/// and the AI-15 technical ones, reported only.</item>
/// </list>
/// It measures and writes the evidence; <c>tools/rel-001/rel001.py</c> judges the thresholds. CI runners do not represent
/// the pilot's burstable database server, and the report says so.
/// </summary>
/// <remarks>
/// Test-host settings only: the module providers the journey needs (Finance on PostgreSQL, the synthetic incident PII
/// protector) and every SEC-004 permit raised to its maximum (10,000 per window; public tracking 1,000) so the 100-sample
/// loops measure the screens, not the rate limiter's waits. The settlement runs on a second API whose clock is two days
/// ahead, so the day of the rehearsal's deliveries is a closed period (D7-SETTLEMENT-RULES); nothing else changes the
/// time.
/// </remarks>
[Collection(PlatformEndToEndCollection.Name)]
public sealed class PilotRehearsalTests(PlatformEndToEndFixture fixture, ITestOutputHelper output)
{
    private const int RegularDeliveries = 52;
    private const int CodDeliveries = 3;
    private const int FailedAttemptDeliveries = 2;
    private const int CsvDeliveries = 3;
    private const int Samples = 100;
    private const int Parallelism = 4;

    /// <summary>250.00 MXN, in centavos (invariant 4).</summary>
    private const long CodCents = 25_000;

    private static readonly Guid FinanceUserId = Guid.Parse("0e500500-0000-4000-8000-0000000001a1");
    private static readonly TimeSpan SettlementClockOffset = TimeSpan.FromDays(2);
    private static readonly TimeZoneInfo Mazatlan = TimeZoneInfo.FindSystemTimeZoneById("America/Mazatlan");

    [Fact]
    [Trait("Category", PilotRehearsalCategory.Name)]
    public async Task Rehearsal_runs_the_cobro_y_fallas_journey_and_measures_completeness_and_latency()
    {
        await SeedFinanceAsync();
        var runTag = Guid.NewGuid().ToString("N")[..10];
        var delivered = new ConcurrentBag<Guid>();
        string cod, failed, csv;
        RehearsalCompleteness completeness;
        RehearsalLatency latency;
        var rehearsalDay = TodayInMazatlan();

        await using (var hosts = PlatformHosts.Start(fixture, Settings()))
        {
            await using var hub = new OperationsHubClient(hosts.ApiAddress, PlatformEndToEndFixture.OrganizationId);
            await hub.StartAsync();
            using var api = new RehearsalApi(hosts.ApiAddress, PlatformEndToEndFixture.OrganizationId, runTag);
            var operations = new RehearsalOperations(fixture, api);
            var watch = Stopwatch.StartNew();

            async Task DeliverAsync(RehearsalOrder order)
            {
                await operations.DeliverAsync(order);
                delivered.Add(order.Id);
            }

            // Regular deliveries, a few at a time, each through quote, order, confirmation, release, assignment, the
            // pickup photo, the trip and the delivery photo.
            await ForEachAsync(Enumerable.Range(1, RegularDeliveries), async number =>
            {
                var order = await operations.CreateOrderAsync(await operations.QuoteAsync(number));
                await operations.ConfirmReleaseAndAssignAsync(order);
                await operations.PickUpAndHeadOutAsync(order);
                await DeliverAsync(order);
            });
            output.WriteLine($"{RegularDeliveries} regular deliveries in {watch.Elapsed.TotalSeconds:F0} s");

            // Cobro: cash recorded by the driver before the delivery, reconciled by dispatch, the order closed.
            for (var index = 0; index < CodDeliveries; index++)
            {
                var order = await operations.CreateOrderAsync(await operations.QuoteAsync(1000 + index), CodCents);
                await operations.ConfirmReleaseAndAssignAsync(order);
                await operations.PickUpAndHeadOutAsync(order);
                var codId = await operations.DeliverAsync(order) ??
                            throw new InvalidOperationException("A COD order recorded no cash.");
                delivered.Add(order.Id);
                await operations.ReconcileAndCloseAsync(order, codId);
            }

            cod = "PASSED";

            // Fallas: the recipient is absent, dispatch retries today and the driver delivers.
            for (var index = 0; index < FailedAttemptDeliveries; index++)
            {
                var order = await operations.CreateOrderAsync(await operations.QuoteAsync(2000 + index));
                await operations.ConfirmReleaseAndAssignAsync(order);
                await operations.PickUpAndHeadOutAsync(order);
                await operations.FailThenRedeliverAsync(order);
                delivered.Add(order.Id);
            }

            failed = "PASSED";

            // CSV: one file, previewed and committed; each created order is then delivered.
            foreach (var order in await operations.ImportCsvAsync(3000, CsvDeliveries))
            {
                await operations.ConfirmReleaseAndAssignAsync(order);
                await operations.PickUpAndHeadOutAsync(order);
                await DeliverAsync(order);
            }

            csv = "PASSED";
            output.WriteLine($"journey and {delivered.Count} deliveries in {watch.Elapsed.TotalSeconds:F0} s");

            // Orders left open for the screens: one waiting in the inbox, one assigned to the driver.
            var waiting = await operations.CreateOrderAsync(await operations.QuoteAsync(4000));
            await operations.TransitionAsync(RehearsalOperations.Dispatcher, waiting, "CONFIRMED", new { restricted_goods_acknowledged = true });
            await operations.TransitionAsync(RehearsalOperations.Dispatcher, waiting, "READY_FOR_PICKUP");
            var assigned = await operations.CreateOrderAsync(await operations.QuoteAsync(4001));
            await operations.ConfirmReleaseAndAssignAsync(assigned);
            var trackingLink = await operations.TrackingLinkAsync(assigned);

            await using (var browser = await PlatformBrowser.LaunchAsync())
            {
                var probe = new RehearsalLatencyProbe(fixture, browser);
                var waitingId = await PublicIdAsync(waiting.Id);
                var assignedId = await PublicIdAsync(assigned.Id);
                var screens = new Dictionary<string, LatencySummary>(StringComparer.Ordinal)
                {
                    ["/ops/inbox"] = await probe.InboxAsync(waitingId, Samples),
                    ["/ops/orders/:id"] = await probe.OrderDetailAsync(assigned.Id, assignedId, Samples),
                    ["/driver/stops"] = await probe.DriverStopsAsync(assignedId, Samples),
                    ["/driver/stops/:id"] = await probe.StopDetailAsync(assigned.Id, assignedId, Samples),
                    ["/track/:token"] = await probe.TrackingAsync(trackingLink, Samples),
                };
                var (creation, _) = await probe.OrderCreationAsync(Samples);
                foreach (var (screen, summary) in screens)
                {
                    output.WriteLine($"{screen}: {summary}");
                }

                output.WriteLine($"order creation: {creation}");
                latency = new RehearsalLatency(
                    screens, creation, await TechnicalAsync(hosts, api, hub, operations.DeliveryRequestedAt));
            }

            completeness = await RehearsalCompletenessReader.ReadAsync(fixture.Database.AdminConnectionString, delivered.ToArray());
            output.WriteLine($"completeness: {completeness}");
            Assert.Equal(RegularDeliveries + CodDeliveries + FailedAttemptDeliveries + CsvDeliveries, completeness.DeliveredOrders);
        }

        // Liquidación, on an API whose clock is two days ahead: the rehearsal's day is then a closed period.
        string settlement;
        var shifted = new ShiftedClock(SettlementClockOffset);
        await using (var hosts = PlatformHosts.Start(fixture, Settings(), services =>
                     {
                         services.RemoveAll<IClock>();
                         services.AddSingleton<IClock>(shifted);
                     }))
        {
            using var api = new RehearsalApi(hosts.ApiAddress, PlatformEndToEndFixture.OrganizationId, runTag + "s");
            var total = await new RehearsalOperations(fixture, api).SettleAsync(rehearsalDay, rehearsalDay);
            output.WriteLine($"settlement total: {total} centavos");
            settlement = "PASSED";
        }

        var evidence = new RehearsalEvidence(
            RehearsalEvidence.Format,
            Environment.GetEnvironmentVariable(RehearsalEvidence.TestedGitShaVariable) ?? new string('0', 40),
            Environment.GetEnvironmentVariable(RehearsalEvidence.RunIdVariable) ?? "0",
            int.TryParse(Environment.GetEnvironmentVariable(RehearsalEvidence.RunAttemptVariable), out var attempt) ? attempt : 1,
            RehearsalEnvironment.LocalCiSynthetic,
            new RehearsalJourneySteps(cod, failed, csv, settlement),
            completeness,
            latency);
        var directory = Environment.GetEnvironmentVariable(RehearsalEvidence.OutputDirectoryVariable) is { Length: > 0 } configured
            ? configured
            : Directory.CreateTempSubdirectory("rel001-rehearsal-").FullName;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, RehearsalEvidence.FileName);
        await File.WriteAllTextAsync(path, evidence.ToJson());
        output.WriteLine($"evidence written: {path}");
    }

    /// <summary>The API settings of both rehearsal hosts (test-host settings only, see the class remarks).</summary>
    private static Dictionary<string, string?> Settings()
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Finance:Provider"] = "PostgreSql",
            ["Finance:CommandTimeoutSeconds"] = "30",
            ["Incidents:PiiProtector"] = "Mock",
            ["PublicTracking:LookupPermitLimit"] = "1000",
        };
        foreach (var policy in new[]
                 {
                     "CsvImport", "SettlementExport", "LocationCreate", "ProofUploadSession", "ProofFinalize",
                     "OperationsDashboard", "QueueCounts", "OrderList", "Quote", "AssignableDrivers",
                 })
        {
            settings[$"RateLimits:{policy}:PermitLimit"] = "10000";
        }

        return settings;
    }

    /// <summary>
    /// AI-15, reported only: the API's request p95 over the whole rehearsal (its hosting log lines), the p95 of the
    /// proof upload sessions it opened (the signed URL generation) and the p95, in seconds, from each delivery request
    /// to its <c>OrderStatusChanged</c> at the operations hub client.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, double>> TechnicalAsync(
        PlatformHosts hosts,
        RehearsalApi api,
        OperationsHubClient hub,
        IReadOnlyDictionary<Guid, long> deliveredAt)
    {
        var technical = new Dictionary<string, double>(StringComparer.Ordinal);
        var requests = HostingDurations.Read(hosts.ApiLogs.Lines);
        if (requests.Count > 0)
        {
            technical["http_p95_ms"] = LatencySummary.From(requests.ToArray()).P95Ms;
        }

        TimeSpan[] uploads;
        lock (api.UploadSessionDurations)
        {
            uploads = api.UploadSessionDurations.ToArray();
        }

        if (uploads.Length > 0)
        {
            technical["signed_url_generation_p95_ms"] = LatencySummary.From(uploads).P95Ms;
        }

        var deliveries = new List<TimeSpan>();
        foreach (var (orderId, sentAt) in deliveredAt)
        {
            var (_, arrivedAt) = await hub.WaitForStatusAsync(orderId, "DELIVERED", TimeSpan.FromSeconds(30));
            deliveries.Add(Stopwatch.GetElapsedTime(sentAt, arrivedAt));
        }

        if (deliveries.Count > 0)
        {
            technical["event_delivery_p95_seconds"] = LatencySummary.From(deliveries).P95Ms / 1000.0;
        }

        return technical;
    }

    private async Task<string> PublicIdAsync(Guid orderId) =>
        await fixture.ScalarAsync<string>("SELECT public_id FROM orders.orders WHERE id=@id", ("id", orderId));

    private static DateOnly TodayInMazatlan() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Mazatlan).DateTime);

    private static async Task ForEachAsync(IEnumerable<int> numbers, Func<int, Task> body) =>
        await Parallel.ForEachAsync(
            numbers,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism },
            async (number, _) => await body(number));

    /// <summary>The synthetic finance member (MFA) of the synthetic organization, the Mock identity active-finance-mfa.</summary>
    private async Task SeedFinanceAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO identity.users(id,identity_subject,status)
            VALUES (@user,'mock-subject-active-finance-mfa','ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default)
            SELECT gen_random_uuid(),@user,@org,'FINANCE','ACTIVE',true
            WHERE NOT EXISTS (
              SELECT 1 FROM organizations.organization_memberships
              WHERE user_id=@user AND organization_id=@org AND role='FINANCE');
            """,
            connection)
        {
            CommandTimeout = 30,
        };
        command.Parameters.Add(new NpgsqlParameter<Guid>("user", NpgsqlDbType.Uuid) { TypedValue = FinanceUserId });
        command.Parameters.Add(new NpgsqlParameter<Guid>("org", NpgsqlDbType.Uuid)
        {
            TypedValue = PlatformEndToEndFixture.OrganizationId,
        });
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The settlement host's clock: the real time plus a fixed offset.</summary>
    private sealed class ShiftedClock(TimeSpan offset) : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + offset;
    }
}
