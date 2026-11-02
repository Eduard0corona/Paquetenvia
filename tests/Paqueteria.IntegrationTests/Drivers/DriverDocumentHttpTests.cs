using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Drivers.Application.Documents;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Paqueteria.IntegrationTests.Drivers;

/// <summary>
/// DRV-DOC-001-IN-PERSON-2026-11-02 over HTTP with the service stubbed: the authorization matrix of Y4 (DISPATCHER and
/// PLATFORM_ADMIN with MFA record, list and revoke; the DRIVER reads only its own; FINANCE, VIEWER and every other role
/// the uniform 403 before the service runs), shape before capability, the uniform 404, 503 when off, the exact answer
/// shapes without a key, a hash, a number or the reviewer, and Cache-Control no-store on every answer.
/// </summary>
public sealed class DriverDocumentHttpTests : IClassFixture<DriverDocumentHttpWebApplicationFactory>
{
    private const string Key = "drv-doc-001-http-key-0001";
    private static readonly Guid DriverId = Guid.Parse("d0c00000-0000-4000-8000-000000000001");
    private static readonly Guid DocumentId = Guid.Parse("d0c00000-0000-4000-8000-0000000000d1");
    private static readonly string RecordPath = $"/api/v1/drivers/{DriverId:D}/documents";
    private static readonly string RevokePath = $"/api/v1/drivers/{DriverId:D}/documents/{DocumentId:D}/revoke";
    private const string ListPath = "/api/v1/drivers/documents";
    private const string MinePath = "/api/v1/driver/me/documents";
    private const string ValidBody =
        """{"document_type":"DRIVER_LICENSE","decision":"VALID","expiry_basis":"PRINTED_EXPIRY","expires_on":"2027-03-01"}""";

    private readonly DriverDocumentHttpWebApplicationFactory factory;
    private readonly HttpClient client;

