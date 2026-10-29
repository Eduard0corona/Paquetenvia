using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.RecipientNotices;
using Notifications.Infrastructure;
using Notifications.Infrastructure.RecipientNotices;
using Notifications.Infrastructure.RecipientNotices.Webhooks;
using Paqueteria.Application.Messaging;
using Paqueteria.Application.RecipientNotices;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Messaging;
using Paqueteria.Infrastructure.Security;

namespace Paqueteria.UnitTests.Notifications;

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-08 NTF-006 required unit tests; O2 to O11): the Meta and Event Grid
/// readers, the opt-out keywords and the quick reply, the constant-time secret checks with their two slots, the start
/// validation of the webhooks, the API services that turn a verified delivery into receipts, opt-outs and automatic-reply
/// requests, the e-mail unsubscribe, and the Worker's automatic-reply processor. Every number and address is a synthetic
/// placeholder; no secret is a real value.
/// </summary>
public sealed class RecipientNoticeReceiptsTests
{
    private const string PhoneNumberId = "100000000000001";
    private const string SourceResourceId =
        "/subscriptions/00000000-0000-4000-8000-000000000001/resourceGroups/rg-synthetic/providers/Microsoft.Communication/communicationServices/acs-synthetic";

    // Synthetic Meta sender ids (digits without "+"): +52 0000000003 written as Meta writes a Mexican mobile (521...).
    private const string SyntheticSender = "5210000000003";
    private const string SyntheticSenderE164 = "+520000000003";
    private static readonly byte[] LookupKey = Enumerable.Range(41, 32).Select(static value => (byte)value).ToArray();
    private static readonly byte[] PreviousLookupKey = Enumerable.Range(81, 32).Select(static value => (byte)value).ToArray();
    private static readonly string AppSecret = new('a', 24);
    private static readonly string PreviousAppSecret = new('b', 24);
    private static readonly string VerifyToken = new('c', 40);
    private static readonly string PreviousVerifyToken = new('d', 40);
    private static readonly string EventSecret = new('e', 40);
    private static readonly string PreviousEventSecret = new('f', 40);

    // ------------------------------------------------------------------ Meta webhook reader (O11 choices 4 and 5)

