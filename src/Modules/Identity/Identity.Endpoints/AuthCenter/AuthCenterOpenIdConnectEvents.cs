using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Identity.Application.Registration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Identity.Endpoints.AuthCenter;

/// <summary>
/// Hardens the standard OIDC handler for the AuthCenter contract. Never logs codes, tokens,
/// verifiers, subjects or protocol error descriptions.
/// </summary>
internal sealed partial class AuthCenterOpenIdConnectEvents(
    IOptions<AuthCenterOptions> options,
    AuthCenterSessionReplacement sessionReplacement,
    ILogger<AuthCenterOpenIdConnectEvents> logger) : OpenIdConnectEvents
{
    public override Task RedirectToIdentityProvider(RedirectContext context)
    {
        // The API may sit behind the Next.js rewrite or an ingress; the redirect URI registered in
        // AuthCenter is always derived from the configured public origin, never from Host headers.
        context.ProtocolMessage.RedirectUri = options.Value.RedirectUri;

        // AUTH-001-MFA-STEP-UP: AuthCenter asks for (or enrolls) the second factor without asking
        // the password again when the single sign-on session is weaker than the requested class.
        if (RequiredContextClass(context.Properties) is { } required)
        {
            context.ProtocolMessage.AcrValues = required;
        }

        return Task.CompletedTask;
    }

    public override Task MessageReceived(MessageReceivedContext context)
    {
        // RFC 9207 mix-up defense: AuthCenter advertises authorization_response_iss_parameter_supported
        // and appends `iss` to every success and error redirect, so a missing `iss` is as suspect as a
        // different one (RFC 9207 section 2.4). Both fail closed before the code is redeemed.
        var issuer = context.ProtocolMessage.GetParameter("iss");
        if (issuer is null || !string.Equals(issuer, options.Value.Issuer, StringComparison.Ordinal))
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
            return Task.CompletedTask;
        }

        // Defense in depth for step-up: AuthCenter is trusted to honor acr_values, but the validated
        // ID token must prove it (acr of the requested class or stronger, and "mfa" in amr).
        if (RequiredContextClass(context.Properties) is not null && !SatisfiesMfa(context.Principal))
        {
            context.Fail("The requested authentication context was not satisfied.");
        }

        return Task.CompletedTask;
    }

    public override async Task TicketReceived(TicketReceivedContext context)
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
            return;
        }

        // AUTH-EMAIL-VERIFIED-REQUIRED: without email_verified=true from the validated ID token no user
        // is created or linked and no session is issued; the refusal is specific, never a detail.
        if (!HasVerifiedEmail(source!))
        {
            LogEmailNotVerified(logger);
            context.HandleResponse();
            context.Response.Redirect(AuthCenterDefaults.EmailNotVerifiedRedirect);
            return;
        }

        // AUTH-OPEN-REGISTRATION: the first sign-in of an unknown subject creates its user (no
        // memberships) exactly once; a known subject is left untouched whatever its status.
        try
        {
            await context.HttpContext.RequestServices
                .GetRequiredService<IIdentityRegistration>()
                .RegisterAsync(subjects[0], context.HttpContext.RequestAborted);
        }
        catch (IdentityRegistrationUnavailableException)
        {
            context.Fail("Identity registration is unavailable.");
            return;
        }

        // Only the subject, AuthCenter session id, MFA evidence, verified-email evidence and display
        // profile survive.
        // AuthCenter roles, permissions and applications are dropped: Paquetenvia authorizes from its
        // own memberships (GATE-002). sid links the session to back-channel logout tokens.
        var mfa = HasMfaMethod(source!);
        var claims = new List<Claim>
        {
            new(AuthCenterDefaults.SubjectClaim, subjects[0]),
            new(AuthCenterDefaults.MfaClaim, mfa.ToString(CultureInfo.InvariantCulture)),
            new(AuthCenterDefaults.EmailVerifiedClaim, "true"),
        };
        AddOptional(claims, source!, AuthCenterDefaults.SessionIdClaim, AuthCenterDefaults.MaximumSessionIdLength);
        AddOptional(claims, source!, AuthCenterDefaults.NameClaim, 200);
        AddOptional(claims, source!, AuthCenterDefaults.EmailClaim, 320);
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            AuthCenterDefaults.ExternalAuthenticationType,
            AuthCenterDefaults.NameClaim,
            roleType: null));

        // Keep only the refresh token (revoked on logout) and the ID token (id_token_hint for
        // RP-initiated logout), both server-side in the protected ticket. The access token is not
        // used by Paquetenvia and is not retained anywhere.
        var retained = new List<AuthenticationToken>();
        foreach (var name in new[] { AuthCenterDefaults.RefreshTokenName, AuthCenterDefaults.IdTokenName })
        {
            if (context.Properties.GetTokenValue(name) is { Length: > 0 } value)
            {
                retained.Add(new AuthenticationToken { Name = name, Value = value });
            }
        }

        context.Properties.StoreTokens(retained);
        context.Properties.Items.Remove(AuthCenterDefaults.RequiredContextClassItemKey);
        context.Properties.Items[AuthCenterDefaults.CsrfPropertyKey] =
            Base64UrlTextEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        context.Properties.IsPersistent = false;
        context.Properties.AllowRefresh = false;
        context.Properties.IssuedUtc = now;
        context.Properties.ExpiresUtc = now.AddMinutes(options.Value.SessionLifetimeMinutes);
        context.Properties.Items[AuthCenterDefaults.SignedInAtItemKey] =
            now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        // The new session replaces any session this browser already had (step-up included): the
        // old ticket is deleted and the cookie handler issues a brand-new key.
        await sessionReplacement.ReplaceAsync(context.HttpContext);
    }

    public override Task AccessDenied(AccessDeniedContext context)
    {
        // AUTH-001-ACCESS-DENIED-MESSAGE: reached only after state, correlation and iss validated.
        LogSignInFailed(logger, "access_denied");
        context.HandleResponse();
        context.Response.Redirect(AuthCenterDefaults.AccessDeniedRedirect);
        return Task.CompletedTask;
    }

    public override Task RemoteFailure(RemoteFailureContext context)
    {
        LogSignInFailed(logger, context.Failure?.GetType().Name ?? "unknown");
        context.HandleResponse();
        context.Response.Redirect(AuthCenterDefaults.RemoteFailureRedirect);
        return Task.CompletedTask;
    }

    internal static bool SatisfiesMfa(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return false;
        }

        var classes = principal.FindAll("acr").Select(claim => claim.Value).ToArray();
        return classes.Length == 1 &&
            classes[0] is AuthCenterDefaults.MfaContextClass or AuthCenterDefaults.PhishingResistantContextClass &&
            HasMfaMethod(principal);
    }

    /// <summary>
    /// Exactly one <c>email_verified</c> claim that is the JSON boolean <c>true</c>: the token handler
    /// types it <see cref="ClaimValueTypes.Boolean"/> and renders it <c>true</c>. A JSON string such as
    /// <c>"true"</c>, any other value, a repeated claim or an absent claim is unverified.
    /// </summary>
    internal static bool HasVerifiedEmail(ClaimsPrincipal principal)
    {
        var claims = principal.FindAll(AuthCenterDefaults.EmailVerifiedClaim).ToArray();
        return claims.Length == 1 &&
            string.Equals(claims[0].ValueType, ClaimValueTypes.Boolean, StringComparison.Ordinal) &&
            string.Equals(claims[0].Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMfaMethod(ClaimsPrincipal principal) =>
        principal.FindAll("amr").Any(claim => string.Equals(claim.Value, "mfa", StringComparison.Ordinal));

    private static string? RequiredContextClass(AuthenticationProperties? properties) =>
        properties?.Items.TryGetValue(AuthCenterDefaults.RequiredContextClassItemKey, out var value) == true &&
        string.Equals(value, AuthCenterDefaults.MfaContextClass, StringComparison.Ordinal)
            ? value
            : null;

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

    [LoggerMessage(EventId = 4106, Level = LogLevel.Warning, Message = "AuthCenter sign-in was refused: the email is not verified.")]
    private static partial void LogEmailNotVerified(ILogger logger);
}
