using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Npgsql;

namespace Paqueteria.IntegrationTests.Security;

/// <summary>
/// REG-001 over the real HTTP surface and PostgreSQL: onboarding, the one-organization limit and replay,
/// own applications, the PLATFORM_ADMIN ALLY decision with its D5 403s, and the rule that a pending or
/// rejected ALLY grants no access while an approved one does. Mock subjects with no memberships are
/// inserted by each test, and the PLATFORM organization of the synthetic seed gets a PLATFORM_ADMIN.
/// </summary>
public sealed class SelfServiceRegistrationHttpTests(SelfServiceRegistrationHttpTests.Factory factory)
    : IClassFixture<SelfServiceRegistrationHttpTests.Factory>
{
    private const string OnboardingPath = "/api/v1/onboarding/organizations";
    private const string ApplicationsPath = "/api/v1/me/organization-applications";
    private const string PendingPath = "/api/v1/platform/ally-applications";
    private static readonly Guid PlatformOrganizationId = PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId;

    public sealed class Factory : PostgreSqlSecurityWebApplicationFactory
    {
        private int _prepared;

        /// <summary>
        /// mock-subject-platform-admin-mfa and -no-mfa become PLATFORM_ADMIN of the PLATFORM organization, and
        /// the two ALLY/BUSINESS operator mock subjects exist as users without memberships.
        /// </summary>
        public async Task PrepareAsync()
        {
            if (Interlocked.Exchange(ref _prepared, 1) == 1)
            {
                return;
            }

            await ExecuteAsync($"""
                INSERT INTO organizations.organization_memberships(user_id,organization_id,role,status,is_default) VALUES
                  ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2','{PlatformOrganizationId:D}','PLATFORM_ADMIN','ACTIVE',false),
                  ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3','{PlatformOrganizationId:D}','PLATFORM_ADMIN','ACTIVE',false);
                INSERT INTO identity.users(id,identity_subject,status) VALUES
                  ('abababab-0000-0000-0000-000000000001','mock-subject-active-ally-operator','ACTIVE'),
                  ('abababab-0000-0000-0000-000000000002','mock-subject-active-business-operator','ACTIVE');
                """);
        }

        public async Task<T> ScalarAsync<T>(string sql)
        {
            await using var connection = new NpgsqlConnection(AdminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return (T)(await command.ExecuteScalarAsync())!;
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(AdminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Ally_is_pending_and_unusable_until_approved_and_the_limit_holds_with_replay()
    {
        await factory.PrepareAsync();
        var applicant = MockIdentityProfiles.ActiveWithoutMemberships;
        using var client = factory.CreateClient();

        Assert.Equal(0, (await GetJsonAsync(client, applicant, "/api/v1/me/organization-contexts")).GetArrayLength());

        using var created = await PostOnboardingAsync(client, applicant, "reg001-http-ally-000001", "ALLY", "Aliado HTTP SA", "Aliado HTTP");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ally = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PENDING_APPROVAL", ally.GetProperty("status").GetString());
        Assert.Equal("ALLY_ADMIN", ally.GetProperty("role").GetString());
        var allyId = Guid.Parse(ally.GetProperty("organization_id").GetString()!);

        // Replaying the same Idempotency-Key returns the original 201; another request with it is a conflict.
        using (var replay = await PostOnboardingAsync(client, applicant, "reg001-http-ally-000001", "ALLY", "Aliado HTTP SA", "Aliado HTTP"))
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(allyId.ToString("D"), (await replay.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("organization_id").GetString());
        }

        await AssertConflictAsync(
            await PostOnboardingAsync(client, applicant, "reg001-http-ally-000001", "ALLY", "Otro nombre", "Aliado HTTP"),
            "IDEMPOTENCY_CONFLICT");
        // REG-ONE-ORGANIZATION-PER-PERSON: a pending ALLY holds the slot.
        await AssertConflictAsync(
            await PostOnboardingAsync(client, applicant, "reg001-http-business-01", "BUSINESS", "Negocio HTTP", "Negocio HTTP"),
            "ORGANIZATION_LIMIT_REACHED");

        // Pending: listed as an application, absent from the contexts, refused as a tenant.
        var applications = await GetJsonAsync(client, applicant, ApplicationsPath);
        var application = Assert.Single(applications.EnumerateArray());
        Assert.Equal("PENDING_APPROVAL", application.GetProperty("status").GetString());
        Assert.Equal(0, (await GetJsonAsync(client, applicant, "/api/v1/me/organization-contexts")).GetArrayLength());
        await AssertTenantStatusAsync(client, applicant, allyId, HttpStatusCode.Forbidden);

        // Reject: CLOSED, still unusable, and the slot is free again.
        using (var rejected = await DecideAsync(client, MockIdentityProfiles.ActivePlatformAdminMfa, PlatformOrganizationId, allyId, "REJECT"))
        {
            Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
            Assert.Equal("CLOSED", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        }

        await AssertTenantStatusAsync(client, applicant, allyId, HttpStatusCode.Forbidden);
        Assert.Equal("CLOSED", Assert.Single((await GetJsonAsync(client, applicant, ApplicationsPath)).EnumerateArray())
            .GetProperty("status").GetString());
        await AssertConflictAsync(
            await DecideAsync(client, MockIdentityProfiles.ActivePlatformAdminMfa, PlatformOrganizationId, allyId, "APPROVE"),
            "ALLY_DECISION_CONFLICT");
        Assert.Equal(2L, await factory.ScalarAsync<long>(
            $"SELECT count(*) FROM platform.audit_logs WHERE entity_id='{allyId:D}' AND action='ALLY_ORGANIZATION_REJECTED'"));

        using var business = await PostOnboardingAsync(client, applicant, "reg001-http-business-02", "BUSINESS", "Negocio HTTP", "Negocio HTTP");
        Assert.Equal(HttpStatusCode.Created, business.StatusCode);
        var businessBody = await business.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACTIVE", businessBody.GetProperty("status").GetString());
        Assert.Equal("BUSINESS_ADMIN", businessBody.GetProperty("role").GetString());
        var businessId = Guid.Parse(businessBody.GetProperty("organization_id").GetString()!);
        await AssertTenantStatusAsync(client, applicant, businessId, HttpStatusCode.OK);
        Assert.Equal(1L, await factory.ScalarAsync<long>(
            $"SELECT count(*) FROM platform.audit_logs WHERE org_id='{businessId:D}' AND action='ORGANIZATION_SELF_REGISTERED'"));
    }

    [Fact]
    public async Task An_approved_ally_becomes_usable_and_approval_creates_no_ally_relationship()
    {
        await factory.PrepareAsync();
        var applicant = MockIdentityProfiles.ActiveAllyOperator;
        using var client = factory.CreateClient();

        using var created = await PostOnboardingAsync(client, applicant, "reg001-http-approve-01", "ALLY", "Aliado Aprobado SA", "Aliado Aprobado");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var allyId = Guid.Parse((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("organization_id").GetString()!);
        await AssertTenantStatusAsync(client, applicant, allyId, HttpStatusCode.Forbidden);

        var pending = await GetJsonAsync(client, MockIdentityProfiles.ActivePlatformAdminMfa, PendingPath, PlatformOrganizationId);
        Assert.Contains(pending.EnumerateArray(), item => item.GetProperty("organization_id").GetString() == allyId.ToString("D"));

        using (var approved = await DecideAsync(client, MockIdentityProfiles.ActivePlatformAdminMfa, PlatformOrganizationId, allyId, "APPROVE"))
        {
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            Assert.Equal("ACTIVE", (await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        }

        await AssertTenantStatusAsync(client, applicant, allyId, HttpStatusCode.OK);
        var context = Assert.Single((await GetJsonAsync(client, applicant, "/api/v1/me/organization-contexts")).EnumerateArray());
        Assert.Equal("ALLY_ADMIN", context.GetProperty("role").GetString());
        Assert.Equal(0L, await factory.ScalarAsync<long>(
            $"SELECT count(*) FROM allies.ally_relationships WHERE ally_org_id='{allyId:D}'"));
        Assert.Equal(
            $"{PlatformOrganizationId:D},{allyId:D}",
            await factory.ScalarAsync<string>(
                $"""
                SELECT string_agg(org_id::text, ',' ORDER BY (org_id='{allyId:D}'))
                FROM platform.audit_logs WHERE entity_id='{allyId:D}' AND action='ALLY_ORGANIZATION_APPROVED'
                """));
    }

    [Fact]
    public async Task Ally_decisions_follow_the_D5_platform_operations_and_answer_uniform_403s()
    {
        await factory.PrepareAsync();
        using var client = factory.CreateClient();
        var unknown = Guid.NewGuid();

        // PLATFORM_ADMIN without MFA: the only missing requirement is the second factor.
        using (var noMfa = await SendAsync(client, HttpMethod.Get, PendingPath, MockIdentityProfiles.ActivePlatformAdminNoMfa, PlatformOrganizationId))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noMfa.StatusCode);
            Assert.Equal("MFA_REQUIRED", (await noMfa.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // Roles outside the matrix, and a PLATFORM_ADMIN whose selected organization is not a PLATFORM one.
        foreach (var (profile, organizationId) in new[]
                 {
                     (MockIdentityProfiles.ActiveViewer, PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId),
                     (MockIdentityProfiles.ActiveMultiOrganization, PlatformOrganizationId),
                     (MockIdentityProfiles.ActivePlatformAdminMfa, PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId),
                 })
        {
            using var list = await SendAsync(client, HttpMethod.Get, PendingPath, profile, organizationId);
            Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
            using var decide = await DecideAsync(client, profile, organizationId, unknown, "APPROVE");
            Assert.Equal(HttpStatusCode.Forbidden, decide.StatusCode);
        }

        using (var missing = await DecideAsync(client, MockIdentityProfiles.ActivePlatformAdminMfa, PlatformOrganizationId, unknown, "APPROVE"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using (var invalid = await DecideAsync(client, MockIdentityProfiles.ActivePlatformAdminMfa, PlatformOrganizationId, unknown, "MAYBE"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        using (var limit = await SendAsync(client, HttpMethod.Get, PendingPath + "?limit=201", MockIdentityProfiles.ActivePlatformAdminMfa, PlatformOrganizationId))
        {
            Assert.Equal(HttpStatusCode.BadRequest, limit.StatusCode);
        }
    }

    [Fact]
    public async Task Onboarding_rejects_malformed_requests_and_anonymous_callers()
    {
        await factory.PrepareAsync();
        using var client = factory.CreateClient();
        var profile = MockIdentityProfiles.ActiveBusinessOperator;

        using (var noKey = await PostOnboardingAsync(client, profile, null, "BUSINESS", "N", "N"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        }

        foreach (var (type, legal, display) in new[]
                 {
                     ("PLATFORM", "N", "N"), ("BUSINESS", "", "N"), ("BUSINESS", " N", "N"),
                     ("BUSINESS", "N", new string('D', 81)), ("BUSINESS", new string('L', 201), "N"),
                 })
        {
            using var invalid = await PostOnboardingAsync(client, profile, "reg001-http-invalid-01", type, legal, display);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        using var anonymous = await PostOnboardingAsync(client, null, "reg001-http-anon-0001", "BUSINESS", "N", "N");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0L, await factory.ScalarAsync<long>(
            "SELECT count(*) FROM organizations.organizations WHERE self_service_creator_user_id='abababab-0000-0000-0000-000000000002'"));
    }

    private static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    private static async Task AssertTenantStatusAsync(HttpClient client, string profile, Guid organizationId, HttpStatusCode expected)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/__tests/tenancy/active", profile, organizationId);
        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string profile, string path, Guid? organizationId = null)
    {
        using var response = await SendAsync(client, HttpMethod.Get, path, profile, organizationId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> PostOnboardingAsync(
        HttpClient client, string? profile, string? key, string type, string legalName, string displayName)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, OnboardingPath)
        {
            Content = JsonContent.Create(new { organization_type = type, legal_name = legalName, display_name = displayName }),
        };
        if (profile is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DecideAsync(
        HttpClient client, string profile, Guid platformOrganizationId, Guid allyOrganizationId, string decision)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{PendingPath}/{allyOrganizationId:D}/decision")
        {
            Content = new StringContent($$"""{"decision":"{{decision}}"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.TryAddWithoutValidation("X-Organization-Id", platformOrganizationId.ToString("D"));
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string profile, Guid? organizationId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        if (organizationId is { } id)
        {
            request.Headers.TryAddWithoutValidation("X-Organization-Id", id.ToString("D"));
        }

        return client.SendAsync(request);
    }
}
