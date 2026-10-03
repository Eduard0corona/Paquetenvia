namespace Pricing.Application.Quotes;

public sealed record QuoteAddressInput(
    string AddressText,
    string ContactName,
    string Phone,
    double? Lat,
    double? Lng,
    string? References);

public sealed record QuotePackageInput(
    string Description,
    int WeightGrams,
    long DeclaredValueCents,
    int? LengthMm,
    int? WidthMm,
    int? HeightMm);

public sealed record CreateQuoteCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid? ClientAccountId,
    QuoteAddressInput Origin,
    QuoteAddressInput Destination,
    string ServiceType,
    bool ConsolidatedRoute,
    IReadOnlyList<QuotePackageInput> Packages,
    string? RequestId,
    QuoteLowPriceAuthorizationInput? LowPriceAuthorization = null);

/// <summary>
/// LOW-PRICE-MANUAL-AUTH-2026-10-02: the optional manual authorization of a low price shipment. The endpoint only
/// forwards it after the caller proved the authorization capability (DISPATCHER, or PLATFORM_ADMIN with MFA); the
/// reason is trimmed, 1 to 200 characters, and must not contain personal data.
/// </summary>
public sealed record QuoteLowPriceAuthorizationInput(string Reason);

/// <summary>
/// The authorization a quote carries (its AI-06 <c>financial_override</c>): who authorized it, why, and until when
/// (the quote's own expiry). The endpoint decides which of these fields a caller may read.
/// </summary>
public sealed record QuoteLowPriceAuthorizationResult(Guid ActorId, string Reason, DateTimeOffset ValidUntil);

public sealed record MoneyResult(string Currency, long AmountCents);

public sealed record QuoteBreakdownLine(
    string LineType,
    Guid RuleId,
    long AmountCents,
    string PricingTier,
    string TaxMode);

public sealed record QuoteResult(
    Guid Id,
    MoneyResult Net,
    MoneyResult Tax,
    MoneyResult Total,
    IReadOnlyList<Guid> RuleIds,
    IReadOnlyList<QuoteBreakdownLine> Breakdown,
    DateTimeOffset ExpiresAt,
    Guid OriginLocationId,
    Guid DestinationLocationId,
    string ServiceType,
    bool ConsolidatedRoute,
    IReadOnlyList<QuotePackageInput> PackageSnapshot,
    Guid CityId,
    Guid? ServiceAreaId,
    string PricingTier,
    long MinimumTotalCentsSnapshot,
    string PricingPolicyVersion,
    string Status,
    IReadOnlyDictionary<string, object?> RequestSnapshotRedacted,
    QuoteLowPriceAuthorizationResult? LowPriceAuthorization = null);

public interface IQuoteService
{
    Task<QuoteResult> CreateAsync(CreateQuoteCommand command, CancellationToken cancellationToken);
    Task<QuoteResult> GetAsync(Guid actorId, Guid organizationId, Guid quoteId, CancellationToken cancellationToken);
}

public enum QuoteValidationCode
{
    InvalidRequest,
    CoordinatesRequired,
    OutsideCoverage,
    ExcludedZone,
    AmbiguousLocation,
    DifferentCities,
    ClientAccountUnavailable,
    ClientAccountRequiresVolumePricing,
    NoTariffRule,
    AmbiguousTariffRule,
    TaxModeBlocked,
    ConsolidatedRouteRequired,
    IdempotencyConflict,

    /// <summary>LOW-PRICE-MANUAL-AUTH-2026-10-02: the reason carries what looks like personal data or a secret.</summary>
    LowPriceAuthorizationReasonRejected,
}

public sealed class QuoteValidationException(QuoteValidationCode code)
    : Exception("The quote request could not be processed.")
{
    public QuoteValidationCode Code { get; } = code;
}

public sealed class QuoteNotFoundException : Exception;

/// <summary>
/// LOW-PRICE-MANUAL-AUTH-2026-10-02: a low price authorization was sent for a quote that does not need one (a
/// consolidated route, or a total above 52 MXN in a tier that does not require the route). It is the uniform 409;
/// an authorization is never stored silently.
/// </summary>
public sealed class QuoteConflictException : Exception
{
    public QuoteConflictException()
        : base("The quote request conflicts with the low price guard.")
    {
    }
}

public sealed class QuoteServiceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>AI05-INPUT-LIMITS as the endpoint sees it; the domain policy stays authoritative.</summary>
public static class QuoteInputLimits
{
    public const int MaximumPackages = Pricing.Domain.PricingPackagePolicy.MaximumPackages;
}

/// <summary>LOW-PRICE-MANUAL-AUTH-2026-10-02 reason rules as the endpoint sees them; the domain stays authoritative.</summary>
public static class QuoteLowPriceAuthorizationPolicy
{
    public const int MaximumReasonLength = Pricing.Domain.LowPriceAuthorization.MaximumReasonLength;

    /// <summary>The trimmed reason (1 to 200 characters, no control characters), or null when it is not valid.</summary>
    public static string? NormalizeReason(string? reason) => Pricing.Domain.LowPriceAuthorization.Normalize(reason);
}

/// <summary>
/// ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02: a quote contact phone is a 10-digit Mexican number. The only
/// separators tolerated are the ASCII space and the ASCII hyphen-minus, which are removed; what remains must be
/// exactly ten ASCII digits <c>0-9</c>. A country prefix (<c>+52</c> or <c>52</c>), parentheses, dots, other
/// whitespace, letters and non-ASCII digits are all rejected, so <c>+52 667 123 4567</c> is invalid while
/// <c>667 123 4567</c> and <c>667-123-4567</c> normalize to <c>6671234567</c>. The normalized value is the one
/// hashed, protected (ADP-001) and stored; neither the input nor the result is ever logged.
/// </summary>
public static class QuotePhonePolicy
{
    public const int DigitCount = 10;

    /// <summary>Upper bound on the raw text, so a separator-padded value cannot grow without limit.</summary>
    public const int MaximumInputLength = 32;

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumInputLength)
        {
            return false;
        }

        Span<char> digits = stackalloc char[DigitCount];
        var count = 0;
        foreach (var character in value)
        {
            if (character is ' ' or '-')
            {
                continue;
            }

            if (!char.IsAsciiDigit(character) || count == DigitCount)
            {
                return false;
            }

            digits[count++] = character;
        }

        if (count != DigitCount)
        {
            return false;
        }

        normalized = new string(digits);
        return true;
    }

    public static bool IsValid(string? value) => TryNormalize(value, out _);
}
