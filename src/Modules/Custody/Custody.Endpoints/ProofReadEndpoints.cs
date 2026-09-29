using System.Text.Json.Serialization;
using Custody.Application.ProofUploads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Tenancy;

namespace Custody.Endpoints;

/// <summary>
/// API-INC-LIST-PROOFS-2026-09-29: listOrderProofs, the proof metadata the incident desk picks
/// evidence from. It is kept apart from <see cref="ProofEndpoints"/>, whose upload surface never
/// publishes a GET: this route returns metadata only and never a download, a storage key or a URL.
/// </summary>
public static class ProofReadEndpoints
{
    public static IEndpointRouteBuilder MapProofReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/orders/{orderId}/proofs", ListOrderProofsAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("listOrderProofs")
            .WithTags("Custody")
            .Produces<ProofPageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    /// <summary>
    /// Capability first, from the session and again inside the tenant transaction; then a malformed,
    /// missing or foreign order is the uniform 404; then an unknown or repeated query parameter, or a
    /// cursor this operation did not issue for this order, is INVALID_REQUEST.
    /// </summary>
    private static async Task<IResult> ListOrderProofsAsync(
        string orderId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IProofReadService service,
        CancellationToken cancellationToken)
    {
        if (!session.IsActive ||
            session.UserId is not { } actorId ||
            actorId == Guid.Empty ||
            !tenantContext.IsSelected ||
            tenantContext.OrganizationId == Guid.Empty)
        {
            return TenantCapabilityGate.Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.ListOrderProofs) is { } denied)
        {
            return denied;
        }

        if (!Guid.TryParseExact(orderId, "D", out var parsedOrderId) || parsedOrderId == Guid.Empty)
        {
            return NotFound();
        }

        if (!TryReadCursor(httpContext.Request.Query, parsedOrderId, out var cursor))
        {
            return Conflict();
        }

        try
        {
            var page = await service.ListOrderProofsAsync(
                new ListOrderProofsQuery(
                    actorId,
                    tenantContext.OrganizationId,
                    session.MfaSatisfied,
                    parsedOrderId,
                    cursor),
                cancellationToken);
            return Results.Ok(new ProofPageResponse(
                page.Items.Select(proof => new ProofResponse(proof.Id, proof.ProofType, proof.Sha256, proof.CapturedAt))
                    .ToArray(),
                page.NextCursor));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProofForbiddenException)
        {
            return TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.ListOrderProofs);
        }
        catch (ProofNotFoundException)
        {
            return NotFound();
        }
        catch (ProofConflictException)
        {
            return Conflict();
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service unavailable.");
        }
    }

    internal static bool TryReadCursor(IQueryCollection query, Guid orderId, out ProofCursor? cursor)
    {
        cursor = null;
        if (query.Keys.Any(key => !string.Equals(key, "cursor", StringComparison.Ordinal)) ||
            query.Any(pair => pair.Value.Count != 1))
        {
            return false;
        }

        return !query.TryGetValue("cursor", out var value) ||
            ProofCursorCodec.TryDecode(value[0], orderId, out cursor);
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not Found.");

    private static IResult Conflict() =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict.",
            extensions: new Dictionary<string, object?> { ["code"] = "INVALID_REQUEST" });
}

public sealed record ProofPageResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<ProofResponse> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);
