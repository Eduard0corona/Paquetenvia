namespace Pricing.Infrastructure;

public enum PricingProviderKind
{
    Disabled,
    PostgreSql,
}

public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    public PricingProviderKind Provider { get; set; }
    public int QuoteLifetimeMinutes { get; set; } = 30;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public string PricingPolicyVersion { get; set; } = "PRC-001-v1";
}
