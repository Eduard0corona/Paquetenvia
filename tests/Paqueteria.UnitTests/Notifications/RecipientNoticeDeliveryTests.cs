using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.Dispatching;
using Notifications.Application.RecipientNotices;
using Notifications.Infrastructure;
using Notifications.Infrastructure.Dispatching;
using Notifications.Infrastructure.RecipientNotices;
using Npgsql;
using Orders.Application.RecipientNotices;
using Orders.Application.Tracking;
using Orders.Infrastructure.RecipientNotices;
using Orders.Infrastructure.Tracking;
using Paqueteria.Application.Messaging;
using Paqueteria.Application.RecipientNotices;
using Paqueteria.Application.Security;
using Paqueteria.Contracts.Tracking;
using Paqueteria.Infrastructure.Messaging;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.Infrastructure.Security;

namespace Paqueteria.UnitTests.Notifications;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 and NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 (AI-08 NTF-005 required unit tests): routing
/// of WhatsApp and e-mail recipient notices to <see cref="IMessagingProvider"/>, redaction, quiet hours, the validity
/// table, link derivation against the stored hash, the switches, the caps, opt-outs and the closed result codes. Every
/// contact here is a synthetic placeholder; no real phone or e-mail is used.
/// </summary>
public sealed class RecipientNoticeDeliveryTests
{
    private static readonly Guid Owner = Guid.Parse("0f5a6c11-0000-4000-8000-0000000000a1");
    private static readonly Guid Order = Guid.Parse("0f5a6c11-0000-4000-8000-0000000000b1");
    private static readonly Guid Destination = Guid.Parse("0f5a6c11-0000-4000-8000-0000000000c1");
    private static readonly Guid NotificationId = Guid.Parse("0f5a6c11-0000-4000-8000-0000000000d1");
    private const string PublicId = "ORD_ntf005syntheticguia01";
    private const string BaseUrl = "https://paquetenvia.test";
    private static readonly byte[] LinkKey = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
    private static readonly byte[] LookupKey = Enumerable.Range(101, 32).Select(static value => (byte)value).ToArray();

    // ------------------------------------------------------------------ quiet hours (AI-24 recipient_notices.quiet_hours)

