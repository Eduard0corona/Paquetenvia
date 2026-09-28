namespace Pricing.Domain;

public sealed class TariffRule
{
    private TariffRule() { }

    public TariffRule(
        Guid id,
        Guid ownerOrganizationId,
        Guid cityId,
        Guid? serviceAreaId,
        Guid? operatingZoneId,
        PricingTier pricingTier,
        ServiceType serviceType,
        long amountCents,
        TaxMode taxMode,
        DateTimeOffset activeFrom,
        DateTimeOffset? activeTo,
        TariffRuleStatus status,
        string policyVersion)
    {
        if (id == Guid.Empty || ownerOrganizationId == Guid.Empty || cityId == Guid.Empty)
        {
            throw new ArgumentException("Tariff identifiers are required.");
        }

        if (activeTo is { } end && end <= activeFrom)
        {
            throw new ArgumentException("Tariff active_to must be later than active_from.");
        }

        if (!PricingPolicyVersionFormat.IsValid(policyVersion))
        {
            throw new ArgumentException("Tariff policy_version must match the pricing policy version format.");
        }

        _ = new Money(amountCents);
        Id = id;
        OwnerOrganizationId = ownerOrganizationId;
        CityId = cityId;
        ServiceAreaId = serviceAreaId;
        OperatingZoneId = operatingZoneId;
        PricingTier = pricingTier;
        ServiceType = serviceType;
        AmountCents = amountCents;
        TaxMode = taxMode;
        ActiveFrom = activeFrom;
        ActiveTo = activeTo;
        Status = status;
        PolicyVersion = policyVersion;
    }

    public Guid Id { get; private set; }
    public Guid OwnerOrganizationId { get; private set; }
    public Guid CityId { get; private set; }
    public Guid? ServiceAreaId { get; private set; }
    public Guid? OperatingZoneId { get; private set; }
    public PricingTier PricingTier { get; private set; }
    public ServiceType ServiceType { get; private set; }
    public long AmountCents { get; private set; }
    public TaxMode TaxMode { get; private set; }
    public DateTimeOffset ActiveFrom { get; private set; }
    public DateTimeOffset? ActiveTo { get; private set; }
    public TariffRuleStatus Status { get; private set; }

    /// <summary>
    /// PRC-POLICY-VERSION-PER-ORG: the version of the owning organization's pricing policy this rule
    /// belongs to. A quote freezes the version of the rule it selected. It is <c>null</c> only for a
    /// rule that predates the decision on an upgraded installation; such a rule is never quoted.
    /// </summary>
    public string? PolicyVersion { get; private set; }
}
