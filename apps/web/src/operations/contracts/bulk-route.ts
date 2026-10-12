import type { BulkOrder } from "./bulk-assignment";
import type { ManualRoute } from "./manual-route";

/**
 * UI-PHASE3-INBOX-BULK-ADD-TO-ROUTE-2026-10-11 ("Al terminar el lote (Recommended)"): when
 * "Asignar seleccionadas…" ends with assigned orders, "Agregar estas órdenes a una ruta…" adds
 * those same orders to one DRAFT route of that same driver. Like the bulk assignment it is a
 * sequence of the existing single-order operation (addRouteStop, one order after another), never a
 * new atomic flow; the backend and RLS decide every stop again. Everything here is pure: what a
 * finished batch offers, the route names people read (no identifier, no amount) and the texts.
 */

/** The driver of the batch: the id for the API, the DRV- reference for people. */
export interface BulkRouteDriver {
  readonly driverId: string;
  readonly driverReference: string;
}

/** What "Agregar estas órdenes a una ruta…" takes from a finished batch. */
export interface BulkRouteOffer {
  readonly driver: BulkRouteDriver;
  /** Only the orders the batch assigned, in batch order. */
  readonly orders: readonly BulkOrder[];
}

/** The parts of a bulk assignment the offer reads (BulkAssignmentState has them). */
export interface FinishedBatch {
  readonly phase: string;
  readonly orders: readonly BulkOrder[];
  readonly run: { readonly driver: BulkRouteDriver } | null;
  readonly results: Readonly<Record<string, { readonly kind: string } | undefined>>;
}

/**
 * The offer of a batch: only once its run ended, only with the orders it assigned and only with
 * its driver; null while it is loading, ready or running, or when nothing was assigned.
 */
export function bulkRouteOffer(batch: FinishedBatch): BulkRouteOffer | null {
  if (batch.phase !== "done" || batch.run === null) return null;
  const orders = batch.orders.filter((order) => batch.results[order.orderId]?.kind === "assigned");
  if (orders.length === 0) return null;
  const { driverId, driverReference } = batch.run.driver;
  return { driver: { driverId, driverReference }, orders };
}

/** The note next to "Agregar estas órdenes a una ruta…" once a batch assigned orders. */
export function bulkRouteOfferText(offer: BulkRouteOffer): string {
  const count = offer.orders.length;
  const reference = offer.driver.driverReference;
  return count === 1
    ? `La orden asignada a ${reference} puede ir a una de sus rutas en borrador; antes verás una vista previa.`
    : `Las ${count} órdenes asignadas a ${reference} pueden ir a una de sus rutas en borrador; antes verás una vista previa.`;
}

const months = [
  "enero",
  "febrero",
  "marzo",
  "abril",
  "mayo",
  "junio",
  "julio",
  "agosto",
  "septiembre",
  "octubre",
  "noviembre",
  "diciembre",
] as const;

/** A route's calendar date ("2026-10-12") in words ("12 de octubre de 2026"); null when it is not a real date. */
export function routeDateText(value: string): string | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (match === null) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const date = new Date(Date.UTC(year, month - 1, day));
  if (year < 1000 || date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day)
    return null;
  return `${day} de ${months[month - 1]} de ${year}`;
}

/**
 * A route the inbox may offer for the driver: DRAFT, of that driver and with a real date or none.
 * Anything else in the answer to `status=DRAFT&driver_id=` breaks the request and fails closed.
 */
export function isDraftRouteOf(route: ManualRoute, driverId: string): boolean {
  return (
    route.status === "DRAFT" &&
    route.driver_id === driverId &&
    (route.scheduled_for === null || routeDateText(route.scheduled_for) !== null)
  );
}

/** "sin paradas", "1 parada", "3 paradas". */
export function stopCountText(count: number): string {
  if (count === 0) return "sin paradas";
  return count === 1 ? "1 parada" : `${count} paradas`;
}

/** A DRAFT route as people read it. */
export interface DraftRouteChoice {
  readonly route: ManualRoute;
  /** "Ruta del 12 de octubre de 2026", "Ruta sin fecha" or, when routes share that, "Ruta sin fecha (la más reciente)". */
  readonly name: string;
  /** The name and the current stops: "Ruta del 12 de octubre de 2026 · 3 paradas". */
  readonly label: string;
}

const ordinals = ["", "segunda", "tercera", "cuarta", "quinta", "sexta", "séptima", "octava", "novena", "décima"];

