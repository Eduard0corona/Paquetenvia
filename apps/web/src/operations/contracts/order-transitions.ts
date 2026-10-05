import { orderStatuses, type OrderStatus } from "./operations-dashboard";
import { array, exactObject, fail, oneOf } from "./strict-json";

/**
 * UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: AI-05 OrderDetail.allowed_transitions and the
 * transitionOrder request. The server decides which transitions the caller may request now;
 * this module only reads that list, picks the ones the order detail offers and labels them in
 * es-MX. It never derives a transition from the status: there is no state machine here.
 */
export const transitionMetadataKeys = ["restricted_goods_acknowledged", "incident_id"] as const;
export type TransitionMetadataKey = (typeof transitionMetadataKeys)[number];

export interface AllowedTransition {
  readonly target_status: OrderStatus;
  readonly required_metadata: readonly TransitionMetadataKey[];
}

/** AI-05 OrderAllowedTransition[]; fails closed on anything outside the contract. */
export function parseAllowedTransitions(value: unknown): readonly AllowedTransition[] {
  const items = array(value, orderStatuses.length);
  const seen = new Set<string>();
  return items.map((item) => {
    const object = exactObject(item, ["target_status", "required_metadata"]);
    const target = oneOf(object.target_status, orderStatuses);
    if (seen.has(target)) fail();
    seen.add(target);
    const metadata = array(object.required_metadata, transitionMetadataKeys.length).map((key) =>
      oneOf(key, transitionMetadataKeys),
    );
    if (new Set(metadata).size !== metadata.length) fail();
    return { target_status: target, required_metadata: metadata };
  });
}

export interface NextStepAction {
  readonly target: OrderStatus;
  readonly label: string;
  readonly danger: boolean;
  /** What the confirmation says will happen. */
  readonly effect: string;
  /** The person must confirm the shipment carries no prohibited goods (restricted_goods_acknowledged). */
  readonly needsRestrictedGoodsAcknowledgement: boolean;
}

/**
 * The transitions "Siguiente paso" offers, in this order. ASSIGNED has its own picker,
 * FAILED_ATTEMPT needs an incident recorded from Incidencias, and the driver steps, returns
 * and claims are not offered from this screen; they never get a button here.
 */
const offered: readonly Omit<NextStepAction, "needsRestrictedGoodsAcknowledgement">[] = [
  {
    target: "CONFIRMED",
    label: "Confirmar orden",
    danger: false,
    effect: "La orden quedará confirmada y lista para prepararse.",
  },
  {
    target: "READY_FOR_PICKUP",
    label: "Liberar para recolección",
    danger: false,
    effect: "La orden quedará lista para asignar un repartidor y recolectarla.",
  },
  {
    target: "CLOSED",
    label: "Cerrar orden",
    danger: false,
    effect: "La orden se cerrará; todavía podrá abrirse una reclamación dentro del plazo.",
  },
  {
    target: "CANCELLED",
    label: "Cancelar orden",
    danger: true,
    effect: "La orden se cancelará y ya no podrá continuar.",
  },
];

/** Metadata this screen can collect; a transition needing any other is not offered. */
const collectableMetadata: ReadonlySet<TransitionMetadataKey> = new Set(["restricted_goods_acknowledged"]);

/** The buttons to show: only server-allowed transitions this screen supports. */
export function nextStepActions(allowed: readonly AllowedTransition[]): readonly NextStepAction[] {
  const byTarget = new Map(allowed.map((item) => [item.target_status, item]));
  return offered.flatMap((action) => {
    const transition = byTarget.get(action.target);
    if (transition === undefined) return [];
    if (transition.required_metadata.some((key) => !collectableMetadata.has(key))) return [];
    return [
      {
        ...action,
        needsRestrictedGoodsAcknowledgement: transition.required_metadata.includes("restricted_goods_acknowledged"),
      },
    ];
  });
}

export const maximumReasonLength = 500;

export interface TransitionRequestBody {
  readonly target_status: OrderStatus;
  readonly reason: string;
  readonly expected_version: number;
  readonly metadata?: { readonly restricted_goods_acknowledged: true };
}

/** AI-05 TransitionRequest; throws when the reason, version or acknowledgement is not acceptable. */
export function transitionRequestBody(
  action: NextStepAction,
  reason: string,
  expectedVersion: number,
  restrictedGoodsAcknowledged: boolean,
): TransitionRequestBody {
  const trimmed = reason.trim();
  if (trimmed.length === 0 || trimmed.length > maximumReasonLength) fail();
  if (!Number.isSafeInteger(expectedVersion) || expectedVersion < 1) fail();
  if (action.needsRestrictedGoodsAcknowledgement && !restrictedGoodsAcknowledged) fail();
  return action.needsRestrictedGoodsAcknowledgement
    ? {
        target_status: action.target,
        reason: trimmed,
        expected_version: expectedVersion,
        metadata: { restricted_goods_acknowledged: true },
      }
    : { target_status: action.target, reason: trimmed, expected_version: expectedVersion };
}

