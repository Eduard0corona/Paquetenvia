import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

describe("operations page privacy policy", () => {
  it("keeps operations routes and authenticated requests network-only", () => {
    const source = readFileSync("public/sw.js", "utf8");
    expect(source).toContain('CACHE_NAME = "paquetenvia-driver-shell-v5"');
    expect(source).toContain('url.pathname === "/ops"');
    expect(source).toContain('url.pathname.startsWith("/ops/")');
    expect(source).toContain('request.headers.has("Authorization")');
    expect(source).toContain('url.searchParams.has("access_token")');
  });

  it("sets private no-store security headers without geolocation", () => {
    const source = readFileSync("next.config.ts", "utf8");
    expect(source).toContain('source: "/ops/:path*"');
    expect(source).toContain('value: "no-store, private"');
    expect(source).toContain("geolocation=()");
    expect(
      readFileSync("src/security/security-headers.ts", "utf8"),
    ).toContain("frame-ancestors 'none'");
  });

  it("does not use browser persistence or an external map provider", () => {
    const sources = [
      "src/operations/state/use-operations-dashboard.ts",
      "src/operations/state/use-operations-order-detail.ts",
      "src/operations/components/operations-positions.tsx",
    ]
      .map((path) => readFileSync(path, "utf8"))
      .join("\n");
    expect(sources).not.toMatch(
      /localStorage|sessionStorage|indexedDB|caches\.|google\.maps|mapbox|leaflet|openstreetmap/i,
    );
    expect(sources).toContain("Vista de posiciones sin cartografía.");
  });

  it("links order actions to the screens where they work instead of disabled placeholders", () => {
    const source = readFileSync(
      "src/operations/components/operations-order-detail-shell.tsx",
      "utf8",
    );
    for (const action of ["Publicar oferta externa", "Abrir incidencia"])
      expect(source).toContain(action);
    expect(source).toContain('href="/ops/dashboard"');
    expect(source).toContain('href="/ops/incidents"');
    expect(source).not.toContain("Acciones futuras");
    expect(source).not.toContain("type=\"button\" disabled");
  });

  it("assigns an OWN driver from a list on the order detail, only while the order admits it (UI-PHASE2-DRIVER-PICKER-2026-10-05)", () => {
    const detail = readFileSync("src/operations/components/operations-order-detail-shell.tsx", "utf8");
    expect(detail).toContain("<OperationsDriverAssignment");
    expect(detail).toContain("assignable={admitsAssignment(order.status, projection)}");
    expect(detail).toContain("onOrderChanged={state.refresh}");
    expect(detail).toContain('(status === "READY_FOR_PICKUP" || status === "RESCHEDULED") && order.assignment === null');
    // The fallback stays in "Otras acciones" and is not duplicated by the picker.
    expect(detail).toContain("Publicar oferta externa desde el tablero");

    const picker = readFileSync("src/operations/components/operations-driver-assignment.tsx", "utf8");
    expect(picker).toContain('canPerform(role, "listAssignableDrivers")');
    expect(picker).toContain('canPerform(role, "assignDriver")');
    expect(picker).toContain('type="radio"');
    expect(picker).toContain("disabled={!driver.eligible}");
    expect(picker).toContain("parseMxnToCents(cost)");
    expect(picker).toContain("driverAssignmentConfirmation(");
    expect(picker).toContain("{driver.driver_reference}");
    expect(picker).not.toMatch(/>\s*\{driver\.driver_id\}/);
    expect(picker).not.toContain('href="/ops/dashboard"');
    expect(picker).not.toMatch(/ID del repartidor|Pega el ID|parseFloat|Number\(cost/);
    expect(picker).not.toMatch(/<button[^>]*\sdisabled[=\s>]/);

    const sources = [
      picker,
      readFileSync("src/operations/state/driver-assignment-controller.ts", "utf8"),
      readFileSync("src/operations/api/assignment-api.ts", "utf8"),
    ].join("\n");
    expect(sources).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\.|console\./);
  });

  it("shows only the server's allowed transitions as next steps (UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05)", () => {
    const detail = readFileSync("src/operations/components/operations-order-detail-shell.tsx", "utf8");
    expect(detail).toContain("<OperationsNextStep");
    expect(detail).toContain("allowedTransitions={order.allowed_transitions}");
    expect(detail).toContain("version={order.version}");
    expect(detail).toContain("onOrderChanged={state.refresh}");

    const step = readFileSync("src/operations/components/operations-next-step.tsx", "utf8");
    expect(step).toContain("nextStepActions(allowedTransitions)");
    expect(step).toContain("transitionConfirmation(");
    expect(step).toContain('name="reason"');
    // No disabled placeholders and no status logic in the screen.
    expect(step).not.toMatch(/<button[^>]*\sdisabled[=\s>]/);
    expect(step).not.toMatch(/status ===|\.status\b|OrderTransitionMatrix|"DRAFT"|"DELIVERED"/);

    const contract = readFileSync("src/operations/contracts/order-transitions.ts", "utf8");
    expect(contract).not.toMatch(/source|from status|transitionsFrom/i);

    const sources = [
      step,
      contract,
      readFileSync("src/operations/state/order-transition-controller.ts", "utf8"),
      readFileSync("src/operations/state/order-search.ts", "utf8"),
      readFileSync("src/operations/api/order-actions-api.ts", "utf8"),
      readFileSync("src/components/app-shell/order-search-box.tsx", "utf8"),
    ].join("\n");
    expect(sources).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\.|console\./);
    expect(sources).not.toMatch(/parseFloat/);
  });

  it("searches only the exact tracking number from the top bar (UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05)", () => {
    const shell = readFileSync("src/components/app-shell/app-shell.tsx", "utf8");
    expect(shell).toContain("canSearchOrders(shell.role)");
    expect(shell).toContain("<OrderSearchBox session={shell.session} />");
    const box = readFileSync("src/components/app-shell/order-search-box.tsx", "utf8");
    expect(box).toContain('role="search"');
    expect(box).toContain("searchOrderByPublicId(");
    expect(box).toContain("orderNotFoundMessage");
    expect(box).not.toMatch(/recipient|destinatario|tel[eé]fono|phone|name=\"q\"/i);
    const api = readFileSync("src/operations/api/order-actions-api.ts", "utf8");
    expect(api).toContain("new URLSearchParams({ public_id: normalized })");
  });

  it("controls the creation-date inputs from the filters so clearing them empties the inputs", () => {
    const source = readFileSync("src/operations/components/operations-filters.tsx", "utf8");
    expect(source).toContain("value={utcToDateTimeLocal(filters.createdFrom)}");
    expect(source).toContain("value={utcToDateTimeLocal(filters.createdTo)}");
    expect(source).toContain("onChange({});");
    expect(source).not.toContain("defaultValue");
  });

  it("labels the assignment the same way on the dashboard card and the order detail", () => {
    for (const path of [
      "src/operations/components/operations-order-card.tsx",
      "src/operations/components/operations-order-detail-shell.tsx",
    ]) {
      const source = readFileSync(path, "utf8");
      expect(source, path).toContain("assignmentTypeLabel(");
      expect(source, path).not.toContain("Flota propia");
    }
  });

  it("reaches order creation and the finance screens from the shared app shell navigation", () => {
    const nav = readFileSync("src/components/app-shell/nav-items.ts", "utf8");
    for (const href of ["/ops/orders/new", "/ops/dashboard", "/finance/cod", "/finance/settlements"])
      expect(nav).toContain(`href: "${href}"`);
    for (const layout of ["src/app/ops/layout.tsx", "src/app/finance/layout.tsx"])
      expect(readFileSync(layout, "utf8")).toContain("<AppShell>");
  });
});
