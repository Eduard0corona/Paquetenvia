using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Identity.Application.Authentication;
using Identity.Application.Bootstrap;
using Identity.Endpoints.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Identity.Endpoints.AuthCenter;

internal sealed record AuthCenterProfile(string? Name, string? Email);

/// <summary>
/// Turns the stored external identity (AuthCenter <c>sub</c> + MFA evidence) into the Paquetenvia
/// session on every request: the authorization context is re-resolved from the database each time,
/// so suspensions and membership changes take effect without a new login. The same hook enforces
/// the BFF request-forgery defenses (same-origin check and session-bound CSRF header on writes).
/// </summary>
internal sealed class AuthCenterCookieEvents(IOptions<AuthCenterOptions> options) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var request = context.HttpContext.Request;
        var external = context.Principal?.Identities.SingleOrDefault(identity =>
            identity.AuthenticationType == AuthCenterDefaults.ExternalAuthenticationType);
        var subject = SingleValue(external, AuthCenterDefaults.SubjectClaim);
        var mfa = SingleValue(external, AuthCenterDefaults.MfaClaim);
        var csrf = context.Properties.Items.TryGetValue(AuthCenterDefaults.CsrfPropertyKey, out var stored)
            ? stored
            : null;

        if (external is null ||
            string.IsNullOrWhiteSpace(subject) ||
            mfa is not ("True" or "False") ||
            string.IsNullOrEmpty(csrf) ||
            !IsSameOrigin(request) ||
            (!IsSafeMethod(request.Method) && !HasValidCsrfHeader(request, csrf)))
        {
            context.RejectPrincipal();
            return;
        }

        var resolver = context.HttpContext.RequestServices.GetRequiredService<IIdentityContextResolver>();
        var resolution = await resolver.ResolveAsync(subject, context.HttpContext.RequestAborted);
        if (!IdentityClaimsPrincipalFactory.TryCreate(
                new ExternalIdentity(subject, mfa == "True"),
                resolution,
                out var principal) ||
            principal is null)
        {
            context.RejectPrincipal();
            return;
        }

        context.HttpContext.Items[AuthCenterDefaults.ProfileItemKey] = new AuthCenterProfile(
            SingleValue(external, AuthCenterDefaults.NameClaim),
            SingleValue(external, AuthCenterDefaults.EmailClaim));
        context.HttpContext.Items[AuthCenterDefaults.CsrfItemKey] = csrf;
        context.ReplacePrincipal(principal);
        context.ShouldRenew = false;
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context) =>
        IdentityProblemDetails.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized, "Unauthorized");

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context) =>
        IdentityProblemDetails.WriteAsync(context.HttpContext, StatusCodes.Status403Forbidden, "Forbidden");

    public override Task RedirectToLogout(RedirectContext<CookieAuthenticationOptions> context) => Task.CompletedTask;

    public override Task RedirectToReturnUrl(RedirectContext<CookieAuthenticationOptions> context) => Task.CompletedTask;

    internal static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private bool IsSameOrigin(HttpRequest request)
    {
        // Browsers send Origin on every cross-origin request, on WebSocket handshakes and on
        // same-origin writes; a present Origin must be exactly the public web origin.
        var origins = request.Headers.Origin;
        return origins.Count == 0 ||
            (origins.Count == 1 &&
             string.Equals(origins[0], options.Value.NormalizedPublicOrigin, StringComparison.Ordinal));
    }

    private static bool HasValidCsrfHeader(HttpRequest request, string expected)
    {
        var values = request.Headers[AuthCenterDefaults.CsrfHeaderName];
        return values.Count == 1 &&
            values[0] is { Length: > 0 } provided &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(provided),
                Encoding.ASCII.GetBytes(expected));
    }

    private static string? SingleValue(ClaimsIdentity? identity, string type)
    {
        if (identity is null)
        {
            return null;
        }

        var values = identity.FindAll(type).Select(claim => claim.Value).ToArray();
        return values.Length == 1 ? values[0] : null;
    }
}
