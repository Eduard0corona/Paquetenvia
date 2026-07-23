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
    string? RequestId);

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
    IReadOnlyDictionary<string, object?> RequestSnapshotRedacted);

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
}

public sealed class QuoteValidationException(QuoteValidationCode code)
    : Exception("The quote request could not be processed.")
{
    public QuoteValidationCode Code { get; } = code;
}

public sealed class QuoteNotFoundException : Exception;

public sealed class QuoteServiceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
