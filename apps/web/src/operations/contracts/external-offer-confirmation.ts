import { formatMxnCentsWithCurrency } from "./money";
import { formatMazatlanTime } from "./operations-formatters";

export type ExternalOfferVehicleType = "MOTORCYCLE" | "CAR" | "VAN" | "BICYCLE" | "WALKER";

export const externalOfferVehicleLabels: Readonly<Record<ExternalOfferVehicleType, string>> = {
  MOTORCYCLE: "Motocicleta",
  CAR: "Automóvil",
  VAN: "Van",
  BICYCLE: "Bicicleta",
  WALKER: "A pie",
};

/** Text of the confirmation shown before an external offer is published; commission in integer cents. */
export function externalOfferConfirmation(
  publicId: string,
  commissionCents: number,
  expiresAt: Date,
  vehicle: ExternalOfferVehicleType,
): { readonly title: string; readonly description: string; readonly confirmLabel: string } {
  return {
    title: "¿Publicar oferta externa?",
    description:
      `Se ofrecerá la orden ${publicId} a repartidores externos con una comisión de ` +
      `${formatMxnCentsWithCurrency(commissionCents)} (${externalOfferVehicleLabels[vehicle].toLowerCase()}), ` +
      `vigente hasta ${formatMazatlanTime(expiresAt)} (hora de Mazatlán).`,
    confirmLabel: "Publicar oferta",
  };
}
