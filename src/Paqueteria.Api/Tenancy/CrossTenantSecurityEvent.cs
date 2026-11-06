using Organizations.Application.Session;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Infrastructure.Observability;

namespace Paqueteria.Api.Tenancy;

/// <summary>
/// REL-001-CROSS-TENANT-SECURITY-EVENT-2026-11-06 (owner answer S4, "Evento en el log (Recommended)"): the security event
/// of the AI-09 scenario "Un aliado no puede leer la orden de otro aliado". The API writes one structured log line with
/// the fixed event <see cref="Id"/> in two cases:
/// <list type="bullet">
/// <item><see cref="OrganizationRefused"/>: the tenant context middleware refused the requested organization with the
/// uniform 403, because the actor has no active membership in it. Another tenant's, an inactive and an unknown
/// organization are refused alike, before anything about them is read.</item>
/// <item><see cref="OrderNotFound"/>: a read of an order (a GET whose route template starts with
/// <see cref="OrderReadRoutePrefix"/>) ended in the uniform 404 inside the selected organization. A missing order and
/// another tenant's order give the same 404, so they give the same line.</item>
/// </list>
/// </summary>
/// <remarks>
/// The line carries only the refusal type and the route template, never the raw path. The OBS-003 log enrichment adds
/// the pseudonym of the actor's own organization and the actor's role kind in it, as on every line written inside an
/// organization context (OBS-003-TENANT-PSEUDONYM-2026-10-12), and the request's trace id, as on every line written
/// inside a request. The actor's own organization is the selected one for a 404; for a 403 it is the organization of the
/// actor's default active membership, or of the only organization the actor is an active member of, and otherwise there
/// is none and the line carries neither the pseudonym nor the role kind. The line never carries an order id, an
/// organization id, a user id, an e-mail, an IP or the <c>X-Organization-Id</c> value. It writes nothing to the database.
/// Its id is outside the OBS-002 block, so no alert or workbook reads it; it reaches Log Analytics with the other console
/// lines.
/// </remarks>
public static partial class CrossTenantSecurityEvent
{
    /// <summary>The fixed event id, outside the OBS-002 block 4600-4699.</summary>
    public const int Id = 4201;

    public const string Name = "CrossTenantAccessRefused";

    /// <summary>The 403 of the tenant context middleware for an organization the actor is not an active member of.</summary>
    public const string OrganizationRefused = "ORGANIZATION_REFUSED";

    /// <summary>The uniform 404 of a read of an order in the selected organization.</summary>
    public const string OrderNotFound = "ORDER_NOT_FOUND";

    /// <summary>The route value of a request that matched no route endpoint.</summary>
    public const string UnknownRoute = "UNKNOWN";

    /// <summary>The route templates of the order reads: the order and its sub-resources.</summary>
    public const string OrderReadRoutePrefix = "/api/v1/orders/{orderId";

    /// <summary>Call right before the tenant context middleware, so it sees both its refusal and the endpoint's answer.</summary>
    public static IApplicationBuilder UseCrossTenantSecurityEvents(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(static async (context, next) =>
        {
            await next(context);

            var tenant = context.RequestServices.GetRequiredService<RequestTenantContext>();
            string? refusal = null;
            if (tenant.OrganizationRefused)
            {
                refusal = OrganizationRefused;
            }
            else if (tenant.IsSelected &&
                     context.Response.StatusCode == StatusCodes.Status404NotFound &&
                     IsOrderRead(context.Request.Method, RouteTemplate(context.GetEndpoint())))
            {
                refusal = OrderNotFound;
            }

            if (refusal is null)
            {
                return;
            }

            var session = context.RequestServices.GetRequiredService<IOrganizationRequestSession>();
            var ownOrganization = tenant.IsSelected ? tenant.OrganizationId : OwnOrganization(session);
            using var organization = ownOrganization is { } organizationId
                ? TelemetryOrganizationContext.Enter(
                    context.RequestServices.GetRequiredService<IOrganizationPseudonymizer>().Pseudonymize(organizationId),
                    TelemetryOrganizationContextMiddleware.RoleKind(session, organizationId))
                : null;
            LogRefused(
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CrossTenantSecurityEvent)),
                refusal,
                RouteTemplate(context.GetEndpoint()));
        });
    }

    /// <summary>The route template of the endpoint, never the request path.</summary>
    public static string RouteTemplate(Endpoint? endpoint) =>
        endpoint is RouteEndpoint { RoutePattern.RawText: { Length: > 0 } template } ? template : UnknownRoute;

    /// <summary>True for a GET of an order or of one of its sub-resources.</summary>
    public static bool IsOrderRead(string method, string routeTemplate) =>
        HttpMethods.IsGet(method) && routeTemplate.StartsWith(OrderReadRoutePrefix, StringComparison.Ordinal);

    /// <summary>
    /// The organization of the actor's default active membership, else the only organization the actor is an active
    /// member of, else none. Only the session's own memberships are read.
    /// </summary>
    public static Guid? OwnOrganization(IOrganizationRequestSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var memberships = session.ActiveMemberships;
        if (memberships.FirstOrDefault(membership => membership.IsDefault) is { } preferred)
        {
            return preferred.OrganizationId;
        }

        var organizations = memberships.Select(membership => membership.OrganizationId).Distinct().Take(2).ToArray();
        return organizations.Length == 1 ? organizations[0] : null;
    }

    [LoggerMessage(
        EventId = Id,
        EventName = Name,
        Level = LogLevel.Warning,
        Message = "Cross-tenant access refused ({RefusalType}) on {RouteTemplate}.")]
    private static partial void LogRefused(ILogger logger, string refusalType, string routeTemplate);
}
