import type { Metadata } from "next";
import { recipientUnsubscribeMetadata } from "@/recipient-notices/unsubscribe/unsubscribe-metadata";
import { RecipientUnsubscribeShell } from "@/recipient-notices/unsubscribe/recipient-unsubscribe-shell";

export const dynamic = "force-dynamic";
export const revalidate = 0;
// AI-07 /avisos/baja/:token: the neutral heading, no driver PWA manifest, not indexed.
export const metadata: Metadata = recipientUnsubscribeMetadata;

export default function RecipientUnsubscribePage() {
  return <RecipientUnsubscribeShell />;
}
