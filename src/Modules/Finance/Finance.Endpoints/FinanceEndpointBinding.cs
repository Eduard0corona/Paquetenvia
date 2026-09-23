using Finance.Application;
using Microsoft.AspNetCore.Http;
using Organizations.Application.Session;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;

namespace Finance.Endpoints;

/// <summary>Request binding and problem shaping shared by the Finance endpoints.</summary>
internal static class FinanceEndpointBinding
{
    internal static bool TrySession(
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        out Guid actorId)
    {
        actorId = default;
        if (!session.IsActive || session.UserId is not { } id || !tenantContext.IsSelected)
        {
            return false;
        }

        actorId = id;
        return true;
    }

    internal static bool TryReadIdempotencyKey(HttpRequest request, out string key)
    {
        key = string.Empty;
        var values = request.Headers["Idempotency-Key"];
        if (values.Count != 1 || !IdempotencyKeyPolicy.IsValid(values[0]))
        {
            return false;
        }

        key = values[0]!;
        return true;
    }

    internal static bool TryGuid(string value, out Guid parsed) =>
        Guid.TryParseExact(value, "D", out parsed) && parsed != Guid.Empty &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    internal static string PublicCode(FinanceConflictCode code) => code switch
    {
        FinanceConflictCode.InvalidRequest => "INVALID_REQUEST",
        FinanceConflictCode.CodNotExpected => "COD_NOT_EXPECTED",
        FinanceConflictCode.CodAmountMismatch => "COD_AMOUNT_MISMATCH",
        FinanceConflictCode.CodAlreadyRecorded => "COD_ALREADY_RECORDED",
        FinanceConflictCode.CodStateConflict => "COD_STATE_CONFLICT",
        FinanceConflictCode.OrderStateConflict => "ORDER_STATE_CONFLICT",
        _ => "CONFLICT",
    };

    internal static IResult Conflict(string code) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Conflict.",
        extensions: new Dictionary<string, object?> { ["code"] = code });

    internal static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");

    internal static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not Found.");
}
