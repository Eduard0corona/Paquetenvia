import { describe, expect, it, vi } from "vitest";
import { emailNotVerifiedMessage, genericSignInFailedMessage, loginErrorMessage } from "./login-errors";
import {
  createIdempotencyKey,
  createOnboardingOrganization,
  createOrganizationMessage,
  fetchOwnApplications,
  onboardingMessages,
  onboardingPaths,
  parseOrganizationApplication,
  validateOnboardingDraft,
} from "./onboarding";
import { csrfHeaderName } from "./request-credentials";

const csrf = "C".repeat(43);
const organizationId = "11111111-1111-4111-8111-111111111111";

function json(status: number, body: unknown, contentType = "application/json"): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": contentType } });
}

describe("onboarding draft validation", () => {
  it("trims names and sends only the three contracted fields", () => {
    const result = validateOnboardingDraft({
      organizationType: "ALLY",
      legalName: "  Paquetería Aliada SA  ",
      displayName: " Aliada ",
    });
    expect(result).toEqual({
      ok: true,
      body: { organization_type: "ALLY", legal_name: "Paquetería Aliada SA", display_name: "Aliada" },
    });
  });

  it("rejects empty, oversized and control-character names and unknown types", () => {
    expect(validateOnboardingDraft({ organizationType: "BUSINESS", legalName: " ", displayName: "X" }).ok).toBe(false);
    expect(validateOnboardingDraft({ organizationType: "BUSINESS", legalName: "X".repeat(201), displayName: "X" }).ok).toBe(false);
    expect(validateOnboardingDraft({ organizationType: "BUSINESS", legalName: "X", displayName: "X".repeat(81) }).ok).toBe(false);
    expect(validateOnboardingDraft({ organizationType: "BUSINESS", legalName: "X\u0007", displayName: "X" }).ok).toBe(false);
    expect(
      validateOnboardingDraft({ organizationType: "PLATFORM" as never, legalName: "X", displayName: "X" }).ok,
    ).toBe(false);
  });
});

