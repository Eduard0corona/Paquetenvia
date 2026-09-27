using System.Text.Json.Serialization;
using Organizations.Application.Registration;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;

namespace Organizations.Endpoints;

/// <summary>
/// REG-001 (AUTH-OPEN-REGISTRATION): self-service onboarding, the caller's own applications and the
/// PLATFORM_ADMIN decision on pending ALLY organizations. Onboarding and own applications need no
/// tenant context because the caller may have none yet; the ALLY operations run in the selected
/// PLATFORM organization and pass the D5 capability gate before anything is read.
/// </summary>
public static class RegistrationEndpoints
{
    public const string IdempotencyConflictCode = "IDEMPOTENCY_CONFLICT";
    public const string OrganizationLimitReachedCode = "ORGANIZATION_LIMIT_REACHED";
    public const string AllyDecisionConflictCode = "ALLY_DECISION_CONFLICT";

    public static IEndpointRouteBuilder MapRegistrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/onboarding/organizations", CreateOrganizationAsync)
            .RequireAuthorization("Identity.Active")
            .WithName("createOnboardingOrganization")
            .WithTags("Onboarding")
            .Accepts<CreateOnboardingOrganizationRequest>("application/json")
            .Produces<OnboardingOrganizationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/v1/me/organization-applications", ListOwnApplicationsAsync)
            .RequireAuthorization("Identity.Active")
            .WithName("listMyOrganizationApplications")
            .WithTags("Onboarding")
            .Produces<IReadOnlyList<OrganizationApplicationResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/v1/platform/ally-applications", ListPendingAlliesAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext()
            .WithName("listPendingAllyOrganizations")
            .WithTags("Platform")
            .Produces<IReadOnlyList<PendingAllyOrganizationResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapPost("/api/v1/platform/ally-applications/{organizationId}/decision", DecideAllyAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext()
            .WithName("decideAllyOrganization")
            .WithTags("Platform")
            .Accepts<AllyDecisionRequest>("application/json")
            .Produces<AllyDecisionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> CreateOrganizationAsync(
        HttpContext httpContext,
        CreateOnboardingOrganizationRequest? request,
        IOrganizationRequestSession session,
        ISelfServiceRegistrationService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadIdempotencyKey(httpContext.Request, out var idempotencyKey) ||
            request is null ||
            !SelfServiceRegistrationLimits.OrganizationTypes.Contains(request.OrganizationType ?? string.Empty) ||
            !IsValidName(request.LegalName, SelfServiceRegistrationLimits.LegalNameMaxLength) ||
            !IsValidName(request.DisplayName, SelfServiceRegistrationLimits.DisplayNameMaxLength))
        {
            return BadRequest();
        }

        // AUTH-EMAIL-VERIFIED-REQUIRED: only an active, linked user whose provider asserted a verified email.
        if (!session.IsActive || session.UserId is not { } userId || !session.EmailVerified)
        {
            return Forbidden();
        }

        return await RunAsync(async () =>
        {
            var result = await service.CreateOrganizationAsync(
                new CreateSelfServiceOrganizationCommand(
                    userId,
                    session.EmailVerified,
                    idempotencyKey,
                    request.OrganizationType!,
                    request.LegalName!,
                    request.DisplayName!,
                    httpContext.TraceIdentifier),
                cancellationToken);
            return result.Outcome switch
            {
                SelfServiceOrganizationOutcome.Created or SelfServiceOrganizationOutcome.Replayed
                    when result.Organization is { } organization =>
                    Results.Created("/api/v1/me/organization-applications", ToResponse(organization)),
                SelfServiceOrganizationOutcome.IdempotencyConflict => Conflict(IdempotencyConflictCode),
                SelfServiceOrganizationOutcome.LimitReached => Conflict(OrganizationLimitReachedCode),
                SelfServiceOrganizationOutcome.Forbidden => Forbidden(),
                _ => Unavailable(),
            };
        }, cancellationToken);
    }

    private static async Task<IResult> ListOwnApplicationsAsync(
        IOrganizationRequestSession session,
        ISelfServiceRegistrationService service,
        CancellationToken cancellationToken)
    {
        if (!session.IsActive || session.UserId is not { } userId)
        {
            return Forbidden();
        }

        return await RunAsync(async () => Results.Ok(
            (await service.ListOwnApplicationsAsync(userId, cancellationToken))
                .Select(application => new OrganizationApplicationResponse(
                    application.OrganizationId,
                    application.OrganizationType,
                    application.DisplayName,
                    application.Status,
                    application.CreatedAt))
                .ToArray()), cancellationToken);
    }

