import { instantToMazatlanWallTime } from "../../lib/mazatlan-time";
import type { BulkOrder } from "./bulk-assignment";
import { routeDateText, routeName, type ManualRoute } from "./manual-route";

export { routeDateText };

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
  /**
   * The name, "hoy" for a route of today (Mazatlán) and the current stops: "Ruta del 12 de octubre de 2026 (hoy) ·
   * 3 paradas", "Ruta del 12 de octubre de 2026 (hoy, la más reciente) · sin paradas".
   */
  readonly label: string;
  /** Scheduled for today in America/Mazatlan (UI-PHASE3-ROUTES-DAILY-2026-10-12). */
  readonly today: boolean;
}

const ordinals = ["", "segunda", "tercera", "cuarta", "quinta", "sexta", "séptima", "octava", "novena", "décima"];

function recency(position: number): string {
  if (position === 0) return "la más reciente";
  return position < ordinals.length ? `la ${ordinals[position]} más reciente` : `la ${position + 1}.ª más reciente`;
}

/**
 * UI-PHASE3-ROUTES-DAILY-2026-10-12 ("Ruta por día (Recommended)"): a route is today's when its scheduled date is
 * `today`, the calendar date in America/Mazatlan (`todayInMazatlan`). A route without a date is never today's.
 */
export function isTodaysRoute(route: Pick<ManualRoute, "scheduled_for">, today: string): boolean {
  return route.scheduled_for !== null && route.scheduled_for === today;
}

/**
 * The driver's DRAFT routes with names that tell them apart: the scheduled date, and the recency when several
 * routes share it. Never an identifier or an amount. With `today` (UI-PHASE3-ROUTES-DAILY-2026-10-12) today's routes
 * come first, marked "hoy", then the rest in the server order (newest first); a date belongs wholly to one part, so
 * the recency of each name is the server's.
 */
export function draftRouteChoices(
  routes: readonly ManualRoute[],
  today: string | null = null,
): readonly DraftRouteChoice[] {
  const isToday = (route: ManualRoute) => today !== null && isTodaysRoute(route, today);
  const ordered = [...routes.filter(isToday), ...routes.filter((route) => !isToday(route))];
  const bases = ordered.map(routeName);
  const seen = new Map<string, number>();
  return ordered.map((route, index) => {
    const base = bases[index] ?? routeName(route);
    const shared = bases.filter((name) => name === base).length > 1;
    const position = seen.get(base) ?? 0;
    seen.set(base, position + 1);
    const name = shared ? `${base} (${recency(position)})` : base;
    const marks = [...(isToday(route) ? ["hoy"] : []), ...(shared ? [recency(position)] : [])];
    const label = `${marks.length === 0 ? base : `${base} (${marks.join(", ")})`} · ${stopCountText(route.stop_count)}`;
    return { route, name, label, today: isToday(route) };
  });
}

