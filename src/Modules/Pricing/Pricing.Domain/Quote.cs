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
        Guid[] ruleIds,
        string requestSnapshotRedacted,
        string packageSnapshot,
        string breakdown,
        byte[] inputHash,
        DateTimeOffset expiresAt,
        DateTimeOffset createdAt,
        LowPriceAuthorization? lowPriceAuthorization = null,
        string? financialOverride = null)
    {
        if (id == Guid.Empty || ownerOrganizationId == Guid.Empty || cityId == Guid.Empty ||
            originLocationId == Guid.Empty || destinationLocationId == Guid.Empty ||
            evaluation.Failure != TariffEvaluationFailure.None || evaluation.Rule is null ||
            !PricingPolicyVersionFormat.IsValid(evaluation.Rule.PolicyVersion) ||
            ruleIds.Length != 1 || ruleIds[0] != evaluation.Rule.Id ||
            inputHash.Length != 32 || expiresAt <= createdAt)
        {
            throw new ArgumentException("The quote aggregate is invalid.");
        }

        // LOW-PRICE-MANUAL-AUTH-2026-10-02: the authorization is stored as the AI-06 financial_override (actor_id,
        // reason, valid_until); financialOverride is its infrastructure serialization and travels with it.
        if ((lowPriceAuthorization is null) != (financialOverride is null))
        {
            throw new ArgumentException("A low price authorization and its financial override travel together.");
        }

        if (lowPriceAuthorization is not null)
        {
            if (!LowPriceGuardPolicy.RequiresAuthorization(pricingTier, consolidatedRoute, evaluation.Total.AmountCents))
            {
                throw new ArgumentException("The quote does not need a low price authorization.");
            }

            if (lowPriceAuthorization.ValidUntil != expiresAt)
            {
                throw new ArgumentException("A low price authorization is valid exactly until the quote expires.");
            }
        }
        else if (TariffRuleEvaluator.RequiresConsolidatedRoute(pricingTier) && !consolidatedRoute)
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
            // PRC-POLICY-VERSION-PER-ORG: frozen from the selected rule; the order copies it unchanged.
            PricingPolicyVersion = evaluation.Rule.PolicyVersion!,
            RuleIds = ruleIds.ToArray(),
            RequestSnapshotRedacted = requestSnapshotRedacted,
            PackageSnapshot = packageSnapshot,
            Breakdown = breakdown,
            InputHash = inputHash.ToArray(),
            FinancialOverride = financialOverride,
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
