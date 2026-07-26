import { describe, expect, it } from "vitest";
import { resolveDriverApiBaseUrl } from "./api-base-url";

describe("resolveDriverApiBaseUrl", () => {
  it("uses and normalizes a safe absolute configured origin", () => {
    expect(
      resolveDriverApiBaseUrl(
        "https://web.synthetic.test",
        "https://api.synthetic.test/",
        "production",
      ),
    ).toBe("https://api.synthetic.test");
  });

  it("uses same-origin when no override exists", () => {
    expect(
      resolveDriverApiBaseUrl(
        "http://127.0.0.1:3000",
        undefined,
        "development",
      ),
    ).toBe("http://127.0.0.1:3000");
  });

  it.each([
    ["relative URL", "/api"],
    ["credentials", "https://user:secret@example.test"],
    ["query", "https://example.test?token=secret"],
    ["hash", "https://example.test/#secret"],
    ["non-http", "file:///tmp/api"],
  ])("rejects %s", (_name, value) => {
    expect(() =>
      resolveDriverApiBaseUrl("https://web.synthetic.test", value, "production"),
    ).toThrow();
  });

  it("requires HTTPS for a configured production origin", () => {
    expect(() =>
      resolveDriverApiBaseUrl(
        "https://web.synthetic.test",
        "http://api.synthetic.test",
        "production",
      ),
    ).toThrow(/HTTPS/);
  });
});
