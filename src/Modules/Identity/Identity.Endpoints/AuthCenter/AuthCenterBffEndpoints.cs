using System.Security.Cryptography;
using System.Text;
using Identity.Application.Authentication;
using Identity.Application.Bootstrap;
using Identity.Application.Session;
using Identity.Endpoints.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Identity.Endpoints.AuthCenter;

public static class AuthCenterBffEndpoints
{
    /// <summary>
    /// Maps the BFF contract (login, session, logout) when <c>Authentication:Provider=AuthCenter</c>.
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
        await context.ChallengeAsync(
            AuthCenterDefaults.OpenIdConnectScheme,
            new AuthenticationProperties { RedirectUri = destination });
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
        CancellationToken cancellationToken)
    {
        NoStore(context.Response);
        // The CSRF header and same-origin check were already enforced while authenticating this POST.
        var authentication = await context.AuthenticateAsync(AuthCenterDefaults.CookieScheme);
        var refreshToken = authentication.Properties?.GetTokenValue(AuthCenterDefaults.RefreshTokenName);
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

        return Results.NoContent();
    }

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
