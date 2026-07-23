namespace Pricing.Domain;

public sealed class Quote
{
    private Quote()
    {
        RuleIds = [];
        Currency = Money.Currency;
        PricingPolicyVersion = string.Empty;
        RequestSnapshotRedacted = "{}";
        PackageSnapshot = "[]";
        Breakdown = "[]";
        InputHash = [];
    }

    public static Quote Create(
        Guid id,
        Guid ownerOrganizationId,
        Guid? clientAccountId,
        Guid cityId,
        Guid? serviceAreaId,
        Guid originLocationId,
        Guid destinationLocationId,
        ServiceType serviceType,
        PricingTier pricingTier,
        bool consolidatedRoute,
        TariffEvaluationResult evaluation,
        string pricingPolicyVersion,
        Guid[] ruleIds,
        string requestSnapshotRedacted,
        string packageSnapshot,
        string breakdown,
        byte[] inputHash,
        DateTimeOffset expiresAt,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || ownerOrganizationId == Guid.Empty || cityId == Guid.Empty ||
            originLocationId == Guid.Empty || destinationLocationId == Guid.Empty ||
            evaluation.Failure != TariffEvaluationFailure.None || evaluation.Rule is null ||
            string.IsNullOrWhiteSpace(pricingPolicyVersion) || ruleIds.Length != 1 ||
            inputHash.Length != 32 || expiresAt <= createdAt)
        {
            throw new ArgumentException("The quote aggregate is invalid.");
        }

        if (TariffRuleEvaluator.RequiresConsolidatedRoute(pricingTier) && !consolidatedRoute)
        {
            throw new ArgumentException("The selected pricing tier requires a consolidated route.");
        }

        return new Quote
        {
            Id = id,
            OwnerOrganizationId = ownerOrganizationId,
            ClientAccountId = clientAccountId,
            CityId = cityId,
            ServiceAreaId = serviceAreaId,
            OriginLocationId = originLocationId,
            DestinationLocationId = destinationLocationId,
            ServiceType = serviceType,
            PricingTier = pricingTier,
            ConsolidatedRoute = consolidatedRoute,
            SubtotalCents = evaluation.Subtotal.AmountCents,
            DiscountCents = evaluation.Discount.AmountCents,
            TaxCents = evaluation.Tax.AmountCents,
            TotalCents = evaluation.Total.AmountCents,
            MinimumTotalCentsSnapshot = evaluation.MinimumTotal.AmountCents,
            Currency = Money.Currency,
            PricingPolicyVersion = pricingPolicyVersion,
            RuleIds = ruleIds.ToArray(),
            RequestSnapshotRedacted = requestSnapshotRedacted,
            PackageSnapshot = packageSnapshot,
            Breakdown = breakdown,
            InputHash = inputHash.ToArray(),
            FinancialOverride = null,
            Status = QuoteStatus.Active,
            ExpiresAt = expiresAt,
            CreatedAt = createdAt,
        };
    }

    public Guid Id { get; private set; }
    public Guid OwnerOrganizationId { get; private set; }
    public Guid? ClientAccountId { get; private set; }
    public Guid CityId { get; private set; }
    public Guid? ServiceAreaId { get; private set; }
    public Guid OriginLocationId { get; private set; }
    public Guid DestinationLocationId { get; private set; }
    public ServiceType ServiceType { get; private set; }
    public PricingTier PricingTier { get; private set; }
    public bool ConsolidatedRoute { get; private set; }
    public long SubtotalCents { get; private set; }
    public long DiscountCents { get; private set; }
    public long TaxCents { get; private set; }
    public long TotalCents { get; private set; }
    public long MinimumTotalCentsSnapshot { get; private set; }
    public string Currency { get; private set; }
    public string PricingPolicyVersion { get; private set; }
    public Guid[] RuleIds { get; private set; }
    public string RequestSnapshotRedacted { get; private set; }
    public string PackageSnapshot { get; private set; }
    public byte[]? PiiSnapshotCiphertext { get; private set; }
    public string? PiiKeyVersion { get; private set; }
    public string Breakdown { get; private set; }
    public byte[] InputHash { get; private set; }
    public string? FinancialOverride { get; private set; }
    public QuoteStatus Status { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
