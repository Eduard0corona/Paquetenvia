import { describe, expect, it, vi } from "vitest";
import {
  buildLoginHref,
  fetchBffSession,
  isLocalReturnUrl,
  buildStepUpHref,
  logoutBffSession,
  navigateAfterLogout,
  parseBffSession,
  parseEndSessionUrl,
} from "./bff-session";
import {
  bootstrapBffSession,
  installBffSession,
  landingPathForRole,
  selectSessionInstallation,
  type SessionHost,
} from "./bff-session-installation";

const csrf = "C".repeat(43);
const namespace = "N".repeat(32);
const organizationA = "11111111-1111-4111-8111-111111111111";
const organizationB = "22222222-2222-4222-8222-222222222222";

function sessionBody(overrides: Record<string, unknown> = {}) {
  return {
    authenticated: true,
    authorized: true,
    mfa: false,
    csrfToken: csrf,
    sessionNamespace: namespace,
    user: { name: "Usuario", email: "user@paquetenvia.test" },
    ...overrides,
  };
}

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function host(): SessionHost & { events: string[] } {
  const events: string[] = [];
  return {
    events,
    dispatchEvent(event: Event) {
      events.push(event.type);
      return true;
    },
  };
}

describe("BFF login href", () => {
  it("navigates to the API login with a local return url only", () => {
    expect(buildLoginHref("/ops/dashboard")).toBe("/auth/login?return_url=%2Fops%2Fdashboard");
    for (const unsafe of ["https://evil.test", "//evil.test", "/\\evil", "javascript:alert(1)", "/a\nb", ""]) {
      expect(isLocalReturnUrl(unsafe)).toBe(false);
      expect(buildLoginHref(unsafe)).toBe("/auth/login?return_url=%2Flogin");
    }
  });
});

describe("BFF session", () => {
  it("parses the session without any token fields", () => {
    const parsed = parseBffSession(sessionBody());
    expect(parsed).toEqual({
      status: "authenticated",
      authorized: true,
      mfa: false,
      csrfToken: csrf,
      sessionNamespace: namespace,
      user: { name: "Usuario", email: "user@paquetenvia.test" },
    });
  });

  it("rejects malformed session documents", () => {
    for (const body of [
      null,
      sessionBody({ authenticated: false }),
      sessionBody({ csrfToken: "short" }),
      sessionBody({ sessionNamespace: "bad namespace" }),
      sessionBody({ authorized: "yes" }),
      sessionBody({ user: null }),
    ]) {
      expect(parseBffSession(body)).toEqual({ status: "unavailable" });
    }
  });

  it("requests the session same-origin with credentials and maps 401 to anonymous", async () => {
    const fetcher = vi.fn(async () => new Response(null, { status: 401 }));
    expect(await fetchBffSession(fetcher as unknown as typeof fetch)).toEqual({ status: "anonymous" });
    expect(fetcher).toHaveBeenCalledWith(
      "/auth/session",
      expect.objectContaining({ credentials: "include", cache: "no-store", method: "GET" }),
    );
  });

  it("maps network and server failures to unavailable", async () => {
    const failing = vi.fn(async () => {
      throw new TypeError("offline");
    });
    const serverError = vi.fn(async () => new Response(null, { status: 503 }));
    expect(await fetchBffSession(failing as unknown as typeof fetch)).toEqual({ status: "unavailable" });
    expect(await fetchBffSession(serverError as unknown as typeof fetch)).toEqual({ status: "unavailable" });
  });

  it("logs out with POST, credentials and the CSRF header and returns the end-session URL", async () => {
    const endSessionUrl =
      "https://authcenter.test/oauth/logout?id_token_hint=a.b.c&post_logout_redirect_uri=https%3A%2F%2Fapp.paquetenvia.test%2Flogin";
    const fetcher = vi.fn(async () => jsonResponse(200, { endSessionUrl }));
    expect(await logoutBffSession(csrf, fetcher as unknown as typeof fetch)).toEqual({
      ok: true,
      endSessionUrl,
    });
    expect(fetcher).toHaveBeenCalledWith(
      "/auth/logout",
      expect.objectContaining({
        method: "POST",
        credentials: "include",
        cache: "no-store",
        headers: { "X-AuthCenter-CSRF": csrf, Accept: "application/json" },
      }),
    );
  });

  it("never sends a logout without a well-formed CSRF token", async () => {
    const fetcher = vi.fn(async () => jsonResponse(200, { endSessionUrl: null }));
    expect(await logoutBffSession("short", fetcher as unknown as typeof fetch)).toEqual({ ok: false });
    expect(fetcher).not.toHaveBeenCalled();
  });

  it("treats a null end-session URL, a gone session or an unreadable body as a local-only logout", async () => {
    const nullUrl = vi.fn(async () => jsonResponse(200, { endSessionUrl: null }));
    const gone = vi.fn(async () => new Response(null, { status: 401 }));
    const unreadable = vi.fn(async () => new Response("not json", { status: 200 }));
    const localOnly = { ok: true, endSessionUrl: null };
    expect(await logoutBffSession(csrf, nullUrl as unknown as typeof fetch)).toEqual(localOnly);
    expect(await logoutBffSession(csrf, gone as unknown as typeof fetch)).toEqual(localOnly);
    expect(await logoutBffSession(csrf, unreadable as unknown as typeof fetch)).toEqual(localOnly);
  });

  it("reports network and server failures as a failed logout", async () => {
    const offline = vi.fn(async () => {
      throw new TypeError("offline");
    });
    const serverError = vi.fn(async () => new Response(null, { status: 503 }));
    const legacyNoContent = vi.fn(async () => new Response(null, { status: 204 }));
    expect(await logoutBffSession(csrf, offline as unknown as typeof fetch)).toEqual({ ok: false });
    expect(await logoutBffSession(csrf, serverError as unknown as typeof fetch)).toEqual({ ok: false });
    expect(await logoutBffSession(csrf, legacyNoContent as unknown as typeof fetch)).toEqual({ ok: false });
  });

  it("follows only absolute HTTPS end-session URLs without credentials", () => {
    expect(parseEndSessionUrl("https://authcenter.test/oauth/logout?id_token_hint=x")).toBe(
      "https://authcenter.test/oauth/logout?id_token_hint=x",
    );
    for (const value of [
      null,
      undefined,
      42,
      "",
      "/oauth/logout",
      "http://authcenter.test/oauth/logout",
      "javascript:alert(1)",
      "https://user:pass@authcenter.test/oauth/logout",
      `https://authcenter.test/${"a".repeat(16_400)}`,
    ]) {
      expect(parseEndSessionUrl(value)).toBeNull();
    }
  });

  it("navigates to AuthCenter with window.location.assign, or to /login when there is no URL", () => {
    const assign = vi.fn();
    const url = "https://authcenter.test/oauth/logout?id_token_hint=x";
    navigateAfterLogout({ ok: true, endSessionUrl: url }, { assign });
    navigateAfterLogout({ ok: true, endSessionUrl: null }, { assign });
    navigateAfterLogout({ ok: false }, { assign });
    expect(assign.mock.calls).toEqual([[url], ["/login"], ["/login"]]);
  });

  it("builds a step-up login that keeps the local-only return_url rule", () => {
    expect(buildStepUpHref("/ops/finance?tab=cod")).toBe(
      "/auth/login?mfa=required&return_url=%2Fops%2Ffinance%3Ftab%3Dcod",
    );
    for (const unsafe of ["https://evil.test", "//evil.test", "/\\evil.test", "javascript:alert(1)", null]) {
      expect(buildStepUpHref(unsafe)).toBe("/auth/login?mfa=required&return_url=%2Flogin");
    }
  });
});