function baseName(route: ManualRoute): string {
  const date = route.scheduled_for === null ? null : routeDateText(route.scheduled_for);
  return date === null ? "Ruta sin fecha" : `Ruta del ${date}`;
}

function recency(position: number): string {
  if (position === 0) return "la más reciente";
  return position < ordinals.length ? `la ${ordinals[position]} más reciente` : `la ${position + 1}.ª más reciente`;
}

/**
 * The driver's DRAFT routes in the server order (newest first) with names that tell them apart: the
 * scheduled date, and the recency when several routes share it. Never an identifier or an amount.
 */
export function draftRouteChoices(routes: readonly ManualRoute[]): readonly DraftRouteChoice[] {
  const bases = routes.map(baseName);
  const seen = new Map<string, number>();
  return routes.map((route, index) => {
    const base = bases[index] ?? baseName(route);
    const shared = bases.filter((name) => name === base).length > 1;
    const position = seen.get(base) ?? 0;
    seen.set(base, position + 1);
    const name = shared ? `${base} (${recency(position)})` : base;
    return { route, name, label: `${name} · ${stopCountText(route.stop_count)}` };
  });
}

/** "1 orden", "3 órdenes". */
export function ordersText(count: number): string {
  return count === 1 ? "1 orden" : `${count} órdenes`;
}

function currentStopsText(count: number): string {
  if (count === 0) return "que hoy no tiene paradas";
  return `que hoy tiene ${stopCountText(count)}`;
}

/** The preview caption after "Vista previa:": the orders, the driver and, once chosen, the route they go to. */
export function bulkRoutePreviewText(
  driverReference: string,
  orderCount: number,
  choice: DraftRouteChoice | null,
): string {
  const target = choice === null ? "" : ` a «${choice.name}»`;
  return (
    `${ordersText(orderCount)} de ${driverReference}${target}. ` +
    "Se agregan al final de la ruta, una por una y en el orden de esta tabla."
  );
}

/** Shown when the route a run used is no longer among the driver's DRAFT routes. */
export const bulkRouteGoneMessage =
  "La ruta elegida ya no está en borrador o ya no es de este repartidor; agrega las órdenes pendientes desde Rutas.";

/** The ConfirmDialog of a run: names the route, the driver and the number of orders. */
export function bulkRouteConfirmation(
  choice: DraftRouteChoice,
  driverReference: string,
  orderCount: number,
): { readonly title: string; readonly description: string; readonly confirmLabel: string } {
  const orders = ordersText(orderCount);
  return {
    title: `¿Agregar ${orders} a la ruta?`,
    description:
      `Se agregará${orderCount === 1 ? "" : "n"} ${orders} de ${driverReference} al final de «${choice.name}», ` +
      `${currentStopsText(choice.route.stop_count)}, en el orden de la vista previa. ` +
      "Se envían una por una y cada fila mostrará su resultado.",
    confirmLabel: `Agregar ${orders}`,
  };
}

/** es-MX text of each addRouteStop 409 code (AI-05 RoutingConflict); an unknown code gets the default. */
export const routeStopConflictMessages: Readonly<Record<string, string>> = {
  DRIVER_INELIGIBLE:
    "La orden no entra en esta ruta: es de otra ciudad o área, o el repartidor ya no puede llevarla.",
  VERSION_CONFLICT: "No se agregó: la ruta cambió mientras se agregaban las órdenes.",
  CONFLICT:
    "La orden ya no se puede agregar a esta ruta: ya está en una ruta, cambió su asignación o la ruta ya no está en borrador.",
  INVALID_REQUEST: "El servidor no aceptó la solicitud. Actualiza la bandeja e intenta de nuevo.",
};

export const routeStopDefaultConflictMessage = "La orden no se pudo agregar a esta ruta.";
export const routeStopNotFoundMessage = "La ruta o la orden ya no están disponibles.";

/** What a run says at the top of its results. */
export function bulkRouteRunSummary(added: number, total: number): string {
  if (added === total)
    return total === 1 ? "Se agregó la orden a la ruta." : `Se agregaron las ${total} órdenes a la ruta.`;
  if (added === 0) return "No se agregó ninguna orden a la ruta. Revisa el motivo en cada fila.";
  return `${added === 1 ? "Se agregó" : "Se agregaron"} ${added} de ${total} órdenes a la ruta. Revisa el motivo de las demás en su fila.`;
}
