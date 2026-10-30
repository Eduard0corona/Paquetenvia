using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.RecipientNotices;
using Notifications.Endpoints;
using Paqueteria.Application.RecipientNotices;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Security;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Notifications;

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-08 NTF-006 required tests; AI-05 part NTF-006; O2, O3, O4, O11 choices 2,
/// 5, 7 and 9) over HTTP with the store replaced by a recorder: the Meta signature vectors (valid under either slot,
/// invalid, missing, a validly signed duplicate and oversized), the verification, the Event Grid handshake and secret
/// header, the unsubscribe body forms and tokens, the uniform 404 of a switched-off webhook that never becomes 429, the
/// SEC-004 budgets and their uniform public 429 for the unsubscribe, and that no number, address, token or secret reaches
/// a log line or an answer. Every value here is synthetic.
/// </summary>
public sealed class RecipientNoticeWebhookHttpTests
{
    private const string WhatsAppPath = "/api/v1/messaging/whatsapp/webhook";
    private const string EventsPath = "/api/v1/messaging/email/events";
    private const string UnsubscribePath = "/api/v1/recipient-notices/unsubscribe/";
    private const string PhoneNumberId = "100000000000001";
    private const string SyntheticSender = "5210000000003";
    private const string SyntheticEmail = "destinatario@example.invalid";
    private const string SourceResourceId =
        "/subscriptions/00000000-0000-4000-8000-000000000001/resourceGroups/rg-synthetic/providers/Microsoft.Communication/communicationServices/acs-synthetic";

    private static readonly string AppSecret = Secret();
    private static readonly string PreviousAppSecret = Secret();
    private static readonly string VerifyToken = Secret();
    private static readonly string PreviousVerifyToken = Secret();
    private static readonly string EventSecret = Secret();
    private static readonly string PreviousEventSecret = Secret();
    private static readonly byte[] LookupKey = RandomNumberGenerator.GetBytes(32);

    // ------------------------------------------------------------------ switched off (O4)

