import type { Metadata } from "next";
import { publicTrackingHeading } from "../../tracking/contracts/public-tracking-branding";

/**
 * Head of /avisos/baja and its /avisos/baja/:token rewrite (AI-07): the neutral heading as the title (GATE-001), no
 * driver PWA manifest and never indexed.
 */
export const recipientUnsubscribeMetadata = {
  title: publicTrackingHeading,
  manifest: null,
  robots: { index: false, follow: false },
} as const satisfies Metadata;
