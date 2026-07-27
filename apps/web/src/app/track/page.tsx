import { PublicTrackingShell } from "@/tracking/components/public-tracking-shell";

export const dynamic = "force-dynamic";
export const revalidate = 0;

export default function PublicTrackingPage() {
  return <PublicTrackingShell />;
}
