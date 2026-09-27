using System.Text.Json.Serialization;
using Organizations.Application.Registration;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Security;
using Paqueteria.Application.Tenancy;

namespace Organizations.Endpoints;

/// <summary>
/// REG-002 (REG-JOIN-EXISTING-BY-EMAIL): an administrator of the selected organization adds a person by
/// email and role, lists the organization's entries (never an email) and renews or revokes one. The path
/// organization must be the one selected with X-Organization-Id, and the D5 capability gate (MFA included)
/// runs before anything is read. Adding answers the same 202 whether or not the email belongs to an
/// account: accounts are never looked at.
/// </summary>
public static class PendingMembershipEndpoints
{
    public const string IdempotencyConflictCode = "IDEMPOTENCY_CONFLICT";
    public const string NotPendingCode = "PENDING_MEMBERSHIP_NOT_PENDING";
    private const string CollectionPath = "/api/v1/organizations/{organizationId}/pending-memberships";

    public static IEndpointRouteBuilder MapPendingMembershipEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(CollectionPath, AddAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("addPendingMembership")
            .WithTags("Organizations")
            .Accepts<AddPendingMembershipRequest>("application/json")
            .Produces<PendingMembershipResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet(CollectionPath, ListAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("listPendingMemberships")
            .WithTags("Organizations")
            .Produces<IReadOnlyList<PendingMembershipResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapPost(CollectionPath + "/{pendingMembershipId}/renew", RenewAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("renewPendingMembership")
            .WithTags("Organizations")
            .Produces<PendingMembershipResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapPost(CollectionPath + "/{pendingMembershipId}/revoke", RevokeAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("revokePendingMembership")
            .WithTags("Organizations")
            .Produces<PendingMembershipResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> AddAsync(
        HttpContext httpContext,
        string organizationId,
        AddPendingMembershipRequest? request,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IPendingMembershipService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadOrganization(organizationId, out var pathOrganizationId) ||
            !TryReadIdempotencyKey(httpContext.Request, out var idempotencyKey) ||
            request is null ||
            !PendingMembershipLimits.Roles.Contains(request.Role ?? string.Empty) ||
            !EmailLookupNormalizer.TryNormalize(request.Email, out _))
        {
            return BadRequest();
        }

        if (Gate(session, tenantContext, pathOrganizationId, TenantCapabilities.AddPendingMembership, out var actorId) is { } denied)
        {
            return denied;
        }

        return await RunAsync(async () => ToResult(
            await service.AddAsync(
                new AddPendingMembershipCommand(
                    actorId,
                    pathOrganizationId,
                    idempotencyKey,
                    request.Email!,
                    request.Role!,
                    httpContext.TraceIdentifier),
                cancellationToken),
            StatusCodes.Status202Accepted), cancellationToken);
    }

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        string organizationId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IPendingMembershipService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadOrganization(organizationId, out var pathOrganizationId) ||
            !TryReadLimit(httpContext.Request, out var limit))
        {
            return BadRequest();
        }

        if (Gate(session, tenantContext, pathOrganizationId, TenantCapabilities.ListPendingMemberships, out var actorId) is { } denied)
        {
            return denied;
        }

        return await RunAsync(async () => Results.Ok(
            (await service.ListAsync(actorId, pathOrganizationId, limit, cancellationToken))
                .Select(ToResponse)
                .ToArray()), cancellationToken);
    }

    private static Task<IResult> RenewAsync(
        HttpContext httpContext,
        string organizationId,
        string pendingMembershipId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IPendingMembershipService service,
        CancellationToken cancellationToken) =>
        ActAsync(
            httpContext, organizationId, pendingMembershipId, session, tenantContext,
            TenantCapabilities.RenewPendingMembership, service.RenewAsync, cancellationToken);

    private static Task<IResult> RevokeAsync(
        HttpContext httpContext,
        string organizationId,
        string pendingMembershipId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IPendingMembershipService service,
        CancellationToken cancellationToken) =>
        ActAsync(
            httpContext, organizationId, pendingMembershipId, session, tenantContext,
            TenantCapabilities.RevokePendingMembership, service.RevokeAsync, cancellationToken);

    private static async Task<IResult> ActAsync(
        HttpContext httpContext,
        string organizationId,
        string pendingMembershipId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        TenantCapability capability,
        Func<PendingMembershipActionCommand, CancellationToken, Task<PendingMembershipResult>> action,
        CancellationToken cancellationToken)
    {
        if (!TryReadOrganization(organizationId, out var pathOrganizationId) ||
            !Guid.TryParseExact(pendingMembershipId, "D", out var entryId) ||
            entryId == Guid.Empty ||
            !TryReadIdempotencyKey(httpContext.Request, out var idempotencyKey))
        {
            return BadRequest();
        }

        if (Gate(session, tenantContext, pathOrganizationId, capability, out var actorId) is { } denied)
        {
            return denied;
        }

        return await RunAsync(async () => ToResult(
            await action(
                new PendingMembershipActionCommand(
                    actorId, pathOrganizationId, entryId, idempotencyKey, httpContext.TraceIdentifier),
                cancellationToken),
            StatusCodes.Status200OK), cancellationToken);
    }

    /// <summary>
    /// The selected organization (X-Organization-Id) must be the path organization; then the D5 capability
    /// in it, which answers MFA_REQUIRED when a second factor is the only thing missing.
    /// </summary>
    private static IResult? Gate(
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        Guid pathOrganizationId,
        TenantCapability capability,
        out Guid actorId)
    {
        actorId = Guid.Empty;
        if (!session.IsActive || session.UserId is not { } userId || !tenantContext.IsSelected ||
            tenantContext.OrganizationId != pathOrganizationId)
        {
            return TenantCapabilityGate.Forbidden();
        }

        actorId = userId;
        return TenantCapabilityGate.Deny(session, tenantContext, capability);
    }

    private static IResult ToResult(PendingMembershipResult result, int successStatusCode) => result.Outcome switch
    {
        PendingMembershipOutcome.Succeeded when result.Entry is { } entry =>
            Results.Json(ToResponse(entry), statusCode: successStatusCode),
        PendingMembershipOutcome.IdempotencyConflict => Conflict(IdempotencyConflictCode),
        PendingMembershipOutcome.NotPending => Conflict(NotPendingCode),
        PendingMembershipOutcome.NotFound =>
            Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found."),
        PendingMembershipOutcome.Forbidden => TenantCapabilityGate.Forbidden(),
        _ => Unavailable(),
    };

    private static async Task<IResult> RunAsync(Func<Task<IResult>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }
        catch (SelfServiceRegistrationUnavailableException)
        {
            return Unavailable();
        }
    }

