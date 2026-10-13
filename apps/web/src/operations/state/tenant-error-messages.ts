import { buildStepUpPromptHref } from "../../auth/step-up";
import { rateLimitFallbackSeconds, rateLimitMessage } from "../../lib/rate-limit-wait";
import { OperationsApiError } from "../api/operations-api";
import { TenantApiError } from "../api/tenant-request";

export interface FailureView {
  readonly message: string;
  /** `/login?mfa=required&return_url=…` when the only missing requirement is MFA. */
  readonly stepUpHref: string | null;
}

/**
 * Spanish copy for a failed tenant operation. It never includes server text,
 * identifiers or anything the person typed.
 */
export function describeFailure(
  error: unknown,
  returnUrl: string,
  conflictMessages: Readonly<Record<string, string>> = {},
  defaultConflict = "El servidor rechazó la operación por un conflicto; actualiza y vuelve a intentar.",
): FailureView {
  if (!(error instanceof TenantApiError)) {
    return { message: "No fue posible completar la operación.", stepUpHref: null };
  }
  switch (error.category) {
    case "unauthorized":
      return { message: "Tu sesión no es válida; inicia sesión de nuevo.", stepUpHref: null };
    case "forbidden":
      return error.mfaRequired
        ? {
            message: "Esta acción requiere verificar tu identidad (MFA).",
            stepUpHref: buildStepUpPromptHref(returnUrl),
          }
        : {
            message: "Tu rol no permite esta acción en la organización activa.",
            stepUpHref: null,
          };
    case "not_found":
      return { message: "El recurso no existe o no está disponible.", stepUpHref: null };
    case "conflict":
      return {
        message:
          (error.code !== null ? conflictMessages[error.code] : undefined) ??
          defaultConflict,
        stepUpHref: null,
      };
    case "invalid":
      return {
        message:
          (error.code !== null ? conflictMessages[error.code] : undefined) ??
          "El servidor rechazó los datos enviados; revísalos.",
        stepUpHref: null,
      };
    case "network":
    case "unavailable":
      return {
        message:
          "Sin respuesta válida del servidor. Puedes reintentar: se reenvía la misma solicitud sin duplicarla.",
        stepUpHref: null,
      };
    case "rate_limited":
      // SEC-004 (owner answers Q6, Q12). The limited screens show it through RateLimitNotice, counting down, and set
      // no message of their own (isRateLimited); this static text is only for a 429 elsewhere.
      return { message: rateLimitMessage(error.retryAfterSeconds ?? rateLimitFallbackSeconds), stepUpHref: null };
  }
}

/**
 * SEC-004: a 429 of a limited operation. The API client already recorded its wait; the screen shows the common
 * message with the countdown (RateLimitNotice) and keeps its buttons disabled until it ends, so it sets no message of
 * its own and keeps what is on screen.
 */
export function isRateLimited(error: unknown): error is TenantApiError | OperationsApiError {
  return (error instanceof TenantApiError || error instanceof OperationsApiError) && error.category === "rate_limited";
}
