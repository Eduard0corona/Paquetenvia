using Drivers.Application.Voice;
using Drivers.Domain.Voice;
using Drivers.Infrastructure;
using Drivers.Infrastructure.Voice;
using Locations.Infrastructure.Geocoding;
using Paqueteria.Application.Voice;
using Paqueteria.Infrastructure.Security.Pii;

namespace Paqueteria.UnitTests.Drivers;

/// <summary>VOICE-001-MASKED-CALLS-2026-10-11: who may be called, how often, and how the phones are handled.</summary>
public sealed class RecipientCallPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 15, 0, 0, TimeSpan.Zero);
    private static readonly RecipientCallLimits Limits = new(3, TimeSpan.FromMinutes(15), 20);

    [Theory]
    [InlineData("DELIVERING", true)]
    [InlineData("ASSIGNED", false)]
    [InlineData("AT_PICKUP", false)]
    [InlineData("PICKED_UP", false)]
    [InlineData("IN_TRANSIT", false)]
    [InlineData("FAILED_ATTEMPT", false)]
    [InlineData("RETURNING", false)]
    [InlineData("DELIVERED", false)]
    [InlineData("delivering", false)]
    [InlineData(null, false)]
    public void Only_a_delivering_order_can_be_called(string? status, bool callable) =>
        Assert.Equal(callable, RecipientCallPolicy.IsCallable(status));

    [Fact]
    public void The_driver_may_call_only_for_its_own_current_assignment() =>
        Assert.Equal(["ACCEPTED", "ACTIVE"], RecipientCallPolicy.CurrentAssignmentStatuses);

    [Fact]
    public void Requests_under_both_limits_fit_now()
    {
        Assert.Null(Limits.RetryAfter([], [], Now));
        Assert.Null(Limits.RetryAfter([Now.AddMinutes(-1), Now.AddMinutes(-2)], Ago(19), Now));
    }

    [Fact]
    public void The_per_order_limit_waits_until_the_oldest_counted_request_leaves_the_window()
    {
        // Newest first: the third newest request (10 minutes ago) leaves the 15-minute window in 5 minutes.
        DateTimeOffset[] order = [Now.AddMinutes(-1), Now.AddMinutes(-4), Now.AddMinutes(-10)];

        Assert.Equal(TimeSpan.FromMinutes(5), Limits.RetryAfter(order, order, Now));
    }

    [Fact]
    public void The_per_driver_hourly_limit_applies_across_orders_and_the_longest_wait_wins()
    {
        // Twenty requests, every two minutes: the twentieth newest (40 minutes ago) leaves the hour in 20 minutes.
        var driver = Ago(20);
        Assert.Equal(TimeSpan.FromMinutes(20), Limits.RetryAfter([], driver, Now));

        DateTimeOffset[] order = [Now.AddMinutes(-1), Now.AddMinutes(-2), Now.AddMinutes(-14)];
        Assert.Equal(TimeSpan.FromMinutes(1), Limits.RetryAfter(order, order, Now));
        Assert.Equal(TimeSpan.FromMinutes(20), Limits.RetryAfter(order, driver, Now));
    }

    [Fact]
    public void The_wait_is_never_shorter_than_one_second()
    {
        DateTimeOffset[] order = [Now.AddMinutes(-1), Now.AddMinutes(-2), Now.AddMinutes(-15).AddMilliseconds(1)];

        Assert.Equal(TimeSpan.FromSeconds(1), Limits.RetryAfter(order, order, Now));
    }

    [Theory]
    [InlineData("5511112222", "5511112222")]
    [InlineData("+52 55 1111 2222", "5511112222")]
    [InlineData("33-1234-5678", "3312345678")]
    [InlineData("+523312345678", "3312345678")]
    public void A_driver_phone_is_ten_dialable_mexican_digits(string value, string expected)
    {
        Assert.True(DriverPhonePolicy.TryNormalize(value, out var digits));
        Assert.Equal(expected, digits);
    }

    [Theory]
    [InlineData("0511112222")]
    [InlineData("1511112222")]
    [InlineData("551111222")]
    [InlineData("55111122223")]
    [InlineData("+15511112222")]
    [InlineData("+52 0000000001")]
    [InlineData("55 1111 2222 ext 3")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? value)
    {
        Assert.False(DriverPhonePolicy.TryNormalize(value, out var digits));
        Assert.Equal(string.Empty, digits);
    }

    [Fact]
    public void Commands_and_protected_phones_never_print_a_number()
    {
        var command = new RegisterDriverPhoneCommand(Guid.NewGuid(), Guid.NewGuid(), "5511112222", DriverPhoneConsent.CurrentVersion, "req");
        Assert.DoesNotContain("5511112222", command.ToString(), StringComparison.Ordinal);
        Assert.Contains(DriverPhoneConsent.CurrentVersion, command.ToString(), StringComparison.Ordinal);

        var phones = new RecipientCallProtectedPhones(
            Guid.NewGuid(), Guid.NewGuid(), [1, 2, 3], "v1", Guid.NewGuid(), Guid.NewGuid(), [4, 5, 6], "v1");
        Assert.Equal("RecipientCallProtectedPhones { [redacted] }", phones.ToString());
    }

    [Fact]
    public void The_recipient_phone_is_read_with_the_locations_purpose()
    {
        Assert.Equal(AzureKeyVaultLocationPiiProtector.PhonePurpose, VoicePiiPurposes.LocationPhone);
        Assert.Equal("drivers.phone", VoicePiiPurposes.DriverPhone);
        Assert.NotEqual(VoicePiiPurposes.DriverPhone, VoicePiiPurposes.LocationPhone);
    }

    [Fact]
    public void Default_limits_are_valid_and_bounded()
    {
        var options = new RecipientCallOptions();
        Assert.True(options.IsValid());
        Assert.Equal(3, options.MaximumPerOrder);
        Assert.Equal(15, options.OrderWindowMinutes);
        Assert.Equal(20, options.MaximumPerDriverPerHour);
        Assert.False(new RecipientCallOptions { MaximumPerOrder = 0 }.IsValid());
        Assert.False(new RecipientCallOptions { MaximumPerDriverPerHour = 1000 }.IsValid());
    }

    // ------------------------------------------------------------------ phone resolution

    [Fact]
    public async Task Live_resolution_decrypts_both_phones_with_their_own_binding_and_purpose()
    {
        var envelope = new FakeEnvelope();
        var phones = Phones(envelope, "5511112222", "+52 33 1234 5678");

        var (driver, recipient) = await new EnvelopeRecipientCallPhoneResolver(envelope).ResolveAsync(phones, default);

        Assert.Equal("+525511112222", driver.E164);
        Assert.Equal("+523312345678", recipient.E164);
        Assert.Equal(
            [
                (new PiiBinding(phones.DriverOrganizationId, phones.DriverId), VoicePiiPurposes.DriverPhone),
                (new PiiBinding(phones.LocationOwnerOrganizationId, phones.LocationId), VoicePiiPurposes.LocationPhone),
            ],
            envelope.Unprotected);
    }

    [Theory]
    [InlineData("0511112222", "3312345678", RecipientCallPhoneProblem.DriverPhoneNotDialable)]
    [InlineData("5511112222", "0312345678", RecipientCallPhoneProblem.RecipientPhoneUnusable)]
    [InlineData("5511112222", "not a phone", RecipientCallPhoneProblem.RecipientPhoneUnusable)]
    [InlineData("5511112222", "+52 55 1111 2222", RecipientCallPhoneProblem.RecipientPhoneUnusable)]
    public async Task Unusable_numbers_are_refused_by_problem_without_the_value(
        string driverPhone,
        string recipientPhone,
        RecipientCallPhoneProblem expected)
    {
        var envelope = new FakeEnvelope();

        var failure = await Assert.ThrowsAsync<RecipientCallPhoneException>(() =>
            new EnvelopeRecipientCallPhoneResolver(envelope).ResolveAsync(Phones(envelope, driverPhone, recipientPhone), default));

        Assert.Equal(expected, failure.Problem);
        Assert.DoesNotContain(driverPhone, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(recipientPhone, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_ciphertext_and_an_unreachable_key_are_told_apart()
    {
        var rejected = new FakeEnvelope { Failure = new PiiCiphertextRejectedException() };
        Assert.Equal(
            RecipientCallPhoneProblem.DriverPhoneUnreadable,
            (await Assert.ThrowsAsync<RecipientCallPhoneException>(() =>
                new EnvelopeRecipientCallPhoneResolver(rejected).ResolveAsync(Phones(rejected, "5511112222", "3312345678"), default))).Problem);

        var down = new FakeEnvelope { Failure = new PiiProtectionUnavailableException() };
        Assert.Equal(
            RecipientCallPhoneProblem.ProtectionUnavailable,
            (await Assert.ThrowsAsync<RecipientCallPhoneException>(() =>
                new EnvelopeRecipientCallPhoneResolver(down).ResolveAsync(Phones(down, "5511112222", "3312345678"), default))).Problem);
    }

    [Fact]
    public async Task Synthetic_resolution_never_decrypts_and_returns_non_dialable_placeholders()
    {
        var phones = new RecipientCallProtectedPhones(
            Guid.NewGuid(), Guid.NewGuid(), [1], "voice001-synthetic-v1", Guid.NewGuid(), Guid.NewGuid(), [2], "mock");

        var (driver, recipient) = await new SyntheticRecipientCallPhoneResolver().ResolveAsync(phones, default);

        Assert.Equal(SyntheticRecipientCallPhoneResolver.DriverPlaceholder, driver.E164);
        Assert.Equal(SyntheticRecipientCallPhoneResolver.RecipientPlaceholder, recipient.E164);
        Assert.False(driver.IsDialable);
        Assert.False(recipient.IsDialable);
    }

    [Fact]
    public async Task Synthetic_protection_is_one_way_and_bound_to_the_row()
    {
        var protector = new SyntheticDriverPhoneProtector();
        var organization = Guid.NewGuid();
        var first = await protector.ProtectAsync(organization, Guid.NewGuid(), "5511112222", default);
        var second = await protector.ProtectAsync(organization, Guid.NewGuid(), "5511112222", default);

        Assert.Equal(SyntheticDriverPhoneProtector.KeyVersion, first.KeyVersion);
        Assert.Equal(32, first.Ciphertext.Length);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
        Assert.DoesNotContain("5511112222", Convert.ToHexString(first.Ciphertext), StringComparison.Ordinal);
    }

    private static IReadOnlyList<DateTimeOffset> Ago(int count) =>
        Enumerable.Range(1, count).Select(minutes => Now.AddMinutes(-minutes * 2)).ToArray();

    private static RecipientCallProtectedPhones Phones(FakeEnvelope envelope, string driver, string recipient)
    {
        var phones = new RecipientCallProtectedPhones(
            Guid.NewGuid(), Guid.NewGuid(), [1], "kv1", Guid.NewGuid(), Guid.NewGuid(), [2], "kv1");
        envelope.Values[(new PiiBinding(phones.DriverOrganizationId, phones.DriverId), VoicePiiPurposes.DriverPhone)] = driver;
        envelope.Values[(new PiiBinding(phones.LocationOwnerOrganizationId, phones.LocationId), VoicePiiPurposes.LocationPhone)] = recipient;
        return phones;
    }

    private sealed class FakeEnvelope : IPiiEnvelopeProtector
    {
        public Dictionary<(PiiBinding, string), string> Values { get; } = [];

        public List<(PiiBinding, string)> Unprotected { get; } = [];

        public Exception? Failure { get; init; }

        public Task<PiiProtectedBatch> ProtectAsync(
            PiiBinding binding,
            IReadOnlyList<PiiPlaintext> values,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Resolution never protects.");

        public Task<string> UnprotectAsync(
            PiiBinding binding,
            string purpose,
            byte[] ciphertext,
            string keyVersion,
            CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                return Task.FromException<string>(Failure);
            }

            Unprotected.Add((binding, purpose));
            return Task.FromResult(Values[(binding, purpose)]);
        }
    }
}