describe("BFF session installation", () => {
  const contexts = [
    { organization_id: organizationA, display_name: "A", role: "DRIVER", is_default: false },
    { organization_id: organizationB, display_name: "B", role: "DISPATCHER", is_default: true },
  ];

  it("prefers an explicit organization, then the default membership", () => {
    expect(selectSessionInstallation(contexts)).toEqual({
      kind: "operations",
      organizationId: organizationB,
      displayName: "B",
      landingPath: "/ops/dashboard",
    });
    expect(selectSessionInstallation(contexts, organizationA)).toEqual({
      kind: "driver",
      organizationId: organizationA,
      displayName: "A",
      landingPath: "/driver/stops",
    });
    expect(selectSessionInstallation([])).toEqual({ kind: "none" });
  });

  it("lands FINANCE on COD with the operations session, DRIVER on stops and the rest on the dashboard", () => {
    expect(
      selectSessionInstallation([
        { organization_id: organizationA, display_name: "A", role: "FINANCE", is_default: true },
      ]),
    ).toEqual({
      kind: "operations",
      organizationId: organizationA,
      displayName: "A",
      landingPath: "/finance/cod",
    });
    expect(landingPathForRole("FINANCE")).toBe("/finance/cod");
    expect(landingPathForRole("DRIVER")).toBe("/driver/stops");
    for (const role of ["DISPATCHER", "PLATFORM_ADMIN", "BUSINESS_ADMIN", "ALLY_ADMIN", "VIEWER", "UNKNOWN"])
      expect(landingPathForRole(role), role).toBe("/ops/dashboard");
  });

  it("installs a cookie-mode session without access tokens", () => {
    const target = host();
    installBffSession(
      target,
      { kind: "operations", organizationId: organizationB, displayName: "B", landingPath: "/ops/dashboard" },
      csrf,
      namespace,
    );
    const session = target.__paquetenviaOperationsSession;
    expect(session?.credentialMode).toBe("cookie");
    expect(session && "getAccessToken" in session).toBe(false);
    expect(session?.organizationId).toBe(organizationB);
    expect(session?.sessionNamespace).toBe(namespace);
    expect(target.__paquetenviaDriverSession).toBeUndefined();
    expect(target.events).toContain("paquetenvia:operations-session-changed");
  });

  it("bootstraps the driver session from the API over the same origin", async () => {
    const target = host();
    const fetcher = vi.fn(async (input: RequestInfo | URL) =>
      String(input) === "/auth/session"
        ? jsonResponse(200, sessionBody())
        : jsonResponse(200, [contexts[0]]),
    );
    const result = await bootstrapBffSession(target, fetcher as unknown as typeof fetch);
    expect(result.installation.kind).toBe("driver");
    expect(target.__paquetenviaDriverSession?.cacheNamespace).toBe(namespace);
    for (const call of fetcher.mock.calls as unknown as [string, RequestInit][]) {
      expect(call[0].startsWith("/")).toBe(true);
      expect(call[1].credentials).toBe("include");
    }
  });

  it("clears sessions for unauthorized identities and never lists organizations", async () => {
    const target = host();
    target.__paquetenviaOperationsSession = {
      organizationId: organizationB,
      sessionNamespace: namespace,
      credentialMode: "cookie",
      getCsrfToken: () => csrf,
    };
    const fetcher = vi.fn(async () => jsonResponse(200, sessionBody({ authorized: false })));
    const result = await bootstrapBffSession(target, fetcher as unknown as typeof fetch);
    expect(result.installation).toEqual({ kind: "none" });
    expect(target.__paquetenviaOperationsSession).toBeUndefined();
    expect(fetcher).toHaveBeenCalledTimes(1);
  });
});
