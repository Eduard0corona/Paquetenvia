"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { bootstrapBffSession, landingPathForRole } from "@/auth/bff-session-installation";
import { isBffAuthenticationEnabled } from "@/auth/auth-mode";
import { signOutInstalledSession } from "@/auth/logout";
import { accountLabel } from "@/auth/session-account";
import { activeNavKey, navItemsForRole, type NavItem, type NavKey } from "./nav-items";
import { useShellContext, type ShellContext } from "./use-shell-context";

const sidebarId = "app-sidebar";

/**
 * Layout of the authenticated operations and finance screens (/ops/*, /finance/*): a left
 * sidebar on desktop, a compact top bar with a disclosure menu on mobile, the organization
 * context and the account menu. Navigation is derived from the role's capabilities; every
 * screen still gates itself through the API.
 */
export function AppShell({ children }: { readonly children: ReactNode }) {
  const pathname = usePathname();
  const shell = useShellContext();
  // The menu is open for the page it was opened on; navigating closes it.
  const [menuOpenedAt, setMenuOpenedAt] = useState<string | null>(null);
  const menuOpen = menuOpenedAt === pathname;
  const menuButtonRef = useRef<HTMLButtonElement>(null);
  const sidebarRef = useRef<HTMLElement>(null);

  const items = shell.status === "ready" ? navItemsForRole(shell.role) : [];
  const active = activeNavKey(items, pathname);
  const homeHref = items.find((item) => item.primary !== true)?.href ?? "/ops/dashboard";

  useEffect(() => {
    if (!menuOpen) return;
    sidebarRef.current?.querySelector<HTMLElement>("a, button")?.focus();
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      setMenuOpenedAt(null);
      menuButtonRef.current?.focus();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [menuOpen]);

  return (
    <div className="appShell">
      <a className="skipLink" href="#main">Saltar al contenido</a>
      <aside
        id={sidebarId}
        ref={sidebarRef}
        className="appSidebar"
        data-open={menuOpen}
        aria-label="Menú principal"
      >
        <Link className="appBrand" href={homeHref}>Paquetenvia</Link>
        <ShellNav shell={shell} items={items} active={active} />
      </aside>
      <button
        type="button"
        className="appBackdrop"
        data-open={menuOpen}
        tabIndex={-1}
        aria-hidden="true"
        onClick={() => setMenuOpenedAt(null)}
      />
      <div className="appMain">
        <header className="appTopbar">
          <button
            ref={menuButtonRef}
            type="button"
            className="btn btnSecondary appMenuButton"
            aria-expanded={menuOpen}
            aria-controls={sidebarId}
            onClick={() => setMenuOpenedAt(menuOpen ? null : pathname)}
          >
            Menú
          </button>
          <Link className="appBrand appTopbarBrand" href={homeHref}>Paquetenvia</Link>
          <OrganizationContext shell={shell} pathname={pathname} />
          {shell.status === "ready" && <AccountMenu shell={shell} />}
        </header>
        <main id="main" className="appContent" tabIndex={-1}>
          {children}
        </main>
      </div>
    </div>
  );
}

function ShellNav({
  shell,
  items,
  active,
}: {
  readonly shell: ShellContext;
  readonly items: readonly NavItem[];
  readonly active: NavKey | null;
}) {
  if (shell.status === "loading") {
    return (
      <div className="appNavSkeleton" role="status">
        <span className="srOnly">Cargando el menú.</span>
        <span aria-hidden="true" />
        <span aria-hidden="true" />
        <span aria-hidden="true" />
      </div>
    );
  }
  if (shell.status === "no_session") {
    return (
      <>
        <p className="appNavNote">Inicia sesión para ver tus secciones.</p>
        <Link className="btn btnPrimary" href="/login">Iniciar sesión</Link>
      </>
    );
  }
  if (items.length === 0) {
    return (
      <p className="appNavNote">
        {shell.contextsUnavailable
          ? "No pudimos cargar tus secciones. Recarga la página para intentarlo de nuevo."
          : "Tu rol en esta organización no tiene secciones en este panel."}
      </p>
    );
  }
  const primary = items.find((item) => item.primary === true);
  const links = items.filter((item) => item.primary !== true);
  return (
    <>
      {primary !== undefined && (
        <Link
          className="btn btnPrimary appNewOrder"
          href={primary.href}
          aria-current={active === primary.key ? "page" : undefined}
        >
          <span aria-hidden="true">+</span> {primary.label}
        </Link>
      )}
      <nav className="appNav" aria-label="Secciones">
        <ul>
          {links.map((item) => (
            <li key={item.key}>
              <Link href={item.href} aria-current={active === item.key ? "page" : undefined}>
                {item.label}
              </Link>
            </li>
          ))}
        </ul>
      </nav>
    </>
  );
}

function OrganizationContext({
  shell,
  pathname,
}: {
  readonly shell: ShellContext;
  readonly pathname: string;
}) {
  const router = useRouter();
  const [switching, setSwitching] = useState(false);
  if (shell.status !== "ready") return <div className="appOrg" />;
  const { session, contexts } = shell;
  const current = contexts.find((context) => context.organization_id === session.organizationId);
  const requestChange = session.requestOrganizationChange;

  if (contexts.length > 1 && requestChange !== undefined) {
    const switchTo = async (organizationId: string) => {
      const target = contexts.find((context) => context.organization_id === organizationId);
      if (target === undefined || organizationId === session.organizationId) return;
      setSwitching(true);
      try {
        // The session owner re-installs the session and fires the session-changed event,
        // so every screen drops the previous organization's data before loading again.
        await requestChange(organizationId);
      } finally {
        setSwitching(false);
      }
      if (activeNavKey(navItemsForRole(target.role), pathname) === null) {
        router.push(landingPathForRole(target.role));
      }
    };
    return (
      <label className="appOrg">
        <span>Organización</span>
        <select
          value={session.organizationId}
          disabled={switching}
          onChange={(event) => void switchTo(event.target.value)}
        >
          {contexts.map((context) => (
            <option key={context.organization_id} value={context.organization_id}>
              {context.display_name}
            </option>
          ))}
        </select>
      </label>
    );
  }
  return (
    <div className="appOrg">
      <span>Organización</span>
      <strong>{current?.display_name ?? "Sin organización"}</strong>
    </div>
  );
}

function AccountMenu({ shell }: { readonly shell: Extract<ShellContext, { status: "ready" }> }) {
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  const detailsRef = useRef<HTMLDetailsElement>(null);
  const name = accountLabel(shell.account);

  async function logout() {
    setBusy(true);
    setFailed(false);
    const left = await signOutInstalledSession(
      shell.session,
      window,
      window.location,
      process.env.NEXT_PUBLIC_AUTH_MODE,
    );
    if (left) return;
    setBusy(false);
    setFailed(true);
    // The local session objects were dropped; restore them from the still-valid cookie.
    if (isBffAuthenticationEnabled(process.env.NEXT_PUBLIC_AUTH_MODE)) void bootstrapBffSession(window);
  }

  return (
    <details
      ref={detailsRef}
      className="appAccount"
      onKeyDown={(event) => {
        if (event.key !== "Escape" || detailsRef.current === null) return;
        detailsRef.current.open = false;
        detailsRef.current.querySelector("summary")?.focus();
      }}
    >
      <summary>
        <span className="srOnly">Cuenta: </span>
        <span className="appAccountName">{name}</span>
      </summary>
      <div className="appAccountPanel">
        <p>Sesión iniciada como {name}.</p>
        <button type="button" className="btn btnSecondary" disabled={busy} onClick={() => void logout()}>
          {busy ? "Cerrando sesión…" : "Cerrar sesión"}
        </button>
        {failed && <p role="alert">No fue posible cerrar la sesión. Intenta de nuevo.</p>}
      </div>
    </details>
  );
}
