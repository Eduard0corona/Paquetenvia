import { describe, expect, it } from "vitest";
import { activeNavKey, navItemsForRole } from "./nav-items";

const keys = (role: string | null) => navItemsForRole(role).map((item) => item.key);

describe("app shell navigation by role", () => {
  it("derives every role's items from the capability mirror", () => {
    // UI-PHASE3-INBOX-2026-10-10: approved order "Bandeja · Órdenes · + Nueva orden · Rutas ·
    // Incidencias · Importar CSV · Repartidores"; "Órdenes" (UI-PHASE3-ORDERS-LIST-2026-10-12)
    // follows Bandeja, the board comes right after it and "Repartidores"
    // (UI-PHASE3-DRIVERS-2026-10-11) closes the operations items.
    expect(keys("DISPATCHER")).toEqual([
      "inbox",
      "orders",
      "operations",
      "new-order",
      "routes",
      "incidents",
      "csv-import",
      "drivers",
      "cod",
    ]);
    // PLATFORM_ADMIN also holds listSettlements (AI-05 x-capability-matrix, MFA enforced by the API).
    expect(keys("PLATFORM_ADMIN")).toEqual([
      "inbox",
      "orders",
      "operations",
      "new-order",
      "routes",
      "incidents",
      "csv-import",
      "drivers",
      "cod",
      "settlements",
    ]);
    expect(keys("FINANCE")).toEqual(["cod", "settlements"]);
    // VIEWER reads orders, operations and routes only (listOrders; listRoutes follows the Operations
    // read capability); like the dashboard and the queue counts, the inbox excludes it.
    expect(keys("VIEWER")).toEqual(["orders", "operations", "routes"]);
    for (const role of ["DRIVER", "BUSINESS_ADMIN", "ALLY_ADMIN", "UNKNOWN"]) expect(keys(role), role).toEqual([]);
    expect(keys(null)).toEqual([]);
  });

  it("offers the work inbox first, only to the dashboard and queue-count roles (UI-PHASE3-INBOX-2026-10-10)", () => {
    for (const role of ["DISPATCHER", "PLATFORM_ADMIN"])
      expect(navItemsForRole(role)[0], role).toEqual({ key: "inbox", href: "/ops/inbox", label: "Bandeja" });
    for (const role of ["VIEWER", "FINANCE", "DRIVER", "BUSINESS_ADMIN", "ALLY_ADMIN", "UNKNOWN"])
      expect(keys(role), role).not.toContain("inbox");
  });

  it("offers order creation as the one prominent action, only to roles that create orders", () => {
    for (const role of ["DISPATCHER", "PLATFORM_ADMIN"]) {
      const primary = navItemsForRole(role).filter((item) => item.primary === true);
      expect(primary).toEqual([{ key: "new-order", href: "/ops/orders/new", label: "Nueva orden", primary: true }]);
    }
    for (const role of ["FINANCE", "VIEWER", "DRIVER"])
      expect(navItemsForRole(role).some((item) => item.primary === true), role).toBe(false);
  });

  it("uses es-MX labels and the existing routes", () => {
    expect(navItemsForRole("PLATFORM_ADMIN").map(({ label, href }) => [label, href])).toEqual([
      ["Bandeja", "/ops/inbox"],
      ["Órdenes", "/ops/orders"],
      ["Tablero", "/ops/dashboard"],
      ["Nueva orden", "/ops/orders/new"],
      ["Rutas", "/ops/routes"],
      ["Incidencias", "/ops/incidents"],
      ["Importar CSV", "/ops/orders/import"],
      ["Repartidores", "/ops/drivers"],
      ["Cobro contra entrega", "/finance/cod"],
      ["Liquidaciones", "/finance/settlements"],
    ]);
  });

  it("offers Repartidores right after Importar CSV, only to the driver picker roles (UI-PHASE3-DRIVERS-2026-10-11)", () => {
    for (const role of ["DISPATCHER", "PLATFORM_ADMIN"]) {
      const items = keys(role);
      expect(items.indexOf("drivers"), role).toBe(items.indexOf("csv-import") + 1);
    }
    // FINANCE reads its payees only inside Liquidaciones; it never gets the dispatcher's list.
    for (const role of ["FINANCE", "VIEWER", "DRIVER", "BUSINESS_ADMIN", "ALLY_ADMIN", "UNKNOWN"])
      expect(keys(role), role).not.toContain("drivers");
    expect(activeNavKey(navItemsForRole("DISPATCHER"), "/ops/drivers")).toBe("drivers");
    expect(activeNavKey(navItemsForRole("FINANCE"), "/ops/drivers")).toBeNull();
  });

  it("offers Órdenes right after Bandeja to the listOrders roles only (UI-PHASE3-ORDERS-LIST-2026-10-12)", () => {
    for (const role of ["DISPATCHER", "PLATFORM_ADMIN"]) {
      const items = keys(role);
      expect(items.indexOf("orders"), role).toBe(items.indexOf("inbox") + 1);
    }
    expect(keys("VIEWER")[0]).toBe("orders");
    // FINANCE lists only COD-pending orders (FIN-PENDING-COD-LIST-FINANCE-2026-10-02); for its approved
    // "Órdenes (lectura)" item the owner answered "No por ahora", so it is not offered the screen.
    for (const role of ["FINANCE", "DRIVER", "BUSINESS_ADMIN", "ALLY_ADMIN", "UNKNOWN"])
      expect(keys(role), role).not.toContain("orders");
    expect(navItemsForRole("VIEWER")[0]).toEqual({ key: "orders", href: "/ops/orders", label: "Órdenes" });
  });

  it("gives Órdenes the list route and keeps the order detail under Bandeja", () => {
    const dispatcher = navItemsForRole("DISPATCHER");
    expect(activeNavKey(dispatcher, "/ops/orders")).toBe("orders");
    expect(activeNavKey(dispatcher, "/ops/orders/11111111-1111-1111-1111-111111111111")).toBe("inbox");
    expect(activeNavKey(dispatcher, "/ops/orders/new")).toBe("new-order");
    expect(activeNavKey(dispatcher, "/ops/orders/import")).toBe("csv-import");
    expect(activeNavKey(dispatcher, "/ops/ordersx")).toBeNull();
    // VIEWER has no Bandeja: a detail it reaches falls under Órdenes, the section it came from.
    const viewer = navItemsForRole("VIEWER");
    expect(activeNavKey(viewer, "/ops/orders")).toBe("orders");
    expect(activeNavKey(viewer, "/ops/orders/11111111-1111-1111-1111-111111111111")).toBe("orders");
    expect(activeNavKey(navItemsForRole("FINANCE"), "/ops/orders")).toBeNull();
  });

  it("marks the most specific item for the current path", () => {
    const items = navItemsForRole("PLATFORM_ADMIN");
    expect(activeNavKey(items, "/ops/inbox")).toBe("inbox");
    expect(activeNavKey(items, "/ops/dashboard")).toBe("operations");
    expect(activeNavKey(items, "/ops/orders/new")).toBe("new-order");
    expect(activeNavKey(items, "/ops/orders/import")).toBe("csv-import");
    // The order detail is opened from the inbox and returns to it.
    expect(activeNavKey(items, "/ops/orders/11111111-1111-1111-1111-111111111111")).toBe("inbox");
    expect(activeNavKey(items, "/ops/routes")).toBe("routes");
    expect(activeNavKey(items, "/finance/settlements")).toBe("settlements");
    expect(activeNavKey(items, "/ops/routesx")).toBeNull();
    expect(activeNavKey(items, "/ops/inboxx")).toBeNull();
    expect(activeNavKey(navItemsForRole("FINANCE"), "/ops/dashboard")).toBeNull();
    // VIEWER has no inbox: an organization switch away from it lands on the role's own page.
    expect(activeNavKey(navItemsForRole("VIEWER"), "/ops/inbox")).toBeNull();
    expect(activeNavKey(navItemsForRole("VIEWER"), "/ops/dashboard")).toBe("operations");
  });
});