    [Fact]
    public void Meta_statuses_become_forward_receipts_and_only_the_configured_business_number_is_read()
    {
        var body = Utf8(
            $$$"""
            {"object":"whatsapp_business_account","entry":[
              {"id":"1","changes":[
                {"field":"messages","value":{"messaging_product":"whatsapp","metadata":{"phone_number_id":"{{{PhoneNumberId}}}"},
                  "statuses":[
                    {"id":"wamid.DELIVERED==","status":"delivered","timestamp":"1761400000","recipient_id":"{{{SyntheticSender}}}"},
                    {"id":"wamid.FAILED==","status":"failed","timestamp":"1761400001","errors":[{"code":131026,"title":"synthetic"}]},
                    {"id":"wamid.NOCODE==","status":"failed","timestamp":"1761400002"},
                    {"id":"wamid.SENT==","status":"sent","timestamp":"1761400003"},
                    {"id":"wamid.READ==","status":"read","timestamp":"1761400004"},
                    {"id":"bad reference with spaces","status":"delivered"}]}},
                {"field":"messages","value":{"metadata":{"phone_number_id":"200000000000002"},
                  "statuses":[{"id":"wamid.OTHER==","status":"delivered"}]}},
                {"field":"account_update","value":{"metadata":{"phone_number_id":"{{{PhoneNumberId}}}"},
                  "statuses":[{"id":"wamid.FIELD==","status":"delivered"}]}}]}]}
            """);

        Assert.True(MetaWhatsAppWebhookParser.TryParse(body, PhoneNumberId, out var batch));

        Assert.Empty(batch.Messages);
        Assert.Equal(
            [
                ("wamid.DELIVERED==", MessagingReceiptStatus.Delivered, (string?)null),
                ("wamid.FAILED==", MessagingReceiptStatus.Failed, MessagingResultCodes.RecipientUndeliverable),
                ("wamid.NOCODE==", MessagingReceiptStatus.Failed, MessagingResultCodes.Rejected),
            ],
            batch.Receipts.Select(receipt => (receipt.ProviderReference, receipt.Status, receipt.Code)));
        Assert.All(batch.Receipts, receipt => Assert.Equal("WHATSAPP", receipt.Channel));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761400000), batch.Receipts[0].ReportedAt);
        Assert.DoesNotContain(SyntheticSender, batch.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Meta_inbound_messages_are_opt_outs_by_keyword_or_quick_reply_and_everything_else_asks_for_the_reply()
    {
        var body = Utf8(
            $$$"""
            {"object":"whatsapp_business_account","entry":[{"changes":[{"field":"messages","value":{
              "metadata":{"phone_number_id":"{{{PhoneNumberId}}}"},
              "contacts":[{"profile":{"name":"Persona Sintetica"},"wa_id":"{{{SyntheticSender}}}"}],
              "messages":[
                {"from":"{{{SyntheticSender}}}","timestamp":"1761400010","type":"text","text":{"body":"  báJa "}},
                {"from":"{{{SyntheticSender}}}","timestamp":"1761400011","type":"text","text":{"body":"Quiero darme de baja"}},
                {"from":"{{{SyntheticSender}}}","timestamp":"1761400012","type":"button","button":{"payload":"RECIPIENT_NOTICE_OPT_OUT","text":"otro"}},
                {"from":"{{{SyntheticSender}}}","timestamp":"1761400013","type":"button","button":{"payload":"OTHER","text":"No recibir avisos"}},
                {"from":"{{{SyntheticSender}}}","timestamp":"1761400014","type":"button","button":{"payload":"OTHER","text":"Ver seguimiento"}},
                {"from":"{{{SyntheticSender}}}","timestamp":"1761400015","type":"image","image":{"id":"synthetic"}},
                {"timestamp":"1761400016","type":"text","text":{"body":"BAJA"}}]}}]}]}
            """);

        Assert.True(MetaWhatsAppWebhookParser.TryParse(body, PhoneNumberId, out var batch));

        Assert.Empty(batch.Receipts);
        Assert.Equal([true, false, true, true, false, false], batch.Messages.Select(message => message.IsOptOut));
        Assert.All(batch.Messages, message => Assert.Equal(SyntheticSender, message.From));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761400010), batch.Messages[0].SentAt);
        Assert.DoesNotContain(SyntheticSender, batch.Messages[0].ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BAJA", true)]
    [InlineData("baja", true)]
    [InlineData(" Bajá\n", true)]
    [InlineData("ALTO", true)]
    [InlineData("stop", true)]
    [InlineData("Cancelar", true)]
    [InlineData("CANCELÁR", true)]
    [InlineData("baja por favor", false)]
    [InlineData("BAJAS", false)]
    [InlineData("ALTA", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Opt_out_keywords_are_exact_after_trimming_and_ignoring_case_and_accents(string? text, bool optOut) =>
        Assert.Equal(optOut, RecipientOptOutMessages.IsOptOutKeyword(text));

    [Fact]
    public void A_signed_body_that_is_not_an_object_is_refused_and_another_account_or_number_is_ignored()
    {
        Assert.False(MetaWhatsAppWebhookParser.TryParse(Utf8("[]"), PhoneNumberId, out _));
        Assert.False(MetaWhatsAppWebhookParser.TryParse(Utf8("\"text\""), PhoneNumberId, out _));
        Assert.False(MetaWhatsAppWebhookParser.TryParse(Utf8("{not json"), PhoneNumberId, out _));
        Assert.False(MetaWhatsAppWebhookParser.TryParse(Utf8(new string('[', 64) + new string(']', 64)), PhoneNumberId, out _));

        Assert.True(MetaWhatsAppWebhookParser.TryParse(Utf8("""{"object":"page","entry":[]}"""), PhoneNumberId, out var other));
        Assert.Same(MetaWhatsAppWebhookBatch.Empty, other);
        Assert.True(MetaWhatsAppWebhookParser.TryParse(
            Utf8($$$"""{"object":"whatsapp_business_account","entry":[{"changes":[{"field":"messages","value":{"metadata":{"phone_number_id":"{{{PhoneNumberId}}}"},"messages":[{"from":"{{{SyntheticSender}}}","type":"text","text":{"body":"hola"}}]}}]}]}"""),
            string.Empty,
            out var unconfigured));
        Assert.Empty(unconfigured.Messages);
    }

    // ------------------------------------------------------------------ Event Grid reader (O6)

    [Theory]
    [InlineData("Delivered", "DELIVERED", null)]
    [InlineData("Bounced", "FAILED", MessagingResultCodes.RecipientUndeliverable)]
    [InlineData("Suppressed", "FAILED", MessagingResultCodes.RecipientUndeliverable)]
    [InlineData("FilteredSpam", "FAILED", MessagingResultCodes.PolicyBlocked)]
    [InlineData("Quarantined", "FAILED", MessagingResultCodes.PolicyBlocked)]
    [InlineData("Failed", "FAILED", MessagingResultCodes.Rejected)]
    public void Acs_statuses_map_exactly_as_the_owner_decided(string status, string expected, string? code)
    {
        var mapped = AcsEmailEventsParser.Map(status);
        Assert.NotNull(mapped);
        Assert.Equal(expected, mapped.Value.Status == MessagingReceiptStatus.Delivered ? "DELIVERED" : "FAILED");
        Assert.Equal(code, mapped.Value.Code);
    }

    [Theory]
    [InlineData("Expanded")]
    [InlineData("OutForDelivery")]
    [InlineData("delivered")]
    [InlineData("")]
    public void Expanded_and_unknown_acs_statuses_are_ignored(string status) => Assert.Null(AcsEmailEventsParser.Map(status));

    [Fact]
    public void Event_grid_events_give_the_handshake_code_and_receipts_of_the_configured_resource_only()
    {
        var body = Utf8(
            $$$"""
            [
              {"id":"1","eventType":"Microsoft.EventGrid.SubscriptionValidationEvent","topic":"{{{SourceResourceId}}}",
               "data":{"validationCode":"512d38b6-c7b8-40c8-89fe-f46f9e9622b6"}},
              {"id":"2","eventType":"Microsoft.Communication.EmailDeliveryReportReceived","topic":"{{{SourceResourceId.ToLowerInvariant()}}}",
               "data":{"sender":"avisos@example.invalid","recipient":"destinatario@example.invalid","messageId":"op-0001",
                 "status":"Delivered","deliveryAttemptTimeStamp":"2026-10-25T18:00:00Z"}},
              {"id":"3","eventType":"Microsoft.Communication.EmailDeliveryReportReceived","topic":"{{{SourceResourceId}}}",
               "data":{"messageId":"op-0002","status":"Quarantined"}},
              {"id":"4","eventType":"Microsoft.Communication.EmailDeliveryReportReceived","topic":"{{{SourceResourceId}}}",
               "data":{"messageId":"op-0003","status":"Expanded"}},
              {"id":"5","eventType":"Microsoft.Communication.EmailDeliveryReportReceived","topic":"/subscriptions/x/other",
               "data":{"messageId":"op-0004","status":"Delivered"}},
              {"id":"6","eventType":"Microsoft.Communication.EmailEngagementTrackingReportReceived","topic":"{{{SourceResourceId}}}",
               "data":{"messageId":"op-0005","status":"Delivered"}}
            ]
            """);

        Assert.True(AcsEmailEventsParser.TryParse(body, SourceResourceId, out var batch));

        Assert.Equal("512d38b6-c7b8-40c8-89fe-f46f9e9622b6", batch.ValidationCode);
        Assert.Equal(
            [("op-0001", MessagingReceiptStatus.Delivered, (string?)null), ("op-0002", MessagingReceiptStatus.Failed, MessagingResultCodes.PolicyBlocked)],
            batch.Receipts.Select(receipt => (receipt.ProviderReference, receipt.Status, receipt.Code)));
        Assert.All(batch.Receipts, receipt => Assert.Equal("EMAIL", receipt.Channel));
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T18:00:00Z", System.Globalization.CultureInfo.InvariantCulture), batch.Receipts[0].ReportedAt);
        Assert.DoesNotContain("example.invalid", batch.ToString(), StringComparison.Ordinal);

        Assert.False(AcsEmailEventsParser.TryParse(Utf8("{}"), SourceResourceId, out _));
        Assert.False(AcsEmailEventsParser.TryParse(Utf8("not json"), SourceResourceId, out _));
        Assert.True(AcsEmailEventsParser.TryParse(
            Utf8("""[{"eventType":"Microsoft.EventGrid.SubscriptionValidationEvent","data":{"validationCode":"<script>"}}]"""),
            SourceResourceId,
            out var hostile));
        Assert.Null(hostile.ValidationCode);
        Assert.True(AcsEmailEventsParser.TryParse(body, string.Empty, out var unconfigured));
        Assert.Empty(unconfigured.Receipts);
    }

    // ------------------------------------------------------------------ constant-time secrets, two slots (O2, O11 choice 9)

    [Fact]
    public void Meta_signatures_are_checked_over_the_raw_body_under_either_app_secret()
    {
        var body = Utf8("""{"object":"whatsapp_business_account"}""");
        var guard = Guard(previous: true);

        Assert.True(guard.IsValidMetaSignature(Signature(AppSecret, body), body.Span));
        Assert.True(guard.IsValidMetaSignature(Signature(PreviousAppSecret, body), body.Span));
        Assert.True(guard.IsValidMetaSignature(Signature(AppSecret, body).ToUpperInvariant().Replace("SHA256=", "sha256=", StringComparison.Ordinal), body.Span));
        Assert.False(guard.IsValidMetaSignature(Signature(new string('z', 24), body), body.Span));
        Assert.False(guard.IsValidMetaSignature(Signature(AppSecret, body), Utf8("""{"object":"whatsapp_business_account" }""").Span));
        Assert.False(guard.IsValidMetaSignature(Signature(AppSecret, body)[7..], body.Span));
        Assert.False(guard.IsValidMetaSignature("sha1=" + Signature(AppSecret, body)[7..], body.Span));
        Assert.False(guard.IsValidMetaSignature(Signature(AppSecret, body) + "0", body.Span));
        Assert.False(guard.IsValidMetaSignature(null, body.Span));
        Assert.False(guard.IsValidMetaSignature(string.Empty, body.Span));

        // Without a previous slot, the previous secret no longer signs anything.
        Assert.False(Guard(previous: false).IsValidMetaSignature(Signature(PreviousAppSecret, body), body.Span));
    }

    [Fact]
    public void Verify_tokens_and_event_grid_secrets_match_either_slot_and_nothing_else()
    {
        var guard = Guard(previous: true);
        Assert.True(guard.IsValidMetaVerifyToken(VerifyToken));
        Assert.True(guard.IsValidMetaVerifyToken(PreviousVerifyToken));
        Assert.False(guard.IsValidMetaVerifyToken(VerifyToken + "x"));
        Assert.False(guard.IsValidMetaVerifyToken(VerifyToken[..^1]));
        Assert.False(guard.IsValidMetaVerifyToken(string.Empty));
        Assert.False(guard.IsValidMetaVerifyToken(null));
        Assert.True(guard.IsValidEventGridSecret(EventSecret));
        Assert.True(guard.IsValidEventGridSecret(PreviousEventSecret));
        Assert.False(guard.IsValidEventGridSecret(EventSecret.ToUpperInvariant()));
        Assert.False(guard.IsValidEventGridSecret(string.Empty));

        var single = Guard(previous: false);
        Assert.False(single.IsValidMetaVerifyToken(PreviousVerifyToken));
        Assert.False(single.IsValidEventGridSecret(PreviousEventSecret));

        // An unconfigured webhook accepts nothing, the empty value included.
        var off = new RecipientNoticeWebhookGuard(
            Options.Create(new WhatsAppWebhookOptions()),
            Options.Create(new EmailEventsOptions()));
        Assert.False(off.WhatsAppEnabled);
        Assert.False(off.EmailEventsEnabled);
        Assert.False(off.IsValidMetaVerifyToken(string.Empty));
        Assert.False(off.IsValidEventGridSecret(string.Empty));
        Assert.False(off.IsValidMetaSignature(Signature(string.Empty, ReadOnlyMemory<byte>.Empty), ReadOnlySpan<byte>.Empty));
    }

    // ------------------------------------------------------------------ start validation

    [Fact]
    public void Both_webhooks_are_off_by_default_and_an_enabled_one_needs_its_secrets_and_ids()
    {
        var validation = Validation("Production", lookupAvailable: true);
        Assert.True(validation.Validate(null, new WhatsAppWebhookOptions()).Succeeded);
        Assert.True(validation.Validate(null, new EmailEventsOptions()).Succeeded);

        var whatsApp = EnabledWhatsApp(RecipientAutoReplyNumberSealerKind.AzureKeyVault);
        Assert.True(validation.Validate(null, whatsApp).Succeeded);
        foreach (var broken in new[]
                 {
                     Copy(whatsApp, options => options.AppSecret = "short"),
                     Copy(whatsApp, options => options.VerifyToken = new string('c', 31)),
                     Copy(whatsApp, options => options.VerifyToken = new string('c', 30) + " x"),
                     Copy(whatsApp, options => options.PhoneNumberId = "+52-not-an-id"),
                     Copy(whatsApp, options => options.NumberSealer = RecipientAutoReplyNumberSealerKind.Disabled),
                     Copy(whatsApp, options => options.PreviousAppSecret = "short"),
                     Copy(whatsApp, options => options.NumberSealer = RecipientAutoReplyNumberSealerKind.Mock),
                 })
        {
            var result = validation.Validate(null, broken);
            Assert.True(result.Failed);
            Assert.DoesNotContain(AppSecret, result.FailureMessage, StringComparison.Ordinal);
            Assert.DoesNotContain(VerifyToken, result.FailureMessage, StringComparison.Ordinal);
        }

        Assert.True(Validation("Production", lookupAvailable: false).Validate(null, whatsApp).Failed);
        Assert.True(Validation("Testing", lookupAvailable: true)
            .Validate(null, EnabledWhatsApp(RecipientAutoReplyNumberSealerKind.Mock)).Succeeded);

        var email = new EmailEventsOptions { Enabled = true, Secret = EventSecret, SourceResourceId = SourceResourceId };
        Assert.True(validation.Validate(null, email).Succeeded);
        Assert.True(validation.Validate(null, new EmailEventsOptions { Enabled = true, SourceResourceId = SourceResourceId }).Failed);
        Assert.True(validation.Validate(null, new EmailEventsOptions { Enabled = true, Secret = EventSecret, SourceResourceId = "acs" }).Failed);
        Assert.True(validation.Validate(null, new EmailEventsOptions { PreviousSecret = "short" }).Failed);
    }

    // ------------------------------------------------------------------ the API service (receipts, opt-outs, requests)

    [Fact]
    public async Task A_verified_meta_delivery_records_receipts_opt_outs_and_requests_with_hashes_and_a_sealed_number_only()
    {
        var store = new FakeReceiptStore();
        var now = DateTimeOffset.Parse("2026-10-25T18:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var service = WebhookService(store, now);
        var batch = new MetaWhatsAppWebhookBatch(
            [new MessagingReceipt("WHATSAPP", "wamid.A==", MessagingReceiptStatus.Delivered, null, now)],
            [
                new InboundWhatsAppMessage(SyntheticSender, now.AddMinutes(-3), isOptOut: true),
                new InboundWhatsAppMessage("520000000004", now.AddHours(1), isOptOut: false),
                new InboundWhatsAppMessage("15550000000", now, isOptOut: false),
                new InboundWhatsAppMessage("not-a-number", now, isOptOut: true),
            ]);

        await service.ReceiveWhatsAppAsync(batch, CancellationToken.None);

        Assert.Equal("wamid.A==", Assert.Single(store.Receipts).ProviderReference);
        Assert.Equal(2, store.Inbound.Count);
        var optOut = store.Inbound[0];
        Assert.True(optOut.IsOptOut);
        Assert.Equal(now.AddMinutes(-3), optOut.ReceivedAt);
        var lookup = new HmacRecipientContactLookup(KeyRing());
        Assert.Equal(
            lookup.HashForLookup(RecipientContactChannel.WhatsApp, SyntheticSenderE164).Select(hash => (hash.KeyVersion, Convert.ToHexString(hash.Hash))),
            optOut.Hashes.Select(hash => (hash.KeyVersion, Convert.ToHexString(hash.Hash))));
        Assert.Equal([2, 1], optOut.Hashes.Select(hash => hash.KeyVersion));
        Assert.Equal(RecipientAutoReplyNumbers.SyntheticKeyVersion, optOut.NumberKeyVersion);
        // Synthetic: 32 random bytes, derived from nothing (two seals of the same number differ).
        Assert.Equal(32, optOut.SealedNumber.Length);
        var again = await new SyntheticAutoReplyNumberSealer().SealAsync(optOut.RequestId, SyntheticSenderE164, CancellationToken.None);
        Assert.NotEqual(Convert.ToHexString(optOut.SealedNumber), Convert.ToHexString(again.Ciphertext));
        Assert.DoesNotContain(SyntheticSenderE164, optOut.ToString(), StringComparison.Ordinal);
        var request = store.Inbound[1];
        Assert.False(request.IsOptOut);
        // A message time in the future is clamped to the server time.
        Assert.Equal(now, request.ReceivedAt);
        Assert.NotEqual(optOut.RequestId, request.RequestId);
        Assert.Equal(
            lookup.HashForLookup(RecipientContactChannel.WhatsApp, "+520000000004")[0].Hash,
            request.Hashes[0].Hash);
    }

    [Fact]
    public async Task Event_grid_receipts_are_recorded_one_by_one_and_an_unavailable_key_is_a_store_failure()
    {
        var store = new FakeReceiptStore();
        var service = WebhookService(store, DateTimeOffset.UtcNow);
        await service.ReceiveEmailEventsAsync(
            [
                new MessagingReceipt("EMAIL", "op-1", MessagingReceiptStatus.Delivered, null, null),
                new MessagingReceipt("EMAIL", "op-2", MessagingReceiptStatus.Failed, MessagingResultCodes.PolicyBlocked, null),
            ],
            CancellationToken.None);
        Assert.Equal(["op-1", "op-2"], store.Receipts.Select(receipt => receipt.ProviderReference));

        var noKey = new RecipientNoticeWebhookService(
            store,
            new HmacRecipientContactLookup(new ContactLookupKeyRing(Options.Create(new ContactLookupOptions()))),
            new SyntheticAutoReplyNumberSealer(),
            TimeProvider.System,
            NullLogger<RecipientNoticeWebhookService>.Instance);
        await Assert.ThrowsAsync<RecipientNoticeStoreUnavailableException>(() => noKey.ReceiveWhatsAppAsync(
            new MetaWhatsAppWebhookBatch([], [new InboundWhatsAppMessage(SyntheticSender, null, false)]),
            CancellationToken.None));
        Assert.Empty(store.Inbound);
        await Assert.ThrowsAsync<RecipientNoticeStoreUnavailableException>(() => new DisabledAutoReplyNumberSealer()
            .SealAsync(Guid.NewGuid(), SyntheticSenderE164, CancellationToken.None));
    }

    // ------------------------------------------------------------------ the e-mail unsubscribe (O4, O10, O11 choice 1)

    [Fact]
    public async Task A_valid_token_opens_the_notice_copy_and_records_the_email_opt_out_and_anything_else_is_not_found()
    {
        var owner = Guid.Parse("0f5a6c11-0000-4000-8000-0000000006a1");
        var notification = Guid.Parse("0f5a6c11-0000-4000-8000-0000000006b1");
        var tokens = new HmacRecipientNoticeUnsubscribeTokens(KeyRing());
        var token = tokens.Issue(owner, notification);
        var store = new FakeReceiptStore
        {
            Contact = new RecipientNoticeUnsubscribeContact(owner, Guid.NewGuid(), "test-v1", [7]),
        };
        var emails = new FakeEmailOpener { Result = OpenedRecipientEmail.Opened("Destinatario@Example.Invalid") };
        var service = UnsubscribeService(tokens, store, emails);

        Assert.Equal(RecipientNoticeUnsubscribeResult.Recorded, await service.UnsubscribeAsync(token, CancellationToken.None));
        Assert.Equal((owner, notification), Assert.Single(store.ContactReads));
        var hashes = Assert.Single(store.EmailOptOuts);
        Assert.Equal(
            new HmacRecipientContactLookup(KeyRing()).HashForLookup(RecipientContactChannel.Email, "destinatario@example.invalid")
                .Select(hash => Convert.ToHexString(hash.Hash)),
            hashes.Select(hash => Convert.ToHexString(hash.Hash)));

        // Already recorded is still 204 (O11 choice 7).
        store.EmailOptOutResult = false;
        Assert.Equal(RecipientNoticeUnsubscribeResult.Recorded, await service.UnsubscribeAsync(token, CancellationToken.None));

        // An invalid, altered or foreign token reads nothing.
        var reads = store.ContactReads.Count;
        foreach (var invalid in new[] { null, string.Empty, token[..^1], token[..^1] + (token[^1] == 'A' ? 'B' : 'A'), new string('A', 88) })
        {
            Assert.Equal(RecipientNoticeUnsubscribeResult.NotFound, await service.UnsubscribeAsync(invalid, CancellationToken.None));
        }

        Assert.Equal(reads, store.ContactReads.Count);

        // The copy is gone or unusable: the uniform 404; the key or the store is unavailable: 503.
        store.Contact = null;
        Assert.Equal(RecipientNoticeUnsubscribeResult.NotFound, await service.UnsubscribeAsync(token, CancellationToken.None));
        store.Contact = new RecipientNoticeUnsubscribeContact(owner, Guid.NewGuid(), "test-v1", [7]);
        emails.Result = OpenedRecipientEmail.Unusable;
        Assert.Equal(RecipientNoticeUnsubscribeResult.NotFound, await service.UnsubscribeAsync(token, CancellationToken.None));
        emails.Result = OpenedRecipientEmail.Unavailable;
        Assert.Equal(RecipientNoticeUnsubscribeResult.Unavailable, await service.UnsubscribeAsync(token, CancellationToken.None));
        emails.Result = OpenedRecipientEmail.Opened("destinatario@example.invalid");
        store.Fail = true;
        Assert.Equal(RecipientNoticeUnsubscribeResult.Unavailable, await service.UnsubscribeAsync(token, CancellationToken.None));
    }

    // ------------------------------------------------------------------ the Worker processor (O7, O11 choice 10)

    [Fact]
    public async Task The_automatic_reply_is_sent_once_as_its_fixed_text_and_settled_with_the_number_erased()
    {
        var harness = new AutoReplyHarness();
        var reply = harness.Request(RecipientAutoReplyKind.AutoReply, TimeSpan.FromMinutes(5));
        await harness.Processor().ProcessAsync(reply, CancellationToken.None);

        var sent = Assert.Single(harness.Provider.Requests);
        Assert.Equal(MessagingChannel.WhatsApp, sent.Channel);
        Assert.Equal(RecipientNoticeTexts.AutoReplyText, sent.FreeFormText);
        Assert.Equal(RecipientNoticeTexts.AutoReplyKey, sent.TemplateKey);
        Assert.Equal(reply.RequestId, sent.MessageId);
        Assert.Equal((RecipientAutoReplyOutcome.Sent, MessagingResultCodes.Accepted, (DateTimeOffset?)null), Assert.Single(harness.Store.Settles));

        var confirmation = harness.Request(RecipientAutoReplyKind.OptOutConfirmation, TimeSpan.FromMinutes(1));
        await harness.Processor().ProcessAsync(confirmation, CancellationToken.None);
        Assert.Equal(RecipientNoticeTexts.OptOutConfirmationText, harness.Provider.Requests[^1].FreeFormText);
        Assert.Equal(RecipientNoticeTexts.OptOutConfirmationKey, harness.Provider.Requests[^1].TemplateKey);
    }

    [Fact]
    public async Task Off_stale_ambiguous_and_unusable_requests_never_retry_and_never_reach_the_provider_twice()
    {
        // O7 point 2: notices off or WhatsApp Disabled drops the request with its number erased, without a call.
        foreach (var harness in new[] { new AutoReplyHarness { NoticesEnabled = false }, new AutoReplyHarness { WhatsApp = WhatsAppProviderKind.Disabled } })
        {
            await harness.Processor().ProcessAsync(harness.Request(RecipientAutoReplyKind.AutoReply, TimeSpan.Zero), CancellationToken.None);
            Assert.Empty(harness.Provider.Requests);
            Assert.Equal((RecipientAutoReplyOutcome.Dropped, MessagingResultCodes.ChannelDisabled, (DateTimeOffset?)null), Assert.Single(harness.Store.Settles));
        }

        // O7 point 1: quiet hours do not apply, Meta's 23-hour bound does.
        var stale = new AutoReplyHarness();
        await stale.Processor().ProcessAsync(stale.Request(RecipientAutoReplyKind.AutoReply, TimeSpan.FromHours(23)), CancellationToken.None);
        Assert.Empty(stale.Provider.Requests);
        Assert.Equal((RecipientAutoReplyOutcome.Dropped, "AUTO_REPLY_STALE", (DateTimeOffset?)null), Assert.Single(stale.Store.Settles));
        var night = new AutoReplyHarness { Now = DateTimeOffset.Parse("2026-10-25T09:30:00Z", System.Globalization.CultureInfo.InvariantCulture) };
        await night.Processor().ProcessAsync(night.Request(RecipientAutoReplyKind.AutoReply, TimeSpan.FromHours(22)), CancellationToken.None);
        Assert.Single(night.Provider.Requests);

        // NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02: an unknown outcome ends FAILED, never retried.
        var ambiguous = new AutoReplyHarness { Result = new(MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.Timeout) };
        await ambiguous.Processor().ProcessAsync(ambiguous.Request(RecipientAutoReplyKind.AutoReply, TimeSpan.Zero), CancellationToken.None);
        Assert.Equal((RecipientAutoReplyOutcome.Failed, "AMBIGUOUS_TIMEOUT", (DateTimeOffset?)null), Assert.Single(ambiguous.Store.Settles));

        var rejected = new AutoReplyHarness { Result = new(MessagingOutcome.PermanentFailure, MessagingResultCodes.ReengagementRequired) };
        await rejected.Processor().ProcessAsync(rejected.Request(RecipientAutoReplyKind.AutoReply, TimeSpan.Zero), CancellationToken.None);
        Assert.Equal((RecipientAutoReplyOutcome.Failed, MessagingResultCodes.ReengagementRequired, (DateTimeOffset?)null), Assert.Single(rejected.Store.Settles));

        var invalid = new AutoReplyHarness { Result = new(MessagingOutcome.PermanentFailure, MessagingResultCodes.RecipientInvalid) };
        await invalid.Processor().ProcessAsync(invalid.Request(RecipientAutoReplyKind.AutoReply, TimeSpan.Zero), CancellationToken.None);
        Assert.Equal((RecipientAutoReplyOutcome.Dropped, MessagingResultCodes.RecipientInvalid, (DateTimeOffset?)null), Assert.Single(invalid.Store.Settles));

        // On the real provider without a usable sealed copy (here: no opener can be built), nothing is called.
        var real = new AutoReplyHarness { WhatsApp = WhatsAppProviderKind.MetaCloudApi };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            real.Processor().ProcessAsync(real.Request(RecipientAutoReplyKind.AutoReply, TimeSpan.Zero), CancellationToken.None));
        Assert.Empty(real.Provider.Requests);
        Assert.Empty(real.Store.Settles);
    }

    // ------------------------------------------------------------------ helpers

    private static ReadOnlyMemory<byte> Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string Signature(string secret, ReadOnlyMemory<byte> body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body.Span));

    private static RecipientNoticeWebhookGuard Guard(bool previous) =>
        new(
            Options.Create(new WhatsAppWebhookOptions
            {
                Enabled = true,
                AppSecret = AppSecret,
                PreviousAppSecret = previous ? PreviousAppSecret : string.Empty,
                VerifyToken = VerifyToken,
                PreviousVerifyToken = previous ? PreviousVerifyToken : string.Empty,
                PhoneNumberId = PhoneNumberId,
                NumberSealer = RecipientAutoReplyNumberSealerKind.Mock,
            }),
            Options.Create(new EmailEventsOptions
            {
                Enabled = true,
                Secret = EventSecret,
                PreviousSecret = previous ? PreviousEventSecret : string.Empty,
                SourceResourceId = SourceResourceId,
            }));

    private static WhatsAppWebhookOptions EnabledWhatsApp(RecipientAutoReplyNumberSealerKind sealer) => new()
    {
        Enabled = true,
        AppSecret = AppSecret,
        VerifyToken = VerifyToken,
        PhoneNumberId = PhoneNumberId,
        NumberSealer = sealer,
    };

    private static WhatsAppWebhookOptions Copy(WhatsAppWebhookOptions source, Action<WhatsAppWebhookOptions> change)
    {
        var copy = new WhatsAppWebhookOptions
        {
            Enabled = source.Enabled,
            AppSecret = source.AppSecret,
            PreviousAppSecret = source.PreviousAppSecret,
            VerifyToken = source.VerifyToken,
            PreviousVerifyToken = source.PreviousVerifyToken,
            PhoneNumberId = source.PhoneNumberId,
            NumberSealer = source.NumberSealer,
        };
        change(copy);
        return copy;
    }

    private static RecipientNoticeWebhookOptionsValidation Validation(string environment, bool lookupAvailable) =>
        new(
            new Environment(environment),
            new HmacRecipientContactLookup(lookupAvailable
                ? KeyRing()
                : new ContactLookupKeyRing(Options.Create(new ContactLookupOptions()))));

    private static ContactLookupKeyRing KeyRing() =>
        new(Options.Create(new ContactLookupOptions
        {
            CurrentKeyVersion = 2,
            Keys = new() { [1] = Convert.ToBase64String(PreviousLookupKey), [2] = Convert.ToBase64String(LookupKey) },
        }));

    private static RecipientNoticeWebhookService WebhookService(FakeReceiptStore store, DateTimeOffset now) =>
        new(
            store,
            new HmacRecipientContactLookup(KeyRing()),
            new SyntheticAutoReplyNumberSealer(),
            new FixedTime(now),
            NullLogger<RecipientNoticeWebhookService>.Instance);

    private static RecipientNoticeUnsubscribeService UnsubscribeService(
        HmacRecipientNoticeUnsubscribeTokens tokens,
        FakeReceiptStore store,
        FakeEmailOpener emails) =>
        new(
            tokens,
            store,
            new SingleServiceProvider(emails),
            new HmacRecipientContactLookup(KeyRing()),
            NullLogger<RecipientNoticeUnsubscribeService>.Instance);

    private sealed class FakeReceiptStore : IRecipientNoticeReceiptStore
    {
        public List<MessagingReceipt> Receipts { get; } = [];

        public List<RecipientInboundRecord> Inbound { get; } = [];

        public List<IReadOnlyList<ContactLookupHash>> EmailOptOuts { get; } = [];

        public List<(Guid, Guid)> ContactReads { get; } = [];

        public RecipientNoticeUnsubscribeContact? Contact { get; set; }

        public bool EmailOptOutResult { get; set; } = true;

        public bool Fail { get; set; }

        public Task<MessagingReceiptResult> RecordReceiptAsync(MessagingReceipt receipt, CancellationToken cancellationToken)
        {
            Receipts.Add(receipt);
            return Task.FromResult(MessagingReceiptResult.Recorded);
        }

        public Task<RecipientInboundResult> RecordWhatsAppInboundAsync(RecipientInboundRecord record, CancellationToken cancellationToken)
        {
            Inbound.Add(record);
            return Task.FromResult(record.IsOptOut ? RecipientInboundResult.OptOutRecorded : RecipientInboundResult.AutoReplyRequested);
        }

        public Task<bool> RecordEmailOptOutAsync(IReadOnlyList<ContactLookupHash> hashes, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new RecipientNoticeStoreUnavailableException();
            }

            EmailOptOuts.Add(hashes);
            return Task.FromResult(EmailOptOutResult);
        }

        public Task<RecipientNoticeUnsubscribeContact?> ReadUnsubscribeContactAsync(
            Guid ownerOrganizationId,
            Guid notificationId,
            CancellationToken cancellationToken)
        {
            ContactReads.Add((ownerOrganizationId, notificationId));
            return Task.FromResult(Contact);
        }
    }

    private sealed class FakeEmailOpener : IRecipientEmailOpener
    {
        public OpenedRecipientEmail Result { get; set; } = OpenedRecipientEmail.Unusable;

        public Task<OpenedRecipientEmail> OpenAsync(StoredRecipientEmail email, CancellationToken cancellationToken) =>
            Task.FromResult(Result);
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Paqueteria.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class AutoReplyHarness
    {
        public bool NoticesEnabled { get; init; } = true;

        public WhatsAppProviderKind WhatsApp { get; init; } = WhatsAppProviderKind.Synthetic;

        public DateTimeOffset Now { get; init; } = DateTimeOffset.Parse("2026-10-25T18:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        public MessagingResult Result { get; init; } = new(MessagingOutcome.Accepted, MessagingResultCodes.Accepted, "wamid.REPLY==");

        public FakeAutoReplyStore Store { get; } = new();

        public CapturingProvider Provider { get; } = new();

        public ClaimedRecipientAutoReply Request(RecipientAutoReplyKind kind, TimeSpan age) =>
            new(Guid.NewGuid(), kind, [1, 2, 3], "ntf006-auto-reply-mock-v1", Now - age, Guid.NewGuid());

        public RecipientAutoReplyProcessor Processor()
        {
            var messaging = Options.Create(new MessagingOptions
            {
                WhatsApp = new() { Provider = WhatsApp },
                Email = new() { Provider = EmailProviderKind.Disabled },
            });
            Provider.Result = Result;
            return new RecipientAutoReplyProcessor(
                Store,
                new RecipientAutoReplyNumberResolver(messaging, new ThrowingServiceProvider()),
                Provider,
                Options.Create(new RecipientNoticesOptions { Enabled = NoticesEnabled }),
                messaging,
                Options.Create(new NotificationsOptions()),
                new FixedTime(Now),
                NullLogger<RecipientAutoReplyProcessor>.Instance);
        }
    }

    private sealed class FakeAutoReplyStore : IRecipientAutoReplyStore
    {
        public List<(RecipientAutoReplyOutcome Outcome, string? Code, DateTimeOffset? AvailableAt)> Settles { get; } = [];

        public Task<IReadOnlyList<ClaimedRecipientAutoReply>> ClaimAutoRepliesAsync(
            int batchSize,
            TimeSpan lease,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClaimedRecipientAutoReply>>([]);

        public Task<bool> SettleAutoReplyAsync(
            ClaimedRecipientAutoReply request,
            RecipientAutoReplyOutcome outcome,
            string? code,
            DateTimeOffset? availableAt,
            CancellationToken cancellationToken)
        {
            Settles.Add((outcome, code, availableAt));
            return Task.FromResult(true);
        }
    }

    private sealed class CapturingProvider : IMessagingProvider
    {
        public List<MessagingRequest> Requests { get; } = [];

        public MessagingResult Result { get; set; } = new(MessagingOutcome.Accepted, MessagingResultCodes.Accepted);

        public ValueTask<MessagingResult> SendAsync(MessagingRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            // The synthetic provider answers the AI-24 placeholder, never a decrypted number.
            Assert.Equal(RecipientNoticeContactResolver.SyntheticPhone, request.Recipient.Address);
            Assert.Equal("[redacted]", request.Recipient.ToString());
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class ThrowingServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            throw new InvalidOperationException($"The synthetic automatic reply must not resolve {serviceType.Name}.");
    }
}
