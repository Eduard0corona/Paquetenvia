using Identity.Application.Authentication;
using Identity.Application.Session;
using Identity.Endpoints.Authorization;
using Identity.Endpoints.Security;
using Identity.Endpoints.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Identity.Endpoints;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentitySecurity(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services
            .AddOptions<IdentityAuthenticationOptions>()
            .Bind(configuration.GetSection(IdentityAuthenticationOptions.SectionName))
            .Validate(
                options => Enum.IsDefined(options.Provider),
                "Authentication:Provider must be Disabled or Mock.")
            .Validate(
                options => options.Provider != IdentityProviderKind.Mock ||
                    environment.IsDevelopment() ||
                    environment.IsEnvironment("Testing"),
                "Authentication:Provider=Mock is permitted only in Development or Testing.")
            .ValidateOnStart();

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentitySecurityDefaults.DefaultScheme;
                options.DefaultChallengeScheme = IdentitySecurityDefaults.DefaultScheme;
                options.DefaultForbidScheme = IdentitySecurityDefaults.DefaultScheme;
            })
            .AddPolicyScheme(
                IdentitySecurityDefaults.DefaultScheme,
                displayName: null,
                options => options.ForwardDefaultSelector = context =>
                    context.RequestServices
                        .GetRequiredService<IOptions<IdentityAuthenticationOptions>>()
                        .Value.Provider == IdentityProviderKind.Mock
                            ? IdentitySecurityDefaults.MockScheme
                            : IdentitySecurityDefaults.DisabledScheme)
            .AddScheme<AuthenticationSchemeOptions, MockAuthenticationHandler>(
                IdentitySecurityDefaults.MockScheme,
                displayName: null,
                configureOptions: static _ => { })
            .AddScheme<AuthenticationSchemeOptions, DisabledAuthenticationHandler>(
                IdentitySecurityDefaults.DisabledScheme,
                displayName: null,
                configureOptions: static _ => { });

        services.AddSingleton<Microsoft.AspNetCore.Authentication.IClaimsTransformation,
            PaquetenviaClaimsTransformation>();
        services.AddHttpContextAccessor();
        services.AddScoped<IAuthenticatedSession>(provider =>
            AuthenticatedSession.FromPrincipal(
                provider.GetRequiredService<IHttpContextAccessor>().HttpContext?.User));

        services.AddSingleton<IAuthorizationHandler, AuthenticatedIdentityHandler>();
        services.AddSingleton<IAuthorizationHandler, ActiveIdentityHandler>();
        services.AddSingleton<IAuthorizationHandler, RequireMfaHandler>();
        services.AddSingleton<IAuthorizationHandler, AnyActiveOrganizationRoleHandler>();
        services.AddSingleton<IAuthorizationHandler, OrganizationMembershipHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, IdentityAuthorizationResultHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(IdentityPolicies.Authenticated, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new AuthenticatedIdentityRequirement());
            })
            .AddPolicy(IdentityPolicies.ActiveIdentity, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ActiveIdentityRequirement());
            })
            .AddPolicy(IdentityPolicies.RequireMfa, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new RequireMfaRequirement());
            })
            .AddPolicy(IdentityPolicies.PrivilegedMfa, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(
                    new ActiveIdentityRequirement(),
                    new AnyActiveOrganizationRoleRequirement(NormalizedOrganizationRole.PlatformAdmin),
                    new RequireMfaRequirement());
            });

        services.AddSignalR();
        return services;
    }
}
