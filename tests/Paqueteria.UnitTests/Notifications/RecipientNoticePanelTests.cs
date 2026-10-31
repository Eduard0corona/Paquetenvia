using System.Text;
using Locations.Infrastructure.Geocoding;
using Locations.Infrastructure.RecipientNotices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.RecipientNotices;
using Notifications.Infrastructure.Persistence.Migrations;
using Notifications.Infrastructure.RecipientNotices.Panel;
using Paqueteria.Application.RecipientNotices;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Security;
using Paqueteria.Infrastructure.Security.Pii;

namespace Paqueteria.UnitTests.Notifications;

/// <summary>
/// NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007; T6 choices 4 to 8 and 11 to 12): the closed sets of the list,
/// the code mapping, the stop's decisions with a fake store (open in memory, hash under every key version, leave out an
/// unusable channel, 409 with none left, 503 without a key), the Locations phone openers and the redaction of every value
/// type. Every phone and address is a synthetic placeholder.
/// </summary>
public sealed class RecipientNoticePanelTests
{
    private static readonly Guid Organization = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Actor = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa10");
    private static readonly Guid Order = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Location = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 16, 0, 0, 123, TimeSpan.Zero);
    private static readonly byte[] LookupKey = Enumerable.Range(41, 32).Select(static value => (byte)value).ToArray();
    private static readonly byte[] PreviousLookupKey = Enumerable.Range(81, 32).Select(static value => (byte)value).ToArray();
    private const string Phone = "+520000000002";
    private const string Email = "destinatario@example.invalid";

    // ------------------------------------------------------------------ closed sets (T6 choice 4)

    [Fact]
    public void The_last_code_set_is_the_lane_external_codes_plus_max_attempts_exhausted()
    {
        Assert.Equal(
            AddRecipientNoticeLane.ExternalCodes.Append("MAX_ATTEMPTS_EXHAUSTED"),
            RecipientNoticePanelContract.LastCodes);
        Assert.Equal(["WHATSAPP", "EMAIL"], RecipientNoticePanelContract.Channels);
        Assert.Equal(["PENDING", "SENT", "DELIVERED", "FAILED", "CANCELLED"], RecipientNoticePanelContract.Statuses);
        Assert.Equal(
            ["INVALID_REQUEST", "IDEMPOTENCY_CONFLICT", "RECIPIENT_CONTACT_UNAVAILABLE"],
            RecipientNoticeStopConflictCodes.All);
        Assert.Equal("RECIPIENT_NOTICES_STOPPED", RecipientNoticePanelContract.StoppedAuditAction);
        Assert.Equal(100, RecipientNoticePanelContract.MaximumItems);
    }

    [Theory]
    [InlineData("recipient.tracking-link", "TRACKING_LINK")]
    [InlineData("recipient.in-transit", "IN_TRANSIT")]
    [InlineData("recipient.delivery-failed", "DELIVERY_FAILED")]
    [InlineData("recipient.delivered", "DELIVERED")]
    public void A_stored_row_maps_its_template_to_the_notice_and_keeps_its_values(string templateKey, string notice)
    {
        var row = Row(templateKey: templateKey, lastCode: "MAX_ATTEMPTS_EXHAUSTED");
        Assert.True(RecipientNoticePanelContract.TryMap(row, out var item));
        Assert.Equal(notice, item.Notice);
        Assert.Equal(row.NotificationId, item.NotificationId);
        Assert.Equal("WHATSAPP", item.Channel);
        Assert.Equal("FAILED", item.Status);
        Assert.Equal(5, item.Attempts);
        Assert.Equal("MAX_ATTEMPTS_EXHAUSTED", item.LastCode);
        Assert.Equal(row.UpdatedAt, item.UpdatedAt);
    }

    [Theory]
    [InlineData("orders.created.operations", "WHATSAPP", "FAILED", 1, null)]
    [InlineData("recipient.tracking-link", "IN_APP", "FAILED", 1, null)]
    [InlineData("recipient.tracking-link", "SMS", "FAILED", 1, null)]
    [InlineData("recipient.tracking-link", "WHATSAPP", "QUEUED", 1, null)]
    [InlineData("recipient.tracking-link", "WHATSAPP", "FAILED", -1, null)]
    [InlineData("recipient.tracking-link", "WHATSAPP", "FAILED", 1, "SYNTHETIC_ACCEPTED")]
    [InlineData("recipient.tracking-link", "WHATSAPP", "FAILED", 1, "RECIPIENT_NOTICES_DISABLED")]
    [InlineData("recipient.tracking-link", "WHATSAPP", "FAILED", 1, "provider said: +520000000002")]
    public void A_value_outside_the_closed_sets_is_refused_and_never_passed_on(
        string templateKey,
        string channel,
        string status,
        int attempts,
        string? lastCode)
    {
        Assert.False(RecipientNoticePanelContract.TryMap(
            Row(templateKey: templateKey, channel: channel, status: status, attempts: attempts, lastCode: lastCode),
            out _));
    }

    [Fact]
    public void Value_types_never_print_a_ciphertext_a_hash_or_a_contact()
    {
        var ciphertext = Encoding.UTF8.GetBytes("ciphertext-that-must-not-print");
        var contact = new RecipientNoticeStopContact(Organization, Location, "akv:pii/1", ciphertext, ciphertext);
        Assert.DoesNotContain("ciphertext-that", contact.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("System.Byte", contact.ToString(), StringComparison.Ordinal);
        var phone = new StoredRecipientPhone(Organization, Location, "akv:pii/1", ciphertext);
        Assert.DoesNotContain("System.Byte", phone.ToString(), StringComparison.Ordinal);
        Assert.Equal("OpenedRecipientPhone { Status = Opened }", OpenedRecipientPhone.Opened(Phone).ToString());
        var optOut = new RecipientNoticeStopOptOut("WHATSAPP", Lookup().HashForLookup(RecipientContactChannel.WhatsApp, Phone));
        Assert.Equal("RecipientNoticeStopOptOut { Channel = WHATSAPP, [hashes redacted] }", optOut.ToString());
        var write = new RecipientNoticeStopWrite(Command(), [1, 2, 3], [optOut], Now);
        Assert.DoesNotContain("+52", write.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("System.Byte", write.ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the stop (T6 choices 5 to 8)

    [Fact]
    public async Task A_stop_opens_both_contacts_in_memory_hashes_them_under_every_key_version_and_records_both()
    {
        var store = new FakeStore { Read = RecipientNoticeStopRead.ForContact(Contact()) };
        var service = Service(store, PhoneOpener(OpenedRecipientPhone.Opened(Phone)), EmailOpener(OpenedRecipientEmail.Opened(Email)));

        var result = await service.StopAsync(Command(), CancellationToken.None);

        Assert.Equal(Order, result.OrderId);
        Assert.Equal(["WHATSAPP", "EMAIL"], result.Channels);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 16, 0, 0, 123, TimeSpan.Zero), result.RecordedAt);
        var write = Assert.Single(store.Writes);
        Assert.Equal(RecipientNoticePanelService.RequestHash(Command()), write.RequestHash);
        Assert.Equal(["WHATSAPP", "EMAIL"], write.OptOuts.Select(optOut => optOut.Channel));
        // Every configured key version, current first (AI-24 opt_out.key_versions).
        Assert.Equal(
            Lookup().HashForLookup(RecipientContactChannel.WhatsApp, Phone).Select(hash => (hash.KeyVersion, Convert.ToHexString(hash.Hash))),
            write.OptOuts[0].Hashes.Select(hash => (hash.KeyVersion, Convert.ToHexString(hash.Hash))));
        Assert.Equal([2, 1], write.OptOuts[1].Hashes.Select(hash => hash.KeyVersion));
        Assert.Equal(
            Lookup().HashForLookup(RecipientContactChannel.Email, Email).Select(hash => Convert.ToHexString(hash.Hash)),
            write.OptOuts[1].Hashes.Select(hash => Convert.ToHexString(hash.Hash)));
    }

    [Fact]
    public async Task A_replay_returns_the_stored_answer_and_opens_nothing()
    {
        var stored = new RecipientNoticeStopResult(Order, ["WHATSAPP"], Now.AddMinutes(-5));
        var store = new FakeStore { Read = RecipientNoticeStopRead.ForReplay(stored) };
        var phones = PhoneOpener(OpenedRecipientPhone.Opened(Phone));
        var service = Service(store, phones, EmailOpener(OpenedRecipientEmail.Opened(Email)));

        Assert.Same(stored, await service.StopAsync(Command(), CancellationToken.None));
        Assert.Empty(store.Writes);
        Assert.Equal(0, phones.Calls);
    }

    [Theory]
    [InlineData(true, false, "WHATSAPP")]
    [InlineData(false, true, "EMAIL")]
    public async Task A_contact_that_cannot_be_used_leaves_its_channel_out(bool phoneUsable, bool emailUsable, string channel)
    {
        var store = new FakeStore { Read = RecipientNoticeStopRead.ForContact(Contact()) };
        var service = Service(
            store,
            PhoneOpener(phoneUsable ? OpenedRecipientPhone.Opened(Phone) : OpenedRecipientPhone.Unusable),
            EmailOpener(emailUsable ? OpenedRecipientEmail.Opened(Email) : OpenedRecipientEmail.Unusable));

        var result = await service.StopAsync(Command(), CancellationToken.None);

        Assert.Equal([channel], result.Channels);
        Assert.Equal([channel], Assert.Single(store.Writes).OptOuts.Select(optOut => optOut.Channel));
    }

    [Fact]
    public async Task An_order_without_an_e_mail_records_whatsapp_only_and_never_opens_an_e_mail()
    {
        var store = new FakeStore { Read = RecipientNoticeStopRead.ForContact(Contact(email: false)) };
        var emails = EmailOpener(OpenedRecipientEmail.Opened(Email));
        var service = Service(store, PhoneOpener(OpenedRecipientPhone.Opened(Phone)), emails);

        Assert.Equal(["WHATSAPP"], (await service.StopAsync(Command(), CancellationToken.None)).Channels);
        Assert.Equal(0, emails.Calls);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public async Task No_usable_contact_is_409_and_writes_nothing(bool phoneUsable, bool emailUsable, bool hasContact)
    {
        var store = new FakeStore { Read = RecipientNoticeStopRead.ForContact(hasContact ? Contact() : null) };
        var service = Service(
            store,
            PhoneOpener(phoneUsable ? OpenedRecipientPhone.Opened(Phone) : OpenedRecipientPhone.Unusable),
            // An address that opens but is no e-mail is unusable too.
            EmailOpener(emailUsable ? OpenedRecipientEmail.Opened(Email) : OpenedRecipientEmail.Opened("not an e-mail")));

        var conflict = await Assert.ThrowsAsync<RecipientNoticeStopConflictException>(() =>
            service.StopAsync(Command(), CancellationToken.None));
        Assert.Equal("RECIPIENT_CONTACT_UNAVAILABLE", conflict.Code);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Without_the_PII_key_or_the_contact_lookup_key_the_stop_is_503_and_writes_nothing()
    {
        var phoneDown = new FakeStore { Read = RecipientNoticeStopRead.ForContact(Contact()) };
        await Assert.ThrowsAsync<RecipientNoticePanelUnavailableException>(() =>
            Service(phoneDown, PhoneOpener(OpenedRecipientPhone.Unavailable), EmailOpener(OpenedRecipientEmail.Opened(Email)))
                .StopAsync(Command(), CancellationToken.None));
        Assert.Empty(phoneDown.Writes);

        var emailDown = new FakeStore { Read = RecipientNoticeStopRead.ForContact(Contact()) };
        await Assert.ThrowsAsync<RecipientNoticePanelUnavailableException>(() =>
            Service(emailDown, PhoneOpener(OpenedRecipientPhone.Opened(Phone)), EmailOpener(OpenedRecipientEmail.Unavailable))
                .StopAsync(Command(), CancellationToken.None));
        Assert.Empty(emailDown.Writes);

        var noKey = new FakeStore { Read = RecipientNoticeStopRead.ForContact(Contact()) };
        var phones = PhoneOpener(OpenedRecipientPhone.Opened(Phone));
        await Assert.ThrowsAsync<RecipientNoticePanelUnavailableException>(() =>
            Service(noKey, phones, EmailOpener(OpenedRecipientEmail.Opened(Email)), lookupAvailable: false)
                .StopAsync(Command(), CancellationToken.None));
        Assert.Empty(noKey.Writes);
        Assert.Equal(0, phones.Calls);
    }

    [Fact]
    public async Task A_missing_order_or_a_malformed_key_is_refused_before_anything_is_opened()
    {
        var missing = new FakeStore { Read = null };
        var phones = PhoneOpener(OpenedRecipientPhone.Opened(Phone));
        await Assert.ThrowsAsync<RecipientNoticePanelNotFoundException>(() =>
            Service(missing, phones, EmailOpener(OpenedRecipientEmail.Opened(Email))).StopAsync(Command(), CancellationToken.None));

        var malformed = new FakeStore { Read = RecipientNoticeStopRead.ForContact(Contact()) };
        var conflict = await Assert.ThrowsAsync<RecipientNoticeStopConflictException>(() =>
            Service(malformed, phones, EmailOpener(OpenedRecipientEmail.Opened(Email)))
                .StopAsync(Command() with { IdempotencyKey = "short" }, CancellationToken.None));
        Assert.Equal("INVALID_REQUEST", conflict.Code);
        Assert.Equal(0, malformed.Reads);
        Assert.Equal(0, phones.Calls);

        await Assert.ThrowsAsync<RecipientNoticePanelNotFoundException>(() =>
            Service(new FakeStore(), phones, EmailOpener(OpenedRecipientEmail.Opened(Email)))
                .ListAsync(new ListOrderRecipientNoticesQuery(Actor, Organization, Order), CancellationToken.None));
    }

    [Fact]
    public void The_request_identity_binds_the_organization_the_person_and_the_order()
    {
        var hash = RecipientNoticePanelService.RequestHash(Command());
        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, RecipientNoticePanelService.RequestHash(Command() with { IdempotencyKey = "another-key-0000000001" }));
        Assert.NotEqual(hash, RecipientNoticePanelService.RequestHash(Command() with { OrderId = Guid.NewGuid() }));
        Assert.NotEqual(hash, RecipientNoticePanelService.RequestHash(Command() with { ActorId = Guid.NewGuid() }));
        Assert.NotEqual(hash, RecipientNoticePanelService.RequestHash(Command() with { OrganizationId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task The_service_logs_the_lane_and_the_outcome_only()
    {
        var logs = new CapturingLogger();
        var store = new FakeStore { Read = RecipientNoticeStopRead.ForContact(Contact()) };
        var service = new RecipientNoticePanelService(
            store,
            Lookup(),
            new Services(PhoneOpener(OpenedRecipientPhone.Opened(Phone)), EmailOpener(OpenedRecipientEmail.Opened(Email))),
            new FixedTime(Now),
            logs);

        await service.StopAsync(Command(), CancellationToken.None);

        var line = Assert.Single(logs.Lines);
        Assert.Equal("Recipient notice panel processed with lane PANEL_STOP, outcome RECORDED.", line);
        foreach (var value in new[] { Phone, Email, Order.ToString("D"), Actor.ToString("D"), Command().IdempotencyKey })
        {
            Assert.DoesNotContain(value, line, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ Locations phone openers (T6 choices 11 and 12)

    [Fact]
    public async Task The_envelope_opener_opens_with_the_PII_key_and_purpose_and_normalizes_to_E164()
    {
        var envelope = new FakeEnvelope("667 123 4567");
        var opened = await new EnvelopeRecipientPhoneOpener(envelope).OpenAsync(Stored(), CancellationToken.None);

        Assert.Equal(RecipientPhoneOpenStatus.Opened, opened.Status);
        Assert.Equal("+526671234567", opened.Normalized);
        Assert.Equal(AzureKeyVaultLocationPiiProtector.PhonePurpose, envelope.Purpose);
        Assert.Equal("locations.phone", envelope.Purpose);
        Assert.Equal(new PiiBinding(Organization, Location), envelope.Binding);
        Assert.Equal("akv:pii/1", envelope.KeyVersion);

        Assert.Equal(
            RecipientPhoneOpenStatus.Unusable,
            (await new EnvelopeRecipientPhoneOpener(new FakeEnvelope("+1 555 0100")).OpenAsync(Stored(), CancellationToken.None)).Status);
        Assert.Equal(
            RecipientPhoneOpenStatus.Unusable,
            (await new EnvelopeRecipientPhoneOpener(new FakeEnvelope(new PiiCiphertextRejectedException())).OpenAsync(Stored(), CancellationToken.None)).Status);
        Assert.Equal(
            RecipientPhoneOpenStatus.Unavailable,
            (await new EnvelopeRecipientPhoneOpener(new FakeEnvelope(new TimeoutException())).OpenAsync(Stored(), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task The_synthetic_opener_never_decrypts_and_the_disabled_one_is_unavailable()
    {
        var synthetic = await new SyntheticRecipientPhoneOpener().OpenAsync(Stored(), CancellationToken.None);
        Assert.Equal(RecipientPhoneOpenStatus.Opened, synthetic.Status);
        Assert.Equal("+520000000002", synthetic.Normalized);
        Assert.Equal(
            RecipientPhoneOpenStatus.Unavailable,
            (await new DisabledRecipientPhoneOpener().OpenAsync(Stored(), CancellationToken.None)).Status);
        // The synthetic placeholder is a number the opt-out hash accepts.
        Assert.True(RecipientContactValue.TryNormalize(RecipientContactChannel.WhatsApp, synthetic.Normalized, out var normalized));
        Assert.Equal("+520000000002", normalized);
    }

    // ------------------------------------------------------------------ helpers

    private static RecipientNoticeStoredRow Row(
        string templateKey = "recipient.tracking-link",
        string channel = "WHATSAPP",
        string status = "FAILED",
        int attempts = 5,
        string? lastCode = null) =>
        new(Guid.Parse("0a1b2c3d-1111-4222-8333-944445555666"), templateKey, channel, status, attempts, lastCode, null, null, Now);

    private static StopOrderRecipientNoticesCommand Command() => new(Actor, Organization, Order, "ntf007-unit-key-000001");

    private static RecipientNoticeStopContact Contact(bool phone = true, bool email = true) =>
        new(Organization, Location, "akv:pii/1", phone ? [1, 2, 3] : null, email ? [4, 5, 6] : null);

    private static StoredRecipientPhone Stored() => new(Organization, Location, "akv:pii/1", [1, 2, 3]);

    private static HmacRecipientContactLookup Lookup(bool available = true) =>
        new(available
            ? new ContactLookupKeyRing(Options.Create(new ContactLookupOptions
            {
                CurrentKeyVersion = 2,
                Keys = new() { [1] = Convert.ToBase64String(PreviousLookupKey), [2] = Convert.ToBase64String(LookupKey) },
            }))
            : new ContactLookupKeyRing(Options.Create(new ContactLookupOptions())));

    private static RecipientNoticePanelService Service(
        FakeStore store,
        FakePhoneOpener phones,
        FakeEmailOpener emails,
        bool lookupAvailable = true) =>
        new(store, Lookup(lookupAvailable), new Services(phones, emails), new FixedTime(Now), NullLogger<RecipientNoticePanelService>.Instance);

    private static FakePhoneOpener PhoneOpener(OpenedRecipientPhone result) => new() { Result = result };

    private static FakeEmailOpener EmailOpener(OpenedRecipientEmail result) => new() { Result = result };

    private sealed class FakeStore : IRecipientNoticePanelStore
    {
        public RecipientNoticeStopRead? Read { get; init; }

        public int Reads { get; private set; }

        public List<RecipientNoticeStopWrite> Writes { get; } = [];

        public Task<RecipientNoticeListResult?> ListAsync(Guid actorId, Guid organizationId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<RecipientNoticeListResult?>(null);

        public Task<RecipientNoticeStopRead?> ReadStopAsync(
            StopOrderRecipientNoticesCommand command,
            byte[] requestHash,
            CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Read);
        }

        public Task<RecipientNoticeStopResult> RecordStopAsync(RecipientNoticeStopWrite write, CancellationToken cancellationToken)
        {
            Writes.Add(write);
            return Task.FromResult(new RecipientNoticeStopResult(
                write.Command.OrderId,
                write.OptOuts.Select(optOut => optOut.Channel).ToArray(),
                write.RecordedAt));
        }
    }

    private sealed class FakePhoneOpener : IRecipientPhoneOpener
    {
        public OpenedRecipientPhone Result { get; init; } = OpenedRecipientPhone.Unusable;

        public int Calls { get; private set; }

        public Task<OpenedRecipientPhone> OpenAsync(StoredRecipientPhone phone, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeEmailOpener : IRecipientEmailOpener
    {
        public OpenedRecipientEmail Result { get; init; } = OpenedRecipientEmail.Unusable;

        public int Calls { get; private set; }

        public Task<OpenedRecipientEmail> OpenAsync(StoredRecipientEmail email, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class Services(FakePhoneOpener phones, FakeEmailOpener emails) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IRecipientPhoneOpener) ? phones :
            serviceType == typeof(IRecipientEmailOpener) ? emails : null;
    }

    private sealed class FakeEnvelope : IPiiEnvelopeProtector
    {
        private readonly string? plaintext;
        private readonly Exception? failure;

        public FakeEnvelope(string plaintext) => this.plaintext = plaintext;

        public FakeEnvelope(Exception failure) => this.failure = failure;

        public PiiBinding? Binding { get; private set; }

        public string? Purpose { get; private set; }

        public string? KeyVersion { get; private set; }

        public Task<PiiProtectedBatch> ProtectAsync(PiiBinding binding, IReadOnlyList<PiiPlaintext> values, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The phone opener never protects.");

        public Task<string> UnprotectAsync(PiiBinding binding, string purpose, byte[] ciphertext, string keyVersion, CancellationToken cancellationToken)
        {
            Binding = binding;
            Purpose = purpose;
            KeyVersion = keyVersion;
            return failure is null ? Task.FromResult(plaintext!) : Task.FromException<string>(failure);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingLogger : ILogger<RecipientNoticePanelService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
