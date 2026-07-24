using Microsoft.AspNetCore.Http;

namespace Realtime.Endpoints.Connection;

internal static class RealtimeOrganizationSelector
{
    private static readonly string[] ForbiddenKeys = ["group", "group_name", "org_group"];

    internal static bool TryRead(HttpRequest request, out Guid organizationId)
    {
        organizationId = Guid.Empty;
        if (ForbiddenKeys.Any(request.Query.ContainsKey))
        {
            return false;
        }

        var values = request.Query["organization_id"];
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
}
