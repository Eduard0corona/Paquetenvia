using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Orders.Application.Tracking;

namespace Orders.Endpoints;

public static class DependencyInjection
{
    public static IServiceCollection AddOrdersEndpoints(this IServiceCollection services)
    {
        services.AddExceptionHandler<PublicTrackingTechnicalExceptionHandler>();
        return services;
    }
}

internal sealed class PublicTrackingTechnicalExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not PublicTrackingInfrastructureException)
        {
            return false;
        }

        await Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service Unavailable",
            extensions: new Dictionary<string, object?>
            {
                ["traceId"] = httpContext.TraceIdentifier,
            }).ExecuteAsync(httpContext);
        return true;
    }
}