    [Fact]
    public async Task Switched_off_webhooks_answer_only_the_uniform_404_however_often_and_record_nothing()
    {
        await using var factory = new RecipientNoticeFactory(enabled: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var body = Encoding.UTF8.GetBytes(MetaBody());

        // Beyond every budget (10, 300 and 120 per minute): still 404, never 429.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            using var verify = await client.SendAsync(Request(HttpMethod.Get, Verification(VerifyToken), "192.0.2.10"));
            Assert.Equal(HttpStatusCode.NotFound, verify.StatusCode);
            Assert.Equal("no-store", verify.Headers.CacheControl?.ToString());
        }

        for (var attempt = 0; attempt < 305; attempt++)
        {
            using var meta = await client.SendAsync(MetaRequest(body, Signature(AppSecret, body), "192.0.2.11"));
            Assert.Equal(HttpStatusCode.NotFound, meta.StatusCode);
        }

        for (var attempt = 0; attempt < 125; attempt++)
        {
            using var events = await client.SendAsync(EventsRequest(AcsBody("op-0001", "Delivered"), EventSecret, "192.0.2.12"));
            Assert.Equal(HttpStatusCode.NotFound, events.StatusCode);
        }

        // A declared body above the host's 1 MiB bound is still the uniform 404, not 413: the webhooks declare their own
        // bound, so nothing runs before the switch (AI-05 "before anything else").
        var huge = new byte[2_000_000];
        using (var verifyHuge = Request(HttpMethod.Get, Verification(VerifyToken), "192.0.2.13"))
        {
            verifyHuge.Content = new ByteArrayContent(huge);
            using var response = await client.SendAsync(verifyHuge);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var metaHuge = await client.SendAsync(MetaRequest(huge, Signature(AppSecret, huge), "192.0.2.13")))
        {
            Assert.Equal(HttpStatusCode.NotFound, metaHuge.StatusCode);
        }

        using (var eventsHuge = Request(HttpMethod.Post, EventsPath, "192.0.2.13"))
        {
            eventsHuge.Content = new ByteArrayContent(huge);
            eventsHuge.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            eventsHuge.Headers.Add("X-Paquetenvia-Event-Secret", EventSecret);
            using var response = await client.SendAsync(eventsHuge);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(0, factory.Store.Calls);
    }

    [Fact]
    public async Task The_unsubscribe_has_no_switch_without_a_lookup_key_every_link_is_the_uniform_404_and_it_keeps_its_budget()
    {
        await using var factory = new RecipientNoticeFactory(enabled: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = new string('A', HmacRecipientNoticeUnsubscribeTokens.TokenLength);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var response = await client.SendAsync(UnsubscribeRequest(token, null, "198.51.100.20"));
            await AssertUniformAsync(response, HttpStatusCode.NotFound, "Not Found");
        }

        // O3: 20 per minute and client network, with the uniform public 429 body of publicTracking.
        using var limited = await client.SendAsync(UnsubscribeRequest(token, null, "198.51.100.20"));
        await AssertUniformAsync(limited, HttpStatusCode.TooManyRequests, "Too Many Requests");
        Assert.True(limited.Headers.RetryAfter?.Delta?.TotalSeconds >= 1);
        // Another network keeps its own budget.
        using var other = await client.SendAsync(UnsubscribeRequest(token, null, "198.51.100.21"));
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(0, factory.Store.Calls);
    }

    // ------------------------------------------------------------------ Meta verification (O11 choices 7 and 9)

    [Fact]
    public async Task The_verification_answers_the_challenge_only_for_subscribe_and_either_verify_token_and_never_logs_it()
    {
        await using var factory = new RecipientNoticeFactory(enabled: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var token in new[] { VerifyToken, PreviousVerifyToken })
        {
            using var response = await client.SendAsync(Request(HttpMethod.Get, Verification(token), "192.0.2.30"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("challenge-1234", await response.Content.ReadAsStringAsync());
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }

        foreach (var query in new[]
                 {
                     Verification(Secret()),
                     Verification(VerifyToken, mode: "unsubscribe"),
                     Verification(VerifyToken, challenge: "<script>"),
                     $"{WhatsAppPath}?hub.mode=subscribe&hub.challenge=challenge-1234",
                     $"{WhatsAppPath}?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.verify_token={VerifyToken}&hub.challenge=x",
                     WhatsAppPath,
                 })
        {
            using var response = await client.SendAsync(Request(HttpMethod.Get, query, "192.0.2.31"));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain(VerifyToken, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // O3: 10 per minute and client network while the webhook is on.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await client.SendAsync(Request(HttpMethod.Get, Verification(VerifyToken), "192.0.2.32"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var limited = await client.SendAsync(Request(HttpMethod.Get, Verification(VerifyToken), "192.0.2.32")))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        }

        AssertNoSecretLogged(factory, VerifyToken, PreviousVerifyToken);
        Assert.Contains(factory.Logs.Entries, entry => entry.Contains("/api/v1/messaging/whatsapp/webhook", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ Meta POST signature vectors

    [Fact]
    public async Task Meta_posts_are_recorded_only_with_a_valid_signature_under_either_app_secret_before_anything_is_parsed()
    {
        await using var factory = new RecipientNoticeFactory(enabled: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var body = Encoding.UTF8.GetBytes(MetaBody());

        using (var valid = await client.SendAsync(MetaRequest(body, Signature(AppSecret, body), "203.0.113.40")))
        {
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            Assert.Equal("no-store", valid.Headers.CacheControl?.ToString());
        }

        Assert.Equal(["RECEIPT:wamid.DELIVERED==:Delivered", "INBOUND:opt-out=False", "INBOUND:opt-out=True"], factory.Store.Recorded());

        // A validly signed duplicate (Meta's retry, the "replayed" vector of O11 choice 11) is accepted again; the store's
        // forward-only receipts, one opt-out per contact and the 24-hour limit make it change nothing (PostgreSQL tests).
        using (var duplicate = await client.SendAsync(MetaRequest(body, Signature(PreviousAppSecret, body), "203.0.113.40")))
        {
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        }

        Assert.Equal(6, factory.Store.Recorded().Count);
        var calls = factory.Store.Calls;

        // Invalid, missing, doubled, other algorithm, other secret, altered body: the uniform 403 and nothing parsed.
        var tampered = Encoding.UTF8.GetBytes(MetaBody().Replace("wamid.DELIVERED==", "wamid.ALTERED===", StringComparison.Ordinal));
        foreach (var request in new[]
                 {
                     MetaRequest(body, Signature(Secret(), body), "203.0.113.41"),
                     MetaRequest(body, null, "203.0.113.41"),
                     MetaRequest(body, Signature(AppSecret, body), "203.0.113.41", duplicateSignature: true),
                     MetaRequest(body, "sha1=" + Signature(AppSecret, body)[7..], "203.0.113.41"),
                     MetaRequest(tampered, Signature(AppSecret, body), "203.0.113.41"),
                     MetaRequest(Encoding.UTF8.GetBytes("not json"), "sha256=" + new string('0', 64), "203.0.113.41"),
                 })
        {
            using (request)
            using (var response = await client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }

        // Oversized (O11 choice 2, 256 KiB): 413 before the signature is even checked.
        var oversized = Encoding.UTF8.GetBytes("{\"object\":\"" + new string('x', 256 * 1024) + "\"}");
        using (var tooLarge = await client.SendAsync(MetaRequest(oversized, Signature(AppSecret, oversized), "203.0.113.42")))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        }

        // A validly signed body that is not a JSON object is 400; another account or number is ignored with 200.
        var array = Encoding.UTF8.GetBytes("[]");
        using (var malformed = await client.SendAsync(MetaRequest(array, Signature(AppSecret, array), "203.0.113.43")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }

        var otherNumber = Encoding.UTF8.GetBytes(MetaBody().Replace(PhoneNumberId, "200000000000002", StringComparison.Ordinal));
        using (var ignored = await client.SendAsync(MetaRequest(otherNumber, Signature(AppSecret, otherNumber), "203.0.113.43")))
        {
            Assert.Equal(HttpStatusCode.OK, ignored.StatusCode);
        }

        Assert.Equal(calls, factory.Store.Calls);

        // The store or a key unavailable: 503, so Meta retries.
        factory.Store.Fail = true;
        using (var unavailable = await client.SendAsync(MetaRequest(body, Signature(AppSecret, body), "203.0.113.44")))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        }

        AssertNoSecretLogged(factory, AppSecret, PreviousAppSecret, SyntheticSender, "+520000000003", "BAJA");
    }

    // ------------------------------------------------------------------ Event Grid (O2, O6)

    [Fact]
    public async Task Event_grid_gets_its_handshake_and_deliveries_need_the_secret_header_before_the_body_is_read()
    {
        await using var factory = new RecipientNoticeFactory(enabled: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var handshake = EventsRequest(
            """[{"id":"1","eventType":"Microsoft.EventGrid.SubscriptionValidationEvent","topic":"x","data":{"validationCode":"512d38b6-c7b8-40c8-89fe-f46f9e9622b6"}}]""",
            secret: null,
            "198.51.100.50");
        handshake.Headers.Add("aeg-event-type", "SubscriptionValidation");
        using (var answer = await client.SendAsync(handshake))
        {
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            using var document = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
            Assert.Equal("512d38b6-c7b8-40c8-89fe-f46f9e9622b6", document.RootElement.GetProperty("validationResponse").GetString());
            Assert.Single(document.RootElement.EnumerateObject());
        }

        Assert.Equal(0, factory.Store.Calls);

        foreach (var secret in new[] { EventSecret, PreviousEventSecret })
        {
            using var delivered = await client.SendAsync(EventsRequest(AcsBody($"op-{Guid.NewGuid():N}", "Quarantined"), secret, "198.51.100.51"));
            Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
        }

        Assert.All(factory.Store.Recorded(), entry => Assert.EndsWith(":Failed", entry, StringComparison.Ordinal));
        Assert.All(factory.Store.Receipts, receipt => Assert.Equal("MESSAGING_POLICY_BLOCKED", receipt.Code));
        var calls = factory.Store.Calls;

        using (var missing = await client.SendAsync(EventsRequest(AcsBody("op-1", "Delivered"), null, "198.51.100.52")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        }

        using (var wrong = await client.SendAsync(EventsRequest(AcsBody("op-1", "Delivered"), Secret(), "198.51.100.52")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        }

        using (var tooLarge = await client.SendAsync(EventsRequest("[" + new string(' ', 64 * 1024) + "]", EventSecret, "198.51.100.52")))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        }

        using (var malformed = await client.SendAsync(EventsRequest("{}", EventSecret, "198.51.100.52")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }

        Assert.Equal(calls, factory.Store.Calls);
        AssertNoSecretLogged(factory, EventSecret, PreviousEventSecret, SyntheticEmail);
    }

    [Fact]
    public async Task A_body_with_an_unreadable_string_is_a_400_on_the_anonymous_handshake_and_behind_the_signature()
    {
        // An escaped lone surrogate parses as JSON but cannot be read as a string: the anonymous Event Grid handshake
        // (no secret) and a validly signed Meta body answer 400, never 500, and log no error.
        await using var factory = new RecipientNoticeFactory(enabled: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var handshake = EventsRequest("""[{"eventType":"\uD800","data":{}}]""", secret: null, "198.51.100.70");
        handshake.Headers.Add("aeg-event-type", "SubscriptionValidation");
        using (var answer = await client.SendAsync(handshake))
        {
            Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        }

        using (var delivery = await client.SendAsync(EventsRequest(
            """[{"eventType":"Microsoft.Communication.EmailDeliveryReportReceived","data":{"messageId":"\uDC00"}}]""",
            EventSecret,
            "198.51.100.71")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, delivery.StatusCode);
        }

        var meta = Encoding.UTF8.GetBytes("""{"object":"whatsapp_business_account","entry":[],"extra":"\uD800"}""");
        using (var signed = await client.SendAsync(MetaRequest(meta, Signature(AppSecret, meta), "203.0.113.70")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, signed.StatusCode);
        }

        Assert.Equal(0, factory.Store.Calls);
        Assert.DoesNotContain(factory.Logs.Entries, entry =>
            entry.Contains(" Error ", StringComparison.Ordinal) || entry.Contains(" Critical ", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the e-mail unsubscribe (O9, O11 choices 1 and 7)

    [Fact]
    public async Task A_valid_unsubscribe_token_records_the_email_opt_out_for_every_accepted_body_and_nothing_else_does()
    {
        await using var factory = new RecipientNoticeFactory(enabled: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var owner = Guid.NewGuid();
        var token = factory.Tokens.Issue(owner, Guid.NewGuid());
        factory.Store.Contact = new RecipientNoticeUnsubscribeContact(owner, Guid.NewGuid(), "test-v1", [7]);

        var accepted = new Func<HttpContent?>[]
        {
            () => null,
            () => new ByteArrayContent([]),
            () => new FormUrlEncodedContent([new("List-Unsubscribe", "One-Click")]),
            () => new MultipartFormDataContent { { new StringContent("One-Click"), "List-Unsubscribe" } },
            () => new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        foreach (var content in accepted)
        {
            using var response = await client.SendAsync(UnsubscribeRequest(token, content(), "203.0.113.60"));
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Null(response.Headers.Location);
        }

        Assert.Equal(accepted.Length, factory.Store.EmailOptOuts);
        var hashes = factory.Store.LastEmailHashes!;
        Assert.Equal(
            new HmacRecipientContactLookup(factory.KeyRing).HashForLookup(RecipientContactChannel.Email, SyntheticEmail)[0].Hash,
            hashes[0].Hash);

        // Any other body is 400 and a body over 1 KiB is 413, before the token is read.
        var refused = new (Func<HttpContent> Content, HttpStatusCode Status)[]
        {
            (() => new StringContent("""{"a":1}""", Encoding.UTF8, "application/json"), HttpStatusCode.BadRequest),
            (() => new StringContent("[]", Encoding.UTF8, "application/json"), HttpStatusCode.BadRequest),
            (() => new FormUrlEncodedContent([new("List-Unsubscribe", "One-Click"), new("x", "y")]), HttpStatusCode.BadRequest),
            (() => new FormUrlEncodedContent([new("List-Unsubscribe", "one-click")]), HttpStatusCode.BadRequest),
            (() => new StringContent("List-Unsubscribe=One-Click", Encoding.UTF8, "text/plain"), HttpStatusCode.BadRequest),
            (() => new StringContent("{" + new string(' ', 1024) + "}", Encoding.UTF8, "application/json"), HttpStatusCode.RequestEntityTooLarge),
        };
        foreach (var (content, status) in refused)
        {
            using var response = await client.SendAsync(UnsubscribeRequest(token, content(), "203.0.113.61"));
            Assert.Equal(status, response.StatusCode);
        }

        // An invalid, altered, foreign-key or malformed token, and a notice whose copy is gone, are the uniform 404.
        var other = new HmacRecipientNoticeUnsubscribeTokens(new ContactLookupKeyRing(Options.Create(new ContactLookupOptions
        {
            CurrentKeyVersion = 1,
            Keys = new() { [1] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) },
        }))).Issue(owner, Guid.NewGuid());
        foreach (var invalid in new[] { other, token[..^2] + "AA", "short", Uri.EscapeDataString(token + "/") })
        {
            using var response = await client.SendAsync(UnsubscribeRequest(invalid, null, "203.0.113.62"));
            await AssertUniformAsync(response, HttpStatusCode.NotFound, "Not Found");
        }

        factory.Store.Contact = null;
        using (var gone = await client.SendAsync(UnsubscribeRequest(token, null, "203.0.113.62")))
        {
            await AssertUniformAsync(gone, HttpStatusCode.NotFound, "Not Found");
        }

        factory.Store.Contact = new RecipientNoticeUnsubscribeContact(owner, Guid.NewGuid(), "test-v1", [7]);
        factory.Store.Fail = true;
        using (var unavailable = await client.SendAsync(UnsubscribeRequest(token, null, "203.0.113.62")))
        {
            await AssertUniformAsync(unavailable, HttpStatusCode.ServiceUnavailable, "Service Unavailable");
        }

        factory.Store.Fail = false;
        var optOuts = factory.Store.EmailOptOuts;

        // There is no GET: a link scanner that follows the one-click URL opts nobody out.
        using (var get = await client.SendAsync(Request(HttpMethod.Get, UnsubscribePath + token, "203.0.113.63")))
        {
            Assert.True(get.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, get.StatusCode.ToString());
        }

        Assert.Equal(optOuts, factory.Store.EmailOptOuts);
        AssertNoSecretLogged(factory, token, SyntheticEmail);
        // The token never reached hosting diagnostics: the request target was redacted.
        Assert.Contains(factory.Logs.Entries, entry => entry.Contains("/api/v1/recipient-notices/unsubscribe/redacted", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ helpers

    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private static string Signature(string secret, byte[] body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    private static string Verification(string token, string mode = "subscribe", string challenge = "challenge-1234") =>
        $"{WhatsAppPath}?hub.mode={Uri.EscapeDataString(mode)}&hub.verify_token={Uri.EscapeDataString(token)}&hub.challenge={Uri.EscapeDataString(challenge)}";

    private static string MetaBody() =>
        $$$"""
        {"object":"whatsapp_business_account","entry":[{"id":"1","changes":[{"field":"messages","value":{
          "messaging_product":"whatsapp","metadata":{"display_phone_number":"0","phone_number_id":"{{{PhoneNumberId}}}"},
          "contacts":[{"profile":{"name":"Persona Sintetica"},"wa_id":"{{{SyntheticSender}}}"}],
          "statuses":[{"id":"wamid.DELIVERED==","status":"delivered","timestamp":"1761400000","recipient_id":"{{{SyntheticSender}}}"}],
          "messages":[
            {"from":"{{{SyntheticSender}}}","id":"wamid.IN1==","timestamp":"1761400001","type":"text","text":{"body":"hola"}},
            {"from":"{{{SyntheticSender}}}","id":"wamid.IN2==","timestamp":"1761400002","type":"text","text":{"body":"BAJA"}}]}}]}]}
        """;

    private static string AcsBody(string messageId, string status) =>
        $$$"""
        [{"id":"{{{Guid.NewGuid()}}}","eventType":"Microsoft.Communication.EmailDeliveryReportReceived","topic":"{{{SourceResourceId}}}",
          "subject":"sender/avisos@example.invalid/message/{{{messageId}}}","eventTime":"2026-10-25T18:00:00Z","dataVersion":"1.0",
          "data":{"sender":"avisos@example.invalid","recipient":"{{{SyntheticEmail}}}","messageId":"{{{messageId}}}","status":"{{{status}}}",
            "deliveryStatusDetails":{"statusMessage":"synthetic"},"deliveryAttemptTimeStamp":"2026-10-25T18:00:00Z"}}]
        """;

    private static HttpRequestMessage Request(HttpMethod method, string path, string peer)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(PeerAddress.Header, peer);
        return request;
    }

    private static HttpRequestMessage MetaRequest(byte[] body, string? signature, string peer, bool duplicateSignature = false)
    {
        var request = Request(HttpMethod.Post, WhatsAppPath, peer);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);
            if (duplicateSignature)
            {
                request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);
            }
        }

        return request;
    }

    private static HttpRequestMessage EventsRequest(string body, string? secret, string peer)
    {
        var request = Request(HttpMethod.Post, EventsPath, peer);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (secret is not null)
        {
            request.Headers.Add("X-Paquetenvia-Event-Secret", secret);
        }

        return request;
    }

    private static HttpRequestMessage UnsubscribeRequest(string token, HttpContent? content, string peer)
    {
        var request = Request(HttpMethod.Post, UnsubscribePath + token, peer);
        request.Content = content;
        return request;
    }

    /// <summary>The uniform public body: type, title and status only, no traceId and nothing of the request.</summary>
    private static async Task AssertUniformAsync(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["status", "title", "type"],
            document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(title, document.RootElement.GetProperty("title").GetString());
        Assert.Equal((int)status, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    private static void AssertNoSecretLogged(RecipientNoticeFactory factory, params string[] secrets)
    {
        Assert.NotEmpty(factory.Logs.Entries);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(factory.Logs.Entries, entry =>
                entry.Contains(secret, StringComparison.OrdinalIgnoreCase) ||
                entry.Contains(Uri.EscapeDataString(secret), StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class RecipientNoticeFactory(bool enabled) : WebApplicationFactory<Program>
    {
        public RecordingStore Store { get; } = new();

        public CapturingLoggerProvider Logs { get; } = new();

        public ContactLookupKeyRing KeyRing { get; } = new(Options.Create(new ContactLookupOptions
        {
            CurrentKeyVersion = 1,
            Keys = new() { [1] = Convert.ToBase64String(LookupKey) },
        }));

        public HmacRecipientNoticeUnsubscribeTokens Tokens => new(KeyRing);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["Authentication:Provider"] = "Mock",
                    ["IdentityBootstrap:Provider"] = "Mock",
                    ["Logging:LogLevel:Default"] = "Trace",
                    ["Logging:LogLevel:Microsoft"] = "Trace",
                    ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                };
                if (enabled)
                {
                    settings["Messaging:WhatsApp:Webhook:Enabled"] = "true";
                    settings["Messaging:WhatsApp:Webhook:AppSecret"] = AppSecret;
                    settings["Messaging:WhatsApp:Webhook:PreviousAppSecret"] = PreviousAppSecret;
                    settings["Messaging:WhatsApp:Webhook:VerifyToken"] = VerifyToken;
                    settings["Messaging:WhatsApp:Webhook:PreviousVerifyToken"] = PreviousVerifyToken;
                    settings["Messaging:WhatsApp:Webhook:PhoneNumberId"] = PhoneNumberId;
                    settings["Messaging:WhatsApp:Webhook:NumberSealer"] = "Mock";
                    settings["Messaging:Email:Events:Enabled"] = "true";
                    settings["Messaging:Email:Events:Secret"] = EventSecret;
                    settings["Messaging:Email:Events:PreviousSecret"] = PreviousEventSecret;
                    settings["Messaging:Email:Events:SourceResourceId"] = SourceResourceId;
                    settings["ContactLookup:CurrentKeyVersion"] = "1";
                    settings["ContactLookup:Keys:1"] = Convert.ToBase64String(LookupKey);
                }

                configuration.AddInMemoryCollection(settings);
            });
            builder.ConfigureLogging(logging => logging
                .AddProvider(Logs)
                .SetMinimumLevel(LogLevel.Trace)
                .AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter, PeerAddress>();
                services.RemoveAll<IRecipientNoticeReceiptStore>();
                services.AddSingleton<IRecipientNoticeReceiptStore>(Store);
                services.RemoveAll<IRecipientEmailOpener>();
                services.AddSingleton<IRecipientEmailOpener>(new FixedEmailOpener());
            });
        }
    }

    private sealed class FixedEmailOpener : IRecipientEmailOpener
    {
        public Task<OpenedRecipientEmail> OpenAsync(StoredRecipientEmail email, CancellationToken cancellationToken) =>
            Task.FromResult(OpenedRecipientEmail.Opened(SyntheticEmail));
    }

    private sealed class RecordingStore : IRecipientNoticeReceiptStore
    {
        private readonly ConcurrentQueue<string> _recorded = new();
        private readonly ConcurrentQueue<MessagingReceipt> _receipts = new();
        private int _calls;
        private int _emailOptOuts;

        public bool Fail { get; set; }

        public RecipientNoticeUnsubscribeContact? Contact { get; set; }

        public IReadOnlyList<ContactLookupHash>? LastEmailHashes { get; private set; }

        public int Calls => Volatile.Read(ref _calls);

        public int EmailOptOuts => Volatile.Read(ref _emailOptOuts);

        public IReadOnlyCollection<MessagingReceipt> Receipts => _receipts.ToArray();

        public List<string> Recorded() => [.. _recorded];

        public Task<MessagingReceiptResult> RecordReceiptAsync(MessagingReceipt receipt, CancellationToken cancellationToken)
        {
            Touch();
            _receipts.Enqueue(receipt);
            _recorded.Enqueue($"RECEIPT:{receipt.ProviderReference}:{receipt.Status}");
            return Task.FromResult(MessagingReceiptResult.Recorded);
        }

        public Task<RecipientInboundResult> RecordWhatsAppInboundAsync(RecipientInboundRecord record, CancellationToken cancellationToken)
        {
            Touch();
            Assert.NotEmpty(record.Hashes);
            Assert.Equal(RecipientAutoReplyNumbers.SyntheticKeyVersion, record.NumberKeyVersion);
            _recorded.Enqueue($"INBOUND:opt-out={record.IsOptOut}");
            return Task.FromResult(record.IsOptOut ? RecipientInboundResult.OptOutRecorded : RecipientInboundResult.AutoReplyRequested);
        }

        public Task<bool> RecordEmailOptOutAsync(IReadOnlyList<ContactLookupHash> hashes, CancellationToken cancellationToken)
        {
            Touch();
            Interlocked.Increment(ref _emailOptOuts);
            LastEmailHashes = hashes;
            return Task.FromResult(true);
        }

        public Task<RecipientNoticeUnsubscribeContact?> ReadUnsubscribeContactAsync(
            Guid ownerOrganizationId,
            Guid notificationId,
            CancellationToken cancellationToken)
        {
            Touch();
            return Task.FromResult(Contact);
        }

        private void Touch()
        {
            Interlocked.Increment(ref _calls);
            if (Fail)
            {
                throw new RecipientNoticeStoreUnavailableException();
            }
        }
    }

    /// <summary>TestServer has no socket peer: this gives each request the transport address it names.</summary>
    private sealed class PeerAddress : IStartupFilter
    {
        internal const string Header = "X-Test-Transport-Peer";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue(Header, out var peer))
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
                        context.Request.Headers.Remove(Header);
                    }

                    await nextMiddleware(context);
                });
                next(app);
            };
    }
}
