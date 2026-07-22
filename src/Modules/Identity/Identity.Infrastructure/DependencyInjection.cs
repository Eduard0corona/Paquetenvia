using Identity.Application.Authentication;
using Identity.Infrastructure.Mock;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IIdentityProvider, MockIdentityProvider>();
        return services;
    }
}
