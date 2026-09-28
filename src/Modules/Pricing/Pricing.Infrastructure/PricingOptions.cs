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

    // PRC-POLICY-VERSION-PER-ORG: there is no global pricing policy version any more. Each
    // organization versions its own policy on pricing.tariff_rules.policy_version, and a quote
    // freezes the version of the rule it selected.
}
