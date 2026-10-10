import { describe, expect, it } from "vitest";
import { assertNeutralTrackingBrand } from "../../../next.config";
import {
  publicTrackingHeading,
  publicTrackingMetadata,
  resolvePublicTrackingBrand,
} from "./public-tracking-branding";

// GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10: the public tracking surface uses a
// neutral heading and never the unvalidated commercial name.
describe("public tracking branding while GATE-001 is open", () => {
  it("uses the owner's neutral heading", () => {
    expect(publicTrackingHeading).toBe("Seguimiento de envío");
  });

  it("gives every /track page a neutral head without the driver PWA manifest", () => {
    expect(publicTrackingMetadata.title).toBe(publicTrackingHeading);
    expect(publicTrackingMetadata.manifest).toBeNull();
    expect(JSON.stringify(publicTrackingMetadata)).not.toMatch(/paquetenv/i);
  });

  it("shows no brand unless a usable one is configured", () => {
    for (const value of [undefined, "", "   ", "x".repeat(81), " Seguimiento de envío "]) {
      expect(resolvePublicTrackingBrand(value)).toBeNull();
    }
    expect(resolvePublicTrackingBrand("  Marca validada  ")).toBe("Marca validada");
  });

  it("refuses to build with the unvalidated commercial name", () => {
    for (const value of [
      "Paquetenvia",
      "PAQUETENVIA",
      "paquetenvía",
      " Paquet Envia ",
      "Paquetenvia.com",
      "Seguimiento Paquetenvia",
    ]) {
      expect(() => assertNeutralTrackingBrand(value), value).toThrow(/GATE-001/);
    }
    for (const value of [undefined, "", publicTrackingHeading, "Marca validada"]) {
      expect(() => assertNeutralTrackingBrand(value)).not.toThrow();
    }
  });
});
