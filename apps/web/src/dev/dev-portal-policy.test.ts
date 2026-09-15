import { describe, expect, it } from "vitest";
import {
  isDevPortalEnabled,
  localProfiles,
  resolveLocalProfile,
} from "./dev-portal-policy";

describe("development portal policy", () => {
  it("requires Development and an exact explicit opt-in", () => {
    expect(isDevPortalEnabled("development", undefined, "true")).toBe(true);
    expect(isDevPortalEnabled("development", "PRODUCTION", "true")).toBe(true);
    expect(isDevPortalEnabled("development", undefined, "TRUE")).toBe(false);
    expect(isDevPortalEnabled("development", undefined, undefined)).toBe(false);
  });

  it("requires the exact server-side DevSynthetic class in production", () => {
    expect(isDevPortalEnabled("production", "DEV_SYNTHETIC", "true")).toBe(true);
    expect(isDevPortalEnabled("production", undefined, "true")).toBe(false);
    expect(isDevPortalEnabled("production", "PRODUCTION", "true")).toBe(false);
    expect(isDevPortalEnabled("production", "DEV_SYNTHETIC", undefined)).toBe(false);
    expect(isDevPortalEnabled("test", "DEV_SYNTHETIC", "true")).toBe(false);
  });

  it("rejects arbitrary identity input", () => {
    expect(resolveLocalProfile("platform-admin")).toBeUndefined();
    expect(resolveLocalProfile("../active-driver")).toBeUndefined();
    expect(resolveLocalProfile("driver")).toEqual(localProfiles.driver);
  });
});
