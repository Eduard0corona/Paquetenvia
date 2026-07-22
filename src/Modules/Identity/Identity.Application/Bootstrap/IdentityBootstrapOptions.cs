namespace Identity.Application.Bootstrap;

public enum IdentityBootstrapProviderKind
{
    Disabled,
    Mock,
    PostgreSql,
}

public sealed class IdentityBootstrapOptions
{
    public const string SectionName = "IdentityBootstrap";

    public IdentityBootstrapProviderKind Provider { get; set; } = IdentityBootstrapProviderKind.Disabled;

    public int CommandTimeoutSeconds { get; set; } = 5;
}
