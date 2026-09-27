namespace Identity.Application.Authentication;

/// <summary>
/// The validated external identity. <paramref name="EmailVerified"/> is true only when the identity
/// provider asserted a verified email (AUTH-EMAIL-VERIFIED-REQUIRED); it never grants authorization.
/// </summary>
public sealed record ExternalIdentity(string Subject, bool MfaSatisfied, bool EmailVerified = false);

public sealed class IdentityAuthenticationResult
{
    private IdentityAuthenticationResult(bool isValid, ExternalIdentity? identity)
    {
        IsValid = isValid;
        Identity = identity;
    }

    public bool IsValid { get; }

    public ExternalIdentity? Identity { get; }

    public static IdentityAuthenticationResult Invalid { get; } = new(false, null);

    public static IdentityAuthenticationResult Success(ExternalIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new IdentityAuthenticationResult(true, identity);
    }
}
