namespace Organizations.Application;

public enum TenancyProviderKind
{
    Disabled,
    PostgreSql,
}

public sealed class TenancyOptions
{
    public const string SectionName = "Tenancy";

    public TenancyProviderKind Provider { get; set; } = TenancyProviderKind.Disabled;

    public int CommandTimeoutSeconds { get; set; } = 5;
}
