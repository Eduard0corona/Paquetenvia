import { describe, expect, it } from "vitest";
import {
  csrfHeaderName,
  hasUsableCredentials,
  MissingCredentialError,
  realtimeCredentials,
  resolveRequestAuthorization,
} from "./request-credentials";

const csrf = "a".repeat(43);

describe("request credentials", () => {
  it("sends only the cookie on reads in BFF mode", async () => {
    const result = await resolveRequestAuthorization(
      { credentialMode: "cookie", getCsrfToken: () => csrf },
      "GET",
    );
    expect(result).toEqual({ headers: {}, credentials: "include" });
  });

  it("adds the CSRF header on every BFF write and never an Authorization header", async () => {
    for (const method of ["POST", "PUT", "PATCH", "DELETE", "post"]) {
      const result = await resolveRequestAuthorization(
        { credentialMode: "cookie", getCsrfToken: () => csrf },
        method,
      );
      expect(result.credentials).toBe("include");
      expect(result.headers).toEqual({ [csrfHeaderName]: csrf });
      expect(Object.keys(result.headers)).not.toContain("Authorization");
    }
  });

  it("fails closed on a missing or malformed CSRF token", async () => {
    for (const token of ["", "short", "x".repeat(300), "bad token with spaces aaaaaaaaaaaaaaaaaaaaaaaaaaaa"]) {
      await expect(
        resolveRequestAuthorization(
          { credentialMode: "cookie", getCsrfToken: () => token },
          "POST",
        ),
      ).rejects.toBeInstanceOf(MissingCredentialError);
    }
  });

  it("keeps the Mock bearer mode without cookies", async () => {
    const result = await resolveRequestAuthorization(
      { getAccessToken: async () => "local-dispatcher-mfa" },
      "POST",
    );
    expect(result).toEqual({
      headers: { Authorization: "Bearer local-dispatcher-mfa" },
      credentials: "omit",
    });
  });

  it("rejects empty or oversized bearer tokens and propagates provider failures", async () => {
    for (const getAccessToken of [() => "", () => "t".repeat(8193)]) {
      await expect(
        resolveRequestAuthorization({ getAccessToken }, "GET"),
      ).rejects.toBeInstanceOf(MissingCredentialError);
    }
    await expect(
      resolveRequestAuthorization(
        {
          getAccessToken: () => {
            throw new Error("expired");
          },
        },
        "GET",
      ),
    ).rejects.toThrow("expired");
  });

  it("recognizes only complete credential shapes", () => {
    expect(hasUsableCredentials({ credentialMode: "cookie", getCsrfToken: () => csrf })).toBe(true);
    expect(hasUsableCredentials({ getAccessToken: () => "x" })).toBe(true);
    expect(hasUsableCredentials({ credentialMode: "cookie", getAccessToken: () => "x" })).toBe(false);
    expect(hasUsableCredentials({ credentialMode: "other", getAccessToken: () => "x" })).toBe(false);
    expect(hasUsableCredentials(null)).toBe(false);
  });

  it("maps realtime credentials without exposing tokens in cookie mode", async () => {
    const cookie = realtimeCredentials({ credentialMode: "cookie", getCsrfToken: () => csrf });
    expect(cookie.tokenFactory).toBeUndefined();
    expect(cookie.csrfTokenFactory?.()).toBe(csrf);

    const bearer = realtimeCredentials({ getAccessToken: () => "token" });
    expect(bearer.csrfTokenFactory).toBeUndefined();
    expect(await bearer.tokenFactory?.()).toBe("token");
  });
});
