using Organizations.Application.OrganizationContexts;
using Organizations.Application.Session;

namespace Organizations.Endpoints;

public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/me/organization-contexts", ListOrganizationContextsAsync)
            .RequireAuthorization("Identity.Active")
            .WithName("listOrganizationContexts")
            .WithTags("Identity");

        return endpoints;
    }

    private static async Task<IResult> ListOrganizationContextsAsync(
        IOrganizationRequestSession session,
        IOrganizationContextReader reader,
        CancellationToken cancellationToken)
    {
        if (!session.IsActive || session.UserId is not { } userId)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");
        }

        var memberships = session.ActiveMemberships
            .Select(membership => new AuthorizedOrganizationMembership(
                membership.OrganizationId,
                membership.Role,
                membership.IsDefault))
            .ToArray();

        try
        {
            return Results.Ok(await reader.ReadAsync(userId, memberships, cancellationToken));
        }
        catch (OrganizationContextUnavailableException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
        }
    }
}
