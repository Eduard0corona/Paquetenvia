import { describe, expect, it } from "vitest";
import { realtimeEventTypes } from "./event-types";

describe("SignalR contract constants", () => {
  it("keeps exact versioned event type values", () => {
    expect(Object.values(realtimeEventTypes)).toEqual([
      "OrderStatusChanged.v1",
      "OrderTimelineEventAdded.v1",
      "AssignmentChanged.v1",
      "RouteChanged.v1",
      "IncidentCreated.v1",
      "ExternalOfferChanged.v1",
      "NotificationStatusChanged.v1",
      "DriverLocationUpdated.v1",
      "PublicOrderStatusChanged.v1",
      "PublicEtaChanged.v1",
    ]);
  });
});
