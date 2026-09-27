using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Identity.Endpoints.AuthCenter;

/// <summary>
/// Builds the OpenID Connect RP-Initiated Logout URL (AUTH-001-RP-INITIATED-LOGOUT) that ends the
/// AuthCenter single sign-on session after the local session is gone. The end-session endpoint comes
/// from discovery and gets the same HTTPS + authority check as revocation. Any failure yields
/// <c>null</c>: the local logout has already happened and the web falls back to <c>/login</c>.
/// </summary>
internal sealed partial class AuthCenterEndSession(
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    IOptions<AuthCenterOptions> options,
    ILogger<AuthCenterEndSession> logger)
{
    public async Task<string?> BuildUrlAsync(string? idToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(idToken))
        {
            return null;
        }

        string? endpoint;
        try
        {
            var oidc = oidcOptions.Get(AuthCenterDefaults.OpenIdConnectScheme);
            endpoint = (await oidc.ConfigurationManager!.GetConfigurationAsync(cancellationToken)).EndSessionEndpoint;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            LogEndSessionUnavailable(logger, exception.GetType().Name);
            return null;
        }

        var url = BuildUrl(endpoint, idToken, options.Value);
        if (url is null)
        {
            LogEndSessionUnavailable(logger, "endpoint_unavailable");
        }

        return url;
    }

    internal static string? BuildUrl(string? endpoint, string? idToken, AuthCenterOptions options)
    {
        if (string.IsNullOrEmpty(idToken) || !IsAuthCenterEndpoint(endpoint, options))
        {
            return null;
        }

        return QueryHelpers.AddQueryString(endpoint!, new Dictionary<string, string?>
        {
            ["id_token_hint"] = idToken,
            ["post_logout_redirect_uri"] = options.PostLogoutRedirectUri,
        });
    }

    /// <summary>Same rule as the revocation endpoint: absolute HTTPS on the configured AuthCenter authority.</summary>
    internal static bool IsAuthCenterEndpoint(string? value, AuthCenterOptions options) =>
        Uri.TryCreate(value, UriKind.Absolute, out var endpoint) &&
        endpoint.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(endpoint.UserInfo) &&
        string.IsNullOrEmpty(endpoint.Fragment) &&
        string.Equals(endpoint.Authority, options.AuthorityUri.Authority, StringComparison.OrdinalIgnoreCase);

    [LoggerMessage(EventId = 4105, Level = LogLevel.Warning, Message = "AuthCenter end-session URL unavailable ({FailureKind}); only the local session was ended.")]
    private static partial void LogEndSessionUnavailable(ILogger logger, string failureKind);
}
