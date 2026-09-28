import { resolveAcceptanceVersions } from "@/operations/contracts/acceptance-versions";
import { CreateOrderShell } from "@/operations/components/create-order-shell";

// The accepted terms/privacy versions are owner-set runtime settings, read per request.
export const dynamic = "force-dynamic";

export default function NewOrderPage() {
  const acceptanceVersions = resolveAcceptanceVersions(
    process.env.PAQUETERIA_TERMS_VERSION,
    process.env.PAQUETERIA_PRIVACY_VERSION,
  );
  return <CreateOrderShell acceptanceVersions={acceptanceVersions} />;
}