    private static async Task<IResult> ListPendingAlliesAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ISelfServiceRegistrationService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadLimit(httpContext.Request, out var limit))
        {
            return BadRequest();
        }

        if (!session.IsActive || session.UserId is not { } actorId || !tenantContext.IsSelected)
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.ListPendingAllyOrganizations) is { } denied)
        {
            return denied;
        }

        return await RunAsync(async () =>
        {
            var pending = await service.ListPendingAlliesAsync(
                actorId, tenantContext.OrganizationId, limit, cancellationToken);
            return pending is null
                ? Forbidden()
                : Results.Ok(pending.Select(ally => new PendingAllyOrganizationResponse(
                    ally.OrganizationId, ally.LegalName, ally.DisplayName, ally.CreatedAt)).ToArray());
        }, cancellationToken);
    }

    private static async Task<IResult> DecideAllyAsync(
        HttpContext httpContext,
        string organizationId,
        AllyDecisionRequest? request,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ISelfServiceRegistrationService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(organizationId, "D", out var allyOrganizationId) ||
            allyOrganizationId == Guid.Empty ||
            request?.Decision is not ("APPROVE" or "REJECT"))
        {
            return BadRequest();
        }

        if (!session.IsActive || session.UserId is not { } actorId || !tenantContext.IsSelected)
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.DecideAllyOrganization) is { } denied)
        {
            return denied;
        }

        return await RunAsync(async () =>
        {
            var outcome = await service.DecideAllyAsync(
                actorId,
                tenantContext.OrganizationId,
                allyOrganizationId,
                request.Decision == "APPROVE",
                httpContext.TraceIdentifier,
                cancellationToken);
            return outcome switch
            {
                AllyDecisionOutcome.Approved => Results.Ok(new AllyDecisionResponse(allyOrganizationId, "ACTIVE")),
                AllyDecisionOutcome.Rejected => Results.Ok(new AllyDecisionResponse(allyOrganizationId, "CLOSED")),
                AllyDecisionOutcome.NotFound => NotFound(),
                AllyDecisionOutcome.Conflict => Conflict(AllyDecisionConflictCode),
                _ => Forbidden(),
            };
        }, cancellationToken);
    }

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
        limit = SelfServiceRegistrationLimits.DefaultPendingPageSize;
        var values = request.Query["limit"];
        if (values.Count == 0)
        {
            return true;
        }

        return values.Count == 1 &&
            int.TryParse(values[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out limit) &&
            limit is >= 1 and <= SelfServiceRegistrationLimits.MaximumPendingPageSize;
    }

    private static bool IsValidName(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        value == value.Trim() &&
        !value.Any(char.IsControl);

    private static OnboardingOrganizationResponse ToResponse(SelfServiceOrganization organization) =>
        new(
            organization.OrganizationId,
            organization.OrganizationType,
            organization.LegalName,
            organization.DisplayName,
            organization.Status,
            organization.Role);

    private static IResult BadRequest() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request.");

    private static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found.");

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");

    private static IResult Conflict(string code) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Conflict.",
        extensions: new Dictionary<string, object?> { ["code"] = code });
}

public sealed record CreateOnboardingOrganizationRequest(
    [property: JsonPropertyName("organization_type")] string? OrganizationType,
    [property: JsonPropertyName("legal_name")] string? LegalName,
    [property: JsonPropertyName("display_name")] string? DisplayName);

public sealed record OnboardingOrganizationResponse(
    [property: JsonPropertyName("organization_id")] Guid OrganizationId,
    [property: JsonPropertyName("organization_type")] string OrganizationType,
    [property: JsonPropertyName("legal_name")] string LegalName,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("role")] string Role);

public sealed record OrganizationApplicationResponse(
    [property: JsonPropertyName("organization_id")] Guid OrganizationId,
    [property: JsonPropertyName("organization_type")] string OrganizationType,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record PendingAllyOrganizationResponse(
    [property: JsonPropertyName("organization_id")] Guid OrganizationId,
    [property: JsonPropertyName("legal_name")] string LegalName,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record AllyDecisionRequest(
    [property: JsonPropertyName("decision")] string? Decision);

public sealed record AllyDecisionResponse(
    [property: JsonPropertyName("organization_id")] Guid OrganizationId,
    [property: JsonPropertyName("status")] string Status);