export function transitionConfirmation(
  publicId: string,
  action: NextStepAction,
): { readonly title: string; readonly description: string; readonly confirmLabel: string } {
  return {
    title: `¿${action.label}?`,
    description: `Orden ${publicId}. ${action.effect}`,
    confirmLabel: action.label,
  };
}

/** A 409 of transitionOrder without a rule code, or with one this screen does not know. */
export const transitionConflictMessage =
  "No se pudo cambiar el estado; la orden cambió o falta un requisito. Actualiza e intenta de nuevo.";

/**
 * ORD-002-GUARD-CODES-2026-10-05: the AI-05 TransitionConflictProblem.code values and what each
 * one tells the person, in es-MX. The server sends a rule code only to whoever may change the
 * order; the message names what is missing and never repeats server text or identifiers.
 */
export const transitionRejectionMessages = Object.freeze({
  OFFLINE_OPERATION_EXPIRED: "El cambio es más antiguo que la ventana permitida; no puede registrarse.",
  VERSION_CONFLICT: "La orden cambió mientras la revisabas. Revisa la información actualizada e intenta de nuevo.",
  TRANSITION_NOT_ALLOWED: "Ese cambio ya no es posible desde el estado actual de la orden.",
  ORDER_TERMINAL: "La orden ya terminó; no admite más cambios.",
  ORDER_FINALIZED: "La orden ya se cerró de forma definitiva; no admite más cambios.",
  CLAIM_WINDOW_CLOSED: "El plazo para abrir una reclamación ya terminó.",
  QUOTE_NOT_VALID: "La cotización de la orden ya no es válida.",
  PAYER_ACCEPTANCE_REQUIRED: "Falta la aceptación de quien paga.",
  RESTRICTED_GOODS_ACK_REQUIRED: "Falta confirmar que el envío no contiene artículos prohibidos.",
  VALID_ASSIGNMENT_REQUIRED: "Falta un repartidor asignado y habilitado para esta orden.",
  DRIVER_CAPACITY_EXCEEDED: "El vehículo del repartidor no tiene capacidad para los paquetes.",
  ASSIGNMENT_COST_REQUIRED: "Falta el costo de la asignación.",
  PICKUP_PROOF_REQUIRED: "Falta la foto de recolección.",
  DELIVERY_PROOF_REQUIRED: "Falta la foto o el código de entrega.",
  COD_NOT_RECORDED: "Falta registrar el cobro contra entrega.",
  UNRESOLVED_INCIDENT: "Hay una incidencia sin resolver.",
  COD_NOT_RECONCILED: "El cobro contra entrega aún no está conciliado.",
  FINANCIAL_RECONCILIATION_INCOMPLETE: "La conciliación financiera de la orden no está completa.",
  CLAIM_WINDOW_NOT_SET: "La orden aún no tiene plazo de reclamación; no puede cerrarse.",
  REASON_REQUIRED: "Escribe el motivo del cambio.",
  CUSTODY_ALREADY_ACQUIRED: "El paquete ya fue recolectado; la orden no puede cancelarse.",
  INCIDENT_REQUIRED: "Falta registrar la incidencia de este intento.",
  CUSTODY_NOT_ACQUIRED: "El paquete aún no ha sido recolectado.",
  NEXT_ACTION_MISMATCH: "La incidencia registrada indica otro siguiente paso.",
} as const);

export type TransitionRejectionCode = keyof typeof transitionRejectionMessages;

export const transitionRejectionCodes = Object.freeze(
  Object.keys(transitionRejectionMessages),
) as readonly TransitionRejectionCode[];

/** The message for a transitionOrder 409; the generic one for an absent, malformed or unknown code. */
export function transitionRejectionMessage(code: string | null | undefined): string {
  return typeof code === "string" && Object.hasOwn(transitionRejectionMessages, code)
    ? transitionRejectionMessages[code as TransitionRejectionCode]
    : transitionConflictMessage;
}

/** AI-04 public identifier: ORD_ and 22 Base64URL characters, case sensitive. */
const publicIdPattern = /^ORD_[A-Za-z0-9_-]{22}$/;

/** The typed tracking number without surrounding spaces, or null when it cannot be one. */
export function normalizePublicIdInput(value: string): string | null {
  const trimmed = value.trim();
  return publicIdPattern.test(trimmed) ? trimmed : null;
}

/** Shown for a malformed, missing or other-organization tracking number alike. */
export const orderNotFoundMessage = "No encontramos esa guía";
