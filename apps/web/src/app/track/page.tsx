import type { Metadata } from "next";
import { publicTrackingMetadata } from "@/tracking/contracts/public-tracking-branding";
import { PublicTrackingShell } from "@/tracking/components/public-tracking-shell";

export const dynamic = "force-dynamic";
export const revalidate = 0;
// GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10: neutral title, no driver PWA manifest.
export const metadata: Metadata = publicTrackingMetadata;

export default function PublicTrackingPage() {
  return <PublicTrackingShell />;
}