    private static bool TryReadOrganization(string value, out Guid organizationId) =>
        Guid.TryParseExact(value, "D", out organizationId) && organizationId != Guid.Empty;

    private static bool TryReadIdempotencyKey(HttpRequest request, out string value)
    {
        value = string.Empty;
        var values = request.Headers["Idempotency-Key"];
        if (values.Count != 1 || !IdempotencyKeyPolicy.IsValid(values[0]))
        {
            return false;
        }

        value = values[0]!;
        return true;
    }

    private static bool TryReadLimit(HttpRequest request, out int limit)
    {
        limit = PendingMembershipLimits.DefaultPageSize;
        var values = request.Query["limit"];
        if (values.Count == 0)
        {
            return true;
        }

        return values.Count == 1 &&
            int.TryParse(values[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out limit) &&
            limit is >= 1 and <= PendingMembershipLimits.MaximumPageSize;
    }

    private static PendingMembershipResponse ToResponse(PendingMembership entry) =>
        new(entry.Id, entry.Role, entry.Status, entry.CreatedAt, entry.ExpiresAt);

    private static IResult BadRequest() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request.");

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");

    private static IResult Conflict(string code) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Conflict.",
        extensions: new Dictionary<string, object?> { ["code"] = code });
}

public sealed record AddPendingMembershipRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("role")] string? Role);

public sealed record PendingMembershipResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
