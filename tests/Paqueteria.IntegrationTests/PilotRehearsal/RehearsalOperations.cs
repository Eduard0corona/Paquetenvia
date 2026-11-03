using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Paqueteria.IntegrationTests.Driver;
using Paqueteria.IntegrationTests.PlatformEndToEnd;

namespace Paqueteria.IntegrationTests.PilotRehearsal;

/// <summary>
/// The rehearsal's operations on the real platform, through the API's own endpoints: the dispatcher quotes, creates,
/// confirms, releases and assigns; the driver arrives, photographs, records the cash, reports a failed attempt and
/// delivers; finance reconciles and settles. Photos go through the signed upload to the storage double and the Worker
/// validates them with the Synthetic scanner before the driver finalizes them, as the pilot does with its own storage.
/// Synthetic data only (SEC-003): every address, contact and description says SINTETICO and every phone starts 0000.
/// </summary>
internal sealed class RehearsalOperations(PlatformEndToEndFixture fixture, RehearsalApi api)
{
    internal const string Dispatcher = MockIdentityProfiles.ActiveDispatcher;
    internal const string Driver = MockIdentityProfiles.ActiveDriver;
    internal const string Finance = MockIdentityProfiles.ActiveFinanceMfa;
    private const string Reason = "REL-001 ensayo sintetico";
    private const long AssignmentCostCents = 4_500;

    private static readonly byte[] Photo = ProofPhotoEvidence.TealPixelPng;
    private static readonly string PhotoSha256 = Convert.ToHexStringLower(SHA256.HashData(Photo));

    internal static Guid DriverId => PlatformEndToEndFixture.DriverId;

    /// <summary>When each order's DELIVERED request was sent (AI-15 event_delivery_p95_seconds, reported only).</summary>
    internal ConcurrentDictionary<Guid, long> DeliveryRequestedAt { get; } = new();

    /// <summary>createQuote for one synthetic shipment inside the fixture's operating zone.</summary>
    internal async Task<Guid> QuoteAsync(int number)
    {
        var offset = number % 50 * 0.001;
        var quote = await api.PostAsync(Dispatcher, "/api/v1/quotes", new
        {
            origin = new
            {
                address_text = $"Calle Origen SINTETICO {number}, Culiacan",
                contact_name = $"Remitente SINTETICO {number}",
                phone = $"0000{number % 1_000_000:D6}",
                lat = 24.78 + offset,
                lng = -107.42 + offset,
                references = "REL-001 SINTETICO",
            },
            destination = new
            {
                address_text = $"Calle Destino SINTETICO {number}, Culiacan",
                contact_name = $"Destinatario SINTETICO {number}",
                phone = $"0000{(number + 500_000) % 1_000_000:D6}",
                lat = 24.79 + offset,
                lng = -107.41 + offset,
                references = "REL-001 SINTETICO",
            },
            service_type = "SAME_DAY",
            consolidated_route = false,
            packages = new[]
            {
                new
                {
                    description = $"Paquete SINTETICO {number}",
                    weight_grams = 1000,
                    declared_value_cents = 0L,
                    length_mm = 200,
                    width_mm = 150,
                    height_mm = 100,
                },
            },
        }, HttpStatusCode.Created, api.NewKey("quote"));
        return quote.GetProperty("id").GetGuid();
    }

    /// <summary>createOrder from a quote (DRAFT, version 1), optionally with a COD expectation in centavos.</summary>
    internal async Task<RehearsalOrder> CreateOrderAsync(Guid quoteId, long codCents = 0)
    {
        var body = new Dictionary<string, object?>
        {
            ["quote_id"] = quoteId,
            ["payer_type"] = "SENDER",
            ["restricted_goods_acknowledged"] = true,
            ["acceptance"] = new
            {
                terms_version = PlatformEndToEndFixture.TermsVersion,
                privacy_version = PlatformEndToEndFixture.PrivacyVersion,
                accepted_at = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture),
                acceptance_channel = "WEB",
            },
        };
        if (codCents > 0)
        {
            body["cod_expected_cents"] = codCents;
        }

