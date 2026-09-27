using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Identity.Endpoints.AuthCenter;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.DataProtection;
using Paqueteria.Infrastructure.DataProtection.Migrations;
using Paqueteria.Infrastructure.Tenancy;
using Testcontainers.PostgreSql;

namespace Paqueteria.IntegrationTests.Security.AuthCenter;

/// <summary>
/// BFF-SESSION-STORE-POSTGRESQL end to end: the real API hosts, the fake AuthCenter and real PostgreSQL
/// with the canonical baseline, the Identity session-store lane and the shared Data Protection key ring.
/// Two API instances sharing one database stand for two replicas behind the ingress; disposing an
/// instance and starting another stands for a restart.
/// </summary>
[Trait("Category", "PostgreSqlIntegration")]
public sealed class AuthCenterPostgreSqlSessionStoreTests(AuthCenterPostgreSqlSessionStoreTests.Database database)
    : IClassFixture<AuthCenterPostgreSqlSessionStoreTests.Database>
{
    private const string ViewerSubject = "mock-subject-active-viewer";
    private const string ActiveProbe = "/__tests/security/authenticated";

    [Fact]
    public async Task Login_stores_one_hashed_session_row_with_an_encrypted_ticket()
    {
        using var api = database.CreateApi();
        using var browser = api.CreateBrowser();
        var before = await database.CountAsync(ViewerSubject, live: true);

        using var callback = await SignInAsync(api, browser);
        using var session = await browser.GetAsync(AuthCenterDefaults.SessionPath);

        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(before + 1, await database.CountAsync(ViewerSubject, live: true));
        var key = SessionKey(api, SessionCookie(callback));
        var row = await database.RowAsync(Hash(key));
        Assert.NotNull(row);
        Assert.Equal(api.AuthCenter.LastSessionId, row.Value.Sid);
        Assert.True(row.Value.TicketLength > 0);
        Assert.Null(row.Value.RevokedAt);
        // Neither the opaque key nor any AuthCenter token is readable in the table.
        Assert.Equal(0, await database.CountContainingAsync(Encoding.UTF8.GetBytes(key)));
        foreach (var token in new[] { api.AuthCenter.LastRefreshToken, api.AuthCenter.LastIdToken, api.AuthCenter.LastAccessToken })
        {
            Assert.Equal(0, await database.CountContainingAsync(Encoding.UTF8.GetBytes(token)));
        }
    }

    [Fact]
    public async Task Logout_revokes_the_row_and_the_cookie_no_longer_works()
    {
        using var api = database.CreateApi();
        using var browser = api.CreateBrowser();
        using var callback = await SignInAsync(api, browser);
        var cookie = SessionCookie(callback);
        var hash = Hash(SessionKey(api, cookie));
        var csrf = await ReadCsrfAsync(browser);

        using var logout = await SendWriteAsync(browser, AuthCenterDefaults.LogoutPath, csrf);

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        var row = await database.RowAsync(hash);
        Assert.NotNull(row);
        Assert.NotNull(row.Value.RevokedAt);
        Assert.Equal(0, row.Value.TicketLength);
        using var replay = await SendWithCookieAsync(api, HttpMethod.Get, ActiveProbe, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task A_backchannel_logout_on_one_instance_ends_the_session_on_the_next_request_to_another()
    {
        using var first = database.CreateApi();
        using var second = database.CreateApi(first.AuthCenter);
        using var browser = first.CreateBrowser();
        using var callback = await SignInAsync(first, browser);
        var cookie = SessionCookie(callback);
        var sid = first.AuthCenter.LastSessionId;

        // One session, two replicas: the second instance resolves the ticket the first one stored.
        using (var shared = await SendWithCookieAsync(second, HttpMethod.Get, ActiveProbe, cookie))
        {
            Assert.Equal(HttpStatusCode.NoContent, shared.StatusCode);
        }

        using var logout = await PostLogoutTokenAsync(second, second.AuthCenter.CreateLogoutToken(ViewerSubject, sid));
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        using var onFirst = await SendWithCookieAsync(first, HttpMethod.Get, ActiveProbe, cookie);
        using var onSecond = await SendWithCookieAsync(second, HttpMethod.Get, ActiveProbe, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, onFirst.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, onSecond.StatusCode);
        Assert.NotNull((await database.RowAsync(Hash(SessionKey(first, cookie))))!.Value.RevokedAt);
    }

    [Fact]
    public async Task A_sub_only_backchannel_logout_ends_earlier_sessions_on_every_instance_but_not_later_ones()
    {
        using var first = database.CreateApi();
        using var second = database.CreateApi(first.AuthCenter);
        using var browser = first.CreateBrowser();
        using var earlier = await SignInAsync(first, browser);
        var earlierCookie = SessionCookie(earlier);

        using var logout = await PostLogoutTokenAsync(first, first.AuthCenter.CreateLogoutToken(ViewerSubject, sessionId: null));
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        using var ended = await SendWithCookieAsync(second, HttpMethod.Get, ActiveProbe, earlierCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);

        using var laterBrowser = second.CreateBrowser();
        using var later = await SignInAsync(second, laterBrowser);
        using var alive = await SendWithCookieAsync(first, HttpMethod.Get, ActiveProbe, SessionCookie(later));
        Assert.Equal(HttpStatusCode.NoContent, alive.StatusCode);
    }

    [Fact]
    public async Task A_session_survives_an_api_restart()
    {
        string cookie;
        var authCenter = new FakeAuthCenterServer();
        try
        {
            using (var before = database.CreateApi(authCenter))
            {
                using var browser = before.CreateBrowser();
                using var callback = await SignInAsync(before, browser);
                cookie = SessionCookie(callback);
                using var probe = await SendWithCookieAsync(before, HttpMethod.Get, ActiveProbe, cookie);
                Assert.Equal(HttpStatusCode.NoContent, probe.StatusCode);
            }

            using var after = database.CreateApi(authCenter);
            using var restarted = await SendWithCookieAsync(after, HttpMethod.Get, ActiveProbe, cookie);
            Assert.Equal(HttpStatusCode.NoContent, restarted.StatusCode);
            using var session = await SendWithCookieAsync(after, HttpMethod.Get, AuthCenterDefaults.SessionPath, cookie);
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
            Assert.True((await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authenticated").GetBoolean());
        }
        finally
        {
            authCenter.Dispose();
        }
    }

    [Fact]
    public void The_postgresql_store_is_the_default_and_requires_the_connection_string()
    {
        using var api = database.CreateApi(overrides: new Dictionary<string, string?> { ["AuthCenter:SessionStore"] = null });
        Assert.Equal(
            AuthCenterSessionStoreKind.PostgreSql,
            api.Services.GetRequiredService<IOptions<AuthCenterOptions>>().Value.SessionStore);
        Assert.Equal(
            "PostgreSqlAuthCenterTicketStore",
            api.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(AuthCenterDefaults.CookieScheme).SessionStore!.GetType().Name);

        using var withoutDatabase = new AuthCenterWebApplicationFactory(new Dictionary<string, string?>
        {
            ["AuthCenter:SessionStore"] = "PostgreSql",
        });
        var exception = Assert.ThrowsAny<Exception>(() => withoutDatabase.CreateClient());
        Assert.Contains("ConnectionStrings:Paqueteria", Flatten(exception), StringComparison.Ordinal);
    }

    private static string Flatten(Exception exception)
    {
        var builder = new StringBuilder();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            builder.AppendLine(current.Message);
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    builder.AppendLine(Flatten(inner));
                }
            }
        }

        return builder.ToString();
    }

    private static async Task<HttpResponseMessage> SignInAsync(AuthCenterWebApplicationFactory api, HttpClient browser)
    {
        using var login = await browser.GetAsync(AuthCenterDefaults.LoginPath);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var grant = api.AuthCenter.Authorize(login.Headers.Location!, ViewerSubject);
        var callback = await browser.GetAsync(FakeAuthCenterServer.CallbackPath(grant.Code, grant.State));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return callback;
    }

    private static async Task<string> ReadCsrfAsync(HttpClient browser)
    {
        var session = await browser.GetFromJsonAsync<JsonElement>(AuthCenterDefaults.SessionPath);
        return session.GetProperty("csrfToken").GetString()!;
    }

    private static Task<HttpResponseMessage> SendWriteAsync(HttpClient browser, string path, string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation(AuthCenterDefaults.CsrfHeaderName, csrf);
        return browser.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendWithCookieAsync(
        AuthCenterWebApplicationFactory api,
        HttpMethod method,
        string path,
        string cookie)
    {
        using var client = api.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostLogoutTokenAsync(AuthCenterWebApplicationFactory api, string token)
    {
        var client = api.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        return client.PostAsync(
            AuthCenterDefaults.BackchannelLogoutPath,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }));
    }

    private static string SessionCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith(AuthCenterDefaults.SessionCookieName + "=", StringComparison.Ordinal))
            .Split(';')[0];

    /// <summary>The opaque session-store key the cookie carries, read the way the cookie handler reads it.</summary>
    private static string SessionKey(AuthCenterWebApplicationFactory api, string sessionCookie)
    {
        var cookie = api.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AuthCenterDefaults.CookieScheme);
        var envelope = cookie.TicketDataFormat.Unprotect(sessionCookie[(sessionCookie.IndexOf('=', StringComparison.Ordinal) + 1)..]);
        return envelope!.Principal.FindFirst("Microsoft.AspNetCore.Authentication.Cookies-SessionId")!.Value;
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>
    /// One PostgreSQL per test class: canonical AI-06/AI-18 baseline, the Identity lane (the BFF session
    /// store) and the Data Protection lane, plus a NOINHERIT API login that assumes paqueteria_app.
    /// </summary>
    public sealed class Database : IAsyncLifetime
    {
        private const string Image = "postgis/postgis:18-3.6@sha256:b410052c6f0d7d37b83cac1369df144e1c843971155dea3317961001704d0a9d";
        private const string AppLogin = "paqueteria_bff_session_api";
        private readonly string _adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        private readonly string _appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        private PostgreSqlContainer _container = null!;
        private string _adminConnectionString = string.Empty;
        private string _appConnectionString = string.Empty;

        public async Task InitializeAsync()
        {
            _container = new PostgreSqlBuilder(Image)
                .WithDatabase("paqueteria_bff_sessions")
                .WithUsername("postgres")
                .WithPassword(_adminPassword)
                .WithCleanUp(true)
                .Build();
            await _container.StartAsync();
            _adminConnectionString = _container.GetConnectionString();
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            await new DatabaseBaselineDeployer().ApplyAsync(baseline, _adminConnectionString);

            await using (var connection = await OpenAsMigratorAsync())
            {
                await using var identity = new IdentityDbContext(
                    new DbContextOptionsBuilder<IdentityDbContext>()
                        .UseNpgsql(connection, postgres =>
                            postgres.MigrationsHistoryTable("__ef_migrations_history_identity", "platform"))
                        .Options,
                    new TenantDatabaseExecutionState());
                await identity.Database.MigrateAsync();
            }

            await using (var connection = await OpenAsMigratorAsync())
            {
                await using var keyRing = new PlatformDataProtectionDbContext(
                    new DbContextOptionsBuilder<PlatformDataProtectionDbContext>()
                        .UseNpgsql(connection, postgres => postgres.MigrationsHistoryTable(
                            PlatformDataProtectionSchema.MigrationsHistoryTable,
                            PlatformDataProtectionSchema.Schema))
                        .Options);
                await keyRing.Database.MigrateAsync();
            }

            await using (var admin = new NpgsqlConnection(_adminConnectionString))
            {
                await admin.OpenAsync();
                await using var login = new NpgsqlCommand(
                    $"""
                    CREATE ROLE {AppLogin} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{_appPassword}';
                    GRANT paqueteria_app TO {AppLogin};
                    """,
                    admin);
                await login.ExecuteNonQueryAsync();
            }

            _appConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
            {
                Username = AppLogin,
                Password = _appPassword,
                Pooling = true,
                MaxPoolSize = 8,
                Timeout = 5,
                CommandTimeout = 5,
                ApplicationName = "Paqueteria.BffSessionStore.IntegrationTests",
            }.ConnectionString;
        }

        public async Task DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await _container.DisposeAsync();
        }

        internal AuthCenterWebApplicationFactory CreateApi(
            FakeAuthCenterServer? sharedAuthCenter = null,
            IReadOnlyDictionary<string, string?>? overrides = null)
        {
            var settings = new Dictionary<string, string?>
            {
                ["AuthCenter:SessionStore"] = "PostgreSql",
                ["ConnectionStrings:Paqueteria"] = _appConnectionString,
                // Replicas share the key ring, otherwise one could not read another's cookie.
                ["DataProtection:Provider"] = "PostgreSql",
            };
            foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
            {
                settings[key] = value;
            }

            return new AuthCenterWebApplicationFactory(settings, sharedAuthCenter);
        }

        public async Task<long> CountAsync(string subject, bool live)
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM identity.bff_sessions WHERE identity_subject=@subject AND (NOT @live OR revoked_at IS NULL)",
                connection);
            command.Parameters.AddWithValue("subject", subject);
            command.Parameters.AddWithValue("live", live);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async Task<long> CountContainingAsync(byte[] value)
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*) FROM identity.bff_sessions s
                WHERE position(@value IN s.session_key_hash) > 0
                   OR position(@value IN COALESCE(s.ticket_ciphertext, ''::bytea)) > 0
                   OR position(@value IN convert_to(s.identity_subject || COALESCE(s.authcenter_sid, ''), 'UTF8')) > 0
                """,
                connection);
            command.Parameters.AddWithValue("value", value);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async Task<(string? Sid, int TicketLength, DateTime? RevokedAt)?> RowAsync(byte[] hash)
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT authcenter_sid, COALESCE(octet_length(ticket_ciphertext),0), revoked_at FROM identity.bff_sessions WHERE session_key_hash=@hash",
                connection);
            command.Parameters.AddWithValue("hash", hash);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return null;
            }

            return (
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2));
        }

        private async Task<NpgsqlConnection> OpenAsMigratorAsync()
        {
            var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var role = new NpgsqlCommand("SET ROLE paqueteria_migrator;", connection);
            await role.ExecuteNonQueryAsync();
            return connection;
        }
    }
}
