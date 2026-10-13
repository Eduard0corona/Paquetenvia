using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// UI-PHASE3-ORDERS-LIST-2026-10-12 and ORD-LIST-DATE-FILTER-2026-10-12 in a real browser against the production
/// Next build: "Órdenes" lists listOrders newest first, filters by status and Mazatlán calendar days (sent as the
/// inclusive created_from / created_to instants whatever the device zone), searches the exact guía, pages with the
/// cursor of the same query, opens the existing detail for the roles that have it, shows no identifier and keeps
/// nothing in browser storage or the console. A date typed with the keyboard is applied only once it is whole, the
/// focus never falls to the page body, and the "Creada" column appears only when the API sends Order.created_at.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class OrdersListPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationA = "11111111-1111-1111-1111-111111111111";

    private static readonly (string Id, string Guide, string Status, long TotalCents)[] Orders =
    [
        ("a3000000-0000-0000-0000-000000000003", "ORD_OrdenesPlaywright00003", "IN_TRANSIT", 12_345),
        ("a2000000-0000-0000-0000-000000000002", "ORD_OrdenesPlaywright00002", "DELIVERED", 9_280),
        ("a1000000-0000-0000-0000-000000000001", "ORD_OrdenesPlaywright00001", "FAILED_ATTEMPT", 120_000),
    ];

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Orders_list_filters_by_status_Mazatlan_days_and_guia_pages_and_opens_the_detail()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        // The device is in New York: the period must still be read in Mazatlán. en-US fixes the order of the date
        // field's parts (month, day, year) for the keyboard steps.
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            TimezoneId = "America/New_York",
            Locale = "en-US",
        });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = Capture(page);
        var api = await InstallAsync(page, "DISPATCHER");

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/orders").AbsoluteUri);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Órdenes", Exact = true }).WaitForAsync();
        await ExpectGuidesAsync(page, Orders[0].Guide, Orders[1].Guide);
        Assert.Equal(string.Empty, Assert.Single(api.Queries));
        // The shell item "Órdenes" (inside the collapsed phone menu) marks this page; "Bandeja" does not.
        Assert.Equal("page", await page.Locator("nav.appNav a[href=\"/ops/orders\"]").GetAttributeAsync("aria-current"));
        Assert.Null(await page.Locator("nav.appNav a[href=\"/ops/inbox\"]").GetAttributeAsync("aria-current"));
        var body = await page.Locator("main").InnerTextAsync();
        Assert.Contains("En tránsito", body, StringComparison.Ordinal);
        Assert.Contains("$123.45", body, StringComparison.Ordinal);
        Assert.Contains("Se muestran 2 órdenes; hay más por cargar.", body, StringComparison.Ordinal);
        // Without Order.created_at from the API there is no "Creada" column.
        Assert.Equal(0, await page.Locator("thead th", new() { HasTextString = "Creada" }).CountAsync());

        // "Cargar más" sends the cursor with the same (empty) query; the last page hands the focus to the summary.
        await page.GetByRole(AriaRole.Button, new() { Name = "Cargar más", Exact = true }).ClickAsync();
        await ExpectGuidesAsync(page, Orders[0].Guide, Orders[1].Guide, Orders[2].Guide);
        Assert.Equal("cursor=c1", api.Queries[^1]);
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Cargar más", Exact = true }).CountAsync());
        await ExpectFocusAsync(page, "p.opsSummaryNote");
        Assert.Equal("Se muestran 3 órdenes.", (await page.Locator("p.opsSummaryNote").InnerTextAsync()).Trim());

        // One status and two Mazatlán days typed with the keyboard: a year typed digit by digit (0002, 0020, 0202)
        // neither clears the field nor sends a request; the URL keeps the whole dates and listOrders receives the
        // inclusive instants.
        await StatusSelect(page).SelectOptionAsync(new SelectOptionValue { Label = "Entregada" });
        await page.WaitForURLAsync(url => url.EndsWith("/ops/orders?status=DELIVERED", StringComparison.Ordinal));
        await ExpectGuidesAsync(page, Orders[1].Guide);
        var from = page.GetByLabel("Creada desde (hora de Mazatlán)");
        var to = page.GetByLabel("Creada hasta (hora de Mazatlán)");
        var sent = api.Queries.Count;
        await from.FocusAsync();
        await page.Keyboard.TypeAsync("10012026", new() { Delay = 60 });
        await page.WaitForURLAsync(url => url.EndsWith("/ops/orders?status=DELIVERED&from=2026-10-01", StringComparison.Ordinal));
        Assert.Equal("2026-10-01", await from.InputValueAsync());
        const string fromQuery = "status=DELIVERED&created_from=2026-10-01T07%3A00%3A00.000Z";
        await ExpectQueryAsync(api, fromQuery);
        Assert.All(api.Queries.Skip(sent), query => Assert.Equal(fromQuery, query));

        // "Hasta" with month and day but no year: nothing is applied and, once the field is left, it says so.
        sent = api.Queries.Count;
        await to.FocusAsync();
        await page.Keyboard.TypeAsync("1007", new() { Delay = 60 });
        await page.Locator("h1").ClickAsync();
        await page.GetByRole(AriaRole.Status).GetByText(DateIncompleteMessage, new() { Exact = true }).WaitForAsync();
        Assert.Equal("true", await to.GetAttributeAsync("aria-invalid"));
        Assert.EndsWith("/ops/orders?status=DELIVERED&from=2026-10-01", page.Url, StringComparison.Ordinal);
        Assert.Equal(sent, api.Queries.Count);

        // The whole "Hasta", year typed digit by digit as well.
        await to.FocusAsync();
        await page.Keyboard.TypeAsync("10072026", new() { Delay = 60 });
        await page.WaitForURLAsync(url => url.EndsWith("status=DELIVERED&from=2026-10-01&to=2026-10-07", StringComparison.Ordinal));
        Assert.Equal("2026-10-07", await to.InputValueAsync());
        Assert.Equal("2026-10-01", await from.InputValueAsync());
        await ExpectGuidesAsync(page, Orders[1].Guide);
        const string periodQuery =
            "status=DELIVERED&created_from=2026-10-01T07%3A00%3A00.000Z&created_to=2026-10-08T06%3A59%3A59.999999Z";
        await ExpectQueryAsync(api, periodQuery);
        Assert.All(api.Queries.Skip(sent), query => Assert.Equal(periodQuery, query));
        await page.Locator("h1").ClickAsync();
        Assert.Equal(0, await page.GetByText(DateIncompleteMessage).CountAsync());
        Assert.Null(await to.GetAttributeAsync("aria-invalid"));

        // A period longer than 31 days is refused on screen and never sent.
        sent = api.Queries.Count;
        await to.FillAsync("2026-11-15");
        await page.GetByRole(AriaRole.Alert).GetByText("Elige un periodo de 31 días o menos.").WaitForAsync();
        Assert.Equal(0, await page.Locator("table").CountAsync());
        Assert.Equal(sent, api.Queries.Count);

        // "Limpiar filtros" goes back to every order.
        await page.GetByRole(AriaRole.Button, new() { Name = "Limpiar filtros", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith("/ops/orders", StringComparison.Ordinal));
        await ExpectGuidesAsync(page, Orders[0].Guide, Orders[1].Guide);

        // The guía search: a value that cannot be one is refused before any request; an exact one is searched. The
        // focus stays on "Buscar guía" and "Quitar búsqueda" returns it to the emptied field.
        var guide = page.GetByLabel("Guía (ORD_…)", new() { Exact = true });
        sent = api.Queries.Count;
        await guide.FillAsync("PQ-24K7Q");
        await page.GetByRole(AriaRole.Button, new() { Name = "Buscar guía", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Status).GetByText("No encontramos esa guía").First.WaitForAsync();
        Assert.Equal(sent, api.Queries.Count);
        await ExpectFocusAsync(page, "form.opsOrderSearch button[type=submit]");
        await guide.FillAsync($"  {Orders[2].Guide} ");
        await page.GetByRole(AriaRole.Button, new() { Name = "Buscar guía", Exact = true }).ClickAsync();
        await ExpectGuidesAsync(page, Orders[2].Guide);
        Assert.Equal($"public_id={Orders[2].Guide}", api.Queries[^1]);
        Assert.DoesNotContain("public_id", page.Url, StringComparison.Ordinal);
        await ExpectFocusAsync(page, "form.opsOrderSearch button[type=submit]");
        await page.GetByRole(AriaRole.Button, new() { Name = "Quitar búsqueda", Exact = true }).ClickAsync();
        await ExpectGuidesAsync(page, Orders[0].Guide, Orders[1].Guide);
        await ExpectFocusAsync(page, "form.opsOrderSearch input[name=public_id]");
        Assert.Equal(string.Empty, await guide.InputValueAsync());

        await AssertNoIdentifiersAsync(page);

        // Nothing of the list reached browser storage or the console.
        Assert.Equal(0, await page.EvaluateAsync<int>("() => localStorage.length + sessionStorage.length"));
        Assert.Empty(console);

        // A click on the row opens the existing order detail; the browser's back returns to the filtered list.
        await StatusSelect(page).SelectOptionAsync(new SelectOptionValue { Label = "Entregada" });
        await page.WaitForURLAsync(url => url.EndsWith("/ops/orders?status=DELIVERED", StringComparison.Ordinal));
        await ExpectGuidesAsync(page, Orders[1].Guide);
        await page.Locator("tbody tr").First.Locator("td").First.ClickAsync();
        await page.WaitForURLAsync(url => url.EndsWith($"/ops/orders/{Orders[1].Id}", StringComparison.Ordinal));
        await page.GoBackAsync();
        await page.WaitForURLAsync(url => url.EndsWith("/ops/orders?status=DELIVERED", StringComparison.Ordinal));
        await ExpectGuidesAsync(page, Orders[1].Guide);
        Assert.Equal("DELIVERED", await StatusSelect(page).InputValueAsync());

        Assert.All(api.Organizations, organization => Assert.Equal(OrganizationA, organization));
        Assert.Equal(0, await page.EvaluateAsync<int>("() => localStorage.length + sessionStorage.length"));
        // The detail page, which this test does not serve, may report its own failed reads; none names an order.
        Assert.DoesNotContain(console, message =>
            Orders.Any(order => message.Contains(order.Guide, StringComparison.Ordinal) ||
                message.Contains(order.Id, StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Viewer_lists_orders_without_links_and_lost_access_clears_the_list()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = Capture(page);
        var api = await InstallAsync(page, "VIEWER");
        // UI-PHASE3-ORDERS-LIST-2026-10-12 ("En dos pasos"): an API that already sends Order.created_at.
        api.SendCreatedAt = true;

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/orders").AbsoluteUri);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Órdenes", Exact = true }).WaitForAsync();
        await ExpectGuidesAsync(page, Orders[0].Guide, Orders[1].Guide);
        Assert.Equal(0, await page.Locator("tbody a").CountAsync());
        // "Creada" sits between Estado and Total, in Mazatlán time (the second order was created the evening before
        // in Mazatlán although it is already the next day in UTC).
        Assert.Equal(
            new[] { "Guía", "Estado", "Creada", "Total (IVA incluido)" },
            (await page.Locator("thead th").AllTextContentsAsync()).Select(text => text.Trim()));
        var created = page.Locator("tbody time");
        Assert.Equal(2, await created.CountAsync());
        Assert.Equal(CreatedAt[0], await created.Nth(0).GetAttributeAsync("datetime"));
        Assert.Equal(CreatedAt[1], await created.Nth(1).GetAttributeAsync("datetime"));
        Assert.Matches("^12 oct 2026, 10:04$", (await created.Nth(0).InnerTextAsync()).Trim());
        Assert.Matches("^4 oct 2026, 23:30$", (await created.Nth(1).InnerTextAsync()).Trim());
        await page.GetByText("El detalle de cada orden lo abren despacho y administración", new() { Exact = false }).WaitForAsync();
        await page.Locator("tbody tr").First.Locator("td").First.ClickAsync();
        await Task.Delay(300);
        Assert.EndsWith("/ops/orders", page.Url, StringComparison.Ordinal);
        await AssertNoIdentifiersAsync(page);

        // The API takes the access away: the list is cleared and the screen says so.
        api.Forbid = true;
        await page.GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Acceso no disponible", Exact = true }).WaitForAsync();
        Assert.Equal(0, await page.Locator("table").CountAsync());
        Assert.Equal(0, await page.EvaluateAsync<int>("() => localStorage.length + sessionStorage.length"));
        Assert.Empty(console);
    }

    /// <summary>
    /// SEC-004-PANEL-SCREENS-2026-10-13, owner answers Q11 "Reintenta sola", Q12 "1 segundo", Q13 "Mensaje común" and
    /// Q14 "Se pausan": listOrders answers 429 with Retry-After: 2. "Órdenes" counts down with the common message,
    /// announced once, holds "Actualizar" (and the top bar's "Buscar", the same limit), sends nothing during the wait,
    /// and reads the list once, by itself, when it ends.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Orders_list_waits_out_a_429_with_the_countdown_and_reads_once_by_itself()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = Capture(page);
        var api = await InstallAsync(page, "DISPATCHER");
        api.TooManyRequests = 1;

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/orders").AbsoluteUri);
        var countdown = page.Locator("main").GetByRole(AriaRole.Timer);
        await countdown.GetByText(TooManyRequestsText(2), new() { Exact = true }).WaitForAsync();
        Assert.Single(api.Queries);
        var refresh = page.GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true });
        Assert.True(await refresh.IsDisabledAsync());
        Assert.Equal("true", await page.Locator("form.appSearch button[type=submit]").GetAttributeAsync("aria-disabled"));
        // Announced once, politely, by the shell; the countdown itself is a timer, never a live region.
        Assert.Equal(TooManyRequestsText(2), (await page.Locator("p.srOnly[role=status]").InnerTextAsync()).Trim());
        Assert.Null(await countdown.GetAttributeAsync("aria-live"));

        await countdown.GetByText(TooManyRequestsText(1), new() { Exact = true }).WaitForAsync();
        Assert.Single(api.Queries);
        Assert.Equal(TooManyRequestsText(2), (await page.Locator("p.srOnly[role=status]").InnerTextAsync()).Trim());

        // At 0 the list is read once, by itself; the message goes and the buttons come back.
        await ExpectGuidesAsync(page, Orders[0].Guide, Orders[1].Guide);
        Assert.Equal(2, api.Queries.Count);
        Assert.Equal(0, await page.GetByRole(AriaRole.Timer).CountAsync());
        Assert.False(await refresh.IsDisabledAsync());
        Assert.Equal("false", await page.Locator("form.appSearch button[type=submit]").GetAttributeAsync("aria-disabled"));
        await Task.Delay(2_500);
        Assert.Equal(2, api.Queries.Count);
        Assert.Empty(console);
    }

    private static string TooManyRequestsText(int seconds) => seconds == 1
        ? "Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo."
        : $"Demasiadas solicitudes. Espera {seconds} segundos e intenta de nuevo.";

    /// <summary>The application's own console output; resource errors of the aborted realtime hub are the browser's.</summary>
    private static ConcurrentQueue<string> Capture(IPage page)
    {
        var messages = new ConcurrentQueue<string>();
        page.Console += (_, message) =>
        {
            if (!message.Text.StartsWith("Failed to load resource", StringComparison.Ordinal))
            {
                messages.Enqueue($"{message.Type}:{message.Text}");
            }
        };
        page.PageError += (_, error) => messages.Enqueue($"page:{error}");
        return messages;
    }

    private static async Task<OrderApi> InstallAsync(IPage page, string role)
    {
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(OrganizationA)}},
              sessionNamespace: "ui-001-orders-playwright",
              getAccessToken: () => "synthetic-operations-token",
            };
            """);
        // Playwright runs the last matching route first: the catch-all is registered before the specific ones.
        // Anything else the shell or the detail page reads is not part of this test.
        await page.RouteAsync("**/api/v1/**", route => ProblemAsync(route, 404));
        await page.RouteAsync("**/hubs/**", route => route.AbortAsync());
        await page.RouteAsync("**/api/v1/me/organization-contexts**", route => FulfillAsync(route, new[]
        {
            new { organization_id = OrganizationA, display_name = "Logística Sintética", role, is_default = true },
        }));
        var api = new OrderApi();
        await page.RouteAsync("**/api/v1/orders**", api.HandleAsync);
        return api;
    }

    private const string DateIncompleteMessage = "Escribe una fecha completa (día, mes y año, desde 2000).";

    /// <summary>Order.created_at as an API that already sends it would (first and second order of the list).</summary>
    private static readonly string[] CreatedAt = ["2026-10-12T17:04:05.1234567Z", "2026-10-05T06:30:00+00:00"];

    /// <summary>Waits until the focused element matches the selector; the focus must never fall to the body.</summary>
    private static async Task ExpectFocusAsync(IPage page, string selector)
    {
        try
        {
            await page.WaitForFunctionAsync(
                "selector => document.activeElement !== null && document.activeElement.matches(selector)",
                selector,
                new() { Timeout = 5_000 });
        }
        catch (TimeoutException)
        {
            var focused = await page.EvaluateAsync<string>(
                "() => document.activeElement === null ? 'none' : document.activeElement.outerHTML.slice(0, 160)");
            Assert.Fail($"The focus is not on {selector}: {focused}");
        }
    }

    /// <summary>Waits until listOrders received the query.</summary>
    private static async Task ExpectQueryAsync(OrderApi api, string query)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (api.Queries.Contains(query)) return;
            await Task.Delay(50);
        }

        Assert.Fail($"listOrders never received {query}: {string.Join(" | ", api.Queries)}");
    }

    /// <summary>The "Estado" filter (its label also holds the option texts, so it is found by its fieldset).</summary>
    private static ILocator StatusSelect(IPage page) => page.Locator("fieldset.opsFilters select");

    private static async Task ExpectGuidesAsync(IPage page, params string[] guides)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        IReadOnlyList<string> actual = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await page.Locator("tbody th").AllTextContentsAsync();
            if (actual.Select(text => text.Trim()).SequenceEqual(guides, StringComparer.Ordinal)) return;
            await Task.Delay(50);
        }

        var main = await page.Locator("main").InnerTextAsync();
        Assert.Fail($"The list did not match the API response: {string.Join(" | ", actual)}; screen: {main}");
    }

    private static async Task AssertNoIdentifiersAsync(IPage page)
    {
        var visible = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotMatch(UuidPattern(), visible);
        foreach (var order in Orders) Assert.DoesNotContain(order.Id[..8], visible, StringComparison.Ordinal);
    }

    private static Task FulfillAsync(IRoute route, object body, int status = 200) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(body),
        });

    private static Task ProblemAsync(IRoute route, int status) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/problem+json",
            Body = JsonSerializer.Serialize(new { type = "about:blank", title = "Problem.", status }),
        });

    /// <summary>The AI-05 Order; with <paramref name="createdAt"/>, also the created_at the API adds later.</summary>
    private static Dictionary<string, object?> Order(
        (string Id, string Guide, string Status, long TotalCents) order,
        string? createdAt = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["id"] = order.Id,
            ["public_id"] = order.Guide,
            ["owner_org_id"] = OrganizationA,
            ["operator_org_id"] = null,
            ["status"] = order.Status,
            ["price_net"] = new { currency = "MXN", amount_cents = order.TotalCents },
            ["version"] = 2,
            ["origin_location_id"] = "61000000-0000-0000-0000-000000000001",
            ["destination_location_id"] = "62000000-0000-0000-0000-000000000001",
            ["service_type"] = "SAME_DAY",
            ["quote_id"] = "63000000-0000-0000-0000-000000000001",
            ["city_id"] = "33333333-3333-3333-3333-333333333333",
            ["service_area_id"] = null,
            ["pricing_tier"] = "OCCASIONAL",
            ["total"] = new { currency = "MXN", amount_cents = order.TotalCents },
            ["claim_window_ends_at"] = null,
            ["finalized_at"] = null,
            ["service_window"] = null,
        };
        if (createdAt is not null) body["created_at"] = createdAt;
        return body;
    }

    /// <summary>listOrders as the server would answer each query of the test; every query string is recorded.</summary>
    private sealed class OrderApi
    {
        internal List<string> Queries { get; } = [];
        internal List<string> Organizations { get; } = [];
        internal bool Forbid { get; set; }

        /// <summary>SEC-004: the next reads answered 429 with Retry-After: 2, as the API's limiter does.</summary>
        internal int TooManyRequests { get; set; }

        /// <summary>Answers each Order with created_at, as the API will once it adds the field.</summary>
        internal bool SendCreatedAt { get; set; }

        internal async Task HandleAsync(IRoute route)
        {
            var uri = new Uri(route.Request.Url);
            if (uri.AbsolutePath != "/api/v1/orders")
            {
                await ProblemAsync(route, 404);
                return;
            }

            Assert.Equal("GET", route.Request.Method);
            Organizations.Add(route.Request.Headers.TryGetValue("x-organization-id", out var organization) ? organization : string.Empty);
            var query = uri.Query.TrimStart('?');
            Queries.Add(query);
            if (Forbid)
            {
                await ProblemAsync(route, 403);
                return;
            }

            if (TooManyRequests > 0)
            {
                TooManyRequests -= 1;
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 429,
                    ContentType = "application/problem+json",
                    // The test API is another origin, so it exposes Retry-After as a cross-origin API would have to
                    // (the pilot is same-origin, where the header is always readable).
                    Headers = new Dictionary<string, string>
                    {
                        ["Retry-After"] = "2",
                        ["Cache-Control"] = "no-store",
                        ["Access-Control-Expose-Headers"] = "Retry-After",
                    },
                    Body = JsonSerializer.Serialize(new
                    {
                        type = "about:blank",
                        title = "Too Many Requests",
                        status = 429,
                        traceId = new string('0', 32),
                    }),
                });
                return;
            }

            var parameters = System.Web.HttpUtility.ParseQueryString(query);
            var publicId = parameters["public_id"];
            var status = parameters["status"];
            Dictionary<string, object?> Item(int index) =>
                Order(Orders[index], SendCreatedAt && index < CreatedAt.Length
                    ? CreatedAt[index]
                    : null);
            var indexes = Enumerable.Range(0, Orders.Length);
            object[] items;
            string? next = null;
            if (publicId is not null)
            {
                items = indexes.Where(index => Orders[index].Guide == publicId).Select(Item).ToArray<object>();
            }
            else if (status is not null)
            {
                items = indexes.Where(index => Orders[index].Status == status).Select(Item).ToArray<object>();
            }
            else if (parameters["cursor"] == "c1")
            {
                items = [Item(2)];
            }
            else
            {
                items = [Item(0), Item(1)];
                next = "c1";
            }

            await FulfillAsync(route, new { items, next_cursor = next });
        }
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex UuidPattern();
}
