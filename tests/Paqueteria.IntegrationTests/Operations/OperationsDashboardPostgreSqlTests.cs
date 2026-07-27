using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Paqueteria.IntegrationTests.Realtime;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Operations;

[Collection(OperationsDashboardPostgreSqlCollection.Name)]
[Trait("Category", "OperationsDashboardPostgreSql")]
public sealed class OperationsDashboardPostgreSqlTests(
    PostgreSqlSecurityWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly Guid ReadyOrderId =
        Guid.Parse("71000000-0000-0000-0000-000000000001");
    private static readonly Guid AssignedOrderId =
        Guid.Parse("71000000-0000-0000-0000-000000000002");

    public async Task InitializeAsync() => await SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Authorized_projection_is_tenant_safe_exact_and_private()
    {
        using var response = await SendAsync(
            MockIdentityProfiles.ActivePlatformAdminMfa,
            PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
            $"?order_id={AssignedOrderId:D}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("address", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cost_cents", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("24.81", json, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(
            ["generated_at", "items", "next_cursor"],
            root.EnumerateObject().Select(property => property.Name).Order().ToArray());
        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal("ORD_obs_assigned", item.GetProperty("public_id").GetString());
        Assert.Equal("Synthetic Viewer", item.GetProperty("owner").GetProperty("display_name").GetString());
        Assert.Equal(
            "Synthetic Viewer",
            item.GetProperty("operator").GetProperty("display_name").GetString());
        Assert.Equal("Synthetic Client", item.GetProperty("client").GetProperty("display_name").GetString());
        Assert.Equal("Synthetic Core", item.GetProperty("delivery_zone").GetProperty("name").GetString());
        Assert.Equal("OWN", item.GetProperty("assignment").GetProperty("assignment_type").GetString());
        Assert.Matches(
            "^DRV-[0-9a-f]{8}$",
            item.GetProperty("assignment").GetProperty("driver_reference").GetString()!);
        Assert.NotEqual(
            item.GetProperty("assignment").GetProperty("driver_id").GetString(),
            item.GetProperty("assignment").GetProperty("driver_reference").GetString());
        Assert.Equal(
            "AUTHORIZED_OVERRIDE",
            item.GetProperty("cost_warning").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("pickup_window").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("delivery_window").ValueKind);
        Assert.False(item.GetProperty("unassigned_alert").GetBoolean());
        Assert.Equal(23.25, item.GetProperty("latest_driver_location").GetProperty("lat").GetDouble());
    }

    [Fact]
    public async Task Authorization_precedence_and_tenant_isolation_are_enforced()
    {
        using var dispatcher = await SendAsync(
            MockIdentityProfiles.ActiveMultiOrganization,
            PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId);
        Assert.Equal(HttpStatusCode.OK, dispatcher.StatusCode);
        var dispatcherPage = await dispatcher.Content.ReadFromJsonAsync<JsonElement>();
        var dispatcherItems = dispatcherPage.GetProperty("items").EnumerateArray().ToArray();
        Assert.All(
            dispatcherItems,
            item => Assert.Equal(
                PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId,
                item.GetProperty("owner").GetProperty("organization_id").GetGuid()));

        foreach (var credential in new[]
        {
            MockIdentityProfiles.ActivePlatformAdminNoMfa,
            MockIdentityProfiles.ActiveDriver,
            MockIdentityProfiles.ActiveViewer,
            MockIdentityProfiles.ActiveWithoutMemberships,
            MockIdentityProfiles.SuspendedMembership,
        })
        {
            using var denied = await SendAsync(
                credential,
                PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using var missingOrganization = await SendAsync(
            MockIdentityProfiles.ActivePlatformAdminMfa,
            organizationId: null);
        Assert.Equal(HttpStatusCode.Forbidden, missingOrganization.StatusCode);

        using var invalidToken = await SendAsync(
            "invalid",
            PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidToken.StatusCode);

        using var client = factory.CreateClient();
        using var repeatedOrganization = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/operations/dashboard");
        repeatedOrganization.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            MockIdentityProfiles.ActivePlatformAdminMfa);
        repeatedOrganization.Headers.TryAddWithoutValidation(
            "X-Organization-Id",
            [
                PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId.ToString("D"),
                PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId.ToString("D"),
            ]);
        using var repeatedResponse = await client.SendAsync(repeatedOrganization);
        Assert.Equal(HttpStatusCode.Forbidden, repeatedResponse.StatusCode);

        using var malformedOrganization = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/operations/dashboard");
        malformedOrganization.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            MockIdentityProfiles.ActivePlatformAdminMfa);
        malformedOrganization.Headers.TryAddWithoutValidation(
            "X-Organization-Id",
            "not-an-organization");
        using var malformedResponse = await client.SendAsync(malformedOrganization);
        Assert.Equal(HttpStatusCode.Forbidden, malformedResponse.StatusCode);
    }

    [Fact]
    public async Task Filters_cursor_alerts_and_position_are_backed_by_persisted_rows()
    {
        using var unassigned = await SendAsync(
            MockIdentityProfiles.ActivePlatformAdminMfa,
            PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
            "?status=READY_FOR_PICKUP&unassigned=true");
        Assert.Equal(HttpStatusCode.OK, unassigned.StatusCode);
        var page = await unassigned.Content.ReadFromJsonAsync<JsonElement>();
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("ORD_obs_ready", item.GetProperty("public_id").GetString());
        Assert.True(item.GetProperty("unassigned_alert").GetBoolean());
        Assert.Equal(
            "BELOW_MINIMUM_SNAPSHOT",
            item.GetProperty("cost_warning").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("assignment").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("latest_driver_location").ValueKind);

        foreach (var query in new[]
        {
            "?service_type=URGENT",
            "?delivery_zone_id=73000000-0000-0000-0000-000000000001",
            "?client_account_id=74000000-0000-0000-0000-000000000001",
            "?owner_org_id=11111111-1111-1111-1111-111111111111",
            "?operator_org_id=11111111-1111-1111-1111-111111111111",
            "?created_from=2026-07-01T00%3A00%3A00Z&created_to=2026-07-31T00%3A00%3A00Z",
        })
        {
            using var filtered = await SendAsync(
                MockIdentityProfiles.ActivePlatformAdminMfa,
                PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                query);
            Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        }
    }

    [Fact]
    public async Task Invalid_queries_are_uniform_and_unknown_query_is_ignored()
    {
        foreach (var query in new[]
        {
            "?status=UNKNOWN",
            "?cursor=invalid",
            "?cursor=" + new string('a', 513),
            "?status=DRAFT&status=CONFIRMED",
            "?created_from=2026-01-01T00%3A00%3A00Z&created_to=2026-02-02T00%3A00%3A00Z",
        })
        {
            using var invalid = await SendAsync(
                MockIdentityProfiles.ActivePlatformAdminMfa,
                PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                query);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain(query, await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var unknown = await SendAsync(
            MockIdentityProfiles.ActivePlatformAdminMfa,
            PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
            "?unknown=value");
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
    }

    [Fact]
    public async Task Disabled_provider_fails_closed()
    {
        var recorder = new RealtimeAuthorizationRecorder();
        await using (var disabled = new RealtimeKestrelWebApplicationFactory(
                         factory.ApplicationConnectionString,
                         recorder,
                         configurationOverrides: new Dictionary<string, string?>
                         {
                             ["OperationsDashboard:Provider"] = "Disabled",
                         }))
        {
            using var client = new HttpClient { BaseAddress = disabled.Start() };
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "/api/v1/operations/dashboard");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                MockIdentityProfiles.ActivePlatformAdminMfa);
            request.Headers.Add(
                "X-Organization-Id",
                PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId.ToString("D"));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain(
                "disabled",
                await response.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        string credential,
        Guid? organizationId,
        string query = "")
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/operations/dashboard" + query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (organizationId is not null)
        {
            request.Headers.Add("X-Organization-Id", organizationId.Value.ToString("D"));
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await client.SendAsync(request);
    }

    private async Task SeedAsync()
    {
        await using var connection = new NpgsqlConnection(factory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO locations.service_areas(
              id,owner_org_id,city_id,name,polygon,status)
            VALUES (
              '72000000-0000-0000-0000-000000000001',
              '11111111-1111-1111-1111-111111111111',
              '33333333-3333-3333-3333-333333333333',
              'Synthetic Area',
              public.ST_GeomFromText('MULTIPOLYGON(((-108 24,-106 24,-106 26,-108 26,-108 24)))',4326),
              'ACTIVE')
            ON CONFLICT DO NOTHING;
            INSERT INTO locations.operating_zones(
              id,owner_org_id,service_area_id,name,zone_type,polygon,status)
            VALUES (
              '73000000-0000-0000-0000-000000000001',
              '11111111-1111-1111-1111-111111111111',
              '72000000-0000-0000-0000-000000000001',
              'Synthetic Core','CORE',
              public.ST_GeomFromText('MULTIPOLYGON(((-108 24,-106 24,-106 26,-108 26,-108 24)))',4326),
              'ACTIVE')
            ON CONFLICT DO NOTHING;
            INSERT INTO clients.client_accounts(id,owner_org_id,name,status)
            VALUES (
              '74000000-0000-0000-0000-000000000001',
              '11111111-1111-1111-1111-111111111111',
              'Synthetic Client','ACTIVE')
            ON CONFLICT DO NOTHING;
            INSERT INTO locations.locations(
              id,owner_org_id,city_id,service_area_id,operating_zone_id,point,
              address_ciphertext,address_summary,pii_key_version)
            VALUES
              (
                '75000000-0000-0000-0000-000000000001',
                '11111111-1111-1111-1111-111111111111',
                '33333333-3333-3333-3333-333333333333',
                '72000000-0000-0000-0000-000000000001',
                '73000000-0000-0000-0000-000000000001',
                public.ST_SetSRID(public.ST_MakePoint(-106.40,23.20),4326),
                decode('01','hex'),'Private synthetic destination','test-v1'
              ),
              (
                '75000000-0000-0000-0000-000000000002',
                '11111111-1111-1111-1111-111111111111',
                '33333333-3333-3333-3333-333333333333',
                '72000000-0000-0000-0000-000000000001',
                '73000000-0000-0000-0000-000000000001',
                public.ST_SetSRID(public.ST_MakePoint(-106.41,23.21),4326),
                decode('02','hex'),'Private synthetic destination','test-v1'
              )
            ON CONFLICT DO NOTHING;
            INSERT INTO pricing.quotes(
              id,owner_org_id,client_account_id,city_id,service_area_id,
              origin_location_id,destination_location_id,service_type,pricing_tier,
              consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,
              minimum_total_cents_snapshot,currency,pricing_policy_version,
              request_snapshot_redacted,package_snapshot,breakdown,financial_override,
              input_hash,status,expires_at)
            VALUES
              (
                '76000000-0000-0000-0000-000000000001',
                '11111111-1111-1111-1111-111111111111',
                '74000000-0000-0000-0000-000000000001',
                '33333333-3333-3333-3333-333333333333',
                '72000000-0000-0000-0000-000000000001',
                '44444444-4444-4444-4444-444444444441',
                '75000000-0000-0000-0000-000000000001',
                'URGENT','OCCASIONAL',false,9000,0,0,9000,10000,'MXN',
                'obs-v1','{}','[]','{}',
                '{"actor_id":"synthetic","reason":"synthetic","valid_until":"2026-08-01T00:00:00Z"}',
                decode(repeat('71',32),'hex'),'USED',clock_timestamp()+interval '1 day'
              ),
              (
                '76000000-0000-0000-0000-000000000002',
                '11111111-1111-1111-1111-111111111111',
                '74000000-0000-0000-0000-000000000001',
                '33333333-3333-3333-3333-333333333333',
                '72000000-0000-0000-0000-000000000001',
                '44444444-4444-4444-4444-444444444441',
                '75000000-0000-0000-0000-000000000002',
                'SAME_DAY','OCCASIONAL',false,10000,0,0,10000,10000,'MXN',
                'obs-v1','{}','[]','{}',
                '{"actor_id":"synthetic","reason":"synthetic","valid_until":"2026-08-01T00:00:00Z"}',
                decode(repeat('72',32),'hex'),'USED',clock_timestamp()+interval '1 day'
              )
            ON CONFLICT DO NOTHING;
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,operator_org_id,client_account_id,city_id,
              service_area_id,origin_location_id,destination_location_id,
              service_type,pricing_tier,consolidated_route,payer_type,status,
              subtotal_cents,discount_cents,tax_cents,total_cents,
              minimum_total_cents_snapshot,currency,pricing_policy_version,
              package_snapshot,financial_override,cod_expected_cents,version,
              created_at,updated_at)
            VALUES
              (
                '71000000-0000-0000-0000-000000000001','ORD_obs_ready',
                '76000000-0000-0000-0000-000000000001',
                '11111111-1111-1111-1111-111111111111',
                NULL,
                '74000000-0000-0000-0000-000000000001',
                '33333333-3333-3333-3333-333333333333',
                '72000000-0000-0000-0000-000000000001',
                '44444444-4444-4444-4444-444444444441',
                '75000000-0000-0000-0000-000000000001',
                'URGENT','OCCASIONAL',false,'SENDER','READY_FOR_PICKUP',
                9000,0,0,9000,10000,'MXN','obs-v1','[]',
                '{"actor_id":"synthetic","reason":"synthetic","valid_until":"2026-08-01T00:00:00Z"}',
                0,2,'2026-07-20T01:00:00Z','2026-07-27T01:00:00Z'
              ),
              (
                '71000000-0000-0000-0000-000000000002','ORD_obs_assigned',
                '76000000-0000-0000-0000-000000000002',
                '11111111-1111-1111-1111-111111111111',
                '11111111-1111-1111-1111-111111111111',
                '74000000-0000-0000-0000-000000000001',
                '33333333-3333-3333-3333-333333333333',
                '72000000-0000-0000-0000-000000000001',
                '44444444-4444-4444-4444-444444444441',
                '75000000-0000-0000-0000-000000000002',
                'SAME_DAY','OCCASIONAL',false,'SENDER','DELIVERING',
                10000,0,0,10000,10000,'MXN','obs-v1','[]',
                '{"actor_id":"synthetic","reason":"synthetic","valid_until":"2026-08-01T00:00:00Z"}',
                0,3,'2026-07-20T02:00:00Z','2026-07-27T02:00:00Z'
              )
            ON CONFLICT DO NOTHING;
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,driver_id,assignment_type,status,cost_cents,accepted_at)
            VALUES (
              '77000000-0000-0000-0000-000000000001',
              '71000000-0000-0000-0000-000000000002',
              '11111111-1111-1111-1111-111111111111',
              'dddddddd-dddd-dddd-dddd-dddddddddd11',
              'OWN','ACCEPTED',1234,'2026-07-27T01:30:00Z')
            ON CONFLICT DO NOTHING;
            INSERT INTO drivers.driver_positions(
              id,driver_id,org_id,city_id,client_event_id,point,accuracy_m,
              captured_at,received_at,publish_realtime)
            VALUES (
              '78000000-0000-0000-0000-000000000001',
              'dddddddd-dddd-dddd-dddd-dddddddddd11',
              '11111111-1111-1111-1111-111111111111',
              '33333333-3333-3333-3333-333333333333',
              '79000000-0000-0000-0000-000000000001',
              public.ST_SetSRID(public.ST_MakePoint(-106.45,23.25),4326),
              4.5,'2026-07-27T01:59:00Z','2026-07-27T01:59:01Z',true)
            ON CONFLICT DO NOTHING;
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class OperationsDashboardPostgreSqlCollection
    : ICollectionFixture<PostgreSqlSecurityWebApplicationFactory>
{
    public const string Name = "OperationsDashboardPostgreSql";
}
