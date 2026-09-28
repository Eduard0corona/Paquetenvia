import { buildStepUpPromptHref } from "../../auth/step-up";
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
  }
}