    [Theory]
    // AI-24 recipient_notices.quiet_hours.window: "messages leave only from 08:00 (inclusive) to 21:00 (exclusive) local
    // time" (America/Mazatlan wall clock): 08:00:00 is the first open instant and 21:00:00 the first closed one.
    [InlineData("07:59:59.9999999", false)]
    [InlineData("08:00:00", true)]
    [InlineData("12:00:00", true)]
    [InlineData("20:59:59.9999999", true)]
    [InlineData("21:00:00", false)]
    [InlineData("23:59:59", false)]
    [InlineData("00:00:00", false)]
    public void Quiet_hours_are_open_from_eight_inclusive_to_twenty_one_exclusive(string time, bool open)
    {
        var window = new RecipientNoticeQuietHours(new TimeOnly(8, 0), new TimeOnly(21, 0));

        Assert.Equal(open, window.IsOpen(TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    // A notice outside the window waits for the next 08:00: the same day before it, the next day at or after 21:00.
    [InlineData("2026-10-23T07:59:59", "2026-10-23T08:00:00")]
    [InlineData("2026-10-23T08:00:00", "2026-10-23T08:00:00")]
    [InlineData("2026-10-23T20:59:59", "2026-10-23T20:59:59")]
    [InlineData("2026-10-23T21:00:00", "2026-10-24T08:00:00")]
    [InlineData("2026-10-23T23:30:00", "2026-10-24T08:00:00")]
    [InlineData("2026-12-31T22:00:00", "2027-01-01T08:00:00")]
    public void A_notice_outside_the_window_waits_for_the_next_opening(string local, string opening)
    {
        var window = new RecipientNoticeQuietHours(new TimeOnly(8, 0), new TimeOnly(21, 0));

        Assert.Equal(Local(opening), window.NextOpening(Local(local)));
    }

    [Fact]
    public void The_stale_age_does_not_count_the_quiet_hours_wait()
    {
        var window = new RecipientNoticeQuietHours(new TimeOnly(8, 0), new TimeOnly(21, 0));

        // Requested at 22:00 and sent at 08:05 the next day: five minutes old, not ten hours.
        Assert.Equal(TimeSpan.FromMinutes(5), window.AgeWithoutWait(Local("2026-10-22T22:00:00"), Local("2026-10-23T08:05:00")));
        // Requested at 10:00 and still pending the next day at 10:00 (an outage): a full day old.
        Assert.Equal(TimeSpan.FromHours(24), window.AgeWithoutWait(Local("2026-10-22T10:00:00"), Local("2026-10-23T10:00:00")));
        // Never negative.
        Assert.Equal(TimeSpan.Zero, window.AgeWithoutWait(Local("2026-10-23T22:00:00"), Local("2026-10-23T22:30:00")));
        Assert.Throws<ArgumentException>(() => new RecipientNoticeQuietHours(new TimeOnly(21, 0), new TimeOnly(8, 0)));
    }

    // ------------------------------------------------------------------ validity at send (AI-24 recipient_notices.notices)

    [Theory]
    [InlineData(RecipientNoticeKind.TrackingLink, "DRAFT,CONFIRMED,READY_FOR_PICKUP,ASSIGNED,AT_PICKUP,PICKED_UP,IN_TRANSIT,DELIVERING,FAILED_ATTEMPT,RESCHEDULED,RETURNING")]
    [InlineData(RecipientNoticeKind.InTransit, "IN_TRANSIT")]
    [InlineData(RecipientNoticeKind.DeliveryFailed, "DRAFT,CONFIRMED,READY_FOR_PICKUP,ASSIGNED,AT_PICKUP,PICKED_UP,IN_TRANSIT,FAILED_ATTEMPT,RESCHEDULED,RETURNING,RETURNED,CANCELLED")]
    [InlineData(RecipientNoticeKind.Delivered, "DELIVERED,CLOSED,CLAIM_OPEN,CLAIM_RESOLVED")]
    public void The_validity_table_covers_the_seventeen_internal_states(RecipientNoticeKind kind, string validStatuses)
    {
        var statuses = new[]
        {
            "DRAFT", "CONFIRMED", "READY_FOR_PICKUP", "ASSIGNED", "AT_PICKUP", "PICKED_UP", "IN_TRANSIT", "DELIVERING",
            "FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "RETURNED", "DELIVERED", "CLOSED", "CLAIM_OPEN", "CLAIM_RESOLVED",
            "CANCELLED",
        };
        var policy = new PublicOrderStatusPolicy();

        var valid = statuses.Where(status => RecipientNoticeValidity.IsValidAtSend(kind, status, policy)).ToArray();

        Assert.Equal(validStatuses.Split(','), valid);
        // AI-01 rule 12: an unmapped status never sends anything; it fails loudly.
        Assert.Throws<PublicStatusMappingException>(() => RecipientNoticeValidity.IsValidAtSend(kind, "LOST", policy));
    }

    // ------------------------------------------------------------------ tracking link (AI-24 recipient_notices.tracking_link)

    [Fact]
    public void The_link_is_derived_in_memory_and_accepted_only_when_it_matches_the_stored_hash()
    {
        var links = TrackingLinks(BaseUrl);
        var token = TrackingLinkTokenDerivation.DeriveToken(LinkKey, 1, Order, 3);
        var stored = new RecipientNoticeLinkRecord(3, 1, SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        var link = links.TryBuild(Order, stored);

        Assert.NotNull(link);
        Assert.Equal(token, link.Token);
        Assert.Equal($"{BaseUrl}/track/{token}", link.Url);
        Assert.Equal("[redacted]", link.ToString());
        Assert.DoesNotContain(Convert.ToHexString(stored.TokenHash), stored.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RecipientNoticeLinkReadiness.Configured, links.Readiness);

        // One flipped bit of the stored hash, another order, another generation or a key version the Worker does not
        // hold: no live link (TRACKING_LINK_UNAVAILABLE).
        var mutated = stored.TokenHash.ToArray();
        mutated[0] ^= 1;
        Assert.Null(links.TryBuild(Order, stored with { TokenHash = mutated }));
        Assert.Null(links.TryBuild(Destination, stored));
        Assert.Null(links.TryBuild(Order, stored with { Generation = 4 }));
        Assert.Null(links.TryBuild(Order, stored with { KeyVersion = 2 }));
        // No public base URL: no link at all.
        Assert.Null(TrackingLinks(null).TryBuild(Order, stored));
        Assert.Equal(RecipientNoticeLinkReadiness.Unavailable, TrackingLinks(null).Readiness);
    }

    // ------------------------------------------------------------------ contact lookup and unsubscribe token

    [Fact]
    public void A_contact_is_hashed_as_its_normalized_value_under_every_configured_key()
    {
        var lookup = new HmacRecipientContactLookup(KeyRing(LookupKey));
        var expected = HMACSHA256.HashData(LookupKey, Encoding.UTF8.GetBytes("paquetenvia-contact-v1|WHATSAPP|+520000000002"));

        // The stored ten digits, the E.164 value and a WhatsApp 521 number are one contact.
        foreach (var value in new[] { "0000000002", "+520000000002", "520000000002", "5210000000002", "+5210000000002" })
        {
            var hash = Assert.Single(lookup.HashForLookup(RecipientContactChannel.WhatsApp, value));
            Assert.Equal(1, hash.KeyVersion);
            Assert.Equal(expected, hash.Hash);
            Assert.DoesNotContain("0000000002", hash.ToString(), StringComparison.Ordinal);
        }

        Assert.Equal(
            HMACSHA256.HashData(LookupKey, Encoding.UTF8.GetBytes("paquetenvia-contact-v1|EMAIL|destinatario@example.invalid")),
            lookup.HashForLookup(RecipientContactChannel.Email, "  Destinatario@Example.INVALID ").Single().Hash);
        Assert.Throws<ArgumentException>(() => lookup.HashForLookup(RecipientContactChannel.WhatsApp, "+1 555 0100"));
        Assert.Throws<ContactLookupUnavailableException>(() =>
            new HmacRecipientContactLookup(new ContactLookupKeyRing(Options.Create(new ContactLookupOptions())))
                .HashForLookup(RecipientContactChannel.Email, "destinatario@example.invalid"));
    }

    [Fact]
    public void The_unsubscribe_token_binds_the_owner_and_the_notification_and_survives_a_key_rotation()
    {
        var tokens = new HmacRecipientNoticeUnsubscribeTokens(KeyRing(LookupKey));
        var token = tokens.Issue(Owner, NotificationId);

        Assert.Equal(HmacRecipientNoticeUnsubscribeTokens.TokenLength, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]{88}$", token);
        Assert.True(tokens.TryVerify(token, out var owner, out var notification));
        Assert.Equal((Owner, NotificationId), (owner, notification));

        // Any changed character, another key, or garbage is refused.
        var tampered = (token[^1] == 'A' ? token[..^1] + "B" : token[..^1] + "A");
        Assert.False(tokens.TryVerify(tampered, out _, out _));
        Assert.False(new HmacRecipientNoticeUnsubscribeTokens(KeyRing(LinkKey)).TryVerify(token, out _, out _));
        Assert.False(tokens.TryVerify("not-a-token", out _, out _));
        Assert.False(tokens.TryVerify(null, out _, out _));

        // A rotation keeps tokens of the previous version verifiable while it stays configured.
        var rotated = new HmacRecipientNoticeUnsubscribeTokens(new ContactLookupKeyRing(Options.Create(new ContactLookupOptions
        {
            CurrentKeyVersion = 2,
            Keys = new() { [1] = Convert.ToBase64String(LookupKey), [2] = Convert.ToBase64String(LinkKey) },
        })));
        Assert.True(rotated.TryVerify(token, out _, out _));
        Assert.NotEqual(token, rotated.Issue(Owner, NotificationId));
    }

    // ------------------------------------------------------------------ texts (AI-24 recipient_notices.templates)

    [Fact]
    public void The_email_text_is_the_ai24_body_link_unsubscribe_and_automatic_lines_with_three_placeholders()
    {
        foreach (var kind in RecipientNoticeCatalog.All)
        {
            var text = RecipientNoticeTexts.EmailPlainText(kind);
            Assert.Equal(
                RecipientNoticeTexts.EmailBody(kind) + "\n\n{{2}}\n\nPara dejar de recibir estos correos: {{3}}\n\n" +
                "Este correo se envía automáticamente; no lo respondas.",
                text);
            Assert.True(MessagingOptionsValidator.PlaceholdersMatch(RecipientNoticeTexts.EmailSubject(kind) + "\n" + text, 3));
            Assert.Equal(1, Regex(RecipientNoticeTexts.WhatsAppBody(kind)));
            Assert.Contains("con el botón Ver seguimiento", RecipientNoticeTexts.WhatsAppBody(kind), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("botón", RecipientNoticeTexts.EmailBody(kind), StringComparison.Ordinal);
            Assert.DoesNotContain("Hoy", RecipientNoticeTexts.WhatsAppBody(kind), StringComparison.Ordinal);
        }

        static int Regex(string text) => System.Text.RegularExpressions.Regex.Matches(text, @"\{\{\d+\}\}").Count;
    }

    // ------------------------------------------------------------------ redaction

    [Fact]
    public void Nothing_personal_or_secret_is_printed()
    {
        var request = new MessagingRequest(NotificationId, MessagingChannel.Email, new MessagingRecipient("destinatario@example.invalid"),
            "recipient.delivered", [PublicId, $"{BaseUrl}/track/secret-token"])
        {
            OneClickUnsubscribeUrl = $"{BaseUrl}/api/v1/recipient-notices/unsubscribe/secret-token",
        };
        var copies = new RecipientNoticeContactCopies(Owner, Destination, "akv:recipient-notice-kek/0123", [1, 2, 3], [4, 5, 6]);

        foreach (var printed in new[] { request.ToString(), copies.ToString(), new MessagingRecipient("+520000000002").ToString() })
        {
            Assert.DoesNotContain("destinatario", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-token", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("+52", printed, StringComparison.Ordinal);
            Assert.DoesNotContain(PublicId, printed, StringComparison.Ordinal);
            Assert.DoesNotContain("akv:", printed, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ expansion (AI-24 recipient_notices.expansion)

    [Theory]
    // consent, phone copy, e-mail copy, WhatsApp provider, e-mail provider -> channels (WhatsApp first; E4-P1, E4-P11).
    [InlineData(true, true, true, WhatsAppProviderKind.Synthetic, EmailProviderKind.Synthetic, "WHATSAPP,EMAIL")]
    [InlineData(false, true, true, WhatsAppProviderKind.Synthetic, EmailProviderKind.Synthetic, "EMAIL")]
    [InlineData(true, true, false, WhatsAppProviderKind.Synthetic, EmailProviderKind.Synthetic, "WHATSAPP")]
    [InlineData(true, true, true, WhatsAppProviderKind.Disabled, EmailProviderKind.Synthetic, "EMAIL")]
    [InlineData(true, true, true, WhatsAppProviderKind.Synthetic, EmailProviderKind.Disabled, "WHATSAPP")]
    [InlineData(true, false, false, WhatsAppProviderKind.Synthetic, EmailProviderKind.Synthetic, "")]
    public async Task Expansion_creates_a_notice_per_eligible_channel(
        bool consent,
        bool phone,
        bool email,
        WhatsAppProviderKind whatsAppProvider,
        EmailProviderKind emailProvider,
        string channels)
    {
        var harness = new Harness
        {
            Consent = consent,
            Copies = new RecipientNoticeContactCopies(Owner, Destination, "ntf002-notice-mock-v1",
                phone ? [1] : null, email ? [2] : null),
        };
        harness.Messaging.WhatsApp.Provider = whatsAppProvider;
        harness.Messaging.Email.Provider = emailProvider;

        await harness.Processor().ExpandAsync(Request("IN_TRANSIT"), default);

        var expansion = Assert.Single(harness.Store.Expansions);
        Assert.True(expansion.Enabled);
        Assert.Equal(PublicId, expansion.PublicId);
        Assert.Equal(channels, string.Join(',', expansion.Channels));
        Assert.Equal(TimeSpan.FromHours(12), expansion.MaximumDelay);
        Assert.Equal((new TimeOnly(8, 0), new TimeOnly(21, 0)), (expansion.Start, expansion.End));
    }

    [Fact]
    public async Task Expansion_with_notices_off_reads_nothing_and_settles_disabled()
    {
        var harness = new Harness();
        harness.Options.Enabled = false;

        await harness.Processor().ExpandAsync(Request("TRACKING_LINK"), default);

        var expansion = Assert.Single(harness.Store.Expansions);
        Assert.False(expansion.Enabled);
        Assert.Empty(expansion.Channels);
        Assert.Null(expansion.PublicId);
        Assert.Equal(0, harness.Reads);
    }

    [Theory]
    [InlineData("{\"schema_version\":\"recipient-notice-requested-v1\",\"order_id\":\"0f5a6c11-0000-4000-8000-0000000000b1\"}", "INVALID_PAYLOAD")]
    [InlineData("{\"schema_version\":\"recipient-notice-requested-v2\",\"order_event_id\":\"0f5a6c11-0000-4000-8000-0000000000e1\",\"order_id\":\"0f5a6c11-0000-4000-8000-0000000000b1\",\"notice\":\"DELIVERED\",\"occurred_at\":\"2026-10-23T10:00:00Z\"}", "INVALID_PAYLOAD")]
    [InlineData("{\"schema_version\":\"recipient-notice-requested-v1\",\"order_event_id\":\"0f5a6c11-0000-4000-8000-0000000000e1\",\"order_id\":\"0f5a6c11-0000-4000-8000-0000000000b1\",\"notice\":\"RETURNED\",\"occurred_at\":\"2026-10-23T10:00:00Z\"}", "INVALID_PAYLOAD")]
    public async Task A_malformed_request_ends_dead_before_anything_is_read(string payload, string code)
    {
        var harness = new Harness();

        await harness.Processor().ExpandAsync(Request("DELIVERED") with { PayloadJson = payload }, default);

        var settle = Assert.Single(harness.Store.Settles);
        Assert.Equal(("DEAD", code), (settle.Status, settle.Code));
        Assert.Empty(harness.Store.Expansions);
        Assert.Equal(0, harness.Reads);

        var foreign = new Harness();
        await foreign.Processor().ExpandAsync(
            Request("DELIVERED") with { TenantContextJson = $"{{\"organization_ids\":[\"{Guid.NewGuid():D}\"]}}" },
            default);
        var foreignSettle = Assert.Single(foreign.Store.Settles);
        Assert.Equal(("DEAD", "TENANT_CONTEXT_MISMATCH"), (foreignSettle.Status, foreignSettle.Code));
    }

    // ------------------------------------------------------------------ send (AI-24 recipient_notices, routing)

    [Fact]
    public async Task A_whatsapp_notice_is_routed_with_the_number_of_guide_the_button_suffix_and_the_quick_reply()
    {
        var harness = new Harness();

        await harness.Processor().SendAsync(SendRequest(), Delivery("WHATSAPP", "recipient.delivered"), default);

        var sent = Assert.Single(harness.Provider.Requests);
        Assert.Equal(MessagingChannel.WhatsApp, sent.Channel);
        Assert.Equal(NotificationId, sent.MessageId);
        Assert.Equal("+520000000002", sent.Recipient.Address);
        Assert.Equal("recipient.delivered", sent.TemplateKey);
        Assert.Equal([PublicId], sent.Parameters);
        Assert.Equal(harness.Token, sent.UrlButtonSuffix);
        Assert.Equal("RECIPIENT_NOTICE_OPT_OUT", sent.QuickReplyPayload);
        Assert.Null(sent.OneClickUnsubscribeUrl);
        Assert.Equal(("SUCCESS", "MESSAGING_ACCEPTED", "wamid.SYNTHETIC==", (DateTimeOffset?)null), Assert.Single(harness.Store.Applies));
        // The opt-out lookup used the keyed hash of the placeholder, on the WhatsApp channel.
        var lookup = Assert.Single(harness.Store.OptOutLookups);
        Assert.Equal("WHATSAPP", lookup.Channel);
        Assert.Equal(
            HMACSHA256.HashData(LookupKey, Encoding.UTF8.GetBytes("paquetenvia-contact-v1|WHATSAPP|+520000000002")),
            Assert.Single(lookup.Hashes));
    }

    [Fact]
    public async Task An_email_notice_is_routed_with_the_link_the_unsubscribe_page_and_the_one_click_header()
    {
        var harness = new Harness { Status = "IN_TRANSIT" };

        await harness.Processor().SendAsync(SendRequest(), Delivery("EMAIL", "recipient.in-transit"), default);

        var sent = Assert.Single(harness.Provider.Requests);
        Assert.Equal(MessagingChannel.Email, sent.Channel);
        Assert.Equal("destinatario@example.invalid", sent.Recipient.Address);
        Assert.Equal(3, sent.Parameters.Count);
        Assert.Equal(PublicId, sent.Parameters[0]);
        Assert.Equal($"{BaseUrl}/track/{harness.Token}", sent.Parameters[1]);
        Assert.StartsWith($"{BaseUrl}/avisos/baja/", sent.Parameters[2], StringComparison.Ordinal);
        var token = sent.Parameters[2][$"{BaseUrl}/avisos/baja/".Length..];
        Assert.Equal($"{BaseUrl}/api/v1/recipient-notices/unsubscribe/{token}", sent.OneClickUnsubscribeUrl);
        Assert.True(new HmacRecipientNoticeUnsubscribeTokens(KeyRing(LookupKey)).TryVerify(token, out var owner, out var notification));
        Assert.Equal((Owner, NotificationId), (owner, notification));
        Assert.Null(sent.UrlButtonSuffix);
        Assert.Null(sent.QuickReplyPayload);
    }

    [Theory]
    [InlineData(false, WhatsAppProviderKind.Synthetic)]
    [InlineData(true, WhatsAppProviderKind.Disabled)]
    public async Task Notices_off_or_a_disabled_provider_end_failed_channel_disabled_without_reading_anything(
        bool enabled,
        WhatsAppProviderKind provider)
    {
        var harness = new Harness();
        harness.Options.Enabled = enabled;
        harness.Messaging.WhatsApp.Provider = provider;

        await harness.Processor().SendAsync(SendRequest(), Delivery("WHATSAPP", "recipient.delivered"), default);

        Assert.Equal(("PERMANENT", "MESSAGING_CHANNEL_DISABLED", (string?)null, (DateTimeOffset?)null), Assert.Single(harness.Store.Applies));
        Assert.Empty(harness.Provider.Requests);
        Assert.Equal(0, harness.Reads);
    }

    [Theory]
    // AI-24 quiet_hours: at 21:00:00 the window is closed (the send waits for 08:00 the next day); at 08:00:00 it is open.
    [InlineData("2026-10-23T21:00:00", "2026-10-24T08:00:00")]
    [InlineData("2026-10-23T07:59:59", "2026-10-23T08:00:00")]
    [InlineData("2026-10-23T08:00:00", null)]
    [InlineData("2026-10-23T20:59:59", null)]
    public async Task Nothing_leaves_outside_the_window_and_a_deferred_send_waits_for_the_next_opening(string now, string? opening)
    {
        var harness = new Harness { NowLocal = Local(now), OccurredLocal = Local(now).AddMinutes(-1) };

        await harness.Processor().SendAsync(SendRequest(), Delivery("WHATSAPP", "recipient.delivered"), default);

        if (opening is null)
        {
            Assert.Single(harness.Provider.Requests);
            return;
        }

        Assert.Empty(harness.Provider.Requests);
        Assert.Empty(harness.Store.Applies);
        // settle_outbox RETRY at the next opening, no code and no attempt counted on the notification.
        Assert.Equal(("RETRY", (string?)null, (DateTimeOffset?)Harness.Utc(Local(opening))), Assert.Single(harness.Store.Settles));
        Assert.Equal(0, harness.Reads);
    }

    [Fact]
    public async Task Stale_superseded_capped_opted_out_and_linkless_notices_never_reach_the_provider()
    {
        // Past the stale bound (13 hours of open window).
        var stale = new Harness { OccurredLocal = Local("2026-10-22T19:00:00"), NowLocal = Local("2026-10-23T19:30:00") };
        await stale.Processor().SendAsync(SendRequest(), Delivery("EMAIL", "recipient.delivered"), default);
        Assert.Equal("NOTICE_STALE", Assert.Single(stale.Store.Cancels));

        // Superseded by an offline replay: the order is already DELIVERED when "Va en camino" is sent.
        var superseded = new Harness { Status = "DELIVERED" };
        await superseded.Processor().SendAsync(SendRequest(), Delivery("WHATSAPP", "recipient.in-transit"), default);
        Assert.Equal("NOTICE_SUPERSEDED", Assert.Single(superseded.Store.Cancels));

        // The caps (NTF-RECIPIENT-NOTICE-CAPS-2026-10-23): 8 others of the order and channel, or 500 of the channel today.
        foreach (var counts in new[] { new RecipientNoticeCounts(8, 0), new RecipientNoticeCounts(0, 500) })
        {
            var capped = new Harness { Counts = counts };
            await capped.Processor().SendAsync(SendRequest(), Delivery("WHATSAPP", "recipient.delivered"), default);
            Assert.Equal("NOTICE_LIMIT_REACHED", Assert.Single(capped.Store.Cancels));
            Assert.Equal(0, capped.ContactReads);
        }

        var underCap = new Harness { Counts = new RecipientNoticeCounts(7, 499) };
        await underCap.Processor().SendAsync(SendRequest(), Delivery("WHATSAPP", "recipient.delivered"), default);
        Assert.Single(underCap.Provider.Requests);

        var optedOut = new Harness { OptedOut = true };
        await optedOut.Processor().SendAsync(SendRequest(), Delivery("EMAIL", "recipient.delivered"), default);
        Assert.Equal("RECIPIENT_OPTED_OUT", Assert.Single(optedOut.Store.Cancels));

        var linkless = new Harness { WithLink = false };
        await linkless.Processor().SendAsync(SendRequest(), Delivery("WHATSAPP", "recipient.delivered"), default);
        Assert.Equal(("PERMANENT", "TRACKING_LINK_UNAVAILABLE", (string?)null, (DateTimeOffset?)null), Assert.Single(linkless.Store.Applies));

        // NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 L3: a contact that cannot be used ends FAILED MESSAGING_RECIPIENT_INVALID.
        var unusable = new Harness { Copies = new RecipientNoticeContactCopies(Owner, Destination, "ntf002-notice-mock-v1", [1], null) };
        await unusable.Processor().SendAsync(SendRequest(), Delivery("EMAIL", "recipient.delivered"), default);
        Assert.Equal(("PERMANENT", "MESSAGING_RECIPIENT_INVALID", (string?)null, (DateTimeOffset?)null), Assert.Single(unusable.Store.Applies));

        foreach (var harness in new[] { stale, superseded, optedOut, linkless, unusable })
        {
            Assert.Empty(harness.Provider.Requests);
        }
    }

    [Fact]
    public async Task An_ambiguous_whatsapp_fails_once_and_an_ambiguous_email_is_retried_inside_the_window()
    {
        var whatsapp = new Harness { Result = new MessagingResult(MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.Timeout) };
        await whatsapp.Processor().SendAsync(SendRequest(), Delivery("WHATSAPP", "recipient.delivered"), default);
        Assert.Equal(("AMBIGUOUS_FAILED", "AMBIGUOUS_TIMEOUT", (string?)null, (DateTimeOffset?)null), Assert.Single(whatsapp.Store.Applies));

        // An e-mail retry that would fall at 21:00:30 waits for 08:00 the next day.
        var email = new Harness
        {
            NowLocal = Local("2026-10-23T20:59:58"),
            Result = new MessagingResult(MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.Timeout),
        };
        await email.Processor().SendAsync(SendRequest() with { Attempts = 5 }, Delivery("EMAIL", "recipient.delivered"), default);
        var apply = Assert.Single(email.Store.Applies);
        Assert.Equal(("AMBIGUOUS", MessagingResultCodes.Timeout), (apply.Outcome, apply.Code));
        Assert.Equal(Harness.Utc(Local("2026-10-24T08:00:00")), apply.Next);
    }

    [Fact]
    public async Task A_transient_database_failure_before_the_provider_call_is_retried_without_an_attempt()
    {
        var harness = new Harness { FailOrderRead = true };

        await harness.Processor().SendAsync(SendRequest(), Delivery("WHATSAPP", "recipient.delivered"), default);

        Assert.Empty(harness.Provider.Requests);
        Assert.Empty(harness.Store.Applies);
        var settle = Assert.Single(harness.Store.Settles);
        Assert.Equal(("RETRY", (string?)null), (settle.Status, settle.Code));
    }

    [Fact]
    public void Every_logged_outcome_maps_to_its_outbox_settlement()
    {
        Assert.Equal(OutboxSettlement.Processed, RecipientNoticeProcessor.Settlement("PROCESSED"));
        Assert.Equal(OutboxSettlement.Processed, RecipientNoticeProcessor.Settlement("CANCELLED"));
        Assert.Equal(OutboxSettlement.Processed, RecipientNoticeProcessor.Settlement("SUCCESS"));
        Assert.Equal(OutboxSettlement.Dead, RecipientNoticeProcessor.Settlement("PERMANENT"));
        Assert.Equal(OutboxSettlement.Dead, RecipientNoticeProcessor.Settlement("AMBIGUOUS_FAILED"));
        Assert.Equal(OutboxSettlement.Retry, RecipientNoticeProcessor.Settlement("DEFERRED"));
        Assert.Equal(OutboxSettlement.Retry, RecipientNoticeProcessor.Settlement("AMBIGUOUS"));
        Assert.True(RecipientNoticeCodes.IsProcessedSourceCode("RECIPIENT_NOTICES_DISABLED"));
        Assert.True(RecipientNoticeCodes.IsProcessedSourceCode("NOTICE_STALE"));
        Assert.False(RecipientNoticeCodes.IsProcessedSourceCode("TEMPLATE_NOT_FOUND"));
    }

    // ------------------------------------------------------------------ helpers

    private static DateTime Local(string value) =>
        DateTime.SpecifyKind(DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Unspecified);

    private static ContactLookupKeyRing KeyRing(byte[] key) =>
        new(Options.Create(new ContactLookupOptions { CurrentKeyVersion = 1, Keys = new() { [1] = Convert.ToBase64String(key) } }));

    private static RecipientNoticeTrackingLinks TrackingLinks(string? baseUrl)
    {
        var options = new PublicTrackingOptions
        {
            PublicBaseUrl = baseUrl,
            CurrentLinkKeyVersion = 1,
            LinkKeys = new() { [1] = Convert.ToBase64String(LinkKey) },
        };
        return new RecipientNoticeTrackingLinks(
            new PublicTrackingLinkKeyRing(options, allowSyntheticKey: false),
            Options.Create(options),
            new PublicTrackingBaseUrlPolicy(allowLoopbackHttp: false));
    }

    private static ClaimedNotificationOutboxMessage Request(string notice) => new(
        Guid.NewGuid(),
        Owner,
        $"{{\"organization_ids\":[\"{Owner:D}\"]}}",
        NotificationOutboxTopics.RecipientNoticeRequested,
        "Order",
        Order,
        2,
        $"{{\"schema_version\":\"recipient-notice-requested-v1\",\"order_event_id\":\"0f5a6c11-0000-4000-8000-0000000000e1\",\"order_id\":\"{Order:D}\",\"notice\":\"{notice}\",\"occurred_at\":\"2026-10-23T17:00:00.000000Z\"}}",
        1,
        Guid.NewGuid(),
        DateTimeOffset.UtcNow.AddMinutes(1),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private static ClaimedNotificationOutboxMessage SendRequest() => new(
        Guid.NewGuid(),
        Owner,
        $"{{\"organization_ids\":[\"{Owner:D}\"]}}",
        NotificationOutboxTopics.SendRequested,
        "Notification",
        NotificationId,
        1,
        $"{{\"schema_version\":\"notification-send-requested-v1\",\"notification_id\":\"{NotificationId:D}\"}}",
        1,
        Guid.NewGuid(),
        DateTimeOffset.UtcNow.AddMinutes(1),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private static NotificationDelivery Delivery(string channel, string templateKey) => new(
        NotificationId,
        Owner,
        channel,
        "PENDING",
        0,
        1,
        templateKey,
        1,
        $"{{\"order_public_id\": \"{PublicId}\"}}",
        null,
        "ORDER_DESTINATION",
        Order,
        Guid.Parse("0f5a6c11-0000-4000-8000-0000000000e1"),
        DateTimeOffset.Parse("2026-10-23T17:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>A processor over fakes: synthetic providers, the AI-24 placeholders and a fixed America/Mazatlan clock.</summary>
    private sealed class Harness :
        IRecipientNoticeOrderReader,
        IRecipientNoticeConsentReader,
        IRecipientNoticeContactReader
    {
        public Harness()
        {
            Token = TrackingLinkTokenDerivation.DeriveToken(LinkKey, 1, Order, 1);
            Store = new FakeStore(this);
        }

        public RecipientNoticesOptions Options { get; } = new() { Enabled = true };

        public MessagingOptions Messaging { get; } = new()
        {
            WhatsApp = new() { Provider = WhatsAppProviderKind.Synthetic },
            Email = new() { Provider = EmailProviderKind.Synthetic },
        };

        public FakeStore Store { get; }

        public CapturingProvider Provider { get; } = new();

        public string Token { get; }

        public string Status { get; init; } = "DELIVERED";

        public bool Consent { get; init; } = true;

        public bool WithLink { get; init; } = true;

        public bool OptedOut { get; init; }

        public bool FailOrderRead { get; init; }

        public RecipientNoticeCounts Counts { get; init; } = new(0, 0);

        public RecipientNoticeContactCopies? Copies { get; init; } =
            new(Owner, Destination, "ntf002-notice-mock-v1", [1], [2]);

        public DateTime NowLocal { get; init; } = Local("2026-10-23T12:00:00");

        public DateTime OccurredLocal { get; init; } = Local("2026-10-23T11:00:00");

        public MessagingResult Result { get; init; } =
            new(MessagingOutcome.Accepted, MessagingResultCodes.Accepted, "wamid.SYNTHETIC==");

        public int Reads { get; private set; }

        public int ContactReads { get; private set; }

        /// <summary>America/Mazatlan is UTC-7 all year (no daylight saving since 2022); the fake converts with it.</summary>
        public static DateTimeOffset Utc(DateTime local) => new DateTimeOffset(local, TimeSpan.FromHours(-7)).ToUniversalTime();

        public RecipientNoticeProcessor Processor()
        {
            var services = new ServiceProviderStub();
            var messaging = Microsoft.Extensions.Options.Options.Create(Messaging);
            Provider.Result = Result;
            return new RecipientNoticeProcessor(
                Store,
                Store,
                this,
                this,
                this,
                TrackingLinks(BaseUrl),
                new RecipientNoticeContactResolver(this, messaging, services),
                new HmacRecipientContactLookup(KeyRing(LookupKey)),
                new HmacRecipientNoticeUnsubscribeTokens(KeyRing(LookupKey)),
                Provider,
                new PublicOrderStatusPolicy(),
                Microsoft.Extensions.Options.Options.Create(Options),
                messaging,
                Microsoft.Extensions.Options.Options.Create(new NotificationsOptions()),
                Microsoft.Extensions.Options.Options.Create(new PublicTrackingOptions { PublicBaseUrl = BaseUrl }),
                new OutboxLaneMonitor(NullLogger<OutboxLaneMonitor>.Instance, TimeProvider.System),
                NullLogger<RecipientNoticeProcessor>.Instance);
        }

        Task<RecipientNoticeOrderSnapshot?> IRecipientNoticeOrderReader.ReadAsync(
            Guid ownerOrganizationId,
            Guid orderId,
            CancellationToken cancellationToken)
        {
            Reads++;
            if (FailOrderRead)
            {
                throw new NpgsqlException("synthetic transient failure", new TimeoutException());
            }

            var link = WithLink ? new RecipientNoticeLinkRecord(1, 1, SHA256.HashData(Encoding.UTF8.GetBytes(Token))) : null;
            return Task.FromResult<RecipientNoticeOrderSnapshot?>(
                new RecipientNoticeOrderSnapshot(orderId, PublicId, Status, Destination, link));
        }

        public Task<bool> HasWhatsAppConsentAsync(Guid ownerOrganizationId, Guid orderId, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Consent);
        }

        Task<RecipientNoticeContactCopies?> IRecipientNoticeContactReader.ReadAsync(
            Guid ownerOrganizationId,
            Guid locationId,
            CancellationToken cancellationToken)
        {
            Reads++;
            ContactReads++;
            return Task.FromResult(Copies);
        }

        public sealed class FakeStore(Harness harness) : INotificationsStore, IRecipientNoticeStore
        {
            public List<(bool Enabled, TimeSpan MaximumDelay, TimeOnly Start, TimeOnly End, string? PublicId, string[] Channels)> Expansions { get; } = [];

            public List<(string Status, string? Code, DateTimeOffset? AvailableAt)> Settles { get; } = [];

            public List<(string Outcome, string Code, string? Reference, DateTimeOffset? Next)> Applies { get; } = [];

            public List<string> Cancels { get; } = [];

            public List<(string Channel, IReadOnlyList<byte[]> Hashes)> OptOutLookups { get; } = [];

            public Task<bool> SettleAsync(Guid id, Guid leaseToken, string status, string? errorCode, DateTimeOffset? availableAt, CancellationToken cancellationToken)
            {
                Settles.Add((status, errorCode, availableAt));
                return Task.FromResult(true);
            }

            public Task<string> ExpandRecipientNoticeAsync(ClaimedNotificationOutboxMessage source, bool enabled, TimeSpan maximumDelay, TimeOnly windowStart, TimeOnly windowEnd, string? publicId, IReadOnlyList<string> channels, CancellationToken cancellationToken)
            {
                Expansions.Add((enabled, maximumDelay, windowStart, windowEnd, publicId, channels.ToArray()));
                return Task.FromResult(enabled ? channels.Count == 0 ? "NO_ELIGIBLE_CHANNEL" : "SOURCE_EXPANDED" : "RECIPIENT_NOTICES_DISABLED");
            }

            public Task<RecipientNoticeCounts?> ReadRecipientNoticeCountsAsync(ClaimedNotificationOutboxMessage request, CancellationToken cancellationToken) =>
                Task.FromResult<RecipientNoticeCounts?>(harness.Counts);

            public Task<bool> IsRecipientOptedOutAsync(string channel, IReadOnlyList<byte[]> contactHashes, CancellationToken cancellationToken)
            {
                OptOutLookups.Add((channel, contactHashes));
                return Task.FromResult(harness.OptedOut);
            }

            public Task<bool> CancelNotificationAsync(ClaimedNotificationOutboxMessage request, NotificationDelivery delivery, string code, DateTimeOffset occurredAt, CancellationToken cancellationToken)
            {
                Cancels.Add(code);
                return Task.FromResult(true);
            }

            public Task<bool> ApplyRecipientOutcomeAsync(ClaimedNotificationOutboxMessage request, NotificationDelivery delivery, string outcome, string code, string? providerReference, DateTimeOffset attemptedAt, DateTimeOffset? availableAt, CancellationToken cancellationToken)
            {
                Applies.Add((outcome, code, providerReference, availableAt));
                return Task.FromResult(true);
            }

            public Task<RecipientNoticeClock> ReadRecipientNoticeClockAsync(DateTimeOffset occurredAt, CancellationToken cancellationToken) =>
                Task.FromResult(new RecipientNoticeClock(Utc(harness.NowLocal), harness.NowLocal, harness.OccurredLocal));

            public Task<DateTime> ToMazatlanLocalAsync(DateTimeOffset instant, CancellationToken cancellationToken) =>
                Task.FromResult(DateTime.SpecifyKind(instant.ToOffset(TimeSpan.FromHours(-7)).DateTime, DateTimeKind.Unspecified));

            public Task<DateTimeOffset> FromMazatlanLocalAsync(DateTime local, CancellationToken cancellationToken) =>
                Task.FromResult(Utc(local));

            public Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> ClaimNotificationsAsync(string workerId, int batchSize, TimeSpan lease, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> ClaimUnownedAsync(string workerId, int batchSize, TimeSpan lease, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<IReadOnlyList<ClaimedNotificationOutboxMessage>> RecoverStaleAsync(string workerId, int batchSize, int maximumAttempts, TimeSpan lease, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<int> RecoverStaleUnownedAsync(int batchSize, int maximumAttempts, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<string> ExpandSourceAsync(ClaimedNotificationOutboxMessage source, ParsedOrderCreated parsed, IReadOnlyList<Guid> recipients, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<NotificationDelivery?> ReadDeliveryAsync(Guid notificationId, Guid ownerOrganizationId, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<bool> ApplyOutcomeAsync(ClaimedNotificationOutboxMessage request, NotificationDelivery delivery, string outcome, string code, DateTimeOffset attemptedAt, DateTimeOffset? availableAt, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<bool> FinalizeMaximumAttemptsAsync(ClaimedNotificationOutboxMessage request, NotificationDelivery delivery, DateTimeOffset finalizedAt, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<string?> ResolveConsumerAsync(string topic, CancellationToken cancellationToken) => throw new NotSupportedException();
        }
    }

    private sealed class CapturingProvider : IMessagingProvider
    {
        public List<MessagingRequest> Requests { get; } = [];

        public MessagingResult Result { get; set; } = new(MessagingOutcome.Accepted, MessagingResultCodes.Accepted);

        public ValueTask<MessagingResult> SendAsync(MessagingRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(Result);
        }
    }

    /// <summary>The synthetic path never asks for the Key Vault opener; any request for a service fails the test.</summary>
    private sealed class ServiceProviderStub : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            throw new InvalidOperationException($"The synthetic contact path must not resolve {serviceType.Name}.");
    }
}
