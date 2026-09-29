import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { OperationsSession } from "../session/operations-session";
import {
  canManageTrackingLink,
  createTrackingLinkApi,
  createTrackingLinkIdempotencyKey,
  isPublicTrackingUrl,
  parsePublicTrackingLink,
  trackingLinkOrderFinishedCode,
  trackingLinkRoles,
} from "./tracking-link-api";
import { TenantApiError } from "./tenant-request";

const organizationId = "11111111-1111-1111-1111-111111111111";
const otherOrganizationId = "22222222-2222-2222-2222-222222222222";
const orderId = "66666666-6666-6666-6666-666666666666";
const token = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA";

const bearer: OperationsSession = {
  organizationId,
  sessionNamespace: "synthetic",
  getAccessToken: () => "ephemeral-token",
};

const cookie: OperationsSession = {
  organizationId,
  sessionNamespace: "synthetic",
  credentialMode: "cookie",
  getCsrfToken: () => "csrf_token_abcdefghijklmnopqrstuvwxyz0123",
};

const openApi = readFileSync(
  resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"),
  "utf8",
);

afterEach(() => {
  vi.unstubAllGlobals();
});

const publicUrl = `https://paquetenvia.test/track/${token}`;

function linkBody(overrides: Record<string, unknown> = {}) {
  return {
    token_id: "77777777-7777-7777-7777-777777777777",
    order_id: orderId,
    token,
    url: publicUrl,
    generation: 1,
    valid_until: null,
    ...overrides,
  };
}

function ok(body: unknown = linkBody()): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
    },
  });
}

