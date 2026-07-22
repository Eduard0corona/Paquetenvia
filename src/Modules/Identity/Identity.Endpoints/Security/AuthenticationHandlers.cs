using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using Identity.Application.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Identity.Endpoints.Security;

public sealed class MockAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IIdentityProvider _identityProvider;

    public MockAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IIdentityProvider identityProvider)
        : base(options, logger, encoder)
    {
        _identityProvider = identityProvider;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var values))
        {
            return AuthenticateResult.NoResult();
        }

        if (values.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(values[0], out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(header.Parameter) ||
            header.Parameter.Any(char.IsWhiteSpace))
        {
            return AuthenticateResult.Fail("The authentication credential is malformed.");
        }

        var result = await _identityProvider.AuthenticateAsync(header.Parameter, Context.RequestAborted);
        if (!result.IsValid ||
            result.Identity is null ||
            !IdentityClaimsPrincipalFactory.TryCreate(result.Identity, out var principal) ||
            principal is null)
        {
            return AuthenticateResult.Fail("The authentication credential is invalid.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        IdentityProblemDetails.WriteAsync(Context, StatusCodes.Status401Unauthorized, "Unauthorized");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        IdentityProblemDetails.WriteAsync(Context, StatusCodes.Status403Forbidden, "Forbidden");
}

public sealed class DisabledAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public DisabledAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        IdentityProblemDetails.WriteAsync(Context, StatusCodes.Status401Unauthorized, "Unauthorized");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        IdentityProblemDetails.WriteAsync(Context, StatusCodes.Status403Forbidden, "Forbidden");
}
