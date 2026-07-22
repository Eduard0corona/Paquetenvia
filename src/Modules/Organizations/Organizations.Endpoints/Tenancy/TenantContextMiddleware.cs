using Organizations.Application.Auditing;
using Organizations.Application.Session;
using Paqueteria.Domain.Tenancy;

namespace Organizations.Endpoints.Tenancy;

public sealed class TenantContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        RequestTenantContext tenantContext,
        IPlatformAdminTenantActivationAudit audit)
    {
        if (httpContext.GetEndpoint()?.Metadata.GetMetadata<RequiresTenantContextMetadata>() is null)
        {
            await next(httpContext);
            return;
        }

        if (!session.IsAuthenticated)
        {
            await next(httpContext);
            return;
        }

        if (!TryReadOrganizationId(httpContext.Request, out var organizationId))
        {
            await WriteProblemAsync(httpContext, StatusCodes.Status400BadRequest, "Invalid organization context.");
            return;
        }

        if (!session.IsActive ||
            session.UserId is not { } userId ||
            !session.HasOrganizationAccess(organizationId))
        {
            await WriteProblemAsync(httpContext, StatusCodes.Status403Forbidden, "Forbidden.");
            return;
        }

        tenantContext.Select(organizationId);
        if (session.HasRole(organizationId, OrganizationRole.PlatformAdmin))
        {
            try
            {
                await audit.RecordAsync(userId, organizationId, httpContext.TraceIdentifier, httpContext.RequestAborted);
            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await WriteProblemAsync(httpContext, StatusCodes.Status503ServiceUnavailable, "Service unavailable.");
                return;
            }
        }

        await next(httpContext);
    }

    internal static bool TryReadOrganizationId(HttpRequest request, out Guid organizationId)
    {
        organizationId = default;
        var values = request.Headers["X-Organization-Id"];
        if (values.Count != 1)
        {
            return false;
        }

        var value = values[0];
        return value is not null &&
            value.Length == 36 &&
            value == value.Trim() &&
            Guid.TryParseExact(value, "D", out organizationId) &&
            organizationId != Guid.Empty;
    }

    private static Task WriteProblemAsync(HttpContext context, int statusCode, string title)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsJsonAsync(new
        {
            type = "about:blank",
            title,
            status = statusCode,
        }, context.RequestAborted);
    }
}
