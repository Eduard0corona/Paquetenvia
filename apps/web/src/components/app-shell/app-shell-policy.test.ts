import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

const read = (path: string) => readFileSync(path, "utf8");

function files(directory: string, pattern: RegExp): string[] {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    if (statSync(path).isDirectory()) return files(path, pattern);
    return pattern.test(name) && !name.includes(".test.") ? [path] : [];
  });
}

/** The screen component each /ops and /finance page renders. */
const screens = [
  "src/operations/components/operations-inbox-shell.tsx",
  "src/operations/components/operations-dashboard-shell.tsx",
  "src/operations/components/operations-order-detail-shell.tsx",
  "src/operations/components/create-order-shell.tsx",
  "src/operations/components/csv-import-shell.tsx",
  "src/operations/components/incidents-shell.tsx",
  "src/operations/components/manual-routes-shell.tsx",
  "src/finance/components/cod-shell.tsx",
  "src/finance/components/settlements-shell.tsx",
];

describe("app shell policy", () => {
  it("wraps every /ops and /finance page in the shared shell layout", () => {
    for (const section of ["ops", "finance"]) {
      const layout = read(`src/app/${section}/layout.tsx`);
      expect(layout).toContain('from "@/components/app-shell/app-shell"');
      expect(layout).toContain("<AppShell>{children}</AppShell>");
      const pages = files(`src/app/${section}`, /^page\.tsx$/);
      expect(pages.length).toBeGreaterThan(0);
      for (const page of pages) expect(read(page), page).not.toMatch(/<main\b/);
    }
  });

  it("keeps the shell off the driver PWA, public tracking, login, onboarding, home, dev and health", () => {
    for (const section of ["driver", "track", "login", "onboarding", "dev", "health"]) {
      const layout = `src/app/${section}/layout.tsx`;
      if (existsSync(layout)) expect(read(layout), layout).not.toContain("AppShell");
    }
    expect(read("src/app/layout.tsx")).not.toContain("AppShell");
    for (const source of files("src/driver", /\.tsx$/)) expect(read(source), source).not.toContain("AppShell");
  });

  it("gives the shell one main landmark, a skip link and aria-current navigation", () => {
    const shell = read("src/components/app-shell/app-shell.tsx");
    expect(shell.match(/<main\b/g)).toHaveLength(1);
    expect(shell).toContain('<main id="main"');
    expect(shell).toContain('<a className="skipLink" href="#main">Saltar al contenido</a>');
    expect(shell).toContain('aria-current={active === item.key ? "page" : undefined}');
    expect(shell).toContain("aria-expanded={menuOpen}");
    expect(shell).toContain("navItemsForRole(");
    // Switching matches the organization by id, never by display name.
    expect(shell).toContain("value={session.organizationId}");
    expect(shell).not.toContain("display_name ===");
    // Logout goes through the shared helper that /login uses as well.
    expect(shell).toContain("signOutInstalledSession(");
    expect(read("src/auth/components/login-experience.tsx")).toContain("endBffSession(");
  });

  it.each(screens)("%s renders inside the shell with its own header and no per-page navigation", (path) => {
    const source = read(path);
    expect(source).not.toMatch(/<main\b/);
    expect(source).toContain("<PageHeader");
    expect(source).not.toContain("Volver a Operaciones");
    expect(source).not.toMatch(/opsHeader|opsEyebrow|opsShell/);
    // Links between sections belong to the shell navigation.
    for (const href of ["/ops/orders/new", "/ops/routes", "/ops/orders/import", "/finance/cod", "/finance/settlements"])
      expect(source).not.toContain(`href="${href}"`);
    expect(source).not.toContain("Organización activa");
  });

  it("leaves a single Feedback and ScreenGate implementation", () => {
    expect(existsSync("src/operations/components/tenant-feedback.tsx")).toBe(false);
    const definitions = files("src", /\.tsx?$/).filter((path) =>
      /function\s+(Feedback|ScreenGate|TenantFeedback)\b/.test(read(path)),
    );
    expect(definitions).toEqual(["src/components/ui/feedback.tsx"]);
    for (const path of screens.filter((screen) => !screen.includes("dashboard") && !screen.includes("detail") && !screen.includes("routes")))
      expect(read(path), path).toContain('from "../../components/ui/feedback"');
  });

  it("leaves no local Row/Detail/Summary/MoneyRow label-value helpers nor ad-hoc money or time formatting", () => {
    for (const path of [...screens, "src/operations/components/operations-order-card.tsx"]) {
      const source = read(path);
      expect(source, path).not.toMatch(/function\s+(Row|Detail|MoneyRow|short|money)\s*\(/);
      expect(source, path).not.toMatch(/<dt>/);
      expect(source, path).not.toMatch(/formatMxnCents(WithCurrency)?\(/);
    }
  });

  it("resolves the API base URL and short ids in one place", () => {
    const fallback = /process\.env\.NEXT_PUBLIC_API_BASE_URL\s*\?\?/;
    const owners = files("src", /\.tsx?$/).filter((path) => fallback.test(read(path)));
    expect(owners).toEqual(["src/lib/api-base-url.ts"]);
    const shortIds = files("src", /\.tsx?$/).filter((path) => /\.slice\(0, 8\)/.test(read(path)));
    expect(shortIds).toEqual(["src/lib/short-id.ts"]);
  });
});