describe("tracking link API (TRK-002-AUTO-LINK)", () => {
  it("matches the AI-05 operations, paths and capability roles", () => {
    expect(openApi).toContain("  /orders/{orderId}/tracking-link:");
    expect(openApi).toContain("      operationId: issueTrackingLink");
    expect(openApi).toContain(
      `    issueTrackingLink: [${trackingLinkRoles.join(", ")}]`,
    );
    // TRK-002-NO-REVOCATION: nobody revokes a link, so AI-05 has no revoke operation.
    expect(openApi).not.toContain("/orders/{orderId}/tracking-link/revoke");
    expect(openApi).not.toContain("revokeTrackingLink");
  });

  it("has no client call that revokes a link (TRK-002-NO-REVOCATION)", () => {
    const api = createTrackingLinkApi("https://api.synthetic.test", bearer);
    expect(Object.keys(api)).toEqual(["getOrCreate"]);
  });

  it("offers the actions only to the owner organization's dispatchers and platform admins", () => {
    const context = (role: string, organization = organizationId) => ({
      organization_id: organization,
      display_name: "Synthetic",
      role,
      is_default: true,
    });
    expect(
      canManageTrackingLink([context("DISPATCHER")], organizationId, organizationId),
    ).toBe(true);
    expect(
      canManageTrackingLink([context("PLATFORM_ADMIN")], organizationId, organizationId),
    ).toBe(true);
    for (const role of ["VIEWER", "DRIVER", "FINANCE", "BUSINESS_ADMIN", "ALLY_ADMIN"])
      expect(
        canManageTrackingLink([context(role)], organizationId, organizationId),
      ).toBe(false);
    // A dispatcher elsewhere, or of an organization that only operates the order, is not offered the action.
    expect(
      canManageTrackingLink(
        [context("DISPATCHER", otherOrganizationId)],
        organizationId,
        organizationId,
      ),
    ).toBe(false);
    expect(
      canManageTrackingLink([context("DISPATCHER")], organizationId, otherOrganizationId),
    ).toBe(false);
    expect(canManageTrackingLink([], organizationId, organizationId)).toBe(false);
  });

  it("declares get-or-create with a 200 link, its public URL and the finished-order conflict", () => {
    expect(openApi).toContain("TRK-002-AUTO-LINK");
    expect(openApi).toContain("get-or-create");
    expect(openApi).toContain("          $ref: '#/components/responses/TrackingLinkConflict'");
    expect(openApi).toContain(`          - ${trackingLinkOrderFinishedCode}`);
    expect(openApi).toContain("          pattern: ^https://[^/?#]+/track/[A-Za-z0-9_-]{43}$");
  });

  it("gets the link with a bearer, the tenant, a fresh idempotency key and no caching", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok());
    vi.stubGlobal("fetch", fetchMock);

    const link = await createTrackingLinkApi("https://api.synthetic.test", bearer).getOrCreate(
      orderId,
      "tracking-link-key-0001",
    );

    expect(link).toEqual({
      tokenId: "77777777-7777-7777-7777-777777777777",
      orderId,
      token,
      url: publicUrl,
      generation: 1,
      validUntil: null,
    });
    const [url, init] = fetchMock.mock.calls[0] as [URL, RequestInit];
    expect(url.toString()).toBe(
      `https://api.synthetic.test/api/v1/orders/${orderId}/tracking-link`,
    );
    expect(init.method).toBe("POST");
    expect(init.cache).toBe("no-store");
    expect(init.credentials).toBe("omit");
    expect(init.referrerPolicy).toBe("no-referrer");
    expect(init.body).toBeUndefined();
    const headers = init.headers as Record<string, string>;
    expect(headers.Authorization).toBe("Bearer ephemeral-token");
    expect(headers["X-Organization-Id"]).toBe(organizationId);
    expect(headers["Idempotency-Key"]).toBe("tracking-link-key-0001");
  });

  it("gets the link through the cookie session with the CSRF header", async () => {
    const fetchMock = vi.fn().mockResolvedValue(ok());
    vi.stubGlobal("fetch", fetchMock);

    await createTrackingLinkApi("https://ops.synthetic.test", cookie).getOrCreate(
      orderId,
      "tracking-link-key-0002",
    );

    const [url, init] = fetchMock.mock.calls[0] as [URL, RequestInit];
    expect(url.toString()).toBe(
      `https://ops.synthetic.test/api/v1/orders/${orderId}/tracking-link`,
    );
    expect(init.credentials).toBe("include");
    const headers = init.headers as Record<string, string>;
    expect(headers["X-AuthCenter-CSRF"]).toBe("csrf_token_abcdefghijklmnopqrstuvwxyz0123");
    expect(headers.Authorization).toBeUndefined();
  });

  it.each([
    [401, "unauthorized"],
    [403, "forbidden"],
    [404, "not_found"],
    [409, "conflict"],
    [503, "unavailable"],
  ] as const)("maps %i to %s without exposing any body", async (status, category) => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify({ title: "x", status }), { status }),
      ),
    );
    const api = createTrackingLinkApi("https://api.synthetic.test", bearer);
    await expect(api.getOrCreate(orderId, "k")).rejects.toEqual(
      new TenantApiError(category),
    );
  });

  it("marks a 403 MFA_REQUIRED problem for the step-up and keeps other 403s generic", async () => {
    const problem = (body: Record<string, unknown>) =>
      new Response(JSON.stringify(body), {
        status: 403,
        headers: { "Content-Type": "application/problem+json" },
      });
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(problem({ status: 403, code: "MFA_REQUIRED" }))
      .mockResolvedValueOnce(problem({ status: 403 }));
    vi.stubGlobal("fetch", fetchMock);
    const api = createTrackingLinkApi("https://api.synthetic.test", bearer);

    const error = await api.getOrCreate(orderId, "k").catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(TenantApiError);
    expect((error as TenantApiError).category).toBe("forbidden");
    expect((error as TenantApiError).mfaRequired).toBe(true);
    const generic = await api.getOrCreate(orderId, "k").catch((caught: unknown) => caught);
    expect((generic as TenantApiError).category).toBe("forbidden");
    expect((generic as TenantApiError).mfaRequired).toBe(false);
  });

  it("carries the finished-order problem code on a 409", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({ status: 409, code: trackingLinkOrderFinishedCode }),
          { status: 409, headers: { "Content-Type": "application/problem+json" } },
        ),
      ),
    );
    const error = await createTrackingLinkApi("https://api.synthetic.test", bearer)
      .getOrCreate(orderId, "k")
      .catch((caught: unknown) => caught);
    expect(error).toEqual(new TenantApiError("conflict", trackingLinkOrderFinishedCode));
  });

  it("parses a finished order's grace end and a later generation", () => {
    expect(
      parsePublicTrackingLink(
        linkBody({ generation: 3, valid_until: "2026-10-01T18:00:00Z" }),
      ),
    ).toMatchObject({ generation: 3, validUntil: "2026-10-01T18:00:00.000Z" });
  });

  it("rejects malformed responses and never echoes the token in the error", async () => {
    for (const body of [
      linkBody({ token: "short" }),
      linkBody({ extra: true }),
      linkBody({ order_id: "88888888-8888-8888-8888-888888888888" }),
      linkBody({ valid_until: "not-a-date" }),
      linkBody({ generation: 0 }),
      linkBody({ generation: 1.5 }),
      linkBody({ url: `http://paquetenvia.test/track/${token}` }),
      linkBody({ url: `https://paquetenvia.test/track/${"A".repeat(43)}` }),
      linkBody({ url: `https://paquetenvia.test/track/${token}?x=1` }),
      linkBody({ url: `https://user@paquetenvia.test/track/${token}` }),
      linkBody({ url: `javascript:alert(1)//track/${token}` }),
      linkBody({ expires_at: "2026-10-05T12:00:00.000Z" }),
      [linkBody()],
    ]) {
      vi.stubGlobal("fetch", vi.fn().mockResolvedValue(ok(body)));
      const error = await createTrackingLinkApi("https://api.synthetic.test", bearer)
        .getOrCreate(orderId, "k")
        .catch((caught: unknown) => caught);
      expect(error).toBeInstanceOf(TenantApiError);
      expect((error as TenantApiError).category).toBe("invalid");
      expect(String((error as Error).message)).not.toContain(token);
    }
    expect(() => parsePublicTrackingLink(null)).toThrow(TenantApiError);
  });

  it("refuses an invalid order id before any request", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const api = createTrackingLinkApi("https://api.synthetic.test", bearer);
    await expect(api.getOrCreate("not-a-uuid", "k")).rejects.toBeInstanceOf(TenantApiError);
    await expect(
      api.getOrCreate("00000000-0000-0000-0000-000000000000", "k"),
    ).rejects.toBeInstanceOf(TenantApiError);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("accepts only the server's https /track URL for the same token, and a namespaced key", () => {
    expect(isPublicTrackingUrl(publicUrl, token)).toBe(true);
    expect(isPublicTrackingUrl(`http://127.0.0.1:3000/track/${token}`, token)).toBe(true);
    expect(isPublicTrackingUrl(`http://paquetenvia.test/track/${token}`, token)).toBe(false);
    expect(isPublicTrackingUrl(`https://paquetenvia.test/track/${token}#x`, token)).toBe(false);
    expect(isPublicTrackingUrl(`https://paquetenvia.test/other/${token}`, token)).toBe(false);
    expect(isPublicTrackingUrl(`https://paquetenvia.test/track/../x`, "../x")).toBe(false);
    expect(isPublicTrackingUrl("not a url", token)).toBe(false);
    expect(createTrackingLinkIdempotencyKey(() => "uuid-1")).toBe(
      "tracking-link-uuid-1",
    );
  });
});
