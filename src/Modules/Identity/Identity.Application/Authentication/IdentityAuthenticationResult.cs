namespace Identity.Application.Authentication;

public sealed record ExternalIdentity(string Subject, bool MfaSatisfied);

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
