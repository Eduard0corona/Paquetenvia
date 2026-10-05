import { describe, expect, it } from "vitest";
import {
  OperationsContractError,
  parseOperationsDashboard,
  parseOperationsOrderDetail,
} from "./operations-parsers";
import {
  formatMazatlanTime,
  orderStatusLabels,
  timelineLabel,
} from "./operations-formatters";
import { orderStatuses } from "./operations-dashboard";

const orderId = "11111111-1111-1111-1111-111111111111";
const ownerId = "22222222-2222-2222-2222-222222222222";
const assignmentId = "33333333-3333-3333-3333-333333333333";
const driverId = "44444444-4444-4444-4444-444444444444";

function item() {
  return {
    order_id: orderId,
    aggregate_version: 2,
    public_id: "ORD_synthetic",
    owner: { organization_id: ownerId, display_name: "Operación sintética" },
    operator: null,
    client: null,
    status: "READY_FOR_PICKUP",
    created_at: "2026-07-27T01:00:00Z",
    updated_at: "2026-07-27T02:00:00Z",
    service_type: "SAME_DAY",
    pickup_window: null,
    delivery_window: null,
    delivery_zone: null,
    assignment: {
      assignment_id: assignmentId,
      assignment_type: "OWN",
      status: "ACCEPTED",
      driver_id: driverId,
      driver_reference: "DRV-abcdef12",
    },
    latest_driver_location: {
      lat: 23.2,
      lng: -106.4,
      accuracy_m: 4.5,
      captured_at: "2026-07-27T01:59:00Z",
    },
    cost_warning: null,
    unassigned_alert: false,
  };
}

function page() {
  return {
    generated_at: "2026-07-27T02:00:00Z",
    items: [item()],
    next_cursor: null,
  };
}

describe("operations dashboard parser", () => {
  it("parses the exact contract", () => {
    expect(parseOperationsDashboard(page()).items[0]?.public_id).toBe(
      "ORD_synthetic",
    );
  });

  it("accepts the explicit zero offset emitted by ASP.NET as UTC", () => {
    expect(
      parseOperationsDashboard({
        ...page(),
        generated_at: "2026-07-27T02:00:00+00:00",
      }).generated_at,
    ).toBe("2026-07-27T02:00:00+00:00");
  });

  it.each([
    ["extra property", () => ({ ...page(), extra: true })],
    [
      "invalid uuid",
      () => ({ ...page(), items: [{ ...item(), order_id: "invalid" }] }),
    ],
    [
      "invalid version",
      () => ({ ...page(), items: [{ ...item(), aggregate_version: 0 }] }),
    ],
    [
      "unknown status",
      () => ({ ...page(), items: [{ ...item(), status: "UNKNOWN" }] }),
    ],
    [
      "unknown service",
      () => ({ ...page(), items: [{ ...item(), service_type: "OVERNIGHT" }] }),
    ],
    [
      "non UTC time",
      () => ({
        ...page(),
        items: [{ ...item(), updated_at: "2026-07-27T02:00:00-07:00" }],
      }),
    ],
    [
      "invalid latitude",
      () => ({
        ...page(),
        items: [
          {
            ...item(),
            latest_driver_location: {
              ...item().latest_driver_location,
              lat: 91,
            },
          },
        ],
      }),
    ],
    [
      "invalid assignment",
      () => ({
        ...page(),
        items: [
          {
            ...item(),
            assignment: { ...item().assignment, driver_reference: driverId },
          },
        ],
      }),
    ],
    [
      "invalid warning",
      () => ({
        ...page(),
        items: [{ ...item(), cost_warning: "MARGIN" }],
      }),
    ],
    [
      "too many items",
      () => ({ ...page(), items: Array.from({ length: 101 }, item) }),
    ],
  ])("rejects %s", (_name, create) => {
    expect(() => parseOperationsDashboard(create())).toThrow(
      OperationsContractError,
    );
  });
});

describe("operations labels and timeline", () => {
  it("maps exactly seventeen statuses without a raw fallback", () => {
    expect(orderStatuses).toHaveLength(17);
    expect(Object.keys(orderStatusLabels)).toHaveLength(17);
    expect(orderStatusLabels.DELIVERING).toBe("En reparto");
  });

  it("uses a safe label for unknown timeline events", () => {
    expect(timelineLabel("SECRET_INTERNAL_EVENT")).toBe(
      "Actualización de la orden",
    );
  });

  it("renders UTC in Mazatlan independently of device time zone", () => {
    const value = formatMazatlanTime("2026-07-27T06:30:00Z");
    expect(value).toContain("26");
  });

  it("preserves authoritative timeline order", () => {
    const detail = {
      id: orderId,
      public_id: "ORD_synthetic",
      owner_org_id: ownerId,
      operator_org_id: null,
      status: "DRAFT",
      price_net: { currency: "MXN", amount_cents: 7500 },
      version: 1,
      origin_location_id: assignmentId,
      destination_location_id: driverId,
      service_type: "SAME_DAY",
      quote_id: "55555555-5555-5555-5555-555555555555",
      city_id: "66666666-6666-6666-6666-666666666666",
      service_area_id: null,
      pricing_tier: "OCCASIONAL",
      total: { currency: "MXN", amount_cents: 8700 },
      claim_window_ends_at: null,
      finalized_at: null,
      service_window: null,
      timeline: [
        { event_type: "SECOND", occurred_at: "2026-07-27T02:00:00Z" },
        { event_type: "FIRST", occurred_at: "2026-07-27T01:00:00Z" },
      ],
    };
    expect(
      parseOperationsOrderDetail(detail).timeline.map(
        (event) => event.event_type,
      ),
    ).toEqual(["SECOND", "FIRST"]);
    // ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02: the order's own window, null meaning the zone schedule.
    const window = { from: "2026-07-27T16:00:00+00:00", to: "2026-07-27T18:00:00+00:00" };
    expect(parseOperationsOrderDetail({ ...detail, service_window: window }).service_window).toEqual(window);
    expect(parseOperationsOrderDetail(detail).service_window).toBeNull();
    const missing: Record<string, unknown> = { ...detail };
    delete missing.service_window;
    expect(parseOperationsOrderDetail(missing).service_window).toBeNull();
    expect(() =>
      parseOperationsOrderDetail({ ...detail, service_window: { from: window.to, to: window.to } }),
    ).toThrow(OperationsContractError);
    // UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: absent reads as no action; present is strict.
    expect(parseOperationsOrderDetail(detail).allowed_transitions).toEqual([]);
    const allowed = [{ target_status: "CANCELLED", required_metadata: [] }];
    expect(parseOperationsOrderDetail({ ...detail, allowed_transitions: allowed }).allowed_transitions).toEqual(allowed);
    expect(() =>
      parseOperationsOrderDetail({
        ...detail,
        allowed_transitions: [{ target_status: "CANCELLED", required_metadata: [], guard: "x" }],
      }),
    ).toThrow(OperationsContractError);
  });
});
