import { canPerform } from "../../operations/contracts/capabilities";

/**
 * Navigation of the app shell for /ops and /finance. Every item is derived from the client
 * mirror of the AI-05 capability matrix (operations/contracts/capabilities.ts); it only
 * decides which links are shown. Each screen still asks the API what the role may do and
 * handles 403 itself: the shell is never the authorization barrier.
 */
export interface NavItem {
  readonly key: NavKey;
  readonly href: string;
  readonly label: string;
  /** Rendered as the prominent "+ Nueva orden" action instead of a list link. */
  readonly primary?: true;
}

export type NavKey =
  | "operations"
  | "new-order"
  | "csv-import"
  | "routes"
  | "incidents"
  | "cod"
  | "settlements";

interface NavRule extends NavItem {
  /** Other path prefixes that belong to this section (the order detail is under Operaciones). */
  readonly sectionPrefixes?: readonly string[];
  readonly visible: (role: string) => boolean;
}

const rules: readonly NavRule[] = [
  {
    key: "new-order",
    href: "/ops/orders/new",
    label: "Nueva orden",
    primary: true,
    // AI-07 /ops/orders/new: quote then order.
    visible: (role) => canPerform(role, "createQuote") && canPerform(role, "createOrder"),
  },
  {
    key: "operations",
    href: "/ops/dashboard",
    label: "Operaciones",
    sectionPrefixes: ["/ops/orders/"],
    visible: (role) => canPerform(role, "listOrders"),
  },
  {
    key: "csv-import",
    href: "/ops/orders/import",
    label: "Importar CSV",
    visible: (role) => canPerform(role, "previewOrderCsv"),
  },
  {
    key: "routes",
    href: "/ops/routes",
    label: "Rutas",
    // AI-05 listRoutes/getRoute: "Access follows the existing Operations read capability"
    // (listOrders); mutations stay DISPATCHER/PLATFORM_ADMIN and are enforced by the API.
    visible: (role) => canPerform(role, "listOrders"),
  },
  {
    key: "incidents",
    href: "/ops/incidents",
    label: "Incidencias",
    visible: (role) => canPerform(role, "listIncidents"),
  },
  {
    key: "cod",
    href: "/finance/cod",
    label: "Cobro contra entrega",
    visible: (role) => canPerform(role, "getOrderFinancials"),
  },
  {
    key: "settlements",
    href: "/finance/settlements",
    label: "Liquidaciones",
    visible: (role) => canPerform(role, "listSettlements"),
  },
];

/** Items the role sees, in display order; an unknown or missing role sees none. */
export function navItemsForRole(role: string | null): readonly NavItem[] {
  if (role === null) return [];
  return rules
    .filter((rule) => rule.visible(role))
    .map(({ key, href, label, primary }) => (primary ? { key, href, label, primary } : { key, href, label }));
}

/**
 * The item that owns a pathname: its own href, or a section prefix, choosing the most
 * specific match (/ops/orders/new is "Nueva orden", /ops/orders/{id} is "Operaciones").
 */
export function activeNavKey(items: readonly NavItem[], pathname: string): NavKey | null {
  let best: { key: NavKey; length: number } | null = null;
  for (const item of items) {
    const rule = rules.find((candidate) => candidate.key === item.key);
    const prefixes = [item.href, ...(rule?.sectionPrefixes ?? [])];
    for (const prefix of prefixes) {
      const matches = prefix.endsWith("/")
        ? pathname.startsWith(prefix)
        : pathname === prefix || pathname.startsWith(`${prefix}/`);
      if (matches && (best === null || prefix.length > best.length)) {
        best = { key: item.key, length: prefix.length };
      }
    }
  }
  return best?.key ?? null;
}
