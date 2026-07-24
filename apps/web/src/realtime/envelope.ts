import type { RealtimeEventType } from "./event-types";

declare const uuidBrand: unique symbol;
declare const publicOrderIdBrand: unique symbol;

export type Uuid = string & { readonly [uuidBrand]: true };
export type PublicOrderId = string & { readonly [publicOrderIdBrand]: true };

export interface RealtimeEnvelope<
  TAggregateId extends Uuid | PublicOrderId,
  TPayload,
> {
  readonly event_id: Uuid;
  readonly event_type: RealtimeEventType;
  readonly occurred_at: string;
  readonly aggregate_id: TAggregateId;
  readonly aggregate_version: number;
  readonly correlation_id?: Uuid;
  readonly payload: TPayload;
}

const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const publicOrderIdPattern = /^ORD_[A-Za-z0-9_-]{22}$/;

export function asUuid(value: string): Uuid {
  if (!uuidPattern.test(value) || value === "00000000-0000-0000-0000-000000000000") {
    throw new Error("A canonical non-empty UUID is required.");
  }

  return value as Uuid;
}

export function asPublicOrderId(value: string): PublicOrderId {
  if (!publicOrderIdPattern.test(value)) {
    throw new Error("A valid public order id is required.");
  }

  return value as PublicOrderId;
}
