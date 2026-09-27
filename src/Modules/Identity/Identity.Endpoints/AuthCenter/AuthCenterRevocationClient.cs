using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Identity.Endpoints.AuthCenter;

/// <summary>Revokes the session refresh token (and with it the AuthCenter refresh family) on logout.</summary>
internal sealed partial class AuthCenterRevocationClient(
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    IOptions<AuthCenterOptions> options,
    ILogger<AuthCenterRevocationClient> logger)
{
    public async Task<bool> RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);
        var oidc = oidcOptions.Get(AuthCenterDefaults.OpenIdConnectScheme);
        try
        {
            var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
            if (!Uri.TryCreate(configuration.RevocationEndpoint, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(endpoint.Authority, options.Value.AuthorityUri.Authority, StringComparison.OrdinalIgnoreCase))
            {
                LogRevocationFailed(logger, "endpoint_unavailable");
                return false;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = refreshToken,
                    ["token_type_hint"] = "refresh_token",
                }),
            };
            var credentials = Uri.EscapeDataString(options.Value.ClientId!) + ":" +
                Uri.EscapeDataString(options.Value.ClientSecret!);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
            using var response = await oidc.Backchannel.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogRevocationFailed(logger, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            LogRevocationFailed(logger, exception.GetType().Name);
            return false;
        }
    }

    [LoggerMessage(EventId = 4102, Level = LogLevel.Warning, Message = "AuthCenter refresh-token revocation failed ({FailureKind}); the local session is still destroyed.")]
    private static partial void LogRevocationFailed(ILogger logger, string failureKind);
}
