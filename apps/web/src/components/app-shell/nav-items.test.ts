import { describe, expect, it } from "vitest";
import { activeNavKey, navItemsForRole } from "./nav-items";

const keys = (role: string | null) => navItemsForRole(role).map((item) => item.key);

describe("app shell navigation by role", () => {
  it("derives every role's items from the capability mirror", () => {
    expect(keys("DISPATCHER")).toEqual(["new-order", "operations", "csv-import", "routes", "incidents", "cod"]);
    // PLATFORM_ADMIN also holds listSettlements (AI-05 x-capability-matrix, MFA enforced by the API).
    expect(keys("PLATFORM_ADMIN")).toEqual([
      "new-order",
      "operations",
      "csv-import",
      "routes",
      "incidents",
      "cod",
      "settlements",
    ]);
    expect(keys("FINANCE")).toEqual(["cod", "settlements"]);
    // VIEWER reads operations and routes only (listOrders; listRoutes follows the Operations read capability).
    expect(keys("VIEWER")).toEqual(["operations", "routes"]);
    for (const role of ["DRIVER", "BUSINESS_ADMIN", "ALLY_ADMIN", "UNKNOWN"]) expect(keys(role), role).toEqual([]);
    expect(keys(null)).toEqual([]);
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
      ["Nueva orden", "/ops/orders/new"],
      ["Operaciones", "/ops/dashboard"],
      ["Importar CSV", "/ops/orders/import"],
      ["Rutas", "/ops/routes"],
      ["Incidencias", "/ops/incidents"],
      ["Cobro contra entrega", "/finance/cod"],
      ["Liquidaciones", "/finance/settlements"],
    ]);
  });

  it("marks the most specific item for the current path", () => {
    const items = navItemsForRole("PLATFORM_ADMIN");
    expect(activeNavKey(items, "/ops/dashboard")).toBe("operations");
    expect(activeNavKey(items, "/ops/orders/new")).toBe("new-order");
    expect(activeNavKey(items, "/ops/orders/import")).toBe("csv-import");
    expect(activeNavKey(items, "/ops/orders/11111111-1111-1111-1111-111111111111")).toBe("operations");
    expect(activeNavKey(items, "/ops/routes")).toBe("routes");
    expect(activeNavKey(items, "/finance/settlements")).toBe("settlements");
    expect(activeNavKey(items, "/ops/routesx")).toBeNull();
    expect(activeNavKey(navItemsForRole("FINANCE"), "/ops/dashboard")).toBeNull();
  });
});
