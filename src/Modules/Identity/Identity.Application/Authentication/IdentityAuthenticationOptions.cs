namespace Identity.Application.Authentication;

public enum IdentityProviderKind
{
    Disabled,
    Mock,
}

public sealed class IdentityAuthenticationOptions
{
    public const string SectionName = "Authentication";

    public IdentityProviderKind Provider { get; set; } = IdentityProviderKind.Disabled;
}
