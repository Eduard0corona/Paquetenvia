namespace Pricing.Domain;

public enum ServiceType { SameDay, Urgent, ScheduledRoute }
public enum PricingTier { Occasional, Business1To49, Business50To199, Business200To499, Business500Plus, Custom }
public enum TaxMode { PlusVat, VatIncluded, Exempt }
public enum TariffRuleStatus { Active, Inactive }
public enum QuoteStatus { Active, Used, Expired, Revoked }

public static class PricingContractValues
{
    public static string ToContractValue(this ServiceType value) => value switch
    {
        ServiceType.SameDay => "SAME_DAY",
        ServiceType.Urgent => "URGENT",
        ServiceType.ScheduledRoute => "SCHEDULED_ROUTE",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this PricingTier value) => value switch
    {
        PricingTier.Occasional => "OCCASIONAL",
        PricingTier.Business1To49 => "BUSINESS_1_49",
        PricingTier.Business50To199 => "BUSINESS_50_199",
        PricingTier.Business200To499 => "BUSINESS_200_499",
        PricingTier.Business500Plus => "BUSINESS_500_PLUS",
        PricingTier.Custom => "CUSTOM",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this TaxMode value) => value switch
    {
        TaxMode.PlusVat => "PLUS_VAT",
        TaxMode.VatIncluded => "VAT_INCLUDED",
        TaxMode.Exempt => "EXEMPT",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this TariffRuleStatus value) => value switch
    {
        TariffRuleStatus.Active => "ACTIVE",
        TariffRuleStatus.Inactive => "INACTIVE",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this QuoteStatus value) => value switch
    {
        QuoteStatus.Active => "ACTIVE",
        QuoteStatus.Used => "USED",
        QuoteStatus.Expired => "EXPIRED",
        QuoteStatus.Revoked => "REVOKED",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
