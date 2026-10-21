using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Paqueteria.Application;
using Paqueteria.Application.Contacts;
using Paqueteria.Application.Auditing;
using Pricing.Application.Quotes;
using Pricing.Infrastructure;
using Pricing.Infrastructure.Quotes;
using Locations.Application.Locations;

namespace Paqueteria.UnitTests.Pricing;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-002): <see cref="RecipientEmailPolicy"/>, the quote input hash and
/// Idempotency-Key fingerprint stability vectors, the destination-only rule enforced before any effect, and the
/// redacted <c>ToString</c> of every type that carries the e-mail.
/// </summary>
public sealed class Ntf002RecipientEmailTests
{
    private static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ActorId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private const string Email = "destinatario@example.invalid";

    /// <summary>The fingerprint the request below had before NTF-002 (pinned by LOW-PRICE-MANUAL-AUTH-2026-10-02 too).</summary>
    private const string HashWithoutEmail = "9B14DDC5F24D9322C66CCD4E01FC77BB03AFFD9562581B3C4280FED631955C0B";

    /// <summary>
    /// The fingerprint of the same request with the destination e-mail: SHA-256 of the same canonical JSON with
    /// <c>"email":"destinatario@example.invalid"</c> as the last member of <c>destination</c> (derived independently of
    /// the writer when this vector was pinned).
    /// </summary>
    private const string HashWithEmail = "14294AEB5EC26C8AF4CC82AA0C8C677041E1ED294CDF530B65BB0B5F42A1B815";

