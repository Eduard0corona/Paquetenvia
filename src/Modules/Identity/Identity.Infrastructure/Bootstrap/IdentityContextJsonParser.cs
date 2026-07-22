using System.Text.Json;
using Identity.Application.Bootstrap;

namespace Identity.Infrastructure.Bootstrap;

public static class IdentityContextJsonParser
{
    public static ResolvedIdentityContext Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            RequireObjectWithProperties(root, "user_id", "status", "memberships");

            if (!Guid.TryParseExact(root.GetProperty("user_id").GetString(), "D", out var userId) ||
                userId == Guid.Empty)
            {
                throw InvalidContract("user_id must be a non-empty UUID in D format.");
            }

            if (root.GetProperty("status").ValueKind != JsonValueKind.String ||
                root.GetProperty("status").GetString() != "ACTIVE")
            {
                throw InvalidContract("status must be ACTIVE.");
            }

            var membershipsElement = root.GetProperty("memberships");
            if (membershipsElement.ValueKind != JsonValueKind.Array)
            {
                throw InvalidContract("memberships must be an array.");
            }

            var memberships = new List<IdentityContextMembership>();
            var uniqueMemberships = new HashSet<(Guid OrganizationId, OrganizationRole Role)>();
            foreach (var element in membershipsElement.EnumerateArray())
            {
                RequireObjectWithProperties(element, "organization_id", "role", "is_default");
                if (!Guid.TryParseExact(element.GetProperty("organization_id").GetString(), "D", out var organizationId) ||
                    organizationId == Guid.Empty)
                {
                    throw InvalidContract("organization_id must be a non-empty UUID in D format.");
                }

                var role = ParseRole(element.GetProperty("role"));
                var isDefaultElement = element.GetProperty("is_default");
                if (isDefaultElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw InvalidContract("is_default must be a boolean.");
                }

                if (!uniqueMemberships.Add((organizationId, role)))
                {
                    throw InvalidContract("Duplicate organization and role membership.");
                }

                memberships.Add(new IdentityContextMembership(
                    organizationId,
                    role,
                    isDefaultElement.GetBoolean()));
            }

            return new ResolvedIdentityContext(userId, IdentityContextStatus.Active, memberships);
        }
        catch (IdentityContextInfrastructureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw InvalidContract("Identity context JSON does not match the expected contract.", exception);
        }
    }

    private static OrganizationRole ParseRole(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw InvalidContract("role must be a string.");
        }

        return element.GetString() switch
        {
            "PLATFORM_ADMIN" => OrganizationRole.PlatformAdmin,
            "DISPATCHER" => OrganizationRole.Dispatcher,
            "FINANCE" => OrganizationRole.Finance,
            "ALLY_ADMIN" => OrganizationRole.AllyAdmin,
            "ALLY_OPERATOR" => OrganizationRole.AllyOperator,
            "BUSINESS_ADMIN" => OrganizationRole.BusinessAdmin,
            "BUSINESS_OPERATOR" => OrganizationRole.BusinessOperator,
            "DRIVER" => OrganizationRole.Driver,
            "VIEWER" => OrganizationRole.Viewer,
            _ => throw InvalidContract("Unknown organization role."),
        };
    }

    private static void RequireObjectWithProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidContract("Expected a JSON object.");
        }

        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length ||
            actual.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
            expected.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
        {
            throw InvalidContract("JSON object properties do not match the expected contract.");
        }
    }

    private static IdentityContextInfrastructureException InvalidContract(
        string message,
        Exception? innerException = null) =>
        new(message, innerException);
}
