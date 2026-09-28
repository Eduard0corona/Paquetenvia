import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { OperationsSession } from "../session/operations-session";
import { OperationsApiError } from "./operations-api";
import {
  canManageTrackingLink,
  createTrackingLinkApi,
  createTrackingLinkIdempotencyKey,
  parsePublicTrackingLink,
  publicTrackingUrl,
  trackingLinkRoles,
} from "./tracking-link-api";

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

function linkBody(overrides: Record<string, unknown> = {}) {
  return {
    token_id: "77777777-7777-7777-7777-777777777777",
    order_id: orderId,
    token,
    expires_at: "2026-10-05T12:00:00.000Z",
    ...overrides,
  };
}

function created(body: unknown = linkBody()): Response {
  return new Response(JSON.stringify(body), {
    status: 201,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
    },
  });
}

describe("tracking link API (TRK-002-ISSUE-ENDPOINT)", () => {
  it("matches the AI-05 operations, paths and capability roles", () => {
    expect(openApi).toContain("  /orders/{orderId}/tracking-link:");
    expect(openApi).toContain("      operationId: issueTrackingLink");
    expect(openApi).toContain("  /orders/{orderId}/tracking-link/revoke:");
    expect(openApi).toContain("      operationId: revokeTrackingLink");
    expect(openApi).toContain(
      `    issueTrackingLink: [${trackingLinkRoles.join(", ")}]`,
    );
    expect(openApi).toContain(
      `    revokeTrackingLink: [${trackingLinkRoles.join(", ")}]`,
    );
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

  it("issues with a bearer, the tenant, a fresh idempotency key and no caching", async () => {
    const fetchMock = vi.fn().mockResolvedValue(created());
    vi.stubGlobal("fetch", fetchMock);

    const link = await createTrackingLinkApi("https://api.synthetic.test", bearer).issue(
      orderId,
      "tracking-link-key-0001",
    );

    expect(link).toEqual({
      tokenId: "77777777-7777-7777-7777-777777777777",
      orderId,
      token,
      expiresAt: "2026-10-05T12:00:00.000Z",
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

  it("revokes through the cookie session with the CSRF header and expects 204", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetchMock);

    await createTrackingLinkApi("https://ops.synthetic.test", cookie).revoke(
      orderId,
      "tracking-link-key-0002",
    );

    const [url, init] = fetchMock.mock.calls[0] as [URL, RequestInit];
    expect(url.toString()).toBe(
      `https://ops.synthetic.test/api/v1/orders/${orderId}/tracking-link/revoke`,
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
    await expect(api.issue(orderId, "k")).rejects.toEqual(
      new OperationsApiError(category),
    );
    await expect(api.revoke(orderId, "k")).rejects.toEqual(
      new OperationsApiError(category),
    );
  });

  it("rejects malformed responses and never echoes the token in the error", async () => {
    for (const body of [
      linkBody({ token: "short" }),
      linkBody({ extra: true }),
      linkBody({ order_id: "88888888-8888-8888-8888-888888888888" }),
      linkBody({ expires_at: "not-a-date" }),
      [linkBody()],
    ]) {
      vi.stubGlobal("fetch", vi.fn().mockResolvedValue(created(body)));
      const error = await createTrackingLinkApi("https://api.synthetic.test", bearer)
        .issue(orderId, "k")
        .catch((caught: unknown) => caught);
      expect(error).toBeInstanceOf(OperationsApiError);
      expect((error as OperationsApiError).category).toBe("invalid");
      expect(String((error as Error).message)).not.toContain(token);
    }
    expect(() => parsePublicTrackingLink(null)).toThrow(OperationsApiError);
  });

  it("refuses an invalid order id before any request", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    const api = createTrackingLinkApi("https://api.synthetic.test", bearer);
    await expect(api.issue("not-a-uuid", "k")).rejects.toBeInstanceOf(OperationsApiError);
    await expect(
      api.revoke("00000000-0000-0000-0000-000000000000", "k"),
    ).rejects.toBeInstanceOf(OperationsApiError);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("builds the public /track URL on the given origin and a namespaced key", () => {
    expect(publicTrackingUrl("https://ops.synthetic.test/ops/orders/x", token)).toBe(
      `https://ops.synthetic.test/track/${token}`,
    );
    expect(() => publicTrackingUrl("https://ops.synthetic.test", "../x")).toThrow(
      OperationsApiError,
    );
    expect(() => publicTrackingUrl("javascript:alert(1)", token)).toThrow(
      OperationsApiError,
    );
    expect(createTrackingLinkIdempotencyKey(() => "uuid-1")).toBe(
      "tracking-link-uuid-1",
    );
  });
});
