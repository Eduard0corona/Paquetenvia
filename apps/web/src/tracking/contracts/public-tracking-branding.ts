import type { Metadata } from "next";

/**
 * GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10 (AI-07 public_tracking.branding): the
 * commercial name is not validated before IMPI (GATE-001), so until GATE-001
 * closes every public tracking page carries this descriptive heading and no
 * brand. Internal screens, login and the driver PWA keep the platform name.
 */
export const publicTrackingHeading = "Seguimiento de envío";

/**
 * Head of every /track page, the /track/:token rewrite included: the neutral
 * title and description (also what a link preview of the tracking link shows).
 * The root layout links the driver PWA manifest, whose name is the platform
 * name; the public page offers no app to install, so it links none.
 */
export const publicTrackingMetadata = {
  title: publicTrackingHeading,
  description: "Consulta el estado de tu envío.",
  manifest: null,
} as const satisfies Metadata;

/**
 * `NEXT_PUBLIC_TRACKING_BRAND_NAME`: an optional brand shown above the
 * heading. Unset, blank, longer than 80 characters or equal to the heading
 * means no brand, which is the default while GATE-001 is open; next.config.ts
 * refuses to build with the unvalidated commercial name.
 */
export function resolvePublicTrackingBrand(
  value: string | undefined,
): string | null {
  const brand = value?.trim();
  return brand && brand.length <= 80 && brand !== publicTrackingHeading
    ? brand
    : null;
}
