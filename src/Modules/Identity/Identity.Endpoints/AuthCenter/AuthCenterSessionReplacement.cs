using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Identity.Endpoints.AuthCenter;

/// <summary>
/// Makes every successful AuthCenter sign-in start a new BFF session (AUTH-001-MFA-STEP-UP). By
/// default the cookie handler reuses the session-store key of a cookie already present on the
/// callback and overwrites that ticket, so the previous cookie value would keep working with the new
/// (possibly MFA-elevated) identity. Before the handler signs in, this deletes the previous ticket
/// from the store and hides the previous cookie from the request, so a fresh key is issued and the
/// old cookie value becomes useless.
/// </summary>
internal sealed class AuthCenterSessionReplacement(IOptionsMonitor<CookieAuthenticationOptions> cookieOptions)
{
    // CookieAuthenticationHandler stores the session-store key under this claim type in the cookie.
    private const string SessionKeyClaimType = "Microsoft.AspNetCore.Authentication.Cookies-SessionId";

    public async Task ReplaceAsync(HttpContext context)
    {
        var cookie = cookieOptions.Get(AuthCenterDefaults.CookieScheme);
        var name = cookie.Cookie.Name!;
        var protectedTicket = cookie.CookieManager.GetRequestCookie(context, name);
        if (string.IsNullOrEmpty(protectedTicket))
        {
            return;
        }

        var previous = cookie.TicketDataFormat.Unprotect(protectedTicket);
        var key = previous?.Principal.FindFirst(SessionKeyClaimType)?.Value;
        if (!string.IsNullOrEmpty(key) && cookie.SessionStore is { } store)
        {
            await store.RemoveAsync(key, context, context.RequestAborted);
        }

        RemoveRequestCookies(context.Request, name);
    }

    private static void RemoveRequestCookies(HttpRequest request, string name)
    {
        // Covers chunked cookies too (name, nameC1, nameC2, ...). The OIDC correlation and nonce
        // cookies were already consumed, so an unparsable header is dropped entirely.
        if (!CookieHeaderValue.TryParseList(request.Headers.Cookie, out var cookies))
        {
            request.Headers.Remove(HeaderNames.Cookie);
            return;
        }

        var kept = cookies
            .Where(value => !value.Name.Value!.StartsWith(name, StringComparison.Ordinal))
            .Select(value => value.ToString())
            .ToArray();
        if (kept.Length == 0)
        {
            request.Headers.Remove(HeaderNames.Cookie);
        }
        else
        {
            request.Headers.Cookie = string.Join("; ", kept);
        }
    }
}