    public DriverDocumentHttpTests(DriverDocumentHttpWebApplicationFactory factory)
    {
        this.factory = factory;
        factory.Service.Reset();
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public static TheoryData<string, string> DispatchOperations() => new()
    {
        { "GET", ListPath },
        { "POST", RecordPath },
        { "POST", RevokePath },
    };

    [Theory]
    [MemberData(nameof(DispatchOperations))]
    public async Task Only_dispatch_and_platform_admin_with_MFA_reach_the_dispatch_operations(string method, string path)
    {
        using (var response = await client.SendAsync(Request(method, path, null)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        foreach (var profile in new[]
                 {
                     MockIdentityProfiles.ActiveViewer,
                     MockIdentityProfiles.ActiveFinance,
                     MockIdentityProfiles.ActiveFinanceMfa,
                     MockIdentityProfiles.ActiveDriver,
                     MockIdentityProfiles.ActiveBusinessAdmin,
                     MockIdentityProfiles.ActiveAllyAdmin,
                     MockIdentityProfiles.SuspendedMembership,
                 })
        {
            using var response = await client.SendAsync(Request(method, path, profile));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Null(await CodeAsync(response));
        }

        using (var response = await client.SendAsync(Request(method, path, MockIdentityProfiles.ActivePlatformAdminNoMfa)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("MFA_REQUIRED", await CodeAsync(response));
        }

        Assert.Equal(0, factory.Service.Invocations);

        foreach (var profile in new[] { MockIdentityProfiles.ActiveDispatcher, MockIdentityProfiles.ActivePlatformAdminMfa })
        {
            using var response = await client.SendAsync(Request(method, path, profile));
            Assert.True(response.IsSuccessStatusCode, $"{method} {path} {profile}: {response.StatusCode}");
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }

        Assert.Equal(2, factory.Service.Invocations);
    }

    [Fact]
    public async Task Only_a_driver_reads_its_own_documents()
    {
        foreach (var profile in new[]
                 {
                     MockIdentityProfiles.ActiveDispatcher,
                     MockIdentityProfiles.ActivePlatformAdminMfa,
                     MockIdentityProfiles.ActiveViewer,
                     MockIdentityProfiles.ActiveFinanceMfa,
                     MockIdentityProfiles.ActiveBusinessAdmin,
                 })
        {
            using var response = await client.SendAsync(Request("GET", MinePath, profile));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Equal(0, factory.Service.Invocations);
        using (var response = await client.SendAsync(Request("GET", MinePath, MockIdentityProfiles.ActiveDriver)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(["documents", "documents_enabled"], Keys(json.RootElement));
            var slot = json.RootElement.GetProperty("documents")[0];
            Assert.Equal(["document", "document_type"], Keys(slot));
            AssertDocumentShape(slot.GetProperty("document"));
        }

        Assert.Equal(DriverDocumentHttpWebApplicationFactory.DriverActorId, factory.Service.MineActors.Single());

        factory.Service.MineForbidden = true;
        using (var response = await client.SendAsync(Request("GET", MinePath, MockIdentityProfiles.ActiveDriver)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_list_answers_the_page_with_its_cursor_and_refuses_any_other_query_before_capability()
    {
        using (var response = await client.SendAsync(Request("GET", ListPath, MockIdentityProfiles.ActiveDispatcher)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(["documents_enabled", "items", "next_cursor"], Keys(json.RootElement));
            Assert.True(json.RootElement.GetProperty("documents_enabled").GetBoolean());
            var item = json.RootElement.GetProperty("items")[0];
            Assert.Equal(["documents", "driver_id"], Keys(item));
            Assert.Equal(DriverId, item.GetProperty("driver_id").GetGuid());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("documents")[1].GetProperty("document").ValueKind);
            var cursor = json.RootElement.GetProperty("next_cursor").GetString();
            Assert.True(DriverDocumentCursorCodec.TryDecode(cursor, out var after));
            Assert.Equal(DriverId, after);

            using var next = await client.SendAsync(Request("GET", $"{ListPath}?cursor={cursor}", MockIdentityProfiles.ActiveDispatcher));
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            Assert.Equal(DriverId, factory.Service.ListQueries.Last().AfterDriverId);
        }

        var invocations = factory.Service.Invocations;
        foreach (var query in new[]
                 {
                     "?driver_id=" + DriverId, "?cursor=a&cursor=b", "?cursor=not-a-cursor",
                     "?cursor=" + Paqueteria.Application.Paging.IdKeysetCursorCodec.Encode(2, DriverId),
                 })
        {
            foreach (var profile in new[] { MockIdentityProfiles.ActiveDispatcher, MockIdentityProfiles.ActiveViewer })
            {
                using var response = await client.SendAsync(Request("GET", ListPath + query, profile));
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Equal("INVALID_REQUEST", await CodeAsync(response));
            }
        }

        Assert.Equal(invocations, factory.Service.Invocations);
    }

    [Fact]
    public async Task A_record_reaches_the_service_with_its_exact_values_and_answers_201()
    {
        using var response = await client.SendAsync(Request("POST", RecordPath, MockIdentityProfiles.ActiveDispatcher));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            AssertDocumentShape(json.RootElement);
        }

        var command = factory.Service.Records.Single();
        Assert.Equal(MockIdentityProfiles.ViewerOrganizationId, command.OrganizationId);
        Assert.Equal(DriverId, command.DriverId);
        Assert.Equal("DRIVER_LICENSE", command.DocumentType);
        Assert.Equal("VALID", command.Decision);
        Assert.Equal("PRINTED_EXPIRY", command.ExpiryBasis);
        Assert.Equal(new DateOnly(2027, 3, 1), command.PrintedDate);
        Assert.Null(command.RejectionCode);
        Assert.Equal(Key, command.IdempotencyKey);
        Assert.False(string.IsNullOrEmpty(command.RequestId));

        using var permanent = await client.SendAsync(Request(
            "POST", RecordPath, MockIdentityProfiles.ActiveDispatcher, key: "drv-doc-001-http-key-0002",
            body: """{"decision":"VALID","document_type":"DRIVER_LICENSE","expiry_basis":"PERMANENT_LICENSE_REVIEW"}"""));
        Assert.Equal(HttpStatusCode.Created, permanent.StatusCode);
        using var rejected = await client.SendAsync(Request(
            "POST", RecordPath, MockIdentityProfiles.ActiveDispatcher, key: "drv-doc-001-http-key-0003",
            body: """{"document_type":"IDENTITY","decision":"REJECTED","rejection_code":"HOLDER_MISMATCH"}"""));
        Assert.Equal(HttpStatusCode.Created, rejected.StatusCode);
        Assert.Equal(
            [("PERMANENT_LICENSE_REVIEW", (DateOnly?)null, (string?)null), (null, null, "HOLDER_MISMATCH")],
            factory.Service.Records.Skip(1).Select(record => (record.ExpiryBasis, record.PrintedDate, record.RejectionCode)));
    }

    [Fact]
    public async Task A_malformed_record_is_refused_before_capability_and_never_echoed()
    {
        var bodies = new[]
        {
            """{"document_type":"DRIVER_LICENSE","decision":"VALID","expiry_basis":"PRINTED_EXPIRY"}""",
            """{"document_type":"DRIVER_LICENSE","decision":"VALID","expires_on":"2027-03-01"}""",
            """{"document_type":"IDENTITY","decision":"VALID","expiry_basis":"PERMANENT_LICENSE_REVIEW"}""",
            """{"document_type":"DRIVER_LICENSE","decision":"VALID","expiry_basis":"PERMANENT_LICENSE_REVIEW","expires_on":"2027-03-01"}""",
            """{"document_type":"DRIVER_LICENSE","decision":"VALID","expiry_basis":"PRINTED_EXPIRY","expires_on":"01/03/2027"}""",
            """{"document_type":"DRIVER_LICENSE","decision":"VALID","expiry_basis":"PRINTED_EXPIRY","expires_on":"2027-02-30"}""",
            """{"document_type":"DRIVER_LICENSE","decision":"VALID","expiry_basis":"PRINTED_EXPIRY","expires_on":"2027-03-01","rejection_code":"ILLEGIBLE"}""",
            """{"document_type":"IDENTITY","decision":"REJECTED"}""",
            """{"document_type":"IDENTITY","decision":"REJECTED","rejection_code":"FAKE"}""",
            """{"document_type":"IDENTITY","decision":"REJECTED","rejection_code":"ILLEGIBLE","expires_on":"2027-03-01"}""",
            """{"document_type":"BACKGROUND_CHECK","decision":"REJECTED","rejection_code":"ILLEGIBLE"}""",
            """{"document_type":"OTHER","decision":"REJECTED","rejection_code":"ILLEGIBLE"}""",
            """{"document_type":"IDENTITY","decision":"PENDING","rejection_code":"ILLEGIBLE"}""",
            """{"document_type":"IDENTITY","decision":"REJECTED","rejection_code":"ILLEGIBLE","document_number":"ABCD123456"}""",
            """{"document_type":"IDENTITY","decision":"REJECTED","rejection_code":"ILLEGIBLE","object_key":"k","sha256":"aa"}""",
            """{"document_type":"IDENTITY","decision":"REJECTED","rejection_code":"ILLEGIBLE","decision":"REJECTED"}""",
            """{"document_type":"IDENTITY","decision":"REJECTED","rejection_code":null}""",
            """{"document_type":"IDENTITY","decision":true}""",
            """["IDENTITY"]""",
            """{"document_type":"IDENTITY",""",
            "{}",
        };
        foreach (var body in bodies)
        {
            foreach (var profile in new[] { MockIdentityProfiles.ActiveDispatcher, MockIdentityProfiles.ActiveViewer })
            {
                using var response = await client.SendAsync(Request("POST", RecordPath, profile, body: body));
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                var text = await response.Content.ReadAsStringAsync();
                Assert.Contains("INVALID_REQUEST", text, StringComparison.Ordinal);
                Assert.DoesNotContain("ABCD123456", text, StringComparison.Ordinal);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            }
        }

        foreach (var request in new[]
                 {
                     Request("POST", RecordPath, MockIdentityProfiles.ActiveDispatcher, key: null),
                     Request("POST", RecordPath, MockIdentityProfiles.ActiveDispatcher, key: "short"),
                     Request("POST", RecordPath + "?document_type=IDENTITY", MockIdentityProfiles.ActiveDispatcher),
                     Request("POST", RecordPath, MockIdentityProfiles.ActiveDispatcher, body: new string(' ', 1100) + ValidBody),
                 })
        {
            using (request)
            using (var response = await client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Equal("INVALID_REQUEST", await CodeAsync(response));
            }
        }

        var twoKeys = Request("POST", RecordPath, MockIdentityProfiles.ActiveDispatcher);
        twoKeys.Headers.Add("Idempotency-Key", "drv-doc-001-http-key-0009");
        using (twoKeys)
        using (var response = await client.SendAsync(twoKeys))
        {
            Assert.Equal("INVALID_REQUEST", await CodeAsync(response));
        }

        var plainText = Request("POST", RecordPath, MockIdentityProfiles.ActiveDispatcher);
        plainText.Content = new StringContent(ValidBody, Encoding.UTF8, "text/plain");
        using (plainText)
        using (var response = await client.SendAsync(plainText))
        {
            // The pipeline may refuse a non-JSON unsafe request first (415); either way nothing reaches the service.
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.UnsupportedMediaType });
        }

        Assert.Equal(0, factory.Service.Invocations);
    }

    [Fact]
    public async Task A_revoke_takes_no_body_or_query()
    {
        var withBody = Request("POST", RevokePath, MockIdentityProfiles.ActiveDispatcher);
        withBody.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using (withBody)
        using (var response = await client.SendAsync(withBody))
        {
            Assert.Equal("INVALID_REQUEST", await CodeAsync(response));
        }

        using (var response = await client.SendAsync(Request("POST", RevokePath + "?reason=SUSPENDED", MockIdentityProfiles.ActiveViewer)))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("INVALID_REQUEST", await CodeAsync(response));
        }

        Assert.Equal(0, factory.Service.Invocations);
        using (var response = await client.SendAsync(Request("POST", RevokePath, MockIdentityProfiles.ActiveDispatcher)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertDocumentShape(json.RootElement);
            Assert.Equal("REVOKED", json.RootElement.GetProperty("status").GetString());
        }

        var revoke = factory.Service.Revokes.Single();
        Assert.Equal((DriverId, DocumentId), (revoke.DriverId, revoke.DocumentId));
    }

    [Fact]
    public async Task Malformed_unknown_and_foreign_ids_are_the_same_not_found()
    {
        foreach (var path in new[]
                 {
                     "/api/v1/drivers/not-a-uuid/documents",
                     $"/api/v1/drivers/{Guid.Empty:D}/documents",
                 })
        {
            using var response = await client.SendAsync(Request("POST", path, MockIdentityProfiles.ActiveDispatcher));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }

        using (var response = await client.SendAsync(Request(
                   "POST", $"/api/v1/drivers/{DriverId:D}/documents/not-a-uuid/revoke", MockIdentityProfiles.ActiveDispatcher)))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(0, factory.Service.Invocations);

        var foreign = Guid.Parse("d0c00000-0000-4000-8000-0000000000ff");
        using var unknown = await client.SendAsync(Request("POST", $"/api/v1/drivers/{foreign:D}/documents", MockIdentityProfiles.ActiveDispatcher));
        using var unknownRevoke = await client.SendAsync(Request(
            "POST", $"/api/v1/drivers/{DriverId:D}/documents/{foreign:D}/revoke", MockIdentityProfiles.ActiveDispatcher));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownRevoke.StatusCode);
        Assert.Equal(await UniformAsync(unknown), await UniformAsync(unknownRevoke));
    }

    /// <summary>The problem without its per-request trace id: the same for every not-found.</summary>
    private static async Task<string> UniformAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return string.Join('|', json.RootElement.EnumerateObject()
            .Where(property => property.Name != "traceId")
            .Select(property => property.Name + "=" + property.Value.GetRawText()));
    }

    [Theory]
    [InlineData("IDEMPOTENCY_CONFLICT")]
    [InlineData("DOCUMENT_TYPE_NOT_REQUIRED")]
    [InlineData("EXPIRY_DATE_OUT_OF_RANGE")]
    [InlineData("DOCUMENT_NOT_REVOCABLE")]
    public async Task Service_conflicts_keep_their_code(string code)
    {
        factory.Service.ConflictCode = code;
        var path = code == "DOCUMENT_NOT_REVOCABLE" ? RevokePath : RecordPath;
        using var response = await client.SendAsync(Request("POST", path, MockIdentityProfiles.ActiveDispatcher));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(code, await CodeAsync(response));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task While_documents_are_off_writes_are_unavailable_and_reads_say_so()
    {
        factory.Service.Unavailable = true;
        foreach (var (method, path) in new[] { ("POST", RecordPath), ("POST", RevokePath) })
        {
            using var response = await client.SendAsync(Request(method, path, MockIdentityProfiles.ActiveDispatcher));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }

        factory.Service.Unavailable = false;
        factory.Service.Disabled = true;
        using (var response = await client.SendAsync(Request("GET", ListPath, MockIdentityProfiles.ActiveDispatcher)))
        {
            Assert.Equal(
                """{"documents_enabled":false,"items":[],"next_cursor":null}""",
                await response.Content.ReadAsStringAsync());
        }

        using (var response = await client.SendAsync(Request("GET", MinePath, MockIdentityProfiles.ActiveDriver)))
        {
            Assert.Equal("""{"documents_enabled":false,"documents":[]}""", await response.Content.ReadAsStringAsync());
        }
    }

    private static void AssertDocumentShape(JsonElement document)
    {
        Assert.Equal(
            ["document_type", "expires_at", "expiry_basis", "id", "recorded_at", "rejection_code", "revoked_at", "status",
                "valid_through"],
            Keys(document));
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}$", document.GetProperty("valid_through").GetString()!);
    }

    private static string[] Keys(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        using var json = JsonDocument.Parse(text);
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>
    /// registerMyDriverDocument stays RESERVED_NOT_IMPLEMENTED (Y1, Y3, Y13): only the GET of the path is mapped, so a POST
    /// - anonymous, a driver or a dispatcher, with a body and a key - is the routing layer's empty 405 with
    /// <c>Allow: GET</c>, decided before authentication; no body is read and the service never runs. AI-05 states this
    /// answer in the operation's description until a later owner decision serves the operation.
    /// </summary>
    [Fact]
    public async Task The_reserved_driver_upload_is_not_served_and_reads_nothing()
    {
        foreach (var profile in new string?[]
                 {
                     null, MockIdentityProfiles.ActiveDriver, MockIdentityProfiles.ActiveDispatcher,
                 })
        {
            using var response = await client.SendAsync(Request("POST", MinePath, profile));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Equal(["GET"], response.Content.Headers.Allow.ToArray());
            Assert.Null(response.Content.Headers.ContentType);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            // No body, so nothing to keep out of a cache: the routing layer sets no Cache-Control.
            Assert.Null(response.Headers.CacheControl);
        }

        Assert.Equal(0, factory.Service.Invocations);
    }

    private static HttpRequestMessage Request(
        string method,
        string path,
        string? profile,
        string? key = Key,
        string? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST" && path.EndsWith("/documents", StringComparison.Ordinal))
        {
            request.Content = new StringContent(body ?? ValidBody, Encoding.UTF8, "application/json");
            if (key is not null)
            {
                request.Headers.Add("Idempotency-Key", key);
            }
        }

        if (profile is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        }

        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        return request;
    }
}

public sealed class DriverDocumentHttpWebApplicationFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid DriverActorId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11");

    internal StubDriverDocumentService Service { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "Mock",
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDriverDocumentService>();
            services.AddSingleton<IDriverDocumentService>(Service);
        });
    }

    internal sealed class StubDriverDocumentService : IDriverDocumentService
    {
        private static readonly Guid DriverId = Guid.Parse("d0c00000-0000-4000-8000-000000000001");
        private static readonly Guid KnownDocumentId = Guid.Parse("d0c00000-0000-4000-8000-0000000000d1");
        private int invocations;

        public int Invocations => Volatile.Read(ref invocations);

        public ConcurrentQueue<RecordDriverDocumentCommand> Records { get; private set; } = new();

        public ConcurrentQueue<RevokeDriverDocumentCommand> Revokes { get; private set; } = new();

        public ConcurrentQueue<ListDriverDocumentsQuery> ListQueries { get; private set; } = new();

        public ConcurrentQueue<Guid> MineActors { get; private set; } = new();

        public string? ConflictCode { get; set; }

        public bool Unavailable { get; set; }

        public bool Disabled { get; set; }

        public bool MineForbidden { get; set; }

        public void Reset()
        {
            Volatile.Write(ref invocations, 0);
            Records = new();
            Revokes = new();
            ListQueries = new();
            MineActors = new();
            ConflictCode = null;
            Unavailable = false;
            Disabled = false;
            MineForbidden = false;
        }

        public Task<DriverDocumentPageResult> ListAsync(ListDriverDocumentsQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocations);
            ListQueries.Enqueue(query);
            return Task.FromResult(Disabled
                ? new DriverDocumentPageResult(false, [], null)
                : new DriverDocumentPageResult(true, [new DriverDocumentSetResult(DriverId, Slots())], DriverId));
        }

        public Task<MyDriverDocumentsResult> ListMineAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocations);
            MineActors.Enqueue(actorId);
            if (MineForbidden)
            {
                throw new DriverDocumentForbiddenException();
            }

            return Task.FromResult(Disabled ? new MyDriverDocumentsResult(false, []) : new MyDriverDocumentsResult(true, Slots()));
        }

