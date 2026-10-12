import { vehicleLabel, vehicleTypes, type VehicleType } from "../../operations/contracts/assignable-driver";
import {
  array,
  boundedString,
  exactObject,
  fail,
  nullable,
  oneOf,
  uuid,
} from "../../operations/contracts/strict-json";

/**
 * UI-PHASE3-DRIVERS-2026-10-11: AI-05 listSettlementPayees (SettlementPayeePage). The drivers Finance may choose to
 * settle - every driver of the organization createSettlement accepts - shown only by their non-personal `DRV-`
 * reference and vehicle (AI-06 stores no driver name); the id is sent, never shown. Every reader fails closed.
 */
export interface SettlementPayee {
  readonly driver_id: string;
  readonly driver_reference: string;
  readonly vehicle_type: VehicleType;
}

export interface SettlementPayeePage {
  readonly items: readonly SettlementPayee[];
  readonly next_cursor: string | null;
}

/** The server page size is 100; anything larger is not this contract. */
const maximumPageItems = 100;
/** AI-05 bounds every list cursor to 128 characters. */
const maximumCursorLength = 128;
const driverReferencePattern = /^DRV-[0-9a-f]{8}$/;

function cursor(value: unknown): string {
  const text = boundedString(value, 1, maximumCursorLength);
  if (!/^[A-Za-z0-9_-]+$/.test(text)) fail();
  return text;
}

export function parseSettlementPayee(value: unknown): SettlementPayee {
  const object = exactObject(value, ["driver_id", "driver_reference", "vehicle_type"]);
  const reference = boundedString(object.driver_reference, 12, 12);
  if (!driverReferencePattern.test(reference)) fail();
  return {
    driver_id: uuid(object.driver_id),
    driver_reference: reference,
    vehicle_type: oneOf(object.vehicle_type, vehicleTypes),
  };
}

export function parseSettlementPayeePage(value: unknown): SettlementPayeePage {
  const object = exactObject(value, ["items", "next_cursor"]);
  const items = array(object.items, maximumPageItems).map(parseSettlementPayee);
  if (new Set(items.map((item) => item.driver_id)).size !== items.length) fail();
  return { items, next_cursor: nullable(object.next_cursor, cursor) };
}

/** Only `cursor`, and only a value this operation issued. */
export function settlementPayeeSearch(cursorValue?: string | null): URLSearchParams {
  const search = new URLSearchParams();
  if (cursorValue !== undefined && cursorValue !== null) search.set("cursor", cursor(cursorValue));
  return search;
}

/** "DRV-1a2b3c4d · Motocicleta": the option text of the payee picker and filter. */
export function settlementPayeeOption(payee: SettlementPayee): string {
  return `${payee.driver_reference} · ${vehicleLabel(payee.vehicle_type)}`;
}

/** Payees by reference, then id: a stable display order for the picker (the API pages by id). */
export function sortSettlementPayees(payees: readonly SettlementPayee[]): SettlementPayee[] {
  return [...payees].sort((left, right) =>
    left.driver_reference < right.driver_reference
      ? -1
      : left.driver_reference > right.driver_reference
        ? 1
        : left.driver_id < right.driver_id
          ? -1
          : left.driver_id > right.driver_id
            ? 1
            : 0,
  );
}

/** Shown where a settlement's payee is not in the payee list (the list failed or was cut short). */
export const unknownPayeeLabel = "sin referencia";
