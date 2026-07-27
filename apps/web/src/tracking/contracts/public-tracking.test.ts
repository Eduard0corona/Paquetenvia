import { describe, expect, it } from "vitest";
import {
  parsePublicTrackingProjection,
  PublicTrackingContractError,
} from "./public-tracking";
import {
  publicStatusLabels,
  publicTimelineLabels,
} from "./public-tracking-labels";

const valid = {
  public_id: "ORD_0123456789abcdefghijkl",
  public_status: "IN_TRANSIT",
  aggregate_version: 4,
  estimated_window: null,
  timeline: [
    {
      code: "ORDER_CREATED",
      occurred_at: "2026-07-27T00:00:00Z",
    },
    {
      code: "IN_TRANSIT",
      occurred_at: "2026-07-27T01:00:00Z",
    },
  ],
};

describe("public tracking response contract", () => {
  it("accepts the exact bounded projection", () => {
    expect(parsePublicTrackingProjection(valid)).toEqual(valid);
  });

  it.each([
    { ...valid, owner_org_id: "private" },
    { ...valid, public_status: "PICKED_UP" },
    { ...valid, aggregate_version: 0 },
    { ...valid, aggregate_version: Number.MAX_SAFE_INTEGER + 1 },
    { ...valid, estimated_window: { private: 42 } },
    { ...valid, estimated_window: Object.fromEntries(
      Array.from({ length: 9 }, (_, index) => [`key-${index}`, null]),
    ) },
    { ...valid, timeline: [...valid.timeline].reverse() },
    { ...valid, timeline: [{ ...valid.timeline[0], actor_id: "private" }] },
    { ...valid, timeline: Array.from({ length: 201 }, () => valid.timeline[0]) },
  ])("fails closed for invalid or expanded data", (candidate) => {
    expect(() => parsePublicTrackingProjection(candidate)).toThrow(
      PublicTrackingContractError,
    );
  });

  it("keeps every contracted Spanish label", () => {
    expect(publicStatusLabels).toEqual({
      CREATED: "Pedido creado",
      SCHEDULED: "Recolección programada",
      IN_TRANSIT: "En tránsito",
      OUT_FOR_DELIVERY: "En reparto",
      DELIVERY_EXCEPTION: "Hay una incidencia en la entrega",
      DELIVERED: "Entregado",
      RETURNING: "En devolución",
      RETURNED: "Devuelto",
      CANCELLED: "Cancelado",
    });
    expect(publicTimelineLabels.DELIVERY_ATTEMPTED).toBe(
      "Intento de entrega",
    );
    expect(publicTimelineLabels.PICKED_UP).toBe("Paquete recolectado");
  });
});
