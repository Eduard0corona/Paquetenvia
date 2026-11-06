using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Identity.Endpoints.AuthCenter;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Npgsql;
using Paqueteria.IntegrationTests.Driver;
using Paqueteria.IntegrationTests.Realtime;

namespace Paqueteria.IntegrationTests.Security.AuthCenter;

/// <summary>
/// AI-07 <c>/onboarding</c> (AUTH-OPEN-REGISTRATION, REG-SELF-SERVICE-ORGANIZATION, REG-OWN-APPLICATIONS-ENDPOINT,
/// REG-ONE-ORGANIZATION-PER-PERSON) and the AI-08 REG-001 "web onboarding screen for a signed-in user without
/// organization context", in a real browser, end to end on one origin as in <see cref="PanelSignInPwaPlaywrightTests"/>:
/// the API on Kestrel over PostgreSQL with AuthCenter at <see cref="FakeAuthCenterServer"/>, the production Next.js build in
/// BFF mode and <see cref="SameOriginFront"/> on <c>https://app.paquetenvia.test</c>. A new AuthCenter account signs in,
/// is created with no organization, is offered <c>/onboarding</c> by <c>/login</c> and creates a business (active at once)
/// or registers an ally (pending until a PLATFORM_ADMIN with MFA of a PLATFORM organization approves or rejects it through
/// the real API); the screen lists the caller's own applications with their status, explains the one-organization limit,
/// replays a lost answer with the same Idempotency-Key and creates one organization. A pending or rejected ally grants
/// no access (no context, a 403 in its context) and another account sees none of it. Visitors without a usable session
/// get no form, and the API refuses a submission whose session was lost or suspended after the form loaded. Only the
/// answers the real API cannot give to this screen (IDEMPOTENCY_CONFLICT, 400, 503, a dropped connection and answers
/// that break the AI-05 shape, and a SUSPENDED application) are fulfilled in the browser. Every page calls only the AI-05
/// operations and BFF paths of the flow, never sends X-Organization-Id from <c>/onboarding</c>, renders no id and no
/// e-mail there, leaves nothing in browser storage and logs nothing to the console. All data is synthetic.
/// </summary>
[Collection(PanelSignInPwaCollection.Name)]
public sealed partial class OnboardingPwaPlaywrightTests(PostgreSqlSecurityWebApplicationFactory database)
{
    private const string PublicOrigin = FakeAuthCenterServer.PublicOrigin;
    private const string OnboardingPath = "/api/v1/onboarding/organizations";
    private const string ApplicationsPath = "/api/v1/me/organization-applications";
    private const string PlatformOrganizationId = "22222222-2222-2222-2222-222222222222";
    private const string PlatformAdminUserId = "a0010000-0000-4000-8000-000000000101";
    private const string PlatformAdminSubject = "reg001-onboarding-platform-admin";
    private const string SuspendedSubject = "mock-subject-suspended";
    private const string SyntheticEmail = "synthetic.user@paquetenvia.test";
    private const string ProbeHeader = "x-onboarding-test-probe";

    // apps/web/src/auth/onboarding.ts and onboarding-experience.tsx, the Spanish copy of the screen.
    private const string LimitReached = "Ya tienes una organización activa o pendiente de aprobación. Solo puedes crear una a la vez.";
    private const string IdempotencyConflict = "Esta solicitud ya se envió con otros datos. Recarga la página e inténtalo de nuevo.";
    private const string Forbidden = "Tu cuenta no puede crear organizaciones. Verifica tu correo en AuthCenter y vuelve a iniciar sesión.";
    private const string Invalid = "Revisa los datos de la organización.";
    private const string Unavailable = "El servicio no está disponible en este momento. Intenta de nuevo.";
    private const string BusinessCreated = "Tu negocio quedó activo. Ya puedes entrar a Paquetenvia.";
    private const string AllyPending =
        "Recibimos tu solicitud de paquetería aliada. Un administrador de Paquetenvia la revisará; mientras tanto no puedes operar.";
    private const string LegalNameRequired = "La razón social es obligatoria (máximo 200 caracteres).";
    private const string AccountUnavailable = "No fue posible consultar tu cuenta. Intenta de nuevo más tarde.";
    private const string SignInToCreate = "Inicia sesión para crear tu organización.";
    private const string NoOrganizationYet = "Tu cuenta todavía no tiene una organización activa.";

    private static readonly (string Method, string Path)[] FlowOperations =
    [
        ("GET", AuthCenterDefaults.SessionPath),
        ("GET", AuthCenterDefaults.LoginPath),
        ("GET", AuthCenterDefaults.CallbackPath),
        ("GET", "/api/v1/me/organization-contexts"),
        ("GET", ApplicationsPath),
        ("POST", OnboardingPath),
    ];

