import { describe, expect, it } from "vitest";
import { maximumRetryAfterSeconds, readRetryAfterSeconds } from "./retry-after";

describe("Retry-After (SEC-004)", () => {
  it("reads whole seconds, at least one, and caps a long wait", () => {
    for (const [value, seconds] of [
      ["60", 60],
      [" 60 ", 60],
      ["1", 1],
      ["3600", 3600],
      ["86400", maximumRetryAfterSeconds],
    ] as const) {
      expect(readRetryAfterSeconds(new Headers({ "Retry-After": value }))).toBe(seconds);
    }
  });

  it("ignores anything the API does not send", () => {
    for (const value of ["0", "-5", "1.5", "+3", "soon", "", "Wed, 21 Oct 2026 07:28:00 GMT", "1234567890"]) {
      expect(readRetryAfterSeconds(new Headers({ "Retry-After": value }))).toBeNull();
    }
    expect(readRetryAfterSeconds(new Headers())).toBeNull();
  });
});
