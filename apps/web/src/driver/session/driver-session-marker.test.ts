import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import {
  clearDriverSessionMarker,
  driverSessionMarkerKey,
  driverSessionMarkerMatches,
  forgetDriverSessionMarker,
  parseDriverSessionMarker,
  readDriverSessionMarker,
  writeDriverSessionMarker,
} from "./driver-session-marker";
import { memoryStorage } from "./memory-storage.fixtures";

const namespace = "N".repeat(32);
const otherNamespace = "M".repeat(32);
const organization = "11111111-1111-4111-8111-111111111111";
const otherOrganization = "22222222-2222-4222-8222-222222222222";

describe("driver session marker (DRV-SESSION-EXPIRY-OFFLINE-2026-10-19, owner answer E3)", () => {
  it("stores only the partition inputs: the per-identity namespace and the organization", () => {
    const storage = memoryStorage();
    writeDriverSessionMarker({ localStorage: storage }, {
      sessionNamespace: namespace,
      organizationId: organization,
    });

    expect([...storage.values.keys()]).toEqual([driverSessionMarkerKey]);
    expect(JSON.parse(storage.values.get(driverSessionMarkerKey)!)).toEqual({
      v: 1,
      sessionNamespace: namespace,
      organizationId: organization,
    });
    expect(readDriverSessionMarker({ localStorage: storage })).toEqual({
      sessionNamespace: namespace,
      organizationId: organization,
    });
  });

  it("never writes a token, a cookie, a name or an e-mail even when handed one", () => {
    const storage = memoryStorage();
    writeDriverSessionMarker({ localStorage: storage }, {
      sessionNamespace: namespace,
      organizationId: organization,
      csrfToken: "C".repeat(43),
      name: "Repartidor Sintético",
      email: "driver@paquetenvia.test",
    } as never);

    const raw = storage.values.get(driverSessionMarkerKey)!;
    expect(Object.keys(JSON.parse(raw)).sort()).toEqual(["organizationId", "sessionNamespace", "v"]);
    for (const secret of ["C".repeat(43), "Repartidor", "@", "token", "cookie"]) {
      expect(raw).not.toContain(secret);
    }
  });

  it.each([
    ["not JSON", "{"],
    ["an array", "[]"],
    ["another version", JSON.stringify({ v: 2, sessionNamespace: namespace, organizationId: organization })],
    ["an extra member", JSON.stringify({ v: 1, sessionNamespace: namespace, organizationId: organization, csrfToken: "x" })],
    ["a short namespace", JSON.stringify({ v: 1, sessionNamespace: "short", organizationId: organization })],
    ["a namespace with other characters", JSON.stringify({ v: 1, sessionNamespace: `${"N".repeat(31)}/`, organizationId: organization })],
    ["a non-canonical organization", JSON.stringify({ v: 1, sessionNamespace: namespace, organizationId: "11111111-1111-4111-8111-11111111111X" })],
    ["the empty organization", JSON.stringify({ v: 1, sessionNamespace: namespace, organizationId: "00000000-0000-0000-0000-000000000000" })],
    ["a value too long", JSON.stringify({ v: 1, sessionNamespace: namespace, organizationId: organization, pad: "x".repeat(600) })],
  ])("drops %s and remembers nobody", (_case, raw) => {
    const storage = memoryStorage({ [driverSessionMarkerKey]: raw });
    expect(parseDriverSessionMarker(raw)).toBeNull();
    expect(readDriverSessionMarker({ localStorage: storage })).toBeNull();
    expect(storage.values.has(driverSessionMarkerKey)).toBe(false);
  });

  it("refuses to write an invalid marker", () => {
    const storage = memoryStorage();
    writeDriverSessionMarker({ localStorage: storage }, { sessionNamespace: "short", organizationId: organization });
    expect(storage.values.size).toBe(0);
  });

  it("remembers nobody when storage is unavailable, and never throws", () => {
    const blocked = {
      getItem: () => {
        throw new DOMException("blocked", "SecurityError");
      },
      setItem: () => {
        throw new DOMException("blocked", "SecurityError");
      },
      removeItem: () => {
        throw new DOMException("blocked", "SecurityError");
      },
    };
    expect(readDriverSessionMarker({ localStorage: blocked })).toBeNull();
    expect(() =>
      writeDriverSessionMarker({ localStorage: blocked }, { sessionNamespace: namespace, organizationId: organization }),
    ).not.toThrow();
    expect(() => clearDriverSessionMarker({ localStorage: blocked })).not.toThrow();
    expect(readDriverSessionMarker({})).toBeNull();
  });

  it("matches only the same driver in the same organization", () => {
    const marker = { sessionNamespace: namespace, organizationId: organization };
    expect(driverSessionMarkerMatches(marker, namespace, organization)).toBe(true);
    expect(driverSessionMarkerMatches(marker, otherNamespace, organization)).toBe(false);
    expect(driverSessionMarkerMatches(marker, namespace, otherOrganization)).toBe(false);
    expect(driverSessionMarkerMatches(null, namespace, organization)).toBe(false);
  });

  it("is forgotten only for the driver and organization it names", () => {
    const storage = memoryStorage();
    const host = { localStorage: storage };
    writeDriverSessionMarker(host, { sessionNamespace: namespace, organizationId: organization });

    forgetDriverSessionMarker(host, otherNamespace, organization);
    forgetDriverSessionMarker(host, namespace, otherOrganization);
    expect(readDriverSessionMarker(host)).not.toBeNull();

    forgetDriverSessionMarker(host, namespace, organization);
    expect(readDriverSessionMarker(host)).toBeNull();
  });
});

describe("AI-07 record (DRV-SESSION-EXPIRY-OFFLINE-2026-10-19)", () => {
  const contract = readFileSync("../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml", "utf8");
  const screen = contract.slice(contract.indexOf("  driver_stop:\n"));
  const plain = screen.replace(/\s+/g, " ");

  it("keeps the partition on 401 and opens the remembered driver after a reload without signal", () => {
    for (const expected of [
      "session_expiry: decision: DRV-SESSION-EXPIRY-OFFLINE-2026-10-19",
      '"Tu sesión venció, entra de nuevo" with "Iniciar sesión"',
      "the same driver (the same sessionNamespace, derived by the API from SHA-256 of the AuthCenter sub)",
      "another person, or the same person in another organization, opens their own partition",
      "a 403 (membership or driver profile revoked) and a session without a usable credential still wipe the partition",
      "the 72-hour rule (OPS-003-OFFLINE-72H) keeps counting from each action's own device time",
      "offline_reload: decision: DRV-SESSION-EXPIRY-OFFLINE-2026-10-19",
      "never a token, cookie, CSRF token, password, name, e-mail or phone, never logged - in localStorage (paquetenvia:driver-session-marker)",
      "the marker authorizes nothing",
      "the offline queue survives a logout and a 401",
    ]) {
      expect(plain, expected).toContain(expected);
    }
    expect(plain).not.toContain("401 or 403 wipes the session's snapshot");
    expect(plain).not.toContain("wiped with the partition on 401 or 403");
    expect(plain).toContain(`in localStorage (${driverSessionMarkerKey})`);
  });
});