/** The hint under the list of DRAFT routes: their order. */
export function draftRouteOrderText(choices: readonly DraftRouteChoice[]): string {
  return choices.some((choice) => choice.today)
    ? "Primero las de hoy (hora de Mazatlán); las demás, de la más reciente a la más antigua."
    : "De la más reciente a la más antigua.";
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

/*
 * UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11 ("Sí, en el paso (Recommended)": "Crea la ruta en borrador para ese
 * repartidor, con la ciudad de las órdenes y la fecha de hoy (hora de Mazatlán), y sigue a la vista previa."). Only
 * when every order of the batch is in one city, "Crear ruta nueva" creates that route (createRoute, no service area)
 * and the step goes on to its preview.
 *
 * UI-PHASE3-ROUTES-DAILY-2026-10-12 ("Ruta por día (Recommended)"): AI-05 has no operation that takes a route out of
 * DRAFT, so routes stay DRAFT. "Crear ruta nueva" is offered when the driver has no DRAFT route dated today in
 * Mazatlán (a route without a date is not today's), next to the driver's older DRAFT routes when there are some.
 */

/** Today's calendar date in America/Mazatlan (`YYYY-MM-DD`): never the device's date nor the UTC date. */
export function todayInMazatlan(now: Date): string {
  const wall = instantToMazatlanWallTime(now);
  if (wall === "") throw new RangeError("A valid instant is required.");
  return wall.slice(0, 10);
}

/** What "Crear ruta nueva" creates, said before it is pressed: the driver, the orders' city and today's date. */
export function createRouteOfferText(driverReference: string, cityName: string | null): string {
  const city = cityName === null ? "la ciudad de las órdenes" : `${cityName}, la ciudad de las órdenes`;
  return (
    `«Crear ruta nueva» crea una ruta en borrador para ${driverReference} en ${city}, con la fecha de hoy (hora de ` +
    "Mazatlán), y sigue a la vista previa. Ninguna orden se agrega hasta que confirmes."
  );
}

/** Orders of the batch in different cities: no route is created in the step. */
export const bulkRouteMixedCitiesMessage =
  "Las órdenes de este lote están en ciudades distintas y una ruta cubre una sola ciudad, así que aquí no se crea " +
  "ninguna. Las rutas se crean en Rutas.";

/** Shown once the route exists: the step is already on its preview. */
export function createdRouteText(route: ManualRoute, driverReference: string): string {
  const [choice] = draftRouteChoices([route]);
  const name = choice?.name ?? "La ruta";
  return `Se creó «${name}» en borrador para ${driverReference}. Revisa la vista previa y confirma para agregar las órdenes.`;
}

/** es-MX text of each createRoute 409 code that refuses the route for sure (AI-05 RoutingConflict); never the code. */
export const createRouteConflictMessages: Readonly<Record<string, string>> = {
  DRIVER_INELIGIBLE:
    "No se creó la ruta: el repartidor no puede tomar rutas en la ciudad de estas órdenes. Puede que su ciudad sea " +
    "otra o que hoy no esté habilitado (su estado o sus documentos).",
  INVALID_REQUEST: "No se creó la ruta: el servidor no aceptó la solicitud. Actualiza la bandeja e intenta de nuevo.",
};

/** The uniform 404 of createRoute: it never says which one is missing. */
export const createRouteNotFoundMessage = "No se creó la ruta: el repartidor o la ciudad ya no están disponibles.";

/**
 * After a failure that cannot say whether the route exists (no answer, a server failure, an unexpected answer or a
 * generic conflict), the driver's DRAFT routes are read again before "Crear ruta nueva" is offered again: `found`
 * when that read lists a DRAFT route of the driver dated today (UI-PHASE3-ROUTES-DAILY-2026-10-12) - the step then
 * asks to choose it and offers no new route - `none` when it lists none of today, `unread` when it failed.
 */
export function unconfirmedRouteText(
  outcome: "found" | "none" | "unread",
  sameRequest: boolean,
  driverReference: string,
): string {
  if (outcome === "unread")
    return "No se pudo confirmar si la ruta se creó. Busca las rutas de nuevo antes de intentarlo otra vez.";
  if (outcome === "found")
    return (
      `No se pudo confirmar si la ruta se creó, pero ${driverReference} ya tiene una ruta en borrador con la fecha ` +
      "de hoy: elígela en la lista."
    );
  return (
    `No se pudo confirmar si la ruta se creó y ${driverReference} no tiene ninguna ruta en borrador con la fecha de ` +
    "hoy. " +
    (sameRequest ? "Puedes intentarlo de nuevo: se reenvía la misma solicitud sin duplicarla." : "Puedes intentarlo de nuevo.")
  );
}

/**
 * UI-PHASE3-ROUTES-DAILY-2026-10-12: what the step says where "Crear ruta nueva" can be offered - the driver has no
 * DRAFT route at all, or only older ones and none dated today.
 */
export function noTodayRouteText(driverReference: string, olderRoutes: boolean): string {
  return olderRoutes
    ? `${driverReference} no tiene una ruta en borrador con la fecha de hoy.`
    : `${driverReference} no tiene rutas en borrador.`;
}
