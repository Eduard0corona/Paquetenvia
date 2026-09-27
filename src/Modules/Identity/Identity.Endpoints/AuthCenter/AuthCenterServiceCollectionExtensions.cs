using Identity.Application.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Identity.Endpoints.AuthCenter;

internal static class AuthCenterServiceCollectionExtensions
{
    /// <summary>
    /// Registers the cookie and OIDC handlers, but adds their schemes to the authentication
    /// options only when <c>Authentication:Provider=AuthCenter</c> at runtime. Other providers
    /// never instantiate the OIDC handler, so they are never asked for AuthCenter settings.
    /// </summary>
    internal static IServiceCollection AddAuthCenterBff(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services
            .AddOptions<AuthCenterOptions>()
            .Bind(configuration.GetSection(AuthCenterDefaults.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthCenterOptions>>(provider => new AuthCenterOptionsValidator(
            environment,
            provider.GetRequiredService<IOptions<IdentityAuthenticationOptions>>()));

        // Single-instance default. A shared IDistributedCache registered before this call wins.
        services.AddDistributedMemoryCache();
        services.AddSingleton<AuthCenterTicketStore>();
        services.AddSingleton<AuthCenterCookieEvents>();
        services.AddSingleton<AuthCenterOpenIdConnectEvents>();
        services.AddSingleton<AuthCenterRevocationClient>();

        services.TryAddTransient<CookieAuthenticationHandler>();
        services.TryAddTransient<OpenIdConnectHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<CookieAuthenticationOptions>,
            PostConfigureCookieAuthenticationOptions>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IPostConfigureOptions<OpenIdConnectOptions>,
            OpenIdConnectPostConfigureOptions>());
        services.AddOptions<OpenIdConnectOptions>(AuthCenterDefaults.OpenIdConnectScheme)
            .Validate(options =>
            {
                options.Validate(AuthCenterDefaults.OpenIdConnectScheme);
                return true;
            });
        services.AddOptions<AuthenticationOptions>()
            .Configure<IOptions<IdentityAuthenticationOptions>>((authentication, identity) =>
            {
                if (identity.Value.Provider != IdentityProviderKind.AuthCenter)
                {
                    return;
                }

                authentication.AddScheme<CookieAuthenticationHandler>(AuthCenterDefaults.CookieScheme, displayName: null);
                authentication.AddScheme<OpenIdConnectHandler>(AuthCenterDefaults.OpenIdConnectScheme, displayName: null);
            });

        services.AddOptions<CookieAuthenticationOptions>(AuthCenterDefaults.CookieScheme)
            .Configure(cookie =>
            {
                cookie.Cookie.Name = AuthCenterDefaults.SessionCookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.Path = "/";
                cookie.Cookie.Domain = null;
                cookie.Cookie.IsEssential = true;
                cookie.SlidingExpiration = false;
                cookie.EventsType = typeof(AuthCenterCookieEvents);
            });

        services.AddOptions<CookieAuthenticationOptions>(AuthCenterDefaults.CookieScheme)
            .Configure<AuthCenterTicketStore, IOptions<AuthCenterOptions>>((cookie, store, options) =>
            {
                cookie.SessionStore = store;
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(options.Value.SessionLifetimeMinutes);
            });

        services.AddOptions<OpenIdConnectOptions>(AuthCenterDefaults.OpenIdConnectScheme)
            .Configure<IOptions<AuthCenterOptions>>((oidc, configured) =>
            {
                var options = configured.Value;
                oidc.Authority = options.AuthorityUri.AbsoluteUri.TrimEnd('/');
                oidc.RequireHttpsMetadata = true;
                oidc.ClientId = options.ClientId;
                oidc.ClientSecret = options.ClientSecret;
                oidc.ResponseType = "code";
                oidc.ResponseMode = "query";
                oidc.UsePkce = true;
                oidc.CallbackPath = AuthCenterDefaults.CallbackPath;
                oidc.SignInScheme = AuthCenterDefaults.CookieScheme;
                oidc.SignOutScheme = AuthCenterDefaults.CookieScheme;
                oidc.MapInboundClaims = false;
                oidc.SaveTokens = true;
                oidc.GetClaimsFromUserInfoEndpoint = false;
                oidc.UseTokenLifetime = false;
                oidc.DisableTelemetry = true;
                oidc.EventsType = typeof(AuthCenterOpenIdConnectEvents);
                oidc.Scope.Clear();
                foreach (var scope in AuthCenterDefaults.RequiredScopes)
                {
                    oidc.Scope.Add(scope);
                }

                oidc.ClaimActions.Clear();
                oidc.ProtocolValidator.RequireNonce = true;
                oidc.ProtocolValidator.RequireState = true;
                oidc.CorrelationCookie.Name = AuthCenterDefaults.CorrelationCookiePrefix;
                oidc.NonceCookie.Name = AuthCenterDefaults.NonceCookiePrefix;
                foreach (var protocolCookie in new[] { oidc.CorrelationCookie, oidc.NonceCookie })
                {
                    protocolCookie.HttpOnly = true;
                    protocolCookie.SecurePolicy = CookieSecurePolicy.Always;
                    protocolCookie.SameSite = SameSiteMode.Lax;
                    protocolCookie.Path = "/";
                    protocolCookie.Domain = null;
                    protocolCookie.IsEssential = true;
                }

                oidc.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateAudience = true,
                    ValidAudience = options.ClientId,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = AuthCenterDefaults.NameClaim,
                    RoleClaimType = "urn:paquetenvia:authcenter:v1:ignored-role",
                };
            });

        return services;
    }

    private sealed class AuthCenterOptionsValidator(
        IHostEnvironment environment,
        IOptions<IdentityAuthenticationOptions> identity) : IValidateOptions<AuthCenterOptions>
    {
        public ValidateOptionsResult Validate(string? name, AuthCenterOptions options)
        {
            if (identity.Value.Provider != IdentityProviderKind.AuthCenter)
            {
                return ValidateOptionsResult.Skip;
            }

            var failures = options.Validate(environment).ToArray();
            return failures.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}
