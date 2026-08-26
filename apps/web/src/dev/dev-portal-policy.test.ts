import { describe, expect, it } from "vitest";
import {
  isDevPortalEnabled,
  localProfiles,
  resolveLocalProfile,
} from "./dev-portal-policy";

describe("development portal policy", () => {
  it("requires Development and an exact explicit opt-in", () => {
    expect(isDevPortalEnabled("development", "true")).toBe(true);
    expect(isDevPortalEnabled("production", "true")).toBe(false);
    expect(isDevPortalEnabled("development", "TRUE")).toBe(false);
    expect(isDevPortalEnabled("development", undefined)).toBe(false);
  });

  it("rejects arbitrary identity input", () => {
    expect(resolveLocalProfile("platform-admin")).toBeUndefined();
    expect(resolveLocalProfile("../active-driver")).toBeUndefined();
    expect(resolveLocalProfile("driver")).toEqual(localProfiles.driver);
  });
});
