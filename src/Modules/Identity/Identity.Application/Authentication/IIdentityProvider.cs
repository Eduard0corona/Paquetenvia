namespace Identity.Application.Authentication;

public interface IIdentityProvider
{
    ValueTask<IdentityAuthenticationResult> AuthenticateAsync(
        string credential,
        CancellationToken cancellationToken);
}
