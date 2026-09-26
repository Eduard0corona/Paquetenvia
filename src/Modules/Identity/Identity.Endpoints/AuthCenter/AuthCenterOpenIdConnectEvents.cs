using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Identity.Endpoints.AuthCenter;

/// <summary>
/// Hardens the standard OIDC handler for the AuthCenter contract. Never logs codes, tokens,
/// verifiers, subjects or protocol error descriptions.
/// </summary>
internal sealed partial class AuthCenterOpenIdConnectEvents(
    IOptions<AuthCenterOptions> options,
    ILogger<AuthCenterOpenIdConnectEvents> logger) : OpenIdConnectEvents
{
    public override Task RedirectToIdentityProvider(RedirectContext context)
    {
        // The API may sit behind the Next.js rewrite or an ingress; the redirect URI registered in
        // AuthCenter is always derived from the configured public origin, never from Host headers.
        context.ProtocolMessage.RedirectUri = options.Value.RedirectUri;
        return Task.CompletedTask;
    }

    public override Task MessageReceived(MessageReceivedContext context)
    {
        // RFC 9207 mix-up defense: AuthCenter advertises authorization_response_iss_parameter_supported.
        var issuer = context.ProtocolMessage.GetParameter("iss");
        if (issuer is not null && !string.Equals(issuer, options.Value.Issuer, StringComparison.Ordinal))
        {
            context.Fail("Unexpected authorization response issuer.");
        }

        return Task.CompletedTask;
    }

    public override Task TokenValidated(TokenValidatedContext context)
    {
        var issuers = context.Principal?.FindAll("iss").Select(claim => claim.Value).ToArray() ?? [];
        if (issuers.Length != 1 || !string.Equals(issuers[0], options.Value.Issuer, StringComparison.Ordinal))
        {
            context.Fail("Unexpected ID token issuer.");
        }

        return Task.CompletedTask;
    }

    public override Task TicketReceived(TicketReceivedContext context)
    {
        var source = context.Principal;
        var subjects = source?.FindAll(AuthCenterDefaults.SubjectClaim).Select(claim => claim.Value).ToArray() ?? [];
        if (context.Properties is null ||
            subjects.Length != 1 ||
            string.IsNullOrWhiteSpace(subjects[0]) ||
            subjects[0].Length > AuthCenterDefaults.MaximumSubjectLength ||
            subjects[0].Any(char.IsControl))
        {
            context.Fail("AuthCenter did not return a usable subject.");
            return Task.CompletedTask;
        }

        // Only the subject, MFA evidence and display profile survive. AuthCenter roles, permissions
        // and applications are dropped: Paquetenvia authorizes from its own memberships (GATE-002).
        var mfa = source!.FindAll("amr").Any(claim => string.Equals(claim.Value, "mfa", StringComparison.Ordinal));
        var claims = new List<Claim>
        {
            new(AuthCenterDefaults.SubjectClaim, subjects[0]),
            new(AuthCenterDefaults.MfaClaim, mfa.ToString(CultureInfo.InvariantCulture)),
        };
        AddOptional(claims, source!, AuthCenterDefaults.NameClaim, 200);
        AddOptional(claims, source!, AuthCenterDefaults.EmailClaim, 320);
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            AuthCenterDefaults.ExternalAuthenticationType,
            AuthCenterDefaults.NameClaim,
            roleType: null));

        // Keep only the refresh token (needed to revoke on logout). ID/access tokens are not used
        // by Paquetenvia and are not retained anywhere.
        var refreshToken = context.Properties.GetTokenValue(AuthCenterDefaults.RefreshTokenName);
        context.Properties.StoreTokens(string.IsNullOrEmpty(refreshToken)
            ? []
            : [new AuthenticationToken { Name = AuthCenterDefaults.RefreshTokenName, Value = refreshToken }]);
        context.Properties.Items[AuthCenterDefaults.CsrfPropertyKey] =
            Base64UrlTextEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        context.Properties.IsPersistent = false;
        context.Properties.AllowRefresh = false;
        context.Properties.IssuedUtc = DateTimeOffset.UtcNow;
        context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(options.Value.SessionLifetimeMinutes);
        return Task.CompletedTask;
    }

    public override Task AccessDenied(AccessDeniedContext context)
    {
        LogSignInFailed(logger, "access_denied");
        context.HandleResponse();
        context.Response.Redirect(AuthCenterDefaults.RemoteFailureRedirect);
        return Task.CompletedTask;
    }

    public override Task RemoteFailure(RemoteFailureContext context)
    {
        LogSignInFailed(logger, context.Failure?.GetType().Name ?? "unknown");
        context.HandleResponse();
        context.Response.Redirect(AuthCenterDefaults.RemoteFailureRedirect);
        return Task.CompletedTask;
    }

    private static void AddOptional(List<Claim> claims, ClaimsPrincipal source, string type, int maximumLength)
    {
        var values = source.FindAll(type).Select(claim => claim.Value).ToArray();
        if (values.Length == 1 && values[0].Length is > 0 && values[0].Length <= maximumLength)
        {
            claims.Add(new Claim(type, values[0]));
        }
    }

    [LoggerMessage(EventId = 4101, Level = LogLevel.Warning, Message = "AuthCenter sign-in was rejected ({FailureKind}).")]
    private static partial void LogSignInFailed(ILogger logger, string failureKind);
}
