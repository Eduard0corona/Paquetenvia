import { describe, expect, it, vi } from "vitest";
import {
  buildLoginHref,
  buildSignInPromptHref,
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
import { endBffSession } from "./logout";
import {
  readDriverSessionMarker,
  writeDriverSessionMarker,
} from "../driver/session/driver-session-marker";
import { memoryStorage } from "../driver/session/memory-storage.fixtures";

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

describe("sign-in prompt href", () => {
  it("opens /login with a local return url only", () => {
    expect(buildSignInPromptHref("/driver/stops")).toBe("/login?return_url=%2Fdriver%2Fstops");
    expect(
      buildSignInPromptHref("/driver/stops/22222222-2222-2222-2222-222222222222"),
    ).toBe("/login?return_url=%2Fdriver%2Fstops%2F22222222-2222-2222-2222-222222222222");
    for (const unsafe of [
      "https://evil.test",
      "//evil.test",
      "/\\evil",
      "javascript:alert(1)",
      "/a\nb",
      "",
      "/login",
      null,
      undefined,
    ]) {
      expect(buildSignInPromptHref(unsafe)).toBe("/login");
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
      landingPath: "/ops/inbox",
    });
    expect(selectSessionInstallation(contexts, organizationA)).toEqual({
      kind: "driver",
      organizationId: organizationA,
      displayName: "A",
      landingPath: "/driver/stops",
    });
    expect(selectSessionInstallation([])).toEqual({ kind: "none" });
  });

  it("lands DISPATCHER and PLATFORM_ADMIN on the work inbox, FINANCE on COD, DRIVER on stops, VIEWER on Órdenes and the rest on the dashboard", () => {
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
    // UI-PHASE3-INBOX-2026-10-10: the roles of the dashboard and the queue counts land on the inbox.
    for (const role of ["DISPATCHER", "PLATFORM_ADMIN"]) expect(landingPathForRole(role), role).toBe("/ops/inbox");
    // UI-PHASE3-ORDERS-LIST-2026-10-12: VIEWER lists orders but has no inbox, so it lands on "Órdenes".
    expect(landingPathForRole("VIEWER")).toBe("/ops/orders");
    expect(
      selectSessionInstallation([
        { organization_id: organizationA, display_name: "A", role: "VIEWER", is_default: true },
      ]),
    ).toMatchObject({ kind: "operations", landingPath: "/ops/orders" });
    // Every other role keeps the dashboard, exactly as before.
    for (const role of ["BUSINESS_ADMIN", "ALLY_ADMIN", "UNKNOWN", ""])
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

describe("remembered driver (DRV-SESSION-EXPIRY-OFFLINE-2026-10-19, owner answers E2 and E3)", () => {
  const otherNamespace = "M".repeat(32);
  const driverContext = { organization_id: organizationA, display_name: "A", role: "DRIVER", is_default: true };

  function phone(initial: Record<string, string> = {}) {
    const storage = memoryStorage(initial);
    const target = host() as ReturnType<typeof host> & { localStorage: typeof storage };
    target.localStorage = storage;
    return { target, storage };
  }

  function signedInAs(sessionNamespace: string, contexts: unknown = [driverContext]) {
    return vi.fn(async (input: RequestInfo | URL) =>
      String(input) === "/auth/session"
        ? jsonResponse(200, sessionBody({ sessionNamespace }))
        : jsonResponse(200, contexts),
    ) as unknown as typeof fetch;
  }

  const offline = vi.fn(async () => {
    throw new TypeError("Failed to fetch");
  }) as unknown as typeof fetch;

  function remember(target: SessionHost, sessionNamespace = namespace, organizationId = organizationA) {
    writeDriverSessionMarker(target, { sessionNamespace, organizationId });
  }

  it("a confirmed driver session is remembered with no secret", async () => {
    const { target, storage } = phone();
    await bootstrapBffSession(target, signedInAs(namespace), null, { rememberDriver: true });

    expect(readDriverSessionMarker(target)).toEqual({ sessionNamespace: namespace, organizationId: organizationA });
    const raw = [...storage.values.values()].join("");
    for (const secret of [csrf, "Usuario", "user@paquetenvia.test"]) expect(raw).not.toContain(secret);
  });

  it("an offline reload opens the remembered driver locally, without a CSRF token", async () => {
    const { target } = phone();
    remember(target);

    const result = await bootstrapBffSession(target, offline, null, { rememberDriver: true });

    expect(result.remembered).toBe(true);
    const session = target.__paquetenviaDriverSession;
    expect(session).toMatchObject({
      organizationId: organizationA,
      cacheNamespace: namespace,
      credentialMode: "cookie",
      remembered: true,
    });
    expect(session && "getCsrfToken" in session ? () => session.getCsrfToken() : null).toThrow();
    expect(target.events).toContain("paquetenvia:driver-session-changed");
    expect(readDriverSessionMarker(target)).not.toBeNull();
  });

  it("a check that still cannot reach the server keeps the same remembered session", async () => {
    const { target } = phone();
    remember(target);
    await bootstrapBffSession(target, offline, null, { rememberDriver: true });
    const first = target.__paquetenviaDriverSession;
    target.events.length = 0;

    await bootstrapBffSession(target, offline, null, { rememberDriver: true });

    expect(target.__paquetenviaDriverSession).toBe(first);
    expect(target.events).toEqual([]);
  });

  it("opens nobody offline without a remembered driver or outside the driver pages", async () => {
    const empty = phone();
    await bootstrapBffSession(empty.target, offline, null, { rememberDriver: true });
    expect(empty.target.__paquetenviaDriverSession).toBeUndefined();

    const other = phone();
    remember(other.target);
    await bootstrapBffSession(other.target, offline);
    expect(other.target.__paquetenviaDriverSession).toBeUndefined();
  });

  it("the same driver in the same organization replaces it with the confirmed session", async () => {
    const { target } = phone();
    remember(target);
    await bootstrapBffSession(target, offline, null, { rememberDriver: true });

    await bootstrapBffSession(target, signedInAs(namespace), null, { rememberDriver: true });

    const session = target.__paquetenviaDriverSession;
    expect(session?.remembered).toBeUndefined();
    expect(session).toMatchObject({ cacheNamespace: namespace, organizationId: organizationA });
    expect(session && "getCsrfToken" in session ? session.getCsrfToken() : null).toBe(csrf);
  });

  it("another person signing in gets their own session and replaces what the phone remembers", async () => {
    const { target } = phone();
    remember(target);

    await bootstrapBffSession(target, signedInAs(otherNamespace), null, { rememberDriver: true });

    expect(target.__paquetenviaDriverSession?.cacheNamespace).toBe(otherNamespace);
    expect(readDriverSessionMarker(target)).toEqual({ sessionNamespace: otherNamespace, organizationId: organizationA });
  });

  it("the same person in another organization replaces it too", async () => {
    const { target } = phone();
    remember(target, namespace, organizationB);

    await bootstrapBffSession(target, signedInAs(namespace), null, { rememberDriver: true });

    expect(target.__paquetenviaDriverSession?.organizationId).toBe(organizationA);
    expect(readDriverSessionMarker(target)).toEqual({ sessionNamespace: namespace, organizationId: organizationA });
  });

  it("a role that is not a driver's, or no organization, forgets the remembered driver", async () => {
    for (const contexts of [
      [{ organization_id: organizationA, display_name: "A", role: "DISPATCHER", is_default: true }],
      [],
    ]) {
      const { target } = phone();
      remember(target);
      await bootstrapBffSession(target, signedInAs(namespace, contexts), null, { rememberDriver: true });
      expect(target.__paquetenviaDriverSession).toBeUndefined();
      expect(readDriverSessionMarker(target)).toBeNull();
    }
  });

  it("a 401 (the session ended) keeps what the phone remembers and installs nothing", async () => {
    const { target } = phone();
    remember(target);
    await bootstrapBffSession(target, offline, null, { rememberDriver: true });

    await bootstrapBffSession(
      target,
      vi.fn(async () => new Response(null, { status: 401 })) as unknown as typeof fetch,
      null,
      { rememberDriver: true },
    );

    expect(target.__paquetenviaDriverSession).toBeUndefined();
    expect(readDriverSessionMarker(target)).toEqual({ sessionNamespace: namespace, organizationId: organizationA });
  });

  it("a person the server does not authorize is forgotten", async () => {
    const { target } = phone();
    remember(target);
    await bootstrapBffSession(
      target,
      vi.fn(async () => jsonResponse(200, sessionBody({ authorized: false }))) as unknown as typeof fetch,
      null,
      { rememberDriver: true },
    );
    expect(target.__paquetenviaDriverSession).toBeUndefined();
    expect(readDriverSessionMarker(target)).toBeNull();
  });

  it("without the organization list, only the same person opens the remembered driver", async () => {
    const contextsDown = (sessionNamespace: string) =>
      vi.fn(async (input: RequestInfo | URL) =>
        String(input) === "/auth/session"
          ? jsonResponse(200, sessionBody({ sessionNamespace }))
          : new Response(null, { status: 503 }),
      ) as unknown as typeof fetch;

    const same = phone();
    remember(same.target);
    const result = await bootstrapBffSession(same.target, contextsDown(namespace), null, { rememberDriver: true });
    expect(result.remembered).toBe(true);
    expect(same.target.__paquetenviaDriverSession?.remembered).toBe(true);

    const other = phone();
    remember(other.target);
    await bootstrapBffSession(other.target, contextsDown(otherNamespace), null, { rememberDriver: true });
    expect(other.target.__paquetenviaDriverSession).toBeUndefined();
    expect(readDriverSessionMarker(other.target)).toBeNull();
  });

  it("logging out forgets the remembered driver once the server ended the session, and only then", async () => {
    const ended = phone();
    remember(ended.target);
    const leave = { assign: vi.fn() };
    await expect(
      endBffSession(
        csrf,
        ended.target,
        leave,
        vi.fn(async () => jsonResponse(200, { endSessionUrl: null })) as unknown as typeof fetch,
      ),
    ).resolves.toBe(true);
    expect(readDriverSessionMarker(ended.target)).toBeNull();

    const failed = phone();
    remember(failed.target);
    await expect(
      endBffSession(
        csrf,
        failed.target,
        leave,
        vi.fn(async () => new Response(null, { status: 500 })) as unknown as typeof fetch,
      ),
    ).resolves.toBe(false);
    expect(readDriverSessionMarker(failed.target)).not.toBeNull();
  });
});
