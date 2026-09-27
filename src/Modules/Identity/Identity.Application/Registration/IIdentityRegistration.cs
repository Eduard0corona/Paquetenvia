namespace Identity.Application.Registration;

/// <summary>
/// AUTH-OPEN-REGISTRATION: the first sign-in of a subject whose identity provider asserted a verified
/// email creates its Paquetenvia user, with no memberships, exactly once. An existing subject is never
/// relinked or modified, whatever its status. Callers must have checked the verified email
/// (AUTH-EMAIL-VERIFIED-REQUIRED) before calling.
/// </summary>
public interface IIdentityRegistration
{
    ValueTask RegisterAsync(string identitySubject, CancellationToken cancellationToken);
}

/// <summary>The registration store could not be reached or returned data outside its contract.</summary>
public sealed class IdentityRegistrationUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
