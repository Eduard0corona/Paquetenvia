import { describe, expect, it } from "vitest";
import type { BffSessionState } from "./bff-session";
import {
  accessDeniedMessage,
  genericSignInFailedMessage,
  loginErrorMessage,
} from "./login-errors";
import {
  buildStepUpPromptHref,
  isMfaRequiredProblem,
  isMfaRequiredResponse,
  mfaRequiredProblemCode,
  shouldOfferStepUp,
} from "./step-up";

function problem(status: number, body: unknown, contentType = "application/problem+json"): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": contentType } });
}

function session(overrides: Partial<Extract<BffSessionState, { status: "authenticated" }>> = {}): BffSessionState {
  return {
    status: "authenticated",
    authorized: true,
    mfa: false,
    csrfToken: "C".repeat(43),
    sessionNamespace: "N".repeat(32),
    user: { name: "Usuario", email: null },
    ...overrides,
  };
}

describe("MFA step-up", () => {
  it("recognizes only a 403 problem whose code is MFA_REQUIRED", () => {
    expect(mfaRequiredProblemCode).toBe("MFA_REQUIRED");
    expect(isMfaRequiredProblem(403, { title: "Forbidden", status: 403, code: "MFA_REQUIRED" })).toBe(true);
    expect(isMfaRequiredProblem(403, { title: "Forbidden", status: 403 })).toBe(false);
    expect(isMfaRequiredProblem(403, { code: "mfa_required" })).toBe(false);
    expect(isMfaRequiredProblem(401, { code: "MFA_REQUIRED" })).toBe(false);
    expect(isMfaRequiredProblem(403, null)).toBe(false);
    expect(isMfaRequiredProblem(403, "MFA_REQUIRED")).toBe(false);
  });

  it("reads the problem from a response without consuming it", async () => {
    const response = problem(403, { status: 403, code: "MFA_REQUIRED" });
    expect(await isMfaRequiredResponse(response)).toBe(true);
    expect(await response.json()).toEqual({ status: 403, code: "MFA_REQUIRED" });
    expect(await isMfaRequiredResponse(problem(403, { status: 403 }))).toBe(false);
    expect(await isMfaRequiredResponse(problem(403, { code: "MFA_REQUIRED" }, "text/plain"))).toBe(false);
    expect(await isMfaRequiredResponse(new Response("{", { status: 403, headers: { "Content-Type": "application/json" } }))).toBe(false);
    expect(await isMfaRequiredResponse(problem(500, { code: "MFA_REQUIRED" }))).toBe(false);
  });

  it("links to the /login prompt with a local return_url only", () => {
    expect(buildStepUpPromptHref("/ops/finance?tab=cod")).toBe(
      "/login?mfa=required&return_url=%2Fops%2Ffinance%3Ftab%3Dcod",
    );
    for (const unsafe of ["https://evil.test", "//evil.test", "/\\evil.test", "javascript:alert(1)", "/login", null, undefined]) {
      expect(buildStepUpPromptHref(unsafe)).toBe("/login?mfa=required");
    }
  });

  it("offers 'Verificar identidad' only when requested to an authorized session without MFA", () => {
    expect(shouldOfferStepUp(session(), true)).toBe(true);
    expect(shouldOfferStepUp(session(), false)).toBe(false);
    expect(shouldOfferStepUp(session({ mfa: true }), true)).toBe(false);
    expect(shouldOfferStepUp(session({ authorized: false }), true)).toBe(false);
    expect(shouldOfferStepUp({ status: "anonymous" }, true)).toBe(false);
    expect(shouldOfferStepUp({ status: "unavailable" }, true)).toBe(false);
  });
});

describe("login error mapping", () => {
  it("gives access_denied its own message", () => {
    expect(loginErrorMessage("access_denied")).toBe(accessDeniedMessage);
    expect(accessDeniedMessage).toBe("Tu cuenta no tiene acceso a Paquetenvia; pídelo a un administrador");
  });

  it("keeps every other error generic and shows nothing without an error", () => {
    for (const error of ["signin_failed", "server_error", "login_required", "ACCESS_DENIED", "<script>"]) {
      expect(loginErrorMessage(error)).toBe(genericSignInFailedMessage);
    }
    expect(loginErrorMessage(null)).toBeNull();
    expect(loginErrorMessage(undefined)).toBeNull();
    expect(loginErrorMessage("")).toBeNull();
  });
});
