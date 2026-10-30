/**
 * NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007; AI-07 /ops/orders/:id recipient_notices): the one helper of
 * the panel "Avisos al destinatario". Every es-MX text the panel shows lives here, exactly as the owner approved it -
 * T1 "Así, como I4 (Recommended)" (the "Detener avisos" dialog), T2 "Sin hora, con nota (Recommended)", T4 "Panel con
 * una línea (Recommended)" and T5 "Aprobar estos textos (Recommended)" - and recipient-notices.test.ts pins each one.
 * The panel never shows the phone, the e-mail, the link, a template parameter, an identifier or an internal code: a
 * code only chooses one of these sentences.
 */

/** AI-05 RecipientNotice.notice. */
export const recipientNoticeKinds = ["TRACKING_LINK", "IN_TRANSIT", "DELIVERY_FAILED", "DELIVERED"] as const;
export type RecipientNoticeKind = (typeof recipientNoticeKinds)[number];

/** AI-05 RecipientNotice.channel, WhatsApp first (the order of the list). */
export const recipientNoticeChannels = ["WHATSAPP", "EMAIL"] as const;
export type RecipientNoticeChannel = (typeof recipientNoticeChannels)[number];

/** AI-05 RecipientNotice.status. */
export const recipientNoticeStatuses = ["PENDING", "SENT", "DELIVERED", "FAILED", "CANCELLED"] as const;
export type RecipientNoticeStatus = (typeof recipientNoticeStatuses)[number];

/**
 * AI-05 RecipientNotice.last_code (T6 choice 4): the closed MessagingResultCodes, the lane's own codes and
 * MAX_ATTEMPTS_EXHAUSTED. Anything else makes the answer invalid.
 */
export const recipientNoticeLastCodes = [
  "MESSAGING_ACCEPTED",
  "MESSAGING_SYNTHETIC_ACCEPTED",
  "MESSAGING_SYNTHETIC_TRANSIENT",
  "MESSAGING_SYNTHETIC_PERMANENT",
  "MESSAGING_SYNTHETIC_AMBIGUOUS_TIMEOUT",
  "MESSAGING_CHANNEL_DISABLED",
  "MESSAGING_TEMPLATE_NOT_CONFIGURED",
  "MESSAGING_TEMPLATE_PARAMETERS_INVALID",
  "MESSAGING_RECIPIENT_INVALID",
  "MESSAGING_PROVIDER_TIMEOUT",
  "MESSAGING_PROVIDER_UNREACHABLE",
  "MESSAGING_PROVIDER_RATE_LIMITED",
  "MESSAGING_PROVIDER_UNAVAILABLE",
  "MESSAGING_PROVIDER_AUTH_FAILED",
  "MESSAGING_PROVIDER_REJECTED",
  "MESSAGING_PROVIDER_RESPONSE_INVALID",
  "MESSAGING_RECIPIENT_UNDELIVERABLE",
  "MESSAGING_REENGAGEMENT_REQUIRED",
  "MESSAGING_PROVIDER_TEMPLATE_REJECTED",
  "MESSAGING_POLICY_BLOCKED",
  "MESSAGING_PROVIDER_CIRCUIT_OPEN",
  "MESSAGING_PROVIDER_CONCURRENCY_LIMITED",
  "AMBIGUOUS_TIMEOUT",
  "TRACKING_LINK_UNAVAILABLE",
  "NOTICE_SUPERSEDED",
  "NOTICE_STALE",
  "NOTICE_LIMIT_REACHED",
  "RECIPIENT_OPTED_OUT",
  "MAX_ATTEMPTS_EXHAUSTED",
] as const;
export type RecipientNoticeLastCode = (typeof recipientNoticeLastCodes)[number];

/** One notice on one channel, as listOrderRecipientNotices answers it. */
export interface RecipientNotice {
  readonly notificationId: string;
  readonly notice: RecipientNoticeKind;
  readonly channel: RecipientNoticeChannel;
  readonly status: RecipientNoticeStatus;
  readonly attempts: number;
  readonly lastCode: RecipientNoticeLastCode | null;
  readonly sentAt: string | null;
  readonly deliveredAt: string | null;
  readonly updatedAt: string;
}

