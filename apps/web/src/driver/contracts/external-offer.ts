export interface ExternalOfferMoney {
  readonly currency: "MXN";
  readonly amount_cents: number;
}

export interface ExternalOffer {
  readonly id: string;
  readonly order_id: string;
  readonly status: "OPEN" | "ACCEPTED" | "EXPIRED" | "CANCELLED";
  readonly commission: ExternalOfferMoney;
  readonly expires_at: string;
  readonly accepted_by_driver_id: string | null;
  readonly accepted_at: string | null;
  readonly version: number;
}

export interface ExternalOfferPage {
  readonly items: readonly ExternalOffer[];
  readonly next_cursor: string | null;
}

export class ExternalOfferContractError extends Error {}

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

export function parseExternalOfferPage(value: unknown): ExternalOfferPage {
  const root = object(value);
  exact(root, ["items", "next_cursor"]);
  if (!Array.isArray(root.items) || !(root.next_cursor === null || typeof root.next_cursor === "string")) {
    throw new ExternalOfferContractError();
  }
  return Object.freeze({
    items: Object.freeze(root.items.map(parseExternalOffer)),
    next_cursor: root.next_cursor,
  });
}

export function parseExternalOffer(value: unknown): ExternalOffer {
  const root = object(value);
  exact(root, ["id", "order_id", "status", "commission", "expires_at", "accepted_by_driver_id", "accepted_at", "version"]);
  const money = object(root.commission);
  exact(money, ["currency", "amount_cents"]);
  if (!validUuid(root.id) || !validUuid(root.order_id) ||
      !["OPEN", "ACCEPTED", "EXPIRED", "CANCELLED"].includes(String(root.status)) ||
      money.currency !== "MXN" || !integer(money.amount_cents, 0) ||
      !utcTimestamp(root.expires_at) || !integer(root.version, 1) ||
      !(root.accepted_by_driver_id === null || validUuid(root.accepted_by_driver_id)) ||
      !(root.accepted_at === null || utcTimestamp(root.accepted_at))) {
    throw new ExternalOfferContractError();
  }
  return Object.freeze({
    id: root.id as string,
    order_id: root.order_id as string,
    status: root.status as ExternalOffer["status"],
    commission: Object.freeze({ currency: "MXN", amount_cents: money.amount_cents as number }),
    expires_at: root.expires_at as string,
    accepted_by_driver_id: root.accepted_by_driver_id as string | null,
    accepted_at: root.accepted_at as string | null,
    version: root.version as number,
  });
}

function object(value: unknown): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new ExternalOfferContractError();
  return value as Record<string, unknown>;
}

function exact(value: Record<string, unknown>, names: readonly string[]): void {
  const keys = Object.keys(value);
  if (keys.length !== names.length || names.some((name) => !keys.includes(name))) throw new ExternalOfferContractError();
}

function validUuid(value: unknown): value is string {
  return typeof value === "string" && uuid.test(value) && value !== "00000000-0000-0000-0000-000000000000";
}

function integer(value: unknown, minimum: number): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= minimum;
}

function utcTimestamp(value: unknown): value is string {
  return typeof value === "string" && /Z$/.test(value) && Number.isFinite(Date.parse(value));
}
