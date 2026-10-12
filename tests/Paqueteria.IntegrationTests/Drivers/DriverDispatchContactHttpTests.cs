using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Paqueteria.IntegrationTests.Drivers;

/// <summary>
/// DRV-DISPATCH-CONTACT-2026-10-12 (project owner, "Sí, configurable (Recommended)") over HTTP: getMyDispatchContact
/// answers the closed object {"phone_e164": ...} to a DRIVER of the selected organization only, with that
/// organization's number or null, never another organization's, and always Cache-Control no-store. The numbers are
/// synthetic.
/// </summary>
public sealed class DriverDispatchContactHttpTests(
    DriverDispatchContactHttpTests.ConfiguredFactory configured,
    DriverDispatchContactHttpTests.ForeignOnlyFactory foreignOnly)
    : IClassFixture<DriverDispatchContactHttpTests.ConfiguredFactory>,
        IClassFixture<DriverDispatchContactHttpTests.ForeignOnlyFactory>
{
    private const string Path = "/api/v1/driver/me/dispatch-contact";
    private const string OwnNumber = "+522000000001";
    private const string ForeignNumber = "+522000000002";

    [Fact]
    public async Task A_driver_reads_the_number_of_its_selected_organization_and_nothing_else()
    {
        using var client = configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var profile in new[] { MockIdentityProfiles.ActiveDriver, MockIdentityProfiles.ExternalDriver })
        {
            using var response = await client.SendAsync(Request(profile));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal(["phone_e164"], document.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal(OwnNumber, document.RootElement.GetProperty("phone_e164").GetString());
            Assert.DoesNotContain(ForeignNumber, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Without_a_number_for_the_selected_organization_the_answer_is_null()
    {
        using var client = foreignOnly.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.SendAsync(Request(MockIdentityProfiles.ActiveDriver));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(["phone_e164"], document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("phone_e164").ValueKind);
        // The organization that has one is never answered in its place.
        Assert.DoesNotContain(ForeignNumber, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_a_driver_member_of_the_selected_organization_is_answered()
    {
        using var client = configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var refusals = new List<HttpResponseMessage>();
        try
        {
            using (var anonymous = await client.SendAsync(Request(null)))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
                await AssertNoNumberAsync(anonymous);
            }

            // No organization header, a malformed one, and an organization the driver is not a member of (one
            // that has a number configured).
            refusals.Add(await client.SendAsync(Request(MockIdentityProfiles.ActiveDriver, organization: null)));
            refusals.Add(await client.SendAsync(Request(MockIdentityProfiles.ActiveDriver, organization: "not-a-uuid")));
            refusals.Add(await client.SendAsync(Request(
                MockIdentityProfiles.ActiveDriver,
                organization: MockIdentityProfiles.ForeignOrganizationId.ToString("D"))));
            foreach (var profile in new[]
                     {
                         MockIdentityProfiles.ActiveViewer,
                         MockIdentityProfiles.ActiveDispatcher,
                         MockIdentityProfiles.ActivePlatformAdminMfa,
                         MockIdentityProfiles.ActiveFinanceMfa,
                         MockIdentityProfiles.ActiveAllyAdmin,
                         MockIdentityProfiles.ActiveBusinessAdmin,
                         MockIdentityProfiles.ActiveBusinessOperator,
                         MockIdentityProfiles.ActiveMultiOrganization,
                         MockIdentityProfiles.SuspendedMembership,
                         MockIdentityProfiles.RevokedMembership,
                         MockIdentityProfiles.ActiveWithoutMemberships,
                     })
            {
                refusals.Add(await client.SendAsync(Request(profile)));
            }

            foreach (var refusal in refusals)
            {
                Assert.Equal(HttpStatusCode.Forbidden, refusal.StatusCode);
                await AssertNoNumberAsync(refusal);
            }
        }
        finally
        {
            refusals.ForEach(response => response.Dispose());
        }
    }

    private static async Task AssertNoNumberAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(OwnNumber, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ForeignNumber, body, StringComparison.Ordinal);
        Assert.DoesNotContain("phone_e164", body, StringComparison.Ordinal);
    }

    private static HttpRequestMessage Request(
        string? profile,
        string? organization = "11111111-1111-1111-1111-111111111111")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Path);
        if (profile is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        }

        if (organization is not null)
        {
            request.Headers.Add("X-Organization-Id", organization);
        }

        return request;
    }

    /// <summary>A number for the drivers' organization and another one for a foreign organization.</summary>
    public sealed class ConfiguredFactory : DispatchContactFactory
    {
        protected override Dictionary<string, string?> Contacts() => new()
        {
            ["Drivers:DispatchContacts:0:OrganizationId"] = MockIdentityProfiles.ForeignOrganizationId.ToString("D"),
            ["Drivers:DispatchContacts:0:PhoneE164"] = ForeignNumber,
            ["Drivers:DispatchContacts:1:OrganizationId"] = MockIdentityProfiles.ViewerOrganizationId.ToString("D"),
            ["Drivers:DispatchContacts:1:PhoneE164"] = OwnNumber,
        };
    }

    /// <summary>Only a foreign organization has a number; the drivers' organization has none.</summary>
    public sealed class ForeignOnlyFactory : DispatchContactFactory
    {
        protected override Dictionary<string, string?> Contacts() => new()
        {
            ["Drivers:DispatchContacts:0:OrganizationId"] = MockIdentityProfiles.ForeignOrganizationId.ToString("D"),
            ["Drivers:DispatchContacts:0:PhoneE164"] = ForeignNumber,
        };
    }

    public abstract class DispatchContactFactory : WebApplicationFactory<Program>
    {
        protected abstract Dictionary<string, string?> Contacts();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["Authentication:Provider"] = "Mock",
                    ["IdentityBootstrap:Provider"] = "Mock",
                };
                foreach (var (key, value) in Contacts())
                {
                    settings[key] = value;
                }

                configuration.AddInMemoryCollection(settings);
            });
        }
    }
}