export interface RecipientNoticeList {
  readonly items: readonly RecipientNotice[];
  readonly whatsappConsentAttested: boolean;
  readonly emailOnFile: boolean;
}

export interface RecipientNoticeStopResult {
  readonly orderId: string;
  readonly channels: readonly RecipientNoticeChannel[];
  readonly recordedAt: string;
}

/** The fixed texts of the panel (T2, T4, T5) and of its dialog (T1). */
export const recipientNoticeTexts = Object.freeze({
  title: "Avisos al destinatario",
  /** T4: removed by the change that closes GATE-004 and GATE-007. */
  notActiveYet: "Los avisos al destinatario todavía no están activos.",
  /** T2: there is no scheduled time; this line says when a pending notice leaves. */
  quietHours: "Los avisos solo salen de 8:00 a 21:00, hora de Mazatlán",
  empty: "Esta orden todavía no tiene avisos.",
  whatsappAllowed: "WhatsApp: permitido (se marcó la casilla)",
  whatsappNotAllowed: "WhatsApp: no permitido (no se marcó la casilla)",
  emailOnFile: "Correo del destinatario: registrado",
  emailNotOnFile: "Correo del destinatario: no registrado",
  retrying: "Reintentando",
  stopButton: "Detener avisos",
  stoppedBoth: "Listo. Se detuvieron los avisos por WhatsApp y correo.",
  stoppedWhatsApp: "Listo. Se detuvieron los avisos por WhatsApp.",
  /**
   * Not in the T5 preview, which covers only "WhatsApp y correo" and "WhatsApp": the same sentence for an order whose
   * phone cannot be used but whose e-mail can (T6 choice 8).
   */
  stoppedEmail: "Listo. Se detuvieron los avisos por correo.",
  stoppedPending: "Los avisos pendientes ya no se enviarán.",
  stopFailure: "No pudimos detener los avisos. Intenta de nuevo.",
  readFailure: "No fue posible leer los avisos.",
  confirmTitle: "¿Detener avisos?",
  confirmEffect:
    "El destinatario dejará de recibir avisos de todas las empresas. Durante el piloto no se puede deshacer.",
  confirmLabel: "Detener avisos",
  cancelLabel: "Cancelar",
});

/** AI-07: the notice by its owner name. */
export const recipientNoticeLabels: Readonly<Record<RecipientNoticeKind, string>> = Object.freeze({
  TRACKING_LINK: "Enlace de seguimiento",
  IN_TRANSIT: "Va en camino",
  DELIVERY_FAILED: "No se pudo entregar",
  DELIVERED: "Entregado",
});

/** T5: the row's channel. */
export const recipientNoticeChannelLabels: Readonly<Record<RecipientNoticeChannel, string>> = Object.freeze({
  WHATSAPP: "WhatsApp",
  EMAIL: "Correo",
});

/** T5: "Pendiente · Enviado · Recibido · No se pudo enviar · No se envió". */
export const recipientNoticeStatusLabels: Readonly<Record<RecipientNoticeStatus, string>> = Object.freeze({
  PENDING: "Pendiente",
  SENT: "Enviado",
  DELIVERED: "Recibido",
  FAILED: "No se pudo enviar",
  CANCELLED: "No se envió",
});

