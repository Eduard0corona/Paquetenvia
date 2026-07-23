namespace Pricing.Domain;

public enum PricingTierSelectionFailure
{
    None,
    ClientAccountUnavailable,
    VolumePricingUnavailable,
    PrivateTariffUnavailable,
}

public sealed record ClientPricingProfile(bool IsActive, Guid? PrivateTariffId);

public sealed record PricingTierSelectionResult(
    PricingTierSelectionFailure Failure,
    PricingTier Tier,
    Guid? PrivateTariffId);

public sealed class PricingTierSelector
{
    public PricingTierSelectionResult Select(
        Guid? clientAccountId,
        ClientPricingProfile? account,
        TariffRule? privateRule)
    {
        if (clientAccountId is null)
        {
            return new PricingTierSelectionResult(PricingTierSelectionFailure.None, PricingTier.Occasional, null);
        }

        if (clientAccountId == Guid.Empty || account is null || !account.IsActive)
        {
            return new PricingTierSelectionResult(PricingTierSelectionFailure.ClientAccountUnavailable, default, null);
        }

        if (account.PrivateTariffId is null)
        {
            return new PricingTierSelectionResult(PricingTierSelectionFailure.VolumePricingUnavailable, default, null);
        }

        if (privateRule is null || privateRule.Id != account.PrivateTariffId)
        {
            return new PricingTierSelectionResult(PricingTierSelectionFailure.PrivateTariffUnavailable, default, null);
        }

        return new PricingTierSelectionResult(
            PricingTierSelectionFailure.None,
            privateRule.PricingTier,
            privateRule.Id);
    }
}

public sealed record PricingLocation(Guid CityId, Guid ServiceAreaId, Guid? OperatingZoneId);

public sealed record QuoteGeographyResult(
    bool IsSameCity,
    Guid CityId,
    Guid? SharedServiceAreaId,
    Guid? SharedOperatingZoneId);

public static class QuoteGeographyPolicy
{
    public static QuoteGeographyResult Resolve(PricingLocation origin, PricingLocation destination)
    {
        if (origin.CityId == Guid.Empty || destination.CityId == Guid.Empty ||
            origin.ServiceAreaId == Guid.Empty || destination.ServiceAreaId == Guid.Empty)
        {
            throw new ArgumentException("Location geography is incomplete.");
        }

        if (origin.CityId != destination.CityId)
        {
            return new QuoteGeographyResult(false, Guid.Empty, null, null);
        }

        var sharedArea = origin.ServiceAreaId == destination.ServiceAreaId ? origin.ServiceAreaId : (Guid?)null;
        var sharedZone = sharedArea is not null &&
            origin.OperatingZoneId is { } originZone && originZone == destination.OperatingZoneId
                ? originZone
                : (Guid?)null;
        return new QuoteGeographyResult(true, origin.CityId, sharedArea, sharedZone);
    }
}

public sealed record PricingPackage(
    string Description,
    int WeightGrams,
    long DeclaredValueCents,
    int? LengthMm,
    int? WidthMm,
    int? HeightMm);

public static class PricingPackagePolicy
{
    public static bool IsValid(IReadOnlyList<PricingPackage>? packages) =>
        packages is { Count: > 0 } && packages.All(package =>
            package is not null &&
            !string.IsNullOrWhiteSpace(package.Description) && package.Description.Length <= 250 &&
            package.WeightGrams >= 1 && package.DeclaredValueCents >= 0 &&
            package.LengthMm is null or > 0 && package.WidthMm is null or > 0 && package.HeightMm is null or > 0);
}

public static class QuoteExpirationPolicy
{
    public static DateTimeOffset Calculate(
        DateTimeOffset createdAt,
        TimeSpan configuredLifetime,
        DateTimeOffset? ruleActiveTo)
    {
        if (configuredLifetime <= TimeSpan.Zero || configuredLifetime > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(configuredLifetime));
        }

        var configured = createdAt.Add(configuredLifetime);
        return ruleActiveTo is { } ruleExpiry && ruleExpiry < configured ? ruleExpiry : configured;
    }
}
