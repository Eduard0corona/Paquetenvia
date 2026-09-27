using System.Security.Cryptography;
using System.Text;
using Identity.Application.Authentication;
using Identity.Application.Bootstrap;
using Identity.Application.Session;
using Identity.Endpoints.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Identity.Endpoints.AuthCenter;

public static class AuthCenterBffEndpoints
{
    /// <summary>
    /// Maps the BFF contract (login, session, logout, back-channel logout) when
    /// <c>Authentication:Provider=AuthCenter</c>.
    /// The OIDC callback (<see cref="AuthCenterDefaults.CallbackPath"/>) is served by the handler itself.
    /// </summary>
    public static IEndpointRouteBuilder MapAuthCenterBff(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var provider = endpoints.ServiceProvider
            .GetRequiredService<IOptions<IdentityAuthenticationOptions>>()
            .Value.Provider;
        if (provider != IdentityProviderKind.AuthCenter)
        {
            return endpoints;
        }

        endpoints.MapGet(AuthCenterDefaults.LoginPath, LoginAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();
        endpoints.MapGet(AuthCenterDefaults.SessionPath, Session)
            .RequireAuthorization(IdentityPolicies.Authenticated)
            .ExcludeFromDescription();
        endpoints.MapPost(AuthCenterDefaults.LogoutPath, LogoutAsync)
            .RequireAuthorization(IdentityPolicies.Authenticated)
            .ExcludeFromDescription();

        // Server-to-server from AuthCenter: no cookie, no CSRF, no Origin (AUTH-001-BACKCHANNEL-LOGOUT).
        endpoints.MapPost(AuthCenterDefaults.BackchannelLogoutPath, BackchannelLogoutAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .ExcludeFromDescription();
        return endpoints;
    }

    internal static bool IsLocalReturnUrl(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= 2048 &&
        value[0] == '/' &&
        (value.Length == 1 || (value[1] != '/' && value[1] != '\\')) &&
        !value.Any(character => char.IsControl(character) || character == '\\');

    private static async Task LoginAsync(HttpContext context)
    {
        NoStore(context.Response);
        var values = context.Request.Query["return_url"];
        var destination = values.Count == 1 && IsLocalReturnUrl(values[0]) ? values[0]! : "/";
        var properties = new AuthenticationProperties { RedirectUri = destination };

        // Step-up (AUTH-001-MFA-STEP-UP): the requirement travels in the protected OIDC state and is
        // re-checked against the validated ID token. Anything but exactly mfa=required is a normal login.
        var mfa = context.Request.Query["mfa"];
        if (mfa.Count == 1 && string.Equals(mfa[0], AuthCenterDefaults.MfaRequiredQueryValue, StringComparison.Ordinal))
        {
            properties.Items[AuthCenterDefaults.RequiredContextClassItemKey] = AuthCenterDefaults.MfaContextClass;
        }

        await context.ChallengeAsync(AuthCenterDefaults.OpenIdConnectScheme, properties);
    }

    private static IResult Session(HttpContext context, IAuthenticatedSession session)
    {
        NoStore(context.Response);
        var profile = context.Items[AuthCenterDefaults.ProfileItemKey] as AuthCenterProfile;
        if (context.Items[AuthCenterDefaults.CsrfItemKey] is not string csrf || !session.IsAuthenticated)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        return Results.Ok(new
        {
            authenticated = true,
            authorized = session.IdentityStatus == IdentityContextStatus.Active,
            mfa = session.MfaSatisfied,
            csrfToken = csrf,
            sessionNamespace = SessionNamespace(session.Subject!),
            user = new
            {
                name = profile?.Name,
                email = profile?.Email,
            },
        });
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        AuthCenterRevocationClient revocation,
        AuthCenterEndSession endSession,
        CancellationToken cancellationToken)
    {
        NoStore(context.Response);
        // The CSRF header and same-origin check were already enforced while authenticating this POST.
        var authentication = await context.AuthenticateAsync(AuthCenterDefaults.CookieScheme);
        var refreshToken = authentication.Properties?.GetTokenValue(AuthCenterDefaults.RefreshTokenName);
        var idToken = authentication.Properties?.GetTokenValue(AuthCenterDefaults.IdTokenName);
        try
        {
            if (!string.IsNullOrEmpty(refreshToken))
            {
                await revocation.RevokeRefreshTokenAsync(refreshToken, cancellationToken);
            }
        }
        finally
        {
            await context.SignOutAsync(AuthCenterDefaults.CookieScheme);
        }

        // AUTH-001-RP-INITIATED-LOGOUT: the web navigates here (window.location.assign) so AuthCenter
        // also ends its single sign-on session; null when it cannot be built (local logout only).
        return Results.Ok(new { endSessionUrl = await endSession.BuildUrlAsync(idToken, cancellationToken) });
    }

    private static async Task<IResult> BackchannelLogoutAsync(
        HttpContext context,
        AuthCenterBackchannelLogout backchannel,
        CancellationToken cancellationToken)
    {
        NoStore(context.Response);
        var request = context.Request;
        if (!IsFormUrlEncoded(request.ContentType))
        {
            return InvalidBackchannelRequest();
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
        {
            bodySize.MaxRequestBodySize = BackchannelFormOptions.BufferBodyLengthLimit;
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(BackchannelFormOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException or IOException
                                              && !cancellationToken.IsCancellationRequested)
        {
            return InvalidBackchannelRequest();
        }

        var tokens = form["logout_token"];
        return tokens.Count == 1 && await backchannel.ProcessAsync(tokens[0], cancellationToken)
            ? Results.Ok()
            : InvalidBackchannelRequest();
    }

    private static readonly FormOptions BackchannelFormOptions = new()
    {
        BufferBodyLengthLimit = 32 * 1024,
        MultipartBodyLengthLimit = 32 * 1024,
        ValueCountLimit = 16,
        KeyLengthLimit = 64,
        ValueLengthLimit = AuthCenterBackchannelLogout.MaximumTokenLength,
    };

    private static bool IsFormUrlEncoded(string? contentType) =>
        Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var mediaType) &&
        mediaType.MediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

    private static IResult InvalidBackchannelRequest() =>
        Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// Stable, non-reversible per-identity key the web uses to partition in-browser caches
    /// (e.g. the driver offline queue) between people sharing a device.
    /// </summary>
    internal static string SessionNamespace(string subject) =>
        Base64UrlTextEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes("paquetenvia.bff.namespace.v1|" + subject)))[..32];

    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }
}
