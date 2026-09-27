namespace Identity.Application.Authentication;

public enum IdentityProviderKind
{
    Disabled,
    Mock,

    /// <summary>Production OIDC provider (GATE-002). Browser sessions use the BFF cookie; AuthCenter only authenticates.</summary>
    AuthCenter,
}

public sealed class IdentityAuthenticationOptions
{
    public const string SectionName = "Authentication";

    public IdentityProviderKind Provider { get; set; } = IdentityProviderKind.Disabled;
}
