namespace Finance.Infrastructure;

public enum FinanceProviderKind
{
    Disabled,
    PostgreSql,
}

public sealed class FinanceOptions
{
    public const string SectionName = "Finance";

    public FinanceProviderKind Provider { get; set; } = FinanceProviderKind.Disabled;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int IdempotencyLifetimeMinutes { get; set; } = 1440;
}