        public Task<DriverDocumentResult> RecordAsync(RecordDriverDocumentCommand command, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocations);
            Records.Enqueue(command);
            Fail();
            if (command.DriverId != DriverId)
            {
                throw new DriverDocumentNotFoundException();
            }

            return Task.FromResult(Document("VALID"));
        }

        public Task<DriverDocumentResult> RevokeAsync(RevokeDriverDocumentCommand command, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocations);
            Revokes.Enqueue(command);
            Fail();
            if (command.DocumentId != KnownDocumentId)
            {
                throw new DriverDocumentNotFoundException();
            }

            return Task.FromResult(Document("REVOKED"));
        }

        private void Fail()
        {
            if (Unavailable)
            {
                throw new DriverDocumentUnavailableException();
            }

            if (ConflictCode is { } code)
            {
                throw new DriverDocumentConflictException(code);
            }
        }

        private static IReadOnlyList<DriverDocumentSlotResult> Slots() =>
        [
            new("IDENTITY", Document("VALID")),
            new("DRIVER_LICENSE", null),
        ];

        private static DriverDocumentResult Document(string status) => new(
            KnownDocumentId,
            "IDENTITY",
            status,
            null,
            new DateTimeOffset(2027, 3, 2, 7, 0, 0, TimeSpan.Zero),
            new DateOnly(2027, 3, 1),
            "PRINTED_EXPIRY",
            new DateTimeOffset(2026, 11, 2, 16, 0, 0, TimeSpan.Zero),
            status == "REVOKED" ? new DateTimeOffset(2026, 11, 3, 16, 0, 0, TimeSpan.Zero) : null);
    }
}