    [Theory]
    [InlineData("destinatario@example.invalid", "destinatario@example.invalid")]
    [InlineData("  Destinatario@Example.INVALID\t", "Destinatario@example.invalid")]
    [InlineData("\r\n a.b+c@sub.example.mx \n", "a.b+c@sub.example.mx")]
    [InlineData("x@a.b", "x@a.b")]
    [InlineData("o'brien!#$%&*+-/=?^_`{|}~@example.com", "o'brien!#$%&*+-/=?^_`{|}~@example.com")]
    [InlineData("a@b..c", "a@b..c")]
    public void Valid_emails_are_trimmed_and_their_domain_lower_cased(string value, string expected)
    {
        Assert.True(RecipientEmailPolicy.TryNormalize(value, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.True(RecipientEmailPolicy.IsValid(value));
        Assert.True(RecipientEmailPolicy.IsNormalized(normalized));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("destinatario")]
    [InlineData("@example.com")]
    [InlineData("destinatario@")]
    [InlineData("destinatario@example")]
    [InlineData("destinatario@.com")]
    [InlineData("destinatario@example.")]
    [InlineData("destinatario@@example.com")]
    [InlineData("des@tinatario@example.com")]
    [InlineData("desti natario@example.com")]
    [InlineData("destinatario@exam ple.com")]
    [InlineData("destinat\u00e1rio@example.com")]
    [InlineData("destinatario@ex\u00e1mple.com")]
    [InlineData("\u00a0destinatario@example.com")]
    [InlineData("destinatario@example.com\u00a0")]
    [InlineData("destinatario@example.com\u0000")]
    [InlineData("destinatario\u007f@example.com")]
    [InlineData("destinatario@example.com\u2028")]
    public void Invalid_emails_are_refused(string value)
    {
        Assert.False(RecipientEmailPolicy.TryNormalize(value, out var normalized));
        Assert.Equal(string.Empty, normalized);
        Assert.False(RecipientEmailPolicy.IsValid(value));
    }

    [Fact]
    public void The_raw_text_is_bounded_to_254_characters_and_null_is_not_an_email()
    {
        var atLimit = new string('a', 254 - "@example.com".Length) + "@example.com";
        Assert.Equal(254, atLimit.Length);
        Assert.True(RecipientEmailPolicy.IsValid(atLimit));
        Assert.False(RecipientEmailPolicy.IsValid("a" + atLimit));
        Assert.False(RecipientEmailPolicy.IsValid(" " + atLimit));
        Assert.False(RecipientEmailPolicy.IsValid(null));
        Assert.False(RecipientEmailPolicy.IsNormalized(" " + Email));
        Assert.False(RecipientEmailPolicy.IsNormalized("destinatario@Example.invalid"));
    }

    [Fact]
    public void Without_an_email_the_input_hash_and_fingerprint_are_byte_identical_to_before()
    {
        var command = Command();
        Assert.Equal(HashWithoutEmail, Convert.ToHexString(PostgreSqlQuoteService.ComputeInputHash(command)));
        // An explicit null is the absent field.
        Assert.Equal(
            HashWithoutEmail,
            Convert.ToHexString(PostgreSqlQuoteService.ComputeInputHash(command with
            {
                Destination = command.Destination with { Email = null },
            })));
    }

    [Fact]
    public void With_an_email_the_hash_changes_and_is_pinned()
    {
        var command = Command();
        var withEmail = command with { Destination = command.Destination with { Email = Email } };
        var hash = Convert.ToHexString(PostgreSqlQuoteService.ComputeInputHash(withEmail));

        Assert.NotEqual(HashWithoutEmail, hash);
        Assert.Equal(HashWithEmail, hash);
        Assert.NotEqual(hash, Convert.ToHexString(PostgreSqlQuoteService.ComputeInputHash(command with
        {
            Destination = command.Destination with { Email = "otro@example.invalid" },
        })));
        Assert.Equal(hash, Convert.ToHexString(PostgreSqlQuoteService.ComputeInputHash(withEmail with { RequestId = "other" })));
    }

    [Theory]
    [InlineData(true, "destinatario@example.invalid")]
    [InlineData(false, "destinatario")]
    [InlineData(false, "")]
    [InlineData(false, " ")]
    public async Task An_origin_email_or_an_invalid_destination_email_is_refused_before_any_effect(bool onOrigin, string email)
    {
        var resolver = new NeverCalledResolver();
        var service = new PostgreSqlQuoteService(
            null!,
            resolver,
            new AuditPayloadRedactor(),
            null!,
            Options.Create(new PricingOptions()),
            new FixedClock(),
            NullLogger<PostgreSqlQuoteService>.Instance);
        var command = Command();
        command = onOrigin
            ? command with { Origin = command.Origin with { Email = email } }
            : command with { Destination = command.Destination with { Email = email } };

        var refused = await Assert.ThrowsAsync<QuoteValidationException>(() => service.CreateAsync(command, default));

        Assert.Equal(QuoteValidationCode.InvalidRequest, refused.Code);
        Assert.Equal(0, resolver.Calls);
        if (!string.IsNullOrWhiteSpace(email))
        {
            Assert.DoesNotContain(email, refused.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Types_that_carry_the_email_never_print_it()
    {
        var destination = new QuoteAddressInput("Calle Sintetica 200", "Destino Sintetico", "6672222222", 24.81, -107.41, null, Email);
        var command = Command() with { Destination = destination };
        var location = new ResolveQuoteLocationCommand(
            ActorId,
            OrganizationId,
            "prcloc-0123456789abcdef",
            QuoteLocationRole.Destination,
            "Calle Sintetica 200",
            "Destino Sintetico",
            "6672222222",
            null,
            24.81,
            -107.41,
            "request-1",
            Email);

        foreach (var printed in new[]
                 {
                     destination.ToString(), command.ToString(), location.ToString(),
                 })
        {
            Assert.DoesNotContain(Email, printed, StringComparison.Ordinal);
            Assert.DoesNotContain("6672222222", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("Destino Sintetico", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("Calle Sintetica", printed, StringComparison.Ordinal);
        }
    }

    private static CreateQuoteCommand Command() => new(
        ActorId,
        OrganizationId,
        "synthetic-key-0001",
        null,
        new QuoteAddressInput("Synthetic origin 100", "Synthetic Sender", "+526671111111", 24.8, -107.4, null),
        new QuoteAddressInput("Synthetic destination 200", "Synthetic Receiver", "+526672222222", 24.81, -107.41, "Synthetic gate"),
        "SAME_DAY",
        false,
        [new QuotePackageInput("Synthetic parcel", 1000, 50_00, 100, 100, 100)],
        "request-1");

    private sealed class NeverCalledResolver : IQuoteLocationResolver
    {
        public int Calls { get; private set; }

        public Task<ResolveQuoteLocationResult> ResolveAsync(ResolveQuoteLocationCommand command, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("The resolver must not be reached.");
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    }
}
