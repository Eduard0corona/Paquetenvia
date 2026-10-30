import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { OperationsSession } from "../session/operations-session";
import {
  canManageRecipientNotices,
  createRecipientNoticesApi,
  parseRecipientNoticeList,
  parseRecipientNoticeStopResult,
  recipientNoticeRoles,
} from "./recipient-notices-api";
import { TenantApiError } from "./tenant-request";

const organizationId = "11111111-1111-1111-1111-111111111111";
const otherOrganizationId = "22222222-2222-2222-2222-222222222222";
const orderId = "66666666-6666-6666-6666-666666666666";

const bearer: OperationsSession = {
  organizationId,
  sessionNamespace: "synthetic",
  getAccessToken: () => "ephemeral-token",
};

const openApi = readFileSync(resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"), "utf8");

afterEach(() => {
  vi.unstubAllGlobals();
});

function noticeBody(overrides: Record<string, unknown> = {}) {
  return {
    notification_id: "77777777-7777-4777-8777-777777777777",
    notice: "TRACKING_LINK",
    channel: "WHATSAPP",
    status: "FAILED",
    attempts: 2,
    last_code: "MESSAGING_RECIPIENT_UNDELIVERABLE",
    sent_at: null,
    delivered_at: null,
    updated_at: "2026-10-30T15:00:00Z",
    ...overrides,
  };
}

function listBody(overrides: Record<string, unknown> = {}) {
  return { items: [noticeBody()], whatsapp_consent_attested: true, email_on_file: false, ...overrides };
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": status === 200 ? "application/json; charset=utf-8" : "application/problem+json" },
  });
}