describe("createOnboardingOrganization", () => {
  it("posts the draft with the Idempotency-Key and CSRF header and maps 201", async () => {
    const fetcher = vi.fn(async () =>
      json(201, {
        organization_id: organizationId,
        organization_type: "BUSINESS",
        legal_name: "Negocio SA",
        display_name: "Negocio",
        status: "ACTIVE",
        role: "BUSINESS_ADMIN",
      }),
    );

    const result = await createOnboardingOrganization(
      { organizationType: "BUSINESS", legalName: "Negocio SA", displayName: "Negocio" },
      csrf,
      "onboarding-key-0000000001",
      fetcher as unknown as typeof fetch,
    );

    expect(result).toMatchObject({ kind: "created", application: { organizationId, status: "ACTIVE" } });
    expect(createOrganizationMessage(result)).toBe(onboardingMessages.businessCreated);
    const [url, init] = fetcher.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe(onboardingPaths.organizations);
    expect(init.method).toBe("POST");
    expect(init.credentials).toBe("include");
    const headers = init.headers as Record<string, string>;
    expect(headers["Idempotency-Key"]).toBe("onboarding-key-0000000001");
    expect(headers[csrfHeaderName]).toBe(csrf);
    expect(JSON.parse(init.body as string)).toEqual({
      organization_type: "BUSINESS",
      legal_name: "Negocio SA",
      display_name: "Negocio",
    });
  });

  it("tells an ally applicant the request is pending approval", async () => {
    const fetcher = vi.fn(async () =>
      json(201, {
        organization_id: organizationId,
        organization_type: "ALLY",
        legal_name: "Aliada SA",
        display_name: "Aliada",
        status: "PENDING_APPROVAL",
        role: "ALLY_ADMIN",
      }),
    );
    const result = await createOnboardingOrganization(
      { organizationType: "ALLY", legalName: "Aliada SA", displayName: "Aliada" },
      csrf,
      "onboarding-key-0000000002",
      fetcher as unknown as typeof fetch,
    );
    expect(createOrganizationMessage(result)).toBe(onboardingMessages.allyPending);
  });

  it("maps the 409 codes, 400, 403 and failures to specific results", async () => {
    const draft = { organizationType: "BUSINESS" as const, legalName: "N", displayName: "N" };
    const run = (response: Response | Error) =>
      createOnboardingOrganization(draft, csrf, "onboarding-key-0000000003", (async () => {
        if (response instanceof Error) {
          throw response;
        }
        return response;
      }) as unknown as typeof fetch);

    expect(await run(json(409, { code: "ORGANIZATION_LIMIT_REACHED" }, "application/problem+json"))).toEqual({
      kind: "limit_reached",
    });
    expect(await run(json(409, { code: "IDEMPOTENCY_CONFLICT" }, "application/problem+json"))).toEqual({
      kind: "idempotency_conflict",
    });
    expect(await run(json(409, { code: "OTHER" }))).toEqual({ kind: "unavailable" });
    expect(await run(json(400, {}))).toEqual({ kind: "invalid" });
    expect(await run(json(403, {}))).toEqual({ kind: "forbidden" });
    expect(await run(json(503, {}))).toEqual({ kind: "unavailable" });
    expect(await run(new TypeError("network"))).toEqual({ kind: "unavailable" });
    expect(createOrganizationMessage({ kind: "limit_reached" })).toBe(onboardingMessages.limitReached);
  });

  it("never sends an invalid draft", async () => {
    const fetcher = vi.fn();
    expect(
      await createOnboardingOrganization(
        { organizationType: "BUSINESS", legalName: "", displayName: "N" },
        csrf,
        "onboarding-key-0000000004",
        fetcher as unknown as typeof fetch,
      ),
    ).toEqual({ kind: "invalid" });
    expect(fetcher).not.toHaveBeenCalled();
  });
});

describe("own applications", () => {
  it("parses the list and rejects anything outside the contract", async () => {
    const application = {
      organization_id: organizationId,
      organization_type: "ALLY",
      display_name: "Aliada",
      status: "PENDING_APPROVAL",
      created_at: "2026-09-27T12:00:00Z",
    };
    expect(await fetchOwnApplications((async () => json(200, [application])) as unknown as typeof fetch)).toEqual([
      {
        organizationId,
        organizationType: "ALLY",
        displayName: "Aliada",
        status: "PENDING_APPROVAL",
        createdAt: "2026-09-27T12:00:00Z",
      },
    ]);
    expect(await fetchOwnApplications((async () => json(200, [{ ...application, status: "UNKNOWN" }])) as unknown as typeof fetch)).toBeNull();
    expect(await fetchOwnApplications((async () => json(503, {})) as unknown as typeof fetch)).toBeNull();
    expect(parseOrganizationApplication({ ...application, organization_type: "PLATFORM" })).toBeNull();
  });
});

describe("idempotency keys and sign-in errors", () => {
  it("builds keys within the AI-05 length limits", () => {
    const key = createIdempotencyKey(() => "11111111-1111-4111-8111-111111111111");
    expect(key).toBe("onboarding-11111111-1111-4111-8111-111111111111");
    expect(key.length).toBeGreaterThanOrEqual(16);
    expect(key.length).toBeLessThanOrEqual(128);
  });

  it("shows a specific message for an unverified email (AUTH-EMAIL-VERIFIED-REQUIRED)", () => {
    expect(loginErrorMessage("email_not_verified")).toBe(emailNotVerifiedMessage);
    expect(emailNotVerifiedMessage).toBe("Verifica tu correo en AuthCenter y vuelve a iniciar sesión");
    expect(loginErrorMessage("EMAIL_NOT_VERIFIED")).toBe(genericSignInFailedMessage);
  });
});
