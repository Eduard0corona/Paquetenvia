import { describe, expect, it } from "vitest";
import { sessionIdentity } from "./driver-session";

describe("driver session boundary", () => {
  it("uses only opaque namespace and organization for lifecycle identity", () => {
    const identity = sessionIdentity({
      organizationId: "11111111-1111-1111-1111-111111111111",
      cacheNamespace: "opaque-session-0001",
      getAccessToken: () => "secret-token",
    });
    expect(identity).toContain("opaque-session-0001");
    expect(identity).toContain("11111111-1111-1111-1111-111111111111");
    expect(identity).not.toContain("secret-token");
  });

  it("does not invent a session server-side", () => {
    expect(sessionIdentity(null)).toBe("unavailable");
  });
});