describe("recipient notices API (NTF-RECIPIENT-NOTICE-PANEL-2026-10-30)", () => {
  it("matches the AI-05 operations, paths and capability roles", () => {
    expect(openApi).toContain("  /orders/{orderId}/recipient-notices:");
    expect(openApi).toContain("  /orders/{orderId}/recipient-notices/stop:");
    expect(openApi).toContain("      operationId: listOrderRecipientNotices");
    expect(openApi).toContain("      operationId: stopOrderRecipientNotices");
    expect(openApi).toContain(`    listOrderRecipientNotices: [${recipientNoticeRoles.join(", ")}]`);
    expect(openApi).toContain(`    stopOrderRecipientNotices: [${recipientNoticeRoles.join(", ")}]`);
    expect([...recipientNoticeRoles]).toEqual(["DISPATCHER", "PLATFORM_ADMIN"]);
  });

  it("mounts the panel only for the owner organization's dispatchers and platform admins (AI-07 shown_when)", () => {
    const context = (role: string, organization = organizationId) => ({
      organization_id: organization,
      display_name: "Synthetic",
      role,
      is_default: true,
    });
    expect(canManageRecipientNotices([context("DISPATCHER")], organizationId, organizationId)).toBe(true);
    expect(canManageRecipientNotices([context("PLATFORM_ADMIN")], organizationId, organizationId)).toBe(true);
    for (const role of ["VIEWER", "DRIVER", "FINANCE", "ALLY_ADMIN", "ALLY_OPERATOR", "BUSINESS_ADMIN", "BUSINESS_OPERATOR"])
      expect(canManageRecipientNotices([context(role)], organizationId, organizationId), role).toBe(false);
    // An organization that only operates the order sees no panel, and neither does a dispatcher elsewhere.
    expect(canManageRecipientNotices([context("DISPATCHER")], organizationId, otherOrganizationId)).toBe(false);
    expect(
      canManageRecipientNotices([context("DISPATCHER", otherOrganizationId)], organizationId, organizationId),
    ).toBe(false);
    expect(canManageRecipientNotices([], organizationId, organizationId)).toBe(false);
  });

  it("reads the list with the tenant, no caching and no body, and parses it strictly", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json(listBody()));
    vi.stubGlobal("fetch", fetchMock);

    const list = await createRecipientNoticesApi("https://api.synthetic.test", bearer).list(orderId);

    expect(list).toEqual({
      items: [
        {
          notificationId: "77777777-7777-4777-8777-777777777777",
          notice: "TRACKING_LINK",
          channel: "WHATSAPP",
          status: "FAILED",
          attempts: 2,
          lastCode: "MESSAGING_RECIPIENT_UNDELIVERABLE",
          sentAt: null,
          deliveredAt: null,
          updatedAt: "2026-10-30T15:00:00.000Z",
        },
      ],
      whatsappConsentAttested: true,
      emailOnFile: false,
    });
    const [url, init] = fetchMock.mock.calls[0] as [URL, RequestInit];
    expect(url.toString()).toBe(`https://api.synthetic.test/api/v1/orders/${orderId}/recipient-notices`);
    expect(init.method).toBe("GET");
    expect(init.cache).toBe("no-store");
    expect(init.body).toBeUndefined();
    const headers = init.headers as Record<string, string>;
    expect(headers["X-Organization-Id"]).toBe(organizationId);
    expect(headers["Idempotency-Key"]).toBeUndefined();
  });

  it("stops with the press's Idempotency-Key and no body, and returns the channels on record", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      json({ order_id: orderId, channels: ["WHATSAPP", "EMAIL"], recorded_at: "2026-10-30T16:00:00Z" }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const result = await createRecipientNoticesApi("https://api.synthetic.test", bearer).stop(
      orderId,
      "recipient-notices-stop-0001",
    );

    expect(result).toEqual({ orderId, channels: ["WHATSAPP", "EMAIL"], recordedAt: "2026-10-30T16:00:00.000Z" });
    const [url, init] = fetchMock.mock.calls[0] as [URL, RequestInit];
    expect(url.toString()).toBe(`https://api.synthetic.test/api/v1/orders/${orderId}/recipient-notices/stop`);
    expect(init.method).toBe("POST");
    expect(init.body).toBeUndefined();
    const headers = init.headers as Record<string, string>;
    expect(headers["Idempotency-Key"]).toBe("recipient-notices-stop-0001");
    expect(headers["Content-Type"]).toBeUndefined();
  });

  it("refuses an answer with a key, a value or a size the contract does not name", () => {
    expect(() => parseRecipientNoticeList(listBody({ phone: "+520000000002" }))).toThrow(TenantApiError);
    expect(() => parseRecipientNoticeList(listBody({ items: [noticeBody({ email: "destinatario@example.invalid" })] }))).toThrow(
      TenantApiError,
    );
    expect(() => parseRecipientNoticeList(listBody({ items: [noticeBody({ scheduled_for: null })] }))).toThrow(TenantApiError);
    for (const bad of [
      { notice: "ORDER_CREATED" },
      { channel: "IN_APP" },
      { status: "QUEUED" },
      { attempts: -1 },
      { attempts: 1.5 },
      { last_code: "SOMETHING_ELSE" },
      { last_code: "SYNTHETIC_ACCEPTED" },
      { updated_at: "not a date" },
      { notification_id: "not-a-uuid" },
    ])
      expect(() => parseRecipientNoticeList(listBody({ items: [noticeBody(bad)] })), JSON.stringify(bad)).toThrow(
        TenantApiError,
      );
    expect(() =>
      parseRecipientNoticeList(listBody({ items: Array.from({ length: 101 }, () => noticeBody()) })),
    ).toThrow(TenantApiError);
    expect(parseRecipientNoticeList(listBody({ items: Array.from({ length: 100 }, () => noticeBody()) })).items).toHaveLength(100);
    expect(parseRecipientNoticeList(listBody({ items: [noticeBody({ last_code: "MAX_ATTEMPTS_EXHAUSTED" })] })).items[0].lastCode).toBe(
      "MAX_ATTEMPTS_EXHAUSTED",
    );

    expect(() => parseRecipientNoticeStopResult({ order_id: orderId, channels: [], recorded_at: "2026-10-30T16:00:00Z" })).toThrow(
      TenantApiError,
    );
    expect(() =>
      parseRecipientNoticeStopResult({ order_id: orderId, channels: ["EMAIL", "EMAIL"], recorded_at: "2026-10-30T16:00:00Z" }),
    ).toThrow(TenantApiError);
    expect(() =>
      parseRecipientNoticeStopResult({
        order_id: orderId,
        channels: ["WHATSAPP"],
        recorded_at: "2026-10-30T16:00:00Z",
        recorded: true,
      }),
    ).toThrow(TenantApiError);
  });

  it("refuses a stop answer for another order and maps problems to categories with their stable code", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        json({ order_id: otherOrganizationId, channels: ["WHATSAPP"], recorded_at: "2026-10-30T16:00:00Z" }),
      ),
    );
    await expect(
      createRecipientNoticesApi("https://api.synthetic.test", bearer).stop(orderId, "recipient-notices-stop-0002"),
    ).rejects.toMatchObject({ category: "invalid" });

    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(json({ type: "about:blank", title: "Conflict.", status: 409, code: "RECIPIENT_CONTACT_UNAVAILABLE" }, 409)),
    );
    await expect(
      createRecipientNoticesApi("https://api.synthetic.test", bearer).stop(orderId, "recipient-notices-stop-0003"),
    ).rejects.toMatchObject({ category: "conflict", code: "RECIPIENT_CONTACT_UNAVAILABLE" });

    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(json({ type: "about:blank", title: "Forbidden.", status: 403, code: "MFA_REQUIRED" }, 403)),
    );
    await expect(createRecipientNoticesApi("https://api.synthetic.test", bearer).list(orderId)).rejects.toMatchObject({
      category: "forbidden",
      mfaRequired: true,
    });

    await expect(createRecipientNoticesApi("https://api.synthetic.test", bearer).list("not-an-order")).rejects.toMatchObject({
      category: "invalid",
    });
  });
});
