using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Npgsql;

namespace Paqueteria.IntegrationTests.Security;

/// <summary>
/// REG-002 (REG-JOIN-EXISTING-BY-EMAIL) over the real HTTP surface and PostgreSQL: the identical 202 whatever
/// the email, a list that never carries an email, the D5 capability with MFA_REQUIRED, the role ceiling, the
/// path/X-Organization-Id rule, renew and revoke with their 404 and 409, and malformed requests.
/// </summary>
public sealed class PendingMembershipHttpTests(PendingMembershipHttpTests.Factory factory)
    : IClassFixture<PendingMembershipHttpTests.Factory>
{
    private static readonly Guid BusinessOrganizationId = PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId;
    private static readonly Guid PlatformOrganizationId = PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId;
    private static readonly Guid AdminBusinessId = Guid.Parse("acacacac-0000-0000-0000-000000000001");

    public sealed class Factory : PostgreSqlSecurityWebApplicationFactory
    {
        private int _prepared;

        /// <summary>
        /// mock-subject-platform-admin-mfa also becomes PLATFORM_ADMIN of the PLATFORM organization; the MFA
        /// subject mock-subject-multi-org becomes BUSINESS_ADMIN of a second BUSINESS organization; and the
        /// no-MFA mock-subject-active-business-admin is BUSINESS_ADMIN of the same one.
        /// </summary>
        public async Task PrepareAsync()
        {
            if (Interlocked.Exchange(ref _prepared, 1) == 1)
            {
                return;
            }

            await ExecuteAsync($"""
                INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
                VALUES ('{AdminBusinessId:D}','Negocio REG-002','Negocio REG-002','BUSINESS');
                INSERT INTO identity.users(id,identity_subject,status) VALUES
                  ('acacacac-0000-0000-0000-000000000002','mock-subject-active-business-admin','ACTIVE');
                INSERT INTO organizations.organization_memberships(user_id,organization_id,role,status,is_default) VALUES
                  ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2','{PlatformOrganizationId:D}','PLATFORM_ADMIN','ACTIVE',false),
                  ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4','{AdminBusinessId:D}','BUSINESS_ADMIN','ACTIVE',false),
                  ('acacacac-0000-0000-0000-000000000002','{AdminBusinessId:D}','BUSINESS_ADMIN','ACTIVE',true);
                """);
        }

        public async Task<T> ScalarAsync<T>(string sql)
        {
            await using var connection = new NpgsqlConnection(AdminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return (T)(await command.ExecuteScalarAsync())!;
        }

        private async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(AdminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Add_answers_an_identical_202_for_any_email_and_the_list_never_carries_one()
    {
        await factory.PrepareAsync();
        using var client = factory.CreateClient();
        var admin = MockIdentityProfiles.ActivePlatformAdminMfa;

        // One address a signed-up person uses, one nobody does: the same status and the same shape.
        using var first = await AddAsync(client, admin, BusinessOrganizationId, "reg002-http-any-00001", "someone.known@paquetenvia.test", "VIEWER");
        using var second = await AddAsync(client, admin, BusinessOrganizationId, "reg002-http-any-00002", $"nobody-{Guid.NewGuid():N}@paquetenvia.test", "VIEWER");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            ["created_at", "expires_at", "id", "role", "status"],
            firstBody.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            firstBody.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal),
            secondBody.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("PENDING", firstBody.GetProperty("status").GetString());
        Assert.Equal(
            TimeSpan.FromDays(7),
            firstBody.GetProperty("expires_at").GetDateTimeOffset() - firstBody.GetProperty("created_at").GetDateTimeOffset());

        using var list = await SendAsync(client, HttpMethod.Get, Collection(BusinessOrganizationId), admin, BusinessOrganizationId);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var raw = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("@", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("email", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hmac", raw, StringComparison.OrdinalIgnoreCase);
        var entries = JsonDocument.Parse(raw).RootElement.EnumerateArray().ToArray();
        Assert.Contains(entries, entry => entry.GetProperty("id").GetString() == firstBody.GetProperty("id").GetString());
        Assert.All(entries, entry => Assert.Equal(
            ["created_at", "expires_at", "id", "role", "status"],
            entry.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)));
        Assert.Equal(0L, await factory.ScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE payload_redacted::text LIKE '%@%' AND action LIKE 'PENDING_MEMBERSHIP_%'"));
    }

    [Fact]
    public async Task Administrators_without_MFA_get_MFA_REQUIRED_and_other_roles_the_uniform_403()
    {
        await factory.PrepareAsync();
        using var client = factory.CreateClient();
        var entry = Guid.NewGuid();

        foreach (var (profile, organizationId) in new[]
                 {
                     (MockIdentityProfiles.ActivePlatformAdminNoMfa, BusinessOrganizationId),
                     (MockIdentityProfiles.ActiveBusinessAdmin, AdminBusinessId),
                 })
        {
            foreach (var response in await AllOperationsAsync(client, profile, organizationId, entry))
            {
                using (response)
                {
                    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                    Assert.Equal("MFA_REQUIRED", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
                }
            }
        }

        // Roles outside membership_operations: VIEWER, DISPATCHER and a member without memberships.
        foreach (var (profile, organizationId) in new[]
                 {
                     (MockIdentityProfiles.ActiveViewer, BusinessOrganizationId),
                     (MockIdentityProfiles.ActiveMultiOrganization, PlatformOrganizationId),
                     (MockIdentityProfiles.ActiveWithoutMemberships, BusinessOrganizationId),
                 })
        {
            foreach (var response in await AllOperationsAsync(client, profile, organizationId, entry))
            {
                using (response)
                {
                    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                    var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                    Assert.False(problem.TryGetProperty("code", out _));
                }
            }
        }

        // The path organization must be the selected one, even for an administrator of both.
        using (var mismatch = await AddAsync(client, MockIdentityProfiles.ActivePlatformAdminMfa, BusinessOrganizationId,
                   "reg002-http-mismatch1", "mismatch@paquetenvia.test", "VIEWER", selected: PlatformOrganizationId))
        {
            Assert.Equal(HttpStatusCode.Forbidden, mismatch.StatusCode);
        }

        using var anonymous = new HttpRequestMessage(HttpMethod.Get, Collection(BusinessOrganizationId));
        using var unauthorized = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(0L, await factory.ScalarAsync<long>(
            "SELECT count(*) FROM organizations.pending_memberships WHERE invited_by IN " +
            "('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1','acacacac-0000-0000-0000-000000000002')"));
    }

    [Fact]
    public async Task The_role_ceiling_holds_over_HTTP()
    {
        await factory.PrepareAsync();
        using var client = factory.CreateClient();
        var businessAdmin = MockIdentityProfiles.ActiveMultiOrganization;
        var platformAdmin = MockIdentityProfiles.ActivePlatformAdminMfa;

        foreach (var (profile, organizationId, role, expected) in new[]
                 {
                     (businessAdmin, AdminBusinessId, "BUSINESS_ADMIN", HttpStatusCode.Accepted),
                     (businessAdmin, AdminBusinessId, "BUSINESS_OPERATOR", HttpStatusCode.Accepted),
                     (businessAdmin, AdminBusinessId, "VIEWER", HttpStatusCode.Accepted),
                     (businessAdmin, AdminBusinessId, "DRIVER", HttpStatusCode.Forbidden),
                     (businessAdmin, AdminBusinessId, "ALLY_ADMIN", HttpStatusCode.Forbidden),
                     (businessAdmin, AdminBusinessId, "DISPATCHER", HttpStatusCode.Forbidden),
                     (businessAdmin, AdminBusinessId, "FINANCE", HttpStatusCode.Forbidden),
                     (businessAdmin, AdminBusinessId, "PLATFORM_ADMIN", HttpStatusCode.Forbidden),
                     (platformAdmin, BusinessOrganizationId, "DISPATCHER", HttpStatusCode.Accepted),
                     (platformAdmin, BusinessOrganizationId, "PLATFORM_ADMIN", HttpStatusCode.Forbidden),
                     (platformAdmin, PlatformOrganizationId, "PLATFORM_ADMIN", HttpStatusCode.Accepted),
                 })
        {
            using var response = await AddAsync(
                client, profile, organizationId, $"reg002-http-{Guid.NewGuid():N}", $"ceiling-{Guid.NewGuid():N}@paquetenvia.test", role);
            Assert.True(expected == response.StatusCode, $"{profile} adding {role}: {response.StatusCode}");
        }
    }

    [Fact]
    public async Task Renew_revoke_replay_and_conflicts_follow_the_contract()
    {
        await factory.PrepareAsync();
        using var client = factory.CreateClient();
        var admin = MockIdentityProfiles.ActiveMultiOrganization;
        var email = $"lifecycle-{Guid.NewGuid():N}@paquetenvia.test";

        using var added = await AddAsync(client, admin, AdminBusinessId, "reg002-http-life-0001", email, "VIEWER");
        Assert.Equal(HttpStatusCode.Accepted, added.StatusCode);
        var id = Guid.Parse((await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);

        // Replay and re-add return the same entry; the same key with another request is a conflict.
        using (var replay = await AddAsync(client, admin, AdminBusinessId, "reg002-http-life-0001", email, "VIEWER"))
        using (var rearm = await AddAsync(client, admin, AdminBusinessId, "reg002-http-life-0002", email.ToUpperInvariant(), "VIEWER"))
        {
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, rearm.StatusCode);
            Assert.Equal(id.ToString("D"), (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());
            Assert.Equal(id.ToString("D"), (await rearm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());
        }

        await AssertConflictAsync(
            await AddAsync(client, admin, AdminBusinessId, "reg002-http-life-0001", email, "BUSINESS_OPERATOR"),
            "IDEMPOTENCY_CONFLICT");

        using (var renewed = await ActAsync(client, admin, AdminBusinessId, id, "renew", "reg002-http-renew-001"))
        {
            Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
            Assert.Equal("PENDING", (await renewed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        }

        using (var revoked = await ActAsync(client, admin, AdminBusinessId, id, "revoke", "reg002-http-revoke-01"))
        {
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
            Assert.Equal("REVOKED", (await revoked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        }

        await AssertConflictAsync(
            await ActAsync(client, admin, AdminBusinessId, id, "renew", "reg002-http-renew-002"),
            "PENDING_MEMBERSHIP_NOT_PENDING");

        // Another organization's entry, or none at all, is the uniform 404.
        using (var foreign = await ActAsync(client, MockIdentityProfiles.ActivePlatformAdminMfa, BusinessOrganizationId, id, "revoke", "reg002-http-foreign1"))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        using (var missing = await ActAsync(client, admin, AdminBusinessId, Guid.NewGuid(), "renew", "reg002-http-missing1"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        Assert.Equal("PENDING_MEMBERSHIP_ADDED,PENDING_MEMBERSHIP_RENEWED,PENDING_MEMBERSHIP_RENEWED,PENDING_MEMBERSHIP_REVOKED",
            await factory.ScalarAsync<string>(
                $"SELECT string_agg(action, ',' ORDER BY occurred_at) FROM platform.audit_logs WHERE entity_id='{id:D}'"));
    }

    [Fact]
    public async Task Malformed_requests_are_400_before_anything_is_written()
    {
        await factory.PrepareAsync();
        using var client = factory.CreateClient();
        var admin = MockIdentityProfiles.ActivePlatformAdminMfa;

        foreach (var (key, email, role) in new (string?, string?, string?)[]
                 {
                     (null, "valid@paquetenvia.test", "VIEWER"),
                     ("short", "valid@paquetenvia.test", "VIEWER"),
                     ("reg002-http-bad-000001", "no-at-sign", "VIEWER"),
                     ("reg002-http-bad-000002", "a b@paquetenvia.test", "VIEWER"),
                     ("reg002-http-bad-000003", null, "VIEWER"),
                     ("reg002-http-bad-000004", "valid@paquetenvia.test", "OWNER"),
                     ("reg002-http-bad-000005", "valid@paquetenvia.test", null),
                 })
        {
            using var response = await AddAsync(client, admin, BusinessOrganizationId, key, email, role);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var badPath = await SendAsync(client, HttpMethod.Get, "/api/v1/organizations/not-a-uuid/pending-memberships", admin, BusinessOrganizationId))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badPath.StatusCode);
        }

        using (var badLimit = await SendAsync(client, HttpMethod.Get, Collection(BusinessOrganizationId) + "?limit=201", admin, BusinessOrganizationId))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);
        }

        using (var noKey = await ActAsync(client, admin, BusinessOrganizationId, Guid.NewGuid(), "revoke", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        }
    }

    private static string Collection(Guid organizationId) =>
        $"/api/v1/organizations/{organizationId:D}/pending-memberships";

    private static async Task<HttpResponseMessage[]> AllOperationsAsync(
        HttpClient client, string profile, Guid organizationId, Guid entry) =>
        [
            await AddAsync(client, profile, organizationId, $"reg002-http-{Guid.NewGuid():N}", "denied@paquetenvia.test", "VIEWER"),
            await SendAsync(client, HttpMethod.Get, Collection(organizationId), profile, organizationId),
            await ActAsync(client, profile, organizationId, entry, "renew", "reg002-http-denied-01"),
            await ActAsync(client, profile, organizationId, entry, "revoke", "reg002-http-denied-02"),
        ];

    private static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    private static Task<HttpResponseMessage> AddAsync(
        HttpClient client, string profile, Guid organizationId, string? key, string? email, string? role, Guid? selected = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Collection(organizationId))
        {
            Content = JsonContent.Create(new { email, role }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.TryAddWithoutValidation("X-Organization-Id", (selected ?? organizationId).ToString("D"));
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> ActAsync(
        HttpClient client, string profile, Guid organizationId, Guid entry, string action, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Collection(organizationId)}/{entry:D}/{action}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.TryAddWithoutValidation("X-Organization-Id", organizationId.ToString("D"));
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string profile, Guid organizationId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.TryAddWithoutValidation("X-Organization-Id", organizationId.ToString("D"));
        return client.SendAsync(request);
    }
}
