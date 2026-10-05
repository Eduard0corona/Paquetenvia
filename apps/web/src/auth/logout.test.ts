import { describe, expect, it, vi } from "vitest";
import { bootstrapBffSession, installBffSession, type SessionHost } from "./bff-session-installation";
import { endBffSession, signOutInstalledSession } from "./logout";
import { accountLabel } from "./session-account";

const csrf = "c".repeat(43);
const namespace = "n".repeat(43);
const organizationA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const organizationB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

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

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const operations = { kind: "operations", organizationId: organizationA, displayName: "A", landingPath: "/ops/dashboard" } as const;

describe("shared logout", () => {
  it("posts the CSRF token, drops the in-memory sessions and leaves to the end-session URL", async () => {
    const target = host();
    installBffSession(target, operations, csrf, namespace, { account: { displayName: "Ana" } });
    const fetcher = vi.fn(async () => json(200, { endSessionUrl: "https://auth.example.test/logout" }));
    const location = { assign: vi.fn() };
    expect(await endBffSession(csrf, target, location, fetcher as unknown as typeof fetch)).toBe(true);
    const [path, init] = (fetcher.mock.calls as unknown as [string, RequestInit][])[0];
    expect(path).toBe("/auth/logout");
    expect(init.method).toBe("POST");
    expect((init.headers as Record<string, string>)["X-AuthCenter-CSRF"]).toBe(csrf);
    expect(target.__paquetenviaOperationsSession).toBeUndefined();
    expect(target.__paquetenviaAccount).toBeUndefined();
    expect(location.assign).toHaveBeenCalledWith("https://auth.example.test/logout");
  });

  it("reports a failed logout without navigating, still dropping the local objects", async () => {
    const target = host();
    installBffSession(target, operations, csrf, namespace);
    const location = { assign: vi.fn() };
    const fetcher = vi.fn(async () => json(500, {}));
    expect(await endBffSession(csrf, target, location, fetcher as unknown as typeof fetch)).toBe(false);
    expect(target.__paquetenviaOperationsSession).toBeUndefined();
    expect(location.assign).not.toHaveBeenCalled();
  });

  it("uses the session's own CSRF token in BFF mode and only clears a local Mock session", async () => {
    const target = host();
    installBffSession(target, operations, csrf, namespace);
    const session = target.__paquetenviaOperationsSession!;
    const location = { assign: vi.fn() };
    const fetcher = vi.fn(async () => json(401, {}));
    expect(await signOutInstalledSession(session, target, location, "bff", fetcher as unknown as typeof fetch)).toBe(true);
    expect(fetcher).toHaveBeenCalledTimes(1);
    expect(location.assign).toHaveBeenCalledWith("/login");

    const local = host();
    local.__paquetenviaOperationsSession = {
      organizationId: organizationA,
      sessionNamespace: namespace,
      getAccessToken: () => "token",
    };
    const localFetcher = vi.fn();
    const localLocation = { assign: vi.fn() };
    expect(
      await signOutInstalledSession(
        local.__paquetenviaOperationsSession,
        local,
        localLocation,
        undefined,
        localFetcher as unknown as typeof fetch,
      ),
    ).toBe(true);
    expect(localFetcher).not.toHaveBeenCalled();
    expect(local.__paquetenviaOperationsSession).toBeUndefined();
    expect(localLocation.assign).toHaveBeenCalledWith("/login");
  });

  it("shows the account name or a neutral label", () => {
    expect(accountLabel({ displayName: "Ana López" })).toBe("Ana López");
    expect(accountLabel({ displayName: "  " })).toBe("Mi cuenta");
    expect(accountLabel(null)).toBe("Mi cuenta");
  });
});

describe("organization switch through the BFF bootstrap", () => {
  it("re-installs the session for the chosen organization and fires the session-changed events", async () => {
    const target = host();
    const contexts = [
      { organization_id: organizationA, display_name: "A", role: "DISPATCHER", is_default: true },
      { organization_id: organizationB, display_name: "B", role: "FINANCE", is_default: false },
    ];
    const fetcher = vi.fn(async (input: RequestInfo | URL) =>
      String(input) === "/auth/session"
        ? json(200, {
            authenticated: true,
            authorized: true,
            mfa: false,
            csrfToken: csrf,
            sessionNamespace: namespace,
            user: { name: "Ana", email: null },
          })
        : json(200, contexts),
    );
    await bootstrapBffSession(target, fetcher as unknown as typeof fetch);
    expect(target.__paquetenviaOperationsSession?.organizationId).toBe(organizationA);
    expect(target.__paquetenviaAccount).toEqual({ displayName: "Ana" });
    target.events.length = 0;

    await target.__paquetenviaOperationsSession?.requestOrganizationChange?.(organizationB);
    expect(target.__paquetenviaOperationsSession?.organizationId).toBe(organizationB);
    // clearInstalledSessions announces the drop before the new session is installed.
    expect(target.events).toEqual([
      "paquetenvia:operations-session-changed",
      "paquetenvia:driver-session-changed",
      "paquetenvia:operations-session-changed",
    ]);
  });
});
