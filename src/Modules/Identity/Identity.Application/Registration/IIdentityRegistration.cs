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

    /// <summary>
    /// REG-ACCEPT-ALL-ORGANIZATIONS: at every sign-in with a verified email, each pending, unexpired
    /// membership an administrator added for that email in an ACTIVE organization becomes a membership of
    /// the subject's ACTIVE user, exactly once. Returns how many entries were accepted; 0 when the email is
    /// unusable or no lookup key is configured. Callers must have checked the verified email first.
    /// </summary>
    ValueTask<int> ApplyPendingMembershipsAsync(
        string identitySubject,
        string verifiedEmail,
        CancellationToken cancellationToken);
}

/// <summary>The registration store could not be reached or returned data outside its contract.</summary>
public sealed class IdentityRegistrationUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
