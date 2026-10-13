using Microsoft.AspNetCore.Http.Features;
using Organizations.Application.Session;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Domain.Tenancy;
using Paqueteria.Infrastructure.Observability;

namespace Paqueteria.Api.Tenancy;

/// <summary>
/// OBS-003-TENANT-PSEUDONYM-2026-10-12 ("Seudónimo de empresa (Recommended)"): once the tenant context middleware has
/// selected the request's organization, the rest of the request runs in an organization context. Its log lines carry
/// the organization pseudonym and the actor role kind (also when the export is off), and a sampled request span carries
/// the same two attributes. The organization id itself is never written, nor the user, the subject or any other
/// identifier. Requests without a selected organization (public tracking, sign-in, probes) carry neither.
/// </summary>
public static class TelemetryOrganizationContextMiddleware
{
    /// <summary>Call right after the tenant context middleware.</summary>
    public static IApplicationBuilder UseTelemetryOrganizationContext(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(static async (context, next) =>
        {
            var tenant = context.RequestServices.GetRequiredService<RequestTenantContext>();
            if (!tenant.IsSelected)
            {
                await next(context);
                return;
            }

            var pseudonym = context.RequestServices.GetRequiredService<IOrganizationPseudonymizer>()
                .Pseudonymize(tenant.OrganizationId);
            var roleKind = RoleKind(
                context.RequestServices.GetRequiredService<IOrganizationRequestSession>(),
                tenant.OrganizationId);
            if (context.Features.Get<IHttpActivityFeature>()?.Activity is { IsAllDataRequested: true } activity)
            {
                TelemetryExportPolicy.SetOrganization(activity, pseudonym, roleKind);
            }

            using (TelemetryOrganizationContext.Enter(pseudonym, roleKind))
            {
                await next(context);
            }
        });
    }

    /// <summary>The actor's roles in the organization as their contract values, sorted and comma separated.</summary>
    public static string RoleKind(IOrganizationRequestSession session, Guid organizationId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var roles = Enum.GetValues<OrganizationRole>()
            .Where(role => session.HasRole(organizationId, role))
            .Select(role => role.ToContractValue())
            .Order(StringComparer.Ordinal)
            .ToArray();
        return roles.Length == 0 ? "NONE" : string.Join(',', roles);
    }
}
