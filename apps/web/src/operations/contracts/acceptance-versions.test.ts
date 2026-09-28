import { describe, expect, it } from "vitest";
import { resolveAcceptanceVersions } from "./acceptance-versions";

describe("owner-set acceptance versions", () => {
  it("accepts the AI-05 version format", () => {
    expect(resolveAcceptanceVersions("terms-2026.09", "privacy_v3")).toEqual({
      termsVersion: "terms-2026.09",
      privacyVersion: "privacy_v3",
    });
  });

  it.each([
    [undefined, "privacy_v3"],
    ["terms-2026.09", undefined],
    ["", "privacy_v3"],
    [" terms-1", "privacy_v3"],
    ["terms 1", "privacy_v3"],
    ["x".repeat(65), "privacy_v3"],
    ["terms-2026.09", "OWNER_DECISION_REQUIRED"],
    ["owner_decision_required", "privacy_v3"],
  ])("fails closed without a default for %o / %o", (terms, privacy) => {
    expect(resolveAcceptanceVersions(terms, privacy)).toBeNull();
  });
});