    /// <summary>
    /// The happy path. An unknown AuthCenter subject with a verified e-mail signs in from <c>/login</c> and is created once
    /// with no membership (AUTH-OPEN-REGISTRATION); <c>/login</c> offers "Crear o registrar tu organización". On
    /// <c>/onboarding</c> a name of spaces is refused on the page and sends nothing; the business is sent once with the
    /// three contracted fields trimmed, an <c>onboarding-</c> Idempotency-Key, the session's CSRF header and no
    /// X-Organization-Id, and is created ACTIVE with its creator as BUSINESS_ADMIN. The screen says so, lists it as
    /// "Activa" without its id, and "Entrar a Paquetenvia" returns to <c>/login</c> with the business as the active
    /// organization.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task A_new_account_creates_its_business_from_login_and_enters_Paquetenvia()
    {
        await using var site = await StartAsync();
        var applicant = site.Applicant;
        var page = applicant.Page;
        var subject = NewSubject();

        await SignInAsync(site, applicant, subject);
        await page.GetByText(NoOrganizationYet, new() { Exact = true }).WaitForAsync();
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM identity.users WHERE identity_subject=@subject", subject));
        Assert.Equal(0L, await ScalarAsync<long>(
            """
            SELECT count(*) FROM organizations.organization_memberships m
            JOIN identity.users u ON u.id = m.user_id WHERE u.identity_subject=@subject
            """,
            subject));
        var offer = page.GetByRole(AriaRole.Link, new() { Name = "Crear o registrar tu organización", Exact = true });
        Assert.Equal("/onboarding", await offer.GetAttributeAsync("href"));
        await offer.ClickAsync();
        await page.WaitForURLAsync($"{PublicOrigin}/onboarding");
        await ExpectFormAsync(page, "Crear negocio");
        Assert.Equal(0, await page.GetByRole(AriaRole.Heading, new() { Name = "Tus solicitudes", Exact = true }).CountAsync());

        // The page applies the AI-05 name rules first and sends nothing it would refuse.
        await page.GetByLabel("Razón social", new() { Exact = true }).FillAsync("   ");
        await page.GetByLabel("Nombre visible", new() { Exact = true }).FillAsync("Negocio Sintético");
        await page.GetByRole(AriaRole.Button, new() { Name = "Crear negocio", Exact = true }).ClickAsync();
        await ExpectMessageAsync(page, LegalNameRequired);
        Assert.Empty(applicant.Posts());

        await page.GetByLabel("Razón social", new() { Exact = true }).FillAsync("  Negocio Sintético SA de CV  ");
        await page.GetByLabel("Nombre visible", new() { Exact = true }).FillAsync(" Negocio Sintético ");
        await page.GetByRole(AriaRole.Button, new() { Name = "Crear negocio", Exact = true }).ClickAsync();
        await ExpectMessageAsync(page, BusinessCreated);
        await ExpectApplicationsAsync(page, "Negocio Sintético · Negocio · Activa");

        var post = Assert.Single(applicant.Posts());
        Assert.Equal([201], applicant.CreationAnswers());
        AssertDraft(post, "BUSINESS", "Negocio Sintético SA de CV", "Negocio Sintético");
        Assert.Matches(IdempotencyKeyPattern(), post.IdempotencyKey);
        Assert.Equal(await CsrfTokenAsync(page), post.Csrf);
        Assert.Null(post.Organization);

        var organization = Assert.Single(await OrganizationsCreatedByAsync(subject));
        Assert.Equal(("BUSINESS", "ACTIVE", "Negocio Sintético SA de CV", "Negocio Sintético", "BUSINESS_ADMIN", "ACTIVE"),
            (organization.Type, organization.Status, organization.LegalName, organization.DisplayName,
                organization.Role, organization.MembershipStatus));
        await AssertNothingPrivateRenderedAsync(page, organization.Id);
        Assert.Equal(200, (await ProbeAsync(page, "GET", "/api/v1/me/organization-roles", organization.Id)).Status);

        var enter = page.GetByRole(AriaRole.Link, new() { Name = "Entrar a Paquetenvia", Exact = true });
        Assert.Equal("/login", await enter.GetAttributeAsync("href"));
        await enter.ClickAsync();
        await page.WaitForURLAsync($"{PublicOrigin}/login");
        await page.GetByText("Organización activa: Negocio Sintético.", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Continuar", Exact = true }).WaitForAsync();

        await site.FinishAsync();
    }

    /// <summary>
    /// An ally application (REG-SELF-SERVICE-ORGANIZATION, REG-ALLY-APPROVAL-PATH, REG-ALLY-REJECTION-CLOSED). The ally is
    /// created PENDING_APPROVAL with its creator as ALLY_ADMIN; the screen says it waits for review and lists it as
    /// "Pendiente de aprobación" with no way in. The pending ally holds the person's one slot (ORGANIZATION_LIMIT_REACHED
    /// explains it) and grants no access: it is not among the person's contexts and its own-roles read answers 403; another
    /// new account sees no application on its screen and gets the same 403 there. A PLATFORM_ADMIN signed in with MFA in the
    /// PLATFORM organization finds it pending and decides through the real API. Approved, the screen shows "Activa" and
    /// "Entrar a Paquetenvia" leads to the ally as the active organization. Rejected, it shows "Cerrada", still grants
    /// nothing, and the freed slot takes a new business, listed first.
    /// </summary>
    [Theory]
    [Trait("Category", "DriverStopsPwa")]
    [InlineData("APPROVE")]
    [InlineData("REJECT")]
    public async Task An_ally_application_grants_no_access_until_a_platform_admin_decides_it(string decision)
    {
        await SeedPlatformAdminAsync();
        await using var site = await StartAsync(decision);
        var applicant = site.Applicant;
        var page = applicant.Page;
        var subject = NewSubject();

        await SignInAsync(site, applicant, subject);
        await page.GotoAsync($"{PublicOrigin}/onboarding");
        await ExpectFormAsync(page, "Crear negocio");
        await page.GetByRole(AriaRole.Radio, new() { Name = "Registrar una paquetería aliada", Exact = true }).CheckAsync();
        await page.GetByLabel("Razón social", new() { Exact = true }).FillAsync("Paquetería Aliada Sintética SA");
        await page.GetByLabel("Nombre visible", new() { Exact = true }).FillAsync("Aliada Sintética");
        await page.GetByRole(AriaRole.Button, new() { Name = "Enviar solicitud de aliado", Exact = true }).ClickAsync();
        await ExpectMessageAsync(page, AllyPending);
        await ExpectApplicationsAsync(page, "Aliada Sintética · Paquetería aliada · Pendiente de aprobación");
        Assert.Equal(0, await page.GetByRole(AriaRole.Link, new() { Name = "Entrar a Paquetenvia", Exact = true }).CountAsync());
        AssertDraft(Assert.Single(applicant.Posts()), "ALLY", "Paquetería Aliada Sintética SA", "Aliada Sintética");
        var ally = Assert.Single(await OrganizationsCreatedByAsync(subject));
        Assert.Equal(("ALLY", "PENDING_APPROVAL", "ALLY_ADMIN"), (ally.Type, ally.Status, ally.Role));
        await AssertNothingPrivateRenderedAsync(page, ally.Id);

        // REG-ONE-ORGANIZATION-PER-PERSON: the pending ally holds the slot.
        await page.GetByRole(AriaRole.Radio, new() { Name = "Crear un negocio", Exact = true }).CheckAsync();
        await page.GetByLabel("Razón social", new() { Exact = true }).FillAsync("Negocio Sintético SA");
        await page.GetByLabel("Nombre visible", new() { Exact = true }).FillAsync("Negocio Sintético");
        await page.GetByRole(AriaRole.Button, new() { Name = "Crear negocio", Exact = true }).ClickAsync();
        await ExpectMessageAsync(page, LimitReached);
        await ExpectApplicationsAsync(page, "Aliada Sintética · Paquetería aliada · Pendiente de aprobación");
        Assert.Equal([201, 409], applicant.CreationAnswers());
        Assert.Single(await OrganizationsCreatedByAsync(subject));

        // Tenant safety: the pending ally is no context of its creator and refuses its creator's requests.
        await AssertNoAccessAsync(page, ally.Id);
        var other = await site.OpenAccountAsync();
        await SignInAsync(site, other, NewSubject());
        await other.Page.GotoAsync($"{PublicOrigin}/onboarding");
        await ExpectFormAsync(other.Page, "Crear negocio");
        Assert.Equal(0, await other.Page.GetByRole(AriaRole.Heading, new() { Name = "Tus solicitudes", Exact = true }).CountAsync());
        Assert.DoesNotContain("Aliada Sintética", await other.Page.Locator("main").InnerTextAsync(), StringComparison.Ordinal);
        Assert.Equal("[]", (await ProbeAsync(other.Page, "GET", ApplicationsPath)).Body);
        await AssertNoAccessAsync(other.Page, ally.Id);

        // The platform decides through the real API: PLATFORM_ADMIN with MFA, in the PLATFORM organization.
        var admin = await site.OpenAccountAsync();
        await SignInPlatformAdminWithMfaAsync(site, admin);
        var pending = await ProbeAsync(admin.Page, "GET", "/api/v1/platform/ally-applications", PlatformOrganizationId);
        Assert.Equal(200, pending.Status);
        using (var list = JsonDocument.Parse(pending.Body))
        {
            Assert.Contains(list.RootElement.EnumerateArray(), entry => entry.GetProperty("organization_id").GetString() == ally.Id);
        }

        var decided = await ProbeAsync(
            admin.Page, "POST", $"/api/v1/platform/ally-applications/{ally.Id}/decision", PlatformOrganizationId,
            $$"""{"decision":"{{decision}}"}""");
        Assert.Equal(200, decided.Status);
        var expectedStatus = decision == "APPROVE" ? "ACTIVE" : "CLOSED";
        using (var body = JsonDocument.Parse(decided.Body))
        {
            Assert.Equal(expectedStatus, body.RootElement.GetProperty("status").GetString());
        }

        await page.ReloadAsync();
        if (decision == "APPROVE")
        {
            await ExpectApplicationsAsync(page, "Aliada Sintética · Paquetería aliada · Activa");
            var roles = await ProbeAsync(page, "GET", "/api/v1/me/organization-roles", ally.Id);
            Assert.Equal((200, """{"roles":["ALLY_ADMIN"]}"""), (roles.Status, roles.Body));
            await page.GetByRole(AriaRole.Link, new() { Name = "Entrar a Paquetenvia", Exact = true }).ClickAsync();
            await page.WaitForURLAsync($"{PublicOrigin}/login");
            await page.GetByText("Organización activa: Aliada Sintética.", new() { Exact = true }).WaitForAsync();
        }
        else
        {
            await ExpectApplicationsAsync(page, "Aliada Sintética · Paquetería aliada · Cerrada");
            Assert.Equal(0, await page.GetByRole(AriaRole.Link, new() { Name = "Entrar a Paquetenvia", Exact = true }).CountAsync());
            await AssertNoAccessAsync(page, ally.Id);

            // REG-ALLY-REJECTION-CLOSED: the person may apply again.
            await ExpectFormAsync(page, "Crear negocio");
            await page.GetByLabel("Razón social", new() { Exact = true }).FillAsync("Negocio Sintético SA");
            await page.GetByLabel("Nombre visible", new() { Exact = true }).FillAsync("Negocio Sintético");
            await page.GetByRole(AriaRole.Button, new() { Name = "Crear negocio", Exact = true }).ClickAsync();
            await ExpectMessageAsync(page, BusinessCreated);
            await ExpectApplicationsAsync(
                page, "Negocio Sintético · Negocio · Activa", "Aliada Sintética · Paquetería aliada · Cerrada");
            var created = await OrganizationsCreatedByAsync(subject);
            Assert.Equal(2, created.Count);
            Assert.Equal(("ALLY", "CLOSED"), (created[0].Type, created[0].Status));
            Assert.Equal(("BUSINESS", "ACTIVE", "BUSINESS_ADMIN"), (created[1].Type, created[1].Status, created[1].Role));
            await AssertNothingPrivateRenderedAsync(page, created[1].Id);
        }

        await site.FinishAsync();
    }

    /// <summary>
    /// ONBOARDING-IDEMPOTENCY-DERIVED-ID: the API creates the business but its answer never reaches the page (the
    /// connection drops), so the screen says the service is unavailable and lists nothing; pressing again with the same
    /// draft sends the same Idempotency-Key, the API answers the original organization and the screen lists it once, and
    /// the database holds one organization. A changed draft is a new request with a new key, and the one-organization
    /// rule answers it with ORGANIZATION_LIMIT_REACHED.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task A_lost_answer_is_retried_with_the_same_key_and_creates_one_organization()
    {
        await using var site = await StartAsync();
        var applicant = site.Applicant;
        var page = applicant.Page;
        var subject = NewSubject();
        await SignInAsync(site, applicant, subject);
        await page.GotoAsync($"{PublicOrigin}/onboarding");
        await ExpectFormAsync(page, "Crear negocio");

        var lost = new ConcurrentQueue<ProbeAnswer>();
        var dropNext = 1;
        await page.RouteAsync($"{PublicOrigin}{OnboardingPath}", async route =>
        {
            if (Interlocked.Exchange(ref dropNext, 0) == 1)
            {
                // The request reaches the API through the front, as the browser's would; only its answer is lost.
                lost.Enqueue(await site.RelayAsync(route.Request));
                await route.AbortAsync("connectionreset");
                return;
            }

            await route.ContinueAsync();
        });

        await page.GetByLabel("Razón social", new() { Exact = true }).FillAsync("Negocio Reintento SA");
        await page.GetByLabel("Nombre visible", new() { Exact = true }).FillAsync("Negocio Reintento");
        await page.GetByRole(AriaRole.Button, new() { Name = "Crear negocio", Exact = true }).ClickAsync();
        await ExpectMessageAsync(page, Unavailable);
        var dropped = Assert.Single(lost);
        Assert.Equal(201, dropped.Status);
        Assert.Equal(0, await page.GetByRole(AriaRole.Heading, new() { Name = "Tus solicitudes", Exact = true }).CountAsync());
        var organization = Assert.Single(await OrganizationsCreatedByAsync(subject));
        using (var body = JsonDocument.Parse(dropped.Body))
        {
            Assert.Equal(organization.Id, body.RootElement.GetProperty("organization_id").GetString());
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Crear negocio", Exact = true }).ClickAsync();
        await ExpectMessageAsync(page, BusinessCreated);
        await ExpectApplicationsAsync(page, "Negocio Reintento · Negocio · Activa");
        var posts = applicant.Posts();
        Assert.Equal(2, posts.Length);
        Assert.Matches(IdempotencyKeyPattern(), posts[0].IdempotencyKey);
        Assert.Equal(posts[0].IdempotencyKey, posts[1].IdempotencyKey);
        Assert.Equal(posts[0].Body, posts[1].Body);
        Assert.Equal(organization.Id, Assert.Single(await OrganizationsCreatedByAsync(subject)).Id);

        await page.GetByLabel("Nombre visible", new() { Exact = true }).FillAsync("Negocio Reintento Dos");
        await page.GetByRole(AriaRole.Button, new() { Name = "Crear negocio", Exact = true }).ClickAsync();
        await ExpectMessageAsync(page, LimitReached);
        posts = applicant.Posts();
        Assert.Equal(3, posts.Length);
        Assert.Matches(IdempotencyKeyPattern(), posts[2].IdempotencyKey);
        Assert.NotEqual(posts[0].IdempotencyKey, posts[2].IdempotencyKey);
        // The dropped answer never reached the page; the replay is a 201 and the changed draft a 409.
        Assert.Equal([201, 409], applicant.CreationAnswers());
        await ExpectApplicationsAsync(page, "Negocio Reintento · Negocio · Activa");
        Assert.Equal(organization.Id, Assert.Single(await OrganizationsCreatedByAsync(subject)).Id);
        await AssertNothingPrivateRenderedAsync(page, organization.Id);

        await site.FinishAsync();
    }

    /// <summary>
    /// The answers of createOnboardingOrganization and listMyOrganizationApplications that the real API cannot give this
    /// screen, fulfilled in the browser in their AI-05 shape: IDEMPOTENCY_CONFLICT (the page never reuses a key with
    /// another draft), a 409 with another code, a 400 (the page applies the same rules first), a 503, a dropped connection
    /// and a 201 that breaks the contract each show their one message and create nothing; a failed or malformed
    /// applications list shows that the account could not be read, with no form; a SUSPENDED application is listed as
    /// "Suspendida" with no way in.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Each_answer_the_real_API_cannot_give_this_screen_shows_its_message()
    {
        await using var site = await StartAsync();
        var applicant = site.Applicant;
        var page = applicant.Page;
        var subject = NewSubject();
        await SignInAsync(site, applicant, subject);

        var creations = new ConcurrentQueue<Func<IRoute, Task>>();
        await page.RouteAsync($"{PublicOrigin}{OnboardingPath}", async route =>
        {
            if (creations.TryDequeue(out var answer))
            {
                await answer(route);
                return;
            }

            await route.ContinueAsync();
        });
        var lists = new ConcurrentQueue<Func<IRoute, Task>>();
        await page.RouteAsync($"{PublicOrigin}{ApplicationsPath}", async route =>
        {
            if (lists.TryDequeue(out var answer))
            {
                await answer(route);
                return;
            }

            await route.ContinueAsync();
        });

        (Func<IRoute, Task> Answer, string Message)[] cases =
        [
            (route => Problem(route, 409, "IDEMPOTENCY_CONFLICT"), IdempotencyConflict),
            (route => Problem(route, 409, "SOMETHING_ELSE"), Unavailable),
            (route => Problem(route, 400), Invalid),
            (route => Problem(route, 503), Unavailable),
            (route => route.AbortAsync("connectionreset"), Unavailable),
            (route => route.FulfillAsync(new()
            {
                Status = 201,
                ContentType = "application/json",
                Body = """{"organization_id":"not-an-id","organization_type":"BUSINESS","legal_name":"Negocio Sintético SA","display_name":"Negocio Sintético","status":"ACTIVE","role":"BUSINESS_ADMIN"}""",
            }), Unavailable),
        ];
        foreach (var (answer, message) in cases)
        {
            // A fresh page each time, so the message on screen is this answer's.
            await page.GotoAsync($"{PublicOrigin}/onboarding");
            await ExpectFormAsync(page, "Crear negocio");
            creations.Enqueue(answer);
            await page.GetByLabel("Razón social", new() { Exact = true }).FillAsync("Negocio Sintético SA");
            await page.GetByLabel("Nombre visible", new() { Exact = true }).FillAsync("Negocio Sintético");
            await page.GetByRole(AriaRole.Button, new() { Name = "Crear negocio", Exact = true }).ClickAsync();
            await ExpectMessageAsync(page, message);
            Assert.Empty(creations);
            Assert.Equal(0, await page.GetByRole(AriaRole.Heading, new() { Name = "Tus solicitudes", Exact = true }).CountAsync());
        }

        Assert.Equal(cases.Length, applicant.Posts().Length);
        Assert.Empty(await OrganizationsCreatedByAsync(subject));
        Assert.Equal("[]", (await ProbeAsync(page, "GET", ApplicationsPath)).Body);

        const string SuspendedBusiness =
            """[{"organization_id":"5e5e5e5e-0000-4000-8000-000000000001","organization_type":"BUSINESS","display_name":"Negocio Sintético","status":"SUSPENDED","created_at":"2026-11-06T18:00:00Z"}]""";
        Func<IRoute, Task>[] unreadable =
        [
            route => Problem(route, 503),
            route => route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = SuspendedBusiness.Replace("SUSPENDED", "UNKNOWN", StringComparison.Ordinal) }),
        ];
        foreach (var answer in unreadable)
        {
            lists.Enqueue(answer);
            await page.GotoAsync($"{PublicOrigin}/onboarding");
            await page.GetByRole(AriaRole.Alert).GetByText(AccountUnavailable, new() { Exact = true }).WaitForAsync();
            Assert.Equal(0, await page.GetByLabel("Razón social", new() { Exact = true }).CountAsync());
            Assert.Empty(lists);
        }

        lists.Enqueue(route => route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = SuspendedBusiness }));
        await page.GotoAsync($"{PublicOrigin}/onboarding");
        await ExpectApplicationsAsync(page, "Negocio Sintético · Negocio · Suspendida");
        await ExpectFormAsync(page, "Crear negocio");
        Assert.Equal(0, await page.GetByRole(AriaRole.Link, new() { Name = "Entrar a Paquetenvia", Exact = true }).CountAsync());
        await AssertNothingPrivateRenderedAsync(page, "5e5e5e5e-0000-4000-8000-000000000001");

        await site.FinishAsync();
    }

    /// <summary>
    /// Visitors without a usable session get no form. Without a session the page (no-store, not indexed, with the platform
    /// name) asks to sign in and links <c>/login</c>, reading nothing but the session. An AuthCenter account whose e-mail
    /// is not verified is refused at the callback with the specific message (AUTH-EMAIL-VERIFIED-REQUIRED), is not
    /// created and stays anonymous. A suspended account is told its account could not be read, its applications are never
    /// requested, and the API refuses its creation with 403.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Visitors_without_a_usable_session_get_no_form()
    {
        await using var site = await StartAsync();
        var visitor = site.Applicant;
        var page = visitor.Page;

        var response = await page.GotoAsync($"{PublicOrigin}/onboarding");
        Assert.NotNull(response);
        Assert.Equal(200, response.Status);
        Assert.Contains("no-store", response.Headers.GetValueOrDefault("cache-control") ?? string.Empty, StringComparison.Ordinal);
        await ExpectAnonymousAsync(page);
        Assert.Equal("Tu organización · Paquetenvia", await page.TitleAsync());
        var robots = await page.Locator("meta[name=robots]").GetAttributeAsync("content") ?? string.Empty;
        Assert.Contains("noindex", robots, StringComparison.Ordinal);
        Assert.Contains("nofollow", robots, StringComparison.Ordinal);
        Assert.Equal([("GET", AuthCenterDefaults.SessionPath)], visitor.Calls().Select(call => (call.Method, call.Path)).Distinct());

        var unverified = NewSubject();
        site.AuthCenter.Behavior = new TokenBehavior { EmailVerified = false };
        try
        {
            site.Subject = unverified;
            await page.GotoAsync($"{PublicOrigin}/login");
            await page.GetByRole(AriaRole.Link, new() { Name = "Continuar con AuthCenter", Exact = true }).ClickAsync();
            await page.WaitForURLAsync($"{PublicOrigin}/login?error=email_not_verified");
            await page.GetByRole(AriaRole.Alert)
                .GetByText("Verifica tu correo en AuthCenter y vuelve a iniciar sesión", new() { Exact = true }).WaitForAsync();
        }
        finally
        {
            site.AuthCenter.Behavior = new TokenBehavior();
        }

        Assert.DoesNotContain(await visitor.Context.CookiesAsync([PublicOrigin]), cookie => cookie.Name == AuthCenterDefaults.SessionCookieName);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM identity.users WHERE identity_subject=@subject", unverified));
        await page.GotoAsync($"{PublicOrigin}/onboarding");
        await ExpectAnonymousAsync(page);

        await SignInAsync(site, visitor, SuspendedSubject);
        await page.GetByText("Tu cuenta no tiene acceso a Paquetenvia en este momento.", new() { Exact = true }).WaitForAsync();
        var before = visitor.Calls().Length;
        await page.GotoAsync($"{PublicOrigin}/onboarding");
        await page.GetByRole(AriaRole.Alert).GetByText(AccountUnavailable, new() { Exact = true }).WaitForAsync();
        Assert.Equal(0, await page.GetByLabel("Razón social", new() { Exact = true }).CountAsync());
        Assert.DoesNotContain(visitor.Calls().Skip(before), call => call.Path == ApplicationsPath);
        var refused = await ProbeAsync(
            page, "POST", OnboardingPath, organizationId: null,
            body: """{"organization_type":"BUSINESS","legal_name":"Negocio Sintético SA","display_name":"Negocio Sintético"}""",
            idempotencyKey: "onboarding-suspended-probe-0001");
        Assert.Equal(403, refused.Status);
        Assert.Empty(await OrganizationsCreatedByAsync(SuspendedSubject));

        await site.FinishAsync();
    }

    /// <summary>
    /// The API, not the page, is the barrier: a submission from a form that loaded while the account could still create is
    /// refused when the account was suspended in between (403) or the session is gone (401), the screen shows the message
    /// that asks to verify the account and sign in again, and nothing is created.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task The_API_refuses_a_submission_whose_account_was_suspended_or_whose_session_ended()
    {
        await using var site = await StartAsync();
        var suspended = site.Applicant;
        var suspendedSubject = NewSubject();
        await SignInAsync(site, suspended, suspendedSubject);
        await suspended.Page.GotoAsync($"{PublicOrigin}/onboarding");
        await ExpectFormAsync(suspended.Page, "Crear negocio");
        await suspended.Page.GetByLabel("Razón social", new() { Exact = true }).FillAsync("Negocio Sintético SA");
        await suspended.Page.GetByLabel("Nombre visible", new() { Exact = true }).FillAsync("Negocio Sintético");
        await ExecuteAsync("UPDATE identity.users SET status='SUSPENDED' WHERE identity_subject=@subject", suspendedSubject);
        await suspended.Page.GetByRole(AriaRole.Button, new() { Name = "Crear negocio", Exact = true }).ClickAsync();
        await ExpectMessageAsync(suspended.Page, Forbidden);
        Assert.Single(suspended.Posts());
        Assert.Equal([403], suspended.CreationAnswers());
        Assert.Empty(await OrganizationsCreatedByAsync(suspendedSubject));

        var expired = await site.OpenAccountAsync();
        var expiredSubject = NewSubject();
        await SignInAsync(site, expired, expiredSubject);
        await expired.Page.GotoAsync($"{PublicOrigin}/onboarding");
        await ExpectFormAsync(expired.Page, "Crear negocio");
        await expired.Page.GetByLabel("Razón social", new() { Exact = true }).FillAsync("Negocio Sintético SA");
        await expired.Page.GetByLabel("Nombre visible", new() { Exact = true }).FillAsync("Negocio Sintético");
        await expired.Context.ClearCookiesAsync();
        await expired.Page.GetByRole(AriaRole.Button, new() { Name = "Crear negocio", Exact = true }).ClickAsync();
        await ExpectMessageAsync(expired.Page, Forbidden);
        Assert.Single(expired.Posts());
        Assert.Equal([401], expired.CreationAnswers());
        Assert.Empty(await OrganizationsCreatedByAsync(expiredSubject));

        await site.FinishAsync();
    }

    // ---------- helpers ----------

    private static string NewSubject() => $"reg001-onboarding-{Guid.NewGuid():N}";

    /// <summary>"Continuar con AuthCenter" from <c>/login</c>; AuthCenter's interactive step signs in the subject.</summary>
    private static async Task SignInAsync(Site site, Account account, string subject)
    {
        site.Subject = subject;
        var page = account.Page;
        await page.GotoAsync($"{PublicOrigin}/login");
        var start = page.GetByRole(AriaRole.Link, new() { Name = "Continuar con AuthCenter", Exact = true });
        await start.WaitForAsync();
        Assert.Equal("/auth/login?return_url=%2Flogin", await start.GetAttributeAsync("href"));
        await start.ClickAsync();
        await page.WaitForURLAsync($"{PublicOrigin}/login");
        await page.GetByRole(AriaRole.Status).GetByText("Sesión iniciada como Usuario Sintético.", new() { Exact = true }).WaitForAsync();
    }

    /// <summary>The PLATFORM_ADMIN of the PLATFORM organization, signed in with a second factor (AUTH-001-MFA-STEP-UP).</summary>
    private static async Task SignInPlatformAdminWithMfaAsync(Site site, Account admin)
    {
        site.Subject = PlatformAdminSubject;
        await admin.Page.GotoAsync($"{PublicOrigin}/auth/login?mfa=required&return_url=%2Flogin");
        await admin.Page.WaitForURLAsync($"{PublicOrigin}/login");
        await admin.Page.GetByText("Organización activa: Synthetic Operations.", new() { Exact = true }).WaitForAsync();
    }

    private static async Task ExpectFormAsync(IPage page, string button)
    {
        await page.GetByRole(AriaRole.Heading, new() { Name = "Crear una organización", Exact = true }).WaitForAsync();
        // The DOM text: the stylesheet shows the platform name in capitals.
        Assert.Equal("Tu organización", (await page.Locator("h1").TextContentAsync())?.Trim());
        Assert.Equal("Paquetenvia", (await page.Locator("main .eyebrow").TextContentAsync())?.Trim());
        Assert.True(await page.GetByRole(AriaRole.Radio, new() { Name = "Crear un negocio", Exact = true }).IsCheckedAsync());
        Assert.False(await page.GetByRole(AriaRole.Radio, new() { Name = "Registrar una paquetería aliada", Exact = true }).IsCheckedAsync());
        Assert.Equal(1, await page.GetByLabel("Razón social", new() { Exact = true }).CountAsync());
        Assert.Equal(1, await page.GetByLabel("Nombre visible", new() { Exact = true }).CountAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = button, Exact = true }).WaitForAsync();
    }

    private static async Task ExpectAnonymousAsync(IPage page)
    {
        await page.GetByText(SignInToCreate, new() { Exact = true }).WaitForAsync();
        Assert.Equal("/login", await page.GetByRole(AriaRole.Link, new() { Name = "Iniciar sesión", Exact = true }).GetAttributeAsync("href"));
        Assert.Equal(0, await page.GetByLabel("Razón social", new() { Exact = true }).CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Heading, new() { Name = "Tus solicitudes", Exact = true }).CountAsync());
    }

    private static Task ExpectMessageAsync(IPage page, string message) =>
        page.GetByRole(AriaRole.Status).GetByText(message, new() { Exact = true }).WaitForAsync();

    /// <summary>"Tus solicitudes" lists exactly these entries, in this order (newest first).</summary>
    private static async Task ExpectApplicationsAsync(IPage page, params string[] entries)
    {
        await page.GetByRole(AriaRole.Heading, new() { Name = "Tus solicitudes", Exact = true }).WaitForAsync();
        var items = page.Locator("main li");
        string[] texts = [];
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            texts = (await items.AllInnerTextsAsync()).Select(text => Regex.Replace(text, @"\s+", " ").Trim()).ToArray();
            if (texts.SequenceEqual(entries, StringComparer.Ordinal))
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Equal(entries, texts);
    }

    private static void AssertDraft(ApiCall post, string type, string legalName, string displayName)
    {
        Assert.Equal("application/json", post.ContentType);
        using var body = JsonDocument.Parse(post.Body!);
        Assert.Equal(
            ["display_name", "legal_name", "organization_type"],
            body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(type, body.RootElement.GetProperty("organization_type").GetString());
        Assert.Equal(legalName, body.RootElement.GetProperty("legal_name").GetString());
        Assert.Equal(displayName, body.RootElement.GetProperty("display_name").GetString());
    }

    /// <summary>The screen never shows an organization id, any other id or the account's e-mail.</summary>
    private static async Task AssertNothingPrivateRenderedAsync(IPage page, string organizationId)
    {
        var text = await page.Locator("body").InnerTextAsync();
        var markup = await page.Locator("main").InnerHTMLAsync();
        Assert.DoesNotContain(organizationId, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(organizationId, markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(UuidPattern(), text);
        Assert.DoesNotContain(SyntheticEmail, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pending or rejected organization is none of the person's contexts, and a read in its context (getMyOrganizationRoles,
    /// which every member role may call) answers the uniform 403.
    /// </summary>
    private static async Task AssertNoAccessAsync(IPage page, string organizationId)
    {
        var contexts = await ProbeAsync(page, "GET", "/api/v1/me/organization-contexts");
        Assert.Equal(200, contexts.Status);
        Assert.DoesNotContain(organizationId, contexts.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(403, (await ProbeAsync(page, "GET", "/api/v1/me/organization-roles", organizationId)).Status);
    }

    private static async Task<string> CsrfTokenAsync(IPage page)
    {
        var session = await ProbeAsync(page, "GET", AuthCenterDefaults.SessionPath);
        using var body = JsonDocument.Parse(session.Body);
        return body.RootElement.GetProperty("csrfToken").GetString()!;
    }

    /// <summary>
    /// A request the test makes from the page, with the session cookie and, for a write, the session's CSRF header. It
    /// carries a marker header so it is not counted among the page's own calls.
    /// </summary>
    private static async Task<ProbeAnswer> ProbeAsync(
        IPage page, string method, string path, string? organizationId = null, string? body = null, string? idempotencyKey = null)
    {
        var answer = await page.EvaluateAsync<JsonElement>(
            """
            async ([method, path, organizationId, body, idempotencyKey, csrfHeader, probeHeader]) => {
              const headers = { Accept: "application/json", [probeHeader]: "1" };
              if (organizationId) headers["X-Organization-Id"] = organizationId;
              if (method !== "GET") {
                const session = await fetch("/auth/session", { credentials: "include", cache: "no-store", headers: { [probeHeader]: "1" } });
                headers[csrfHeader] = (await session.json()).csrfToken;
                headers["Content-Type"] = "application/json";
              }
              if (idempotencyKey) headers["Idempotency-Key"] = idempotencyKey;
              const response = await fetch(path, { method, credentials: "include", cache: "no-store", headers, body: body ?? undefined });
              return { status: response.status, body: await response.text() };
            }
            """,
            new object?[] { method, path, organizationId, body, idempotencyKey, AuthCenterDefaults.CsrfHeaderName, ProbeHeader });
        return new ProbeAnswer(answer.GetProperty("status").GetInt32(), answer.GetProperty("body").GetString() ?? string.Empty);
    }

    /// <summary>An AI-05 problem answer, with the <c>code</c> extension of a 409 when one is given.</summary>
    private static Task Problem(IRoute route, int status, string? code = null) => route.FulfillAsync(new()
    {
        Status = status,
        ContentType = "application/problem+json",
        Body = code is null
            ? $$"""{"type":"about:blank","title":"Synthetic answer.","status":{{status}}}"""
            : $$"""{"type":"about:blank","title":"Synthetic answer.","status":{{status}},"code":"{{code}}"}""",
    });

    /// <summary>The organizations the subject created itself, oldest first, with the subject's membership in each.</summary>
    private async Task<IReadOnlyList<CreatedOrganization>> OrganizationsCreatedByAsync(string subject)
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT o.id::text, o.organization_type, o.status, o.legal_name, o.display_name, m.role, m.status
            FROM identity.users u
            JOIN organizations.organizations o ON o.self_service_creator_user_id = u.id
            JOIN organizations.organization_memberships m ON m.organization_id = o.id AND m.user_id = u.id
            WHERE u.identity_subject = @subject
            ORDER BY o.created_at, o.id
            """,
            connection);
        command.Parameters.AddWithValue("subject", subject);
        await using var reader = await command.ExecuteReaderAsync();
        var organizations = new List<CreatedOrganization>();
        while (await reader.ReadAsync())
        {
            organizations.Add(new CreatedOrganization(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6)));
        }

        return organizations;
    }

    private async Task<T> ScalarAsync<T>(string sql, string subject)
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("subject", subject);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql, string subject)
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("subject", subject);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    /// <summary>REG-ALLY-APPROVAL-PATH: a PLATFORM_ADMIN member of the PLATFORM organization of the synthetic seed.</summary>
    private async Task SeedPlatformAdminAsync()
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO identity.users(id,identity_subject,status) VALUES (@user::uuid,@subject,'ACTIVE') ON CONFLICT DO NOTHING;
            INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default)
            SELECT gen_random_uuid(), @user::uuid, @organization::uuid, 'PLATFORM_ADMIN', 'ACTIVE', true
            WHERE NOT EXISTS (
              SELECT 1 FROM organizations.organization_memberships existing WHERE existing.user_id = @user::uuid);
            """,
            connection);
        command.Parameters.AddWithValue("user", PlatformAdminUserId);
        command.Parameters.AddWithValue("subject", PlatformAdminSubject);
        command.Parameters.AddWithValue("organization", PlatformOrganizationId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The API (Kestrel, PostgreSQL, AuthCenter at the fake), the Next.js BFF build, the same-origin front and a browser
    /// that resolves <c>app.paquetenvia.test</c> to the front; the applicant's context records a trace kept on failure.
    /// </summary>
    private async Task<Site> StartAsync(
        string? variant = null,
        [System.Runtime.CompilerServices.CallerFilePath] string testFile = "",
        [System.Runtime.CompilerServices.CallerMemberName] string test = "")
    {
        var authCenter = new FakeAuthCenterServer();
        var api = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            new RealtimeAuthorizationRecorder(),
            allowedOrigin: PublicOrigin,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "AuthCenter",
                ["AuthCenter:Authority"] = FakeAuthCenterServer.Authority,
                ["AuthCenter:Issuer"] = FakeAuthCenterServer.Issuer,
                ["AuthCenter:ClientId"] = FakeAuthCenterServer.ClientId,
                ["AuthCenter:ClientSecret"] = FakeAuthCenterServer.ClientSecret,
                ["AuthCenter:PublicOrigin"] = PublicOrigin,
                ["AuthCenter:SessionStore"] = "Memory",
            },
            configureTestServices: services => services.Configure<OpenIdConnectOptions>(
                AuthCenterDefaults.OpenIdConnectScheme,
                options => options.BackchannelHttpHandler = authCenter));
        var apiAddress = api.Start();
        var web = await DriverStopsNextServer.StartProductionBffAsync();
        var front = await SameOriginFront.StartAsync(apiAddress, web.BaseAddress, RealtimeKestrelWebApplicationFactory.LoopbackCertificate);
        var interactive = await AuthCenterInteractiveStep.StartAsync(authCenter);
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            // The public origin is the front on loopback and is reached directly; any other host would go to a proxy
            // that does not exist, so nothing leaves the machine (and a proxy of the environment is never used).
            Proxy = new Proxy { Server = "http://127.0.0.1:9", Bypass = "app.paquetenvia.test,authcenter.test" },
            Args =
            [
                "--ignore-certificate-errors",
                $"--host-resolver-rules=MAP app.paquetenvia.test:443 127.0.0.1:{front.Port},MAP authcenter.test:443 127.0.0.1:{interactive.Port}",
            ],
        });
        var site = new Site(authCenter, interactive, api, web, front, playwright, browser);
        try
        {
            await site.OpenApplicantAsync(variant, testFile, test);
        }
        catch
        {
            await site.DisposeAsync();
            throw;
        }

        return site;
    }

    [GeneratedRegex("^onboarding-[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex IdempotencyKeyPattern();

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex UuidPattern();

    private sealed record ProbeAnswer(int Status, string Body);

    private sealed record CreatedOrganization(
        string Id, string Type, string Status, string LegalName, string DisplayName, string Role, string MembershipStatus);

    /// <summary>A request the page itself made to the API or the BFF.</summary>
    private sealed record ApiCall(
        string Method,
        string Path,
        string? Body,
        string? ContentType,
        string? IdempotencyKey,
        string? Csrf,
        string? Organization);

    /// <summary>One person's browser context: its page, the API and BFF calls the page made and its console.</summary>
    private sealed class Account(IBrowserContext context, IPage page)
    {
        private readonly ConcurrentQueue<ApiCall> _calls = new();
        private readonly ConcurrentQueue<int> _creationAnswers = new();
        private readonly ConcurrentQueue<string> _console = new();

        internal IBrowserContext Context => context;

        internal IPage Page => page;

        internal ApiCall[] Calls() => [.. _calls];

        internal ApiCall[] Posts() =>
            [.. _calls.Where(call => call.Method == "POST" && call.Path == OnboardingPath)];

        /// <summary>The status of each answer to the page's own createOnboardingOrganization that reached it.</summary>
        internal int[] CreationAnswers() => [.. _creationAnswers];

        internal async Task InstallAsync()
        {
            page.SetDefaultTimeout(20_000);
            page.Console += (_, message) =>
            {
                // The browser's own lines: resource errors and Playwright's note that it blocked the service worker.
                if (!message.Text.StartsWith("Failed to load resource", StringComparison.Ordinal) &&
                    message.Text != "Service Worker registration blocked by Playwright")
                {
                    _console.Enqueue($"{message.Type}:{message.Text}");
                }
            };
            page.PageError += (_, error) => _console.Enqueue($"page:{error}");
            context.Request += (_, request) =>
            {
                var url = new Uri(request.Url);
                if (url.Host != "app.paquetenvia.test" || request.Headers.ContainsKey(ProbeHeader) ||
                    !(url.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) ||
                      url.AbsolutePath.StartsWith("/auth/", StringComparison.Ordinal) ||
                      url.AbsolutePath == AuthCenterDefaults.CallbackPath))
                {
                    return;
                }

                _calls.Enqueue(new ApiCall(
                    request.Method,
                    url.AbsolutePath,
                    request.PostData,
                    request.Headers.GetValueOrDefault("content-type"),
                    request.Headers.GetValueOrDefault("idempotency-key"),
                    request.Headers.GetValueOrDefault(AuthCenterDefaults.CsrfHeaderName.ToLowerInvariant()),
                    request.Headers.GetValueOrDefault("x-organization-id")));
            };
            context.Response += (_, response) =>
            {
                if (response.Request.Method == "POST" && !response.Request.Headers.ContainsKey(ProbeHeader) &&
                    response.Url == $"{PublicOrigin}{OnboardingPath}")
                {
                    _creationAnswers.Enqueue(response.Status);
                }
            };
            // The hubs are not part of this flow; the front does not relay WebSocket upgrades.
            await context.RouteAsync($"{PublicOrigin}/hubs/**", route => route.AbortAsync());
        }

        /// <summary>
        /// Only the AI-05 operations and BFF paths of the flow were called, /onboarding sent no X-Organization-Id, nothing
        /// is left in browser storage and the console stayed clean.
        /// </summary>
        internal async Task AssertCleanAsync()
        {
            Assert.All(_calls, call => Assert.Contains((call.Method, call.Path), FlowOperations));
            Assert.All(
                _calls.Where(call => call.Path is OnboardingPath or ApplicationsPath),
                call => Assert.Null(call.Organization));
            var storage = await page.EvaluateAsync<int[]>(
                """
                async () => [
                  localStorage.length,
                  sessionStorage.length,
                  (await indexedDB.databases()).length,
                  (await caches.keys()).length,
                ]
                """);
            Assert.Equal([0, 0, 0, 0], storage);
            Assert.True(_console.IsEmpty, string.Join(" || ", _console));
        }
    }

    private sealed class Site(
        FakeAuthCenterServer authCenter,
        AuthCenterInteractiveStep interactive,
        RealtimeKestrelWebApplicationFactory api,
        DriverStopsNextServer web,
        SameOriginFront front,
        IPlaywright playwright,
        IBrowser browser) : IAsyncDisposable
    {
        private readonly List<Account> _accounts = [];
        private PlaywrightFailureTrace? _trace;

        internal FakeAuthCenterServer AuthCenter => authCenter;

        internal Account Applicant => _accounts[0];

        /// <summary>Who AuthCenter's interactive step signs in next.</summary>
        internal string Subject
        {
            get => interactive.Subject;
            set => interactive.Subject = value;
        }

        internal async Task OpenApplicantAsync(string? variant, string testFile, string test)
        {
            var context = await NewContextAsync();
            _trace = await PlaywrightFailureTrace.StartAsync(context, variant, PlaywrightFailureTrace.DriverStopsJob, testFile, test);
            await AddAsync(context);
        }

        /// <summary>Another person on the same origin, in its own browser context (its own cookies).</summary>
        internal async Task<Account> OpenAccountAsync() => await AddAsync(await NewContextAsync());

        /// <summary>
        /// Sends the browser's request to the API through the front exactly as the browser would have (its headers, the
        /// session cookie and CSRF header among them, and its body) and returns the answer, which the browser never sees.
        /// The client trusts only this process's loopback certificate and never uses a proxy.
        /// </summary>
        internal async Task<ProbeAnswer> RelayAsync(IRequest request)
        {
            var expected = RealtimeKestrelWebApplicationFactory.LoopbackCertificate.GetCertHashString();
            using var client = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                UseCookies = false,
                AllowAutoRedirect = false,
                SslOptions =
                {
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                        certificate is not null &&
                        string.Equals(certificate.GetCertHashString(), expected, StringComparison.OrdinalIgnoreCase),
                },
            });
            var url = new Uri(request.Url);
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), $"https://127.0.0.1:{front.Port}{url.PathAndQuery}")
            {
                Content = new ByteArrayContent(request.PostDataBuffer ?? []),
            };
            foreach (var (name, value) in await request.AllHeadersAsync())
            {
                if (name.StartsWith(':') || name is "host" or "content-length" or "connection" or "accept-encoding")
                {
                    continue;
                }

                if (!message.Headers.TryAddWithoutValidation(name, value))
                {
                    message.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }

            message.Headers.Host = url.Host;
            using var response = await client.SendAsync(message);
            return new ProbeAnswer((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        internal async Task FinishAsync()
        {
            foreach (var account in _accounts)
            {
                await account.AssertCleanAsync();
            }

            _trace?.Passed();
        }

        private Task<IBrowserContext> NewContextAsync() => browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            // The panel does not use the driver's service worker; with it blocked, every request is the page's own.
            ServiceWorkers = ServiceWorkerPolicy.Block,
        });

        private async Task<Account> AddAsync(IBrowserContext context)
        {
            var account = new Account(context, await context.NewPageAsync());
            await account.InstallAsync();
            _accounts.Add(account);
            return account;
        }

        public async ValueTask DisposeAsync()
        {
            if (_trace is not null)
            {
                await _trace.DisposeAsync();
            }

            foreach (var account in _accounts)
            {
                await account.Context.DisposeAsync();
            }

            await browser.DisposeAsync();
            playwright.Dispose();
            await front.DisposeAsync();
            await interactive.DisposeAsync();
            await web.DisposeAsync();
            await api.DisposeAsync();
            authCenter.Dispose();
        }
    }
}
