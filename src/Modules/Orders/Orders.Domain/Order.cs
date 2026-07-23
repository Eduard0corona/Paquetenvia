namespace Orders.Domain;

public sealed class Order
{
    private Order()
    {
    }

    private Order(
        Guid id,
        string publicId,
        Guid quoteId,
        Guid ownerOrganizationId,
        Guid? clientAccountId,
        Guid cityId,
        Guid? serviceAreaId,
        Guid originLocationId,
        Guid destinationLocationId,
        string serviceType,
        string pricingTier,
        bool consolidatedRoute,
        PayerType payerType,
        long subtotalCents,
        long discountCents,
        long taxCents,
        long totalCents,
        long minimumTotalCentsSnapshot,
        string currency,
        string pricingPolicyVersion,
        string packageSnapshot,
        string? financialOverride,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || quoteId == Guid.Empty || ownerOrganizationId == Guid.Empty ||
            cityId == Guid.Empty || originLocationId == Guid.Empty || destinationLocationId == Guid.Empty ||
            !OrderPublicIdPolicy.IsValid(publicId) || string.IsNullOrWhiteSpace(serviceType) ||
            string.IsNullOrWhiteSpace(pricingTier) || string.IsNullOrWhiteSpace(pricingPolicyVersion) ||
            string.IsNullOrWhiteSpace(packageSnapshot) || currency != "MXN" ||
            subtotalCents < 0 || discountCents < 0 || taxCents < 0 || totalCents < 0 ||
            minimumTotalCentsSnapshot < 0 ||
            totalCents != checked(subtotalCents - discountCents + taxCents))
        {
            throw new ArgumentException("The order snapshot is invalid.");
        }

        Id = id;
        PublicId = publicId;
        QuoteId = quoteId;
        OwnerOrganizationId = ownerOrganizationId;
        OperatorOrganizationId = null;
        ClientAccountId = clientAccountId;
        CityId = cityId;
        ServiceAreaId = serviceAreaId;
        OriginLocationId = originLocationId;
        DestinationLocationId = destinationLocationId;
        ServiceType = serviceType;
        PricingTier = pricingTier;
        ConsolidatedRoute = consolidatedRoute;
        PayerType = payerType;
        Status = OrderStatus.Draft;
        SubtotalCents = subtotalCents;
        DiscountCents = discountCents;
        TaxCents = taxCents;
        TotalCents = totalCents;
        MinimumTotalCentsSnapshot = minimumTotalCentsSnapshot;
        Currency = currency;
        PricingPolicyVersion = pricingPolicyVersion;
        PackageSnapshot = packageSnapshot;
        FinancialOverride = financialOverride;
        CodExpectedCents = 0;
        Version = 1;
        ClaimWindowEndsAt = null;
        FinalizedAt = null;
        ArchivedAt = null;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string PublicId { get; private set; } = string.Empty;
    public Guid QuoteId { get; private set; }
    public Guid OwnerOrganizationId { get; private set; }
    public Guid? OperatorOrganizationId { get; private set; }
    public Guid? ClientAccountId { get; private set; }
    public Guid CityId { get; private set; }
    public Guid? ServiceAreaId { get; private set; }
    public Guid OriginLocationId { get; private set; }
    public Guid DestinationLocationId { get; private set; }
    public string ServiceType { get; private set; } = string.Empty;
    public string PricingTier { get; private set; } = string.Empty;
    public bool ConsolidatedRoute { get; private set; }
    public PayerType PayerType { get; private set; }
    public OrderStatus Status { get; private set; }
    public long SubtotalCents { get; private set; }
    public long DiscountCents { get; private set; }
    public long TaxCents { get; private set; }
    public long TotalCents { get; private set; }
    public long MinimumTotalCentsSnapshot { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public string PricingPolicyVersion { get; private set; } = string.Empty;
    public string PackageSnapshot { get; private set; } = string.Empty;
    public string? FinancialOverride { get; private set; }
    public long CodExpectedCents { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset? ClaimWindowEndsAt { get; private set; }
    public DateTimeOffset? FinalizedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Order Create(
        Guid id,
        string publicId,
        Guid quoteId,
        Guid ownerOrganizationId,
        Guid? clientAccountId,
        Guid cityId,
        Guid? serviceAreaId,
        Guid originLocationId,
        Guid destinationLocationId,
        string serviceType,
        string pricingTier,
        bool consolidatedRoute,
        PayerType payerType,
        long subtotalCents,
        long discountCents,
        long taxCents,
        long totalCents,
        long minimumTotalCentsSnapshot,
        string currency,
        string pricingPolicyVersion,
        string packageSnapshot,
        string? financialOverride,
        DateTimeOffset createdAt) =>
        new(
            id,
            publicId,
            quoteId,
            ownerOrganizationId,
            clientAccountId,
            cityId,
            serviceAreaId,
            originLocationId,
            destinationLocationId,
            serviceType,
            pricingTier,
            consolidatedRoute,
            payerType,
            subtotalCents,
            discountCents,
            taxCents,
            totalCents,
            minimumTotalCentsSnapshot,
            currency,
            pricingPolicyVersion,
            packageSnapshot,
            financialOverride,
            createdAt);
}
