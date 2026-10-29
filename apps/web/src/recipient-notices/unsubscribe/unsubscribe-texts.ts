import { publicTrackingHeading } from "../../tracking/contracts/public-tracking-branding";

/**
 * NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (project owner, O9 "Aprobar estos textos (Recommended)"; AI-07
 * /avisos/baja/:token texts): the only words of the e-mail unsubscribe page. The heading is the neutral public heading
 * (GATE-001), the wait is the common SEC-004 notice ("1 segundo" at one) and nothing else is shown: never the e-mail,
 * the order or the organization.
 */
export const recipientUnsubscribeTexts = {
  heading: publicTrackingHeading,
  button: "Dejar de recibir correos",
  success: "Listo. Ya no recibirás correos de avisos en esta dirección.",
  invalidLink: "Este enlace no es válido o ya no está disponible.",
  failure: "No pudimos procesar tu solicitud. Intenta más tarde.",
} as const;
