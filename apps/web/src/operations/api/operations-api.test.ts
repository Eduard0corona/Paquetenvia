import { afterEach, describe, expect, it, vi } from "vitest";
import {
  buildOperationsDashboardSearch,
  createOperationsApi,
} from "./operations-api";
import type { OperationsSession } from "../session/operations-session";

const session: OperationsSession = {
  organizationId: "11111111-1111-1111-1111-111111111111",
  sessionNamespace: "synthetic",
  getAccessToken: () => "ephemeral-token",
};

afterEach(() => vi.unstubAllGlobals());

describe("operations api", () => {
  it("builds canonical server filters without page size", () => {
    const search = buildOperationsDashboardSearch({
      status: "DELIVERING",
      serviceType: "URGENT",
      unassigned: true,
      cursor: "opaque",
    });
    expect(search.toString()).toBe(
      "status=DELIVERING&service_type=URGENT&unassigned=true&cursor=opaque",
    );
    expect(search.has("page_size")).toBe(false);
  });

  it("sends current bearer and tenant without credentials", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          generated_at: "2026-07-27T02:00:00Z",
          items: [],
          next_cursor: null,
        }),
        { headers: { "content-type": "application/json" } },
      ),
    );
    vi.stubGlobal("fetch", fetchMock);

    await createOperationsApi("https://api.synthetic.test", session).list({});

    const [, init] = fetchMock.mock.calls[0] as [URL, RequestInit];
    expect(init.cache).toBe("no-store");
    expect(init.credentials).toBe("omit");
    expect(init.referrerPolicy).toBe("no-referrer");
    expect((init.headers as Record<string, string>).Authorization).toBe(
      "Bearer ephemeral-token",
    );
    expect((init.headers as Record<string, string>)["X-Organization-Id"]).toBe(
      session.organizationId,
    );
  });

  it.each([
    [401, "unauthorized"],
    [403, "forbidden"],
    [404, "not_found"],
    [400, "invalid"],
    [503, "unavailable"],
  ] as const)("classifies HTTP %s", async (status, category) => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(new Response(null, { status })),
    );
    await expect(
      createOperationsApi("https://api.synthetic.test", session).list({}),
    ).rejects.toMatchObject({ category });
  });
});
