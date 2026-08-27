import { notFound } from "next/navigation";
import { DevPortal } from "./portal";
import { isDevPortalEnabled } from "../../dev/dev-portal-policy";

export const dynamic = "force-dynamic";

export default function DevelopmentPortalPage() {
  if (
    !isDevPortalEnabled(
      process.env.NODE_ENV,
      process.env.PAQUETERIA_DEV_PORTAL_ENABLED,
    )
  ) {
    notFound();
  }

  return <DevPortal />;
}
