using System.Text.Json;
using Microsoft.Playwright;
using Organizations.Application.OrganizationRoles;
using Paqueteria.Domain.Tenancy;

namespace Paqueteria.IntegrationTests.Driver;

/// <summary>
/// ORG-MY-ROLES-2026-10-12: the driver PWA tests answer GET /api/v1/me/organization-roles with the API's own answer -
/// <see cref="MyOrganizationRolesResponse"/> holding the roles <see cref="MyOrganizationRoles.FromRoles"/> orders,
/// serialized with the web JSON defaults - so the app derives who reports "No se pudo entregar" from the shape the
/// server really sends. Nothing is injected into the session.
/// </summary>
internal static class DriverOrganizationRoles
{
    internal const string Route = "**/api/v1/me/organization-roles";

    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web);

    /// <summary>The API's answer for a person whose ACTIVE memberships there hold <paramref name="roles"/>.</summary>
    internal static string Answer(IReadOnlyCollection<OrganizationRole> roles) =>
        JsonSerializer.Serialize(new MyOrganizationRolesResponse(MyOrganizationRoles.FromRoles(roles)), ApiJson);

    /// <summary>Answers the page's roles read for <paramref name="roles"/> (DRIVER only when none is given).</summary>
    internal static Task RouteAsync(IPage page, params OrganizationRole[] roles)
    {
        var body = Answer(roles.Length == 0 ? [OrganizationRole.Driver] : roles);
        return page.RouteAsync(Route, route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json; charset=utf-8",
            Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
            Body = body,
        }));
    }

    /// <summary>An API older than ORG-MY-ROLES-2026-10-12, which does not serve the read: the uniform 404.</summary>
    internal static Task RouteOlderApiAsync(IPage page) =>
        page.RouteAsync(Route, route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 404,
            ContentType = "application/problem+json",
            Body = "{\"title\":\"Not Found\",\"status\":404}",
        }));

    /// <summary>Waits, at most ten seconds, until the phone holds <paramref name="count"/> saved answers.</summary>
    internal static async Task<JsonElement[]> WaitForSavedAsync(IPage page, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var saved = await ReadSavedAsync(page);
            if (saved.Length == count)
            {
                return saved;
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    /// <summary>The roles the phone saved, one entry per partition, read from IndexedDB.</summary>
    internal static async Task<JsonElement[]> ReadSavedAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(
            """
            () => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 5);
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const database = open.result;
                const request = database.transaction("organization_roles", "readonly")
                  .objectStore("organization_roles").getAll();
                request.onerror = () => {
                  database.close();
                  reject(request.error);
                };
                request.onsuccess = () => {
                  database.close();
                  resolve(JSON.stringify(request.result));
                };
              };
            })
            """);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
    }
}