        var order = await api.PostAsync(Dispatcher, "/api/v1/orders", body, HttpStatusCode.Created, api.NewKey("order"));
        return new RehearsalOrder(order.GetProperty("id").GetGuid(), order.GetProperty("version").GetInt32(), codCents);
    }

    /// <summary>The dispatcher confirms the order, releases it for pickup and assigns the synthetic own driver.</summary>
    internal async Task ConfirmReleaseAndAssignAsync(RehearsalOrder order)
    {
        if (await StatusAsync(order) == "DRAFT")
        {
            await TransitionAsync(Dispatcher, order, "CONFIRMED", new { restricted_goods_acknowledged = true });
        }

        await TransitionAsync(Dispatcher, order, "READY_FOR_PICKUP");
        await AssignAsync(order);
    }

    internal async Task AssignAsync(RehearsalOrder order)
    {
        await api.PostAsync(Dispatcher, $"/api/v1/orders/{order.Id:D}/assignments", new
        {
            driver_id = DriverId,
            assignment_type = "OWN",
            cost_cents = AssignmentCostCents,
            route_id = (Guid?)null,
        }, HttpStatusCode.Created, api.NewKey("assign"));
        order.Version = await VersionAsync(order);
    }

    /// <summary>The driver picks up with a photo and goes to the destination (ASSIGNED to DELIVERING).</summary>
    internal async Task PickUpAndHeadOutAsync(RehearsalOrder order)
    {
        await TransitionAsync(Driver, order, "AT_PICKUP");
        await PhotoAsync(order, "PICKUP_PHOTO");
        await TransitionAsync(Driver, order, "PICKED_UP");
        await TransitionAsync(Driver, order, "IN_TRANSIT");
        await TransitionAsync(Driver, order, "DELIVERING");
    }

    /// <summary>
    /// The driver delivers with a photo; a COD order first records exactly its expected cash
    /// (recordCodCollection, which ORD-002 requires before DELIVERED).
    /// </summary>
    internal async Task<Guid?> DeliverAsync(RehearsalOrder order)
    {
        Guid? codId = null;
        if (order.CodCents > 0)
        {
            var cod = await api.PostAsync(Driver, $"/api/v1/orders/{order.Id:D}/cod-records", new
            {
                amount_cents = order.CodCents,
                reference = $"REL001-{order.Id.ToString("N")[..8]}",
            }, HttpStatusCode.Created, api.NewKey("cod-record"));
            Assert.Equal("RECORDED", cod.GetProperty("status").GetString());
            codId = cod.GetProperty("id").GetGuid();
        }

        await PhotoAsync(order, "DELIVERY_PHOTO");
        DeliveryRequestedAt[order.Id] = Stopwatch.GetTimestamp();
        await TransitionAsync(Driver, order, "DELIVERED");
        return codId;
    }

    /// <summary>Z5 cobro: the dispatcher reconciles the recorded cash and closes the delivered order.</summary>
    internal async Task ReconcileAndCloseAsync(RehearsalOrder order, Guid codId)
    {
        var reconciled = await api.PostAsync(
            Dispatcher, $"/api/v1/cod-records/{codId:D}/reconcile", null, HttpStatusCode.OK, api.NewKey("cod-reconcile"));
        Assert.Equal("RECONCILED", reconciled.GetProperty("status").GetString());
        await TransitionAsync(Dispatcher, order, "CLOSED");
    }

    /// <summary>
    /// Z5 fallas: at the destination the driver reports the recipient absent with a delivery photo as evidence and
    /// moves the order to FAILED_ATTEMPT; dispatch decides to retry today (FAILED_ATTEMPT to DELIVERING); the driver then
    /// delivers with a new photo, because the evidence photo never completes the retry; dispatch finally resolves the
    /// incident (resolveIncident, RESOLVED), which the settlement approval and the close require.
    /// </summary>
    internal async Task FailThenRedeliverAsync(RehearsalOrder order)
    {
        var evidence = await PhotoAsync(order, "DELIVERY_PHOTO");
        var incident = await api.PostAsync(Driver, $"/api/v1/orders/{order.Id:D}/incidents", new
        {
            type = "FAILED_DELIVERY_ATTEMPT",
            reason_code = "RECIPIENT_ABSENT",
            occurred_at = DateTimeOffset.UtcNow.AddSeconds(-5).ToString("O", CultureInfo.InvariantCulture),
            evidence_proof_ids = new[] { evidence },
        }, HttpStatusCode.Created, api.NewKey("incident"));
        var incidentId = incident.GetProperty("id").GetGuid();
        await TransitionAsync(Driver, order, "FAILED_ATTEMPT", new { incident_id = incidentId });
        await TransitionAsync(Dispatcher, order, "DELIVERING");
        await DeliverAsync(order);
        var resolved = await api.PostAsync(Dispatcher, $"/api/v1/incidents/{incidentId:D}/resolution", new
        {
            outcome = "RESOLVED",
            reason = "Entregada en el segundo intento (REL-001 ensayo sintetico)",
        }, HttpStatusCode.OK, api.NewKey("incident-resolution"));
        Assert.Equal("RESOLVED", resolved.GetProperty("status").GetString());
    }

    /// <summary>
    /// Z5 CSV: one file of synthetic rows, each naming a fresh quote, previewed and then committed with the
    /// prohibited-goods confirmation; every row becomes a real order through the ORD-001 create path.
    /// </summary>
    internal async Task<IReadOnlyList<RehearsalOrder>> ImportCsvAsync(int firstNumber, int rows)
    {
        var lines = new List<string> { "quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel" };
        for (var index = 0; index < rows; index++)
        {
            var quote = await QuoteAsync(firstNumber + index);
            lines.Add(string.Join(
                ',',
                quote.ToString("D"),
                "SENDER",
                PlatformEndToEndFixture.TermsVersion,
                PlatformEndToEndFixture.PrivacyVersion,
                DateTimeOffset.UtcNow.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                "WEB"));
        }

        var file = Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n");
        var preview = await api.PostMultipartAsync(Dispatcher, "/api/v1/orders/csv/preview", Form(file), HttpStatusCode.OK);
        Assert.Equal(rows, preview.GetProperty("valid_rows").GetInt32());
        var digest = preview.GetProperty("content_digest").GetString()!;
        var commitForm = Form(file);
        commitForm.Add(new StringContent(digest), "content_digest");
        commitForm.Add(new StringContent("true"), "restricted_goods_acknowledged");
        var commit = await api.PostMultipartAsync(
            Dispatcher, "/api/v1/orders/csv/commit", commitForm, HttpStatusCode.OK, api.NewKey("csv-commit"));
        Assert.Equal(rows, commit.GetProperty("created_rows").GetInt32());
        Assert.Equal(0, commit.GetProperty("failed_rows").GetInt32());
        var orders = new List<RehearsalOrder>();
        foreach (var row in commit.GetProperty("rows").EnumerateArray())
        {
            Assert.Equal("CREATED", row.GetProperty("status").GetString());
            var order = new RehearsalOrder(row.GetProperty("order_id").GetGuid(), 0, 0);
            order.Version = await VersionAsync(order);
            orders.Add(order);
        }

        return orders;
    }

    /// <summary>
    /// Z5 liquidación: finance (MFA) calculates the driver's settlement for the period, approves it, marks it paid and
    /// exports it. The caller runs it on an API whose clock is past the period, so the period is closed (D7).
    /// </summary>
    internal async Task<long> SettleAsync(DateOnly periodFrom, DateOnly periodTo)
    {
        var settlement = await api.PostAsync(Finance, "/api/v1/settlements", new
        {
            driver_id = DriverId,
            period_from = periodFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            period_to = periodTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }, HttpStatusCode.Created, api.NewKey("settlement"));
        Assert.Equal("CALCULATED", settlement.GetProperty("status").GetString());
        var id = settlement.GetProperty("id").GetGuid();
        var total = settlement.GetProperty("total_cents").GetInt64();
        Assert.True(total > 0, "The settlement found no delivered work of the driver in the period.");
        var approved = await api.PostAsync(
            Finance, $"/api/v1/settlements/{id:D}/approve", null, HttpStatusCode.OK, api.NewKey("settlement-approve"));
        Assert.Equal("APPROVED", approved.GetProperty("status").GetString());
        var paid = await api.PostAsync(
            Finance, $"/api/v1/settlements/{id:D}/pay", null, HttpStatusCode.OK, api.NewKey("settlement-pay"));
        Assert.Equal("PAID", paid.GetProperty("status").GetString());
        var csv = await api.GetTextAsync(Finance, $"/api/v1/settlements/{id:D}/export.csv");
        Assert.Contains(id.ToString("D"), csv, StringComparison.Ordinal);
        return total;
    }

    /// <summary>issueTrackingLink: the public link of the order, as "Ver enlace" gives it.</summary>
    internal async Task<Uri> TrackingLinkAsync(RehearsalOrder order)
    {
        var link = await api.PostAsync(
            Dispatcher, $"/api/v1/orders/{order.Id:D}/tracking-link", null, HttpStatusCode.OK, api.NewKey("tracking"));
        return new Uri(link.GetProperty("url").GetString()!, UriKind.Absolute);
    }

    internal async Task TransitionAsync(string identity, RehearsalOrder order, string target, object? metadata = null)
    {
        var answer = await api.PostAsync(identity, $"/api/v1/orders/{order.Id:D}/transitions", new
        {
            target_status = target,
            reason = Reason,
            expected_version = order.Version,
            metadata = metadata ?? new { },
        }, HttpStatusCode.OK, api.NewKey("transition"));
        Assert.Equal(target, answer.GetProperty("status").GetString());
        order.Version = answer.GetProperty("version").GetInt32();
    }

    /// <summary>
    /// One proof photo by the driver: the upload session (timed), the signed PUT to storage, the Worker's validation
    /// with the Synthetic scanner (observed in the database) and the finalization. Returns the proof id.
    /// </summary>
    internal async Task<Guid> PhotoAsync(RehearsalOrder order, string proofType)
    {
        var session = await api.CreateUploadSessionAsync(Driver, order.Id, new
        {
            proof_type = proofType,
            content_type = "image/png",
            size_bytes = Photo.Length,
            sha256 = PhotoSha256,
        });
        await RehearsalApi.PutSignedAsync(session, Photo);
        var sessionId = session.GetProperty("id").GetGuid();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        string? status;
        while ((status = await fixture.ScalarAsync<string>(
                   "SELECT status FROM custody.proof_upload_sessions WHERE id=@id", ("id", sessionId))) != "READY")
        {
            Assert.True(status is "CREATED" or "UPLOADED" or "VALIDATING", $"The upload session ended {status}.");
            Assert.True(DateTimeOffset.UtcNow < deadline, "The Worker did not validate the photo within 60 s.");
            await Task.Delay(100);
        }

        var proof = await api.PostAsync(Driver, $"/api/v1/orders/{order.Id:D}/proofs", new
        {
            upload_session_id = sessionId,
            proof_type = proofType,
            captured_at = DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O", CultureInfo.InvariantCulture),
            sha256 = PhotoSha256,
        }, HttpStatusCode.Created, api.NewKey("proof"));
        return proof.GetProperty("id").GetGuid();
    }

    private Task<string> StatusAsync(RehearsalOrder order) =>
        fixture.ScalarAsync<string>("SELECT status FROM orders.orders WHERE id=@id", ("id", order.Id));

    private Task<int> VersionAsync(RehearsalOrder order) =>
        fixture.ScalarAsync<int>("SELECT version FROM orders.orders WHERE id=@id", ("id", order.Id));

    private static MultipartFormDataContent Form(byte[] file)
    {
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        return new MultipartFormDataContent { { content, "file", "ordenes.csv" } };
    }
}

/// <summary>One order of the rehearsal: its id, its last known version and its COD expectation in centavos.</summary>
internal sealed class RehearsalOrder(Guid id, int version, long codCents)
{
    internal Guid Id { get; } = id;

    internal int Version { get; set; } = version;

    internal long CodCents { get; } = codCents;
}