/** T5 "Por qué no salió": the sentences that name one code (two of them per channel). */
export const recipientNoticeReasons = Object.freeze({
  RECIPIENT_OPTED_OUT: "El destinatario pidió no recibir avisos.",
  NOTICE_SUPERSEDED: "Ya no aplicaba cuando tocaba enviarlo.",
  NOTICE_STALE: "Pasó demasiado tiempo para enviarlo.",
  NOTICE_LIMIT_REACHED: "Se alcanzó el límite de avisos.",
  MESSAGING_CHANNEL_DISABLED: "Los avisos por este medio están apagados.",
  MESSAGING_RECIPIENT_INVALID: Object.freeze({
    WHATSAPP: "No se pudo usar el teléfono.",
    EMAIL: "No se pudo usar el correo.",
  }),
  MESSAGING_RECIPIENT_UNDELIVERABLE: Object.freeze({
    WHATSAPP: "No llegó: el número no recibe WhatsApp.",
    EMAIL: "No llegó: el correo rebotó.",
  }),
  MESSAGING_POLICY_BLOCKED: "El servicio de mensajes lo bloqueó.",
  TRACKING_LINK_UNAVAILABLE: "No había enlace de seguimiento vigente.",
  AMBIGUOUS_TIMEOUT: "No se sabe si llegó; no se reintenta para no duplicarlo.",
  MAX_ATTEMPTS_EXHAUSTED: "Falló después de varios intentos.",
});

/** T5: "(cualquier otra falla)". */
export const recipientNoticeOtherFailure = "Falla del servicio de mensajes.";

/** T5: «{aviso} · {WhatsApp|Correo} — {estado}». */
export function recipientNoticeRowTitle(notice: RecipientNotice): string {
  return `${recipientNoticeLabels[notice.notice]} · ${recipientNoticeChannelLabels[notice.channel]} — ${recipientNoticeStatusLabels[notice.status]}`;
}

/** T5: «Intentos: N» only when there was more than one. */
export function recipientNoticeAttempts(notice: RecipientNotice): string | null {
  return notice.attempts > 1 ? `Intentos: ${notice.attempts}` : null;
}

/**
 * T5: the line under a notice. Why it did not go out (FAILED or CANCELLED), "Reintentando" for a PENDING notice that
 * already met a temporary failure, nothing otherwise. A code never shows itself.
 */
export function recipientNoticeReason(notice: RecipientNotice): string | null {
  if (notice.status === "PENDING") return notice.lastCode === null ? null : recipientNoticeTexts.retrying;
  if (notice.status !== "FAILED" && notice.status !== "CANCELLED") return null;
  const code = notice.lastCode;
  if (code === null || !(code in recipientNoticeReasons)) return recipientNoticeOtherFailure;
  const reason = recipientNoticeReasons[code as keyof typeof recipientNoticeReasons];
  return typeof reason === "string" ? reason : reason[notice.channel];
}

/** T5: the two lines about the order. */
export function recipientNoticeOrderFacts(list: RecipientNoticeList): readonly [string, string] {
  return [
    list.whatsappConsentAttested ? recipientNoticeTexts.whatsappAllowed : recipientNoticeTexts.whatsappNotAllowed,
    list.emailOnFile ? recipientNoticeTexts.emailOnFile : recipientNoticeTexts.emailNotOnFile,
  ];
}

/** T5 "Al detener", from the channels the server names. */
export function recipientNoticeStoppedLines(channels: readonly RecipientNoticeChannel[]): readonly [string, string] {
  const whatsapp = channels.includes("WHATSAPP");
  const email = channels.includes("EMAIL");
  const first =
    whatsapp && email
      ? recipientNoticeTexts.stoppedBoth
      : whatsapp
        ? recipientNoticeTexts.stoppedWhatsApp
        : recipientNoticeTexts.stoppedEmail;
  return [first, recipientNoticeTexts.stoppedPending];
}

/**
 * T1 "Así, como I4 (Recommended)": title «¿Detener avisos?», text «Orden {guía}. El destinatario dejará de recibir
 * avisos de todas las empresas. Durante el piloto no se puede deshacer.», buttons «Cancelar» and «Detener avisos»
 * (ConfirmDialog starts on «Cancelar»).
 */
export function stopRecipientNoticesConfirmation(publicId: string): {
  readonly title: string;
  readonly description: string;
  readonly confirmLabel: string;
  readonly cancelLabel: string;
} {
  return {
    title: recipientNoticeTexts.confirmTitle,
    description: `Orden ${publicId}. ${recipientNoticeTexts.confirmEffect}`,
    confirmLabel: recipientNoticeTexts.confirmLabel,
    cancelLabel: recipientNoticeTexts.cancelLabel,
  };
}
