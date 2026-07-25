import { describe, expect, it } from "vitest";
import {
  mapDriverAggregateVersions,
  mapOperationsAggregateVersions,
  mapTrackingAggregateVersions,
} from "./resynchronization";

const orderId = "00000000-0000-0000-0000-000000000111";
const publicOrderId = "ORD_abcdefghijklmnopqrstuv";

describe("REST resynchronization cursor mappers", () => {
  it("maps operations order IDs to current versions", () => {
    expect(mapOperationsAggregateVersions([{ id: orderId, version: 12 }])).toEqual({
      [orderId]: 12,
    });
  });

  it("maps driver stops by the SignalR order aggregate ID", () => {
    expect(
      mapDriverAggregateVersions([{ order_id: orderId, aggregate_version: 13 }]),
    ).toEqual({ [orderId]: 13 });
  });

  it("maps public tracking by public aggregate ID", () => {
    expect(
      mapTrackingAggregateVersions({
        public_id: publicOrderId,
        aggregate_version: 14,
      }),
    ).toEqual({ [publicOrderId]: 14 });
  });

  it("rejects unsafe or duplicate cursors", () => {
    expect(() =>
      mapOperationsAggregateVersions([
        { id: orderId, version: 1 },
        { id: orderId, version: 2 },
      ]),
    ).toThrow("duplicate");
    expect(() =>
      mapDriverAggregateVersions([
        { order_id: orderId, aggregate_version: Number.MAX_SAFE_INTEGER + 1 },
      ]),
    ).toThrow("invalid");
  });
});
