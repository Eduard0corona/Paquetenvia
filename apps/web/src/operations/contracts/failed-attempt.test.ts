import { describe, expect, it } from "vitest";
import type { IncidentsApi } from "../api/incidents-api";
import { TenantApiError, type TenantApiErrorCategory } from "../api/tenant-request";
import { loadFailedAttempt } from "../state/failed-attempt-loader";
import { failedAttemptPendingNote, failedAttemptView } from "./failed-attempt";
import { parseIncident, type IncidentPage } from "./incident";
import { incidentResponse, orderId, syntheticUuid } from "./ui-001-screens.fixtures";

const reported = parseIncident(
  incidentResponse({
    reason_code: "SECURITY_RISK",
    severity: "HIGH",
    next_action: null,
    sla_due_at: "2026-09-29T00:00:00Z",
  }),
);
const decided = parseIncident(
  incidentResponse({ id: syntheticUuid(0x901), reason_code: "ADDRESS_NOT_FOUND", next_action: "RETURNING" }),
);

function api(result: IncidentPage | TenantApiErrorCategory, calls: unknown[] = []): Pick<IncidentsApi, "list"> {
  return {
    list: async (filter, cursor) => {
      calls.push([filter, cursor]);
      if (typeof result === "string") throw new TenantApiError(result);
      return result;
    },
  };
}

// INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11 (AI-07 /ops/orders/:id failed_attempt).
describe("failed attempt view", () => {
  it("shows the reason, that dispatch decides and the attention deadline of a driver report", () => {
    expect(failedAttemptView([reported, decided])).toEqual({
      reasonLabel: "Riesgo de seguridad",
      decisionPending: true,
      nextStepLabel: "Lo decide despacho",
      attendBy: "2026-09-29T00:00:00Z",
    });
    expect(failedAttemptPendingNote).toBe(
      "El repartidor reportó el intento. Despacho decide cómo sigue la orden en «Siguiente paso».",
    );
  });

  it("names the next action dispatch recorded when it reported the attempt itself", () => {
    expect(failedAttemptView([decided])).toMatchObject({
      reasonLabel: "Dirección incorrecta",
      decisionPending: false,
      nextStepLabel: "Devolver al remitente",
    });
    expect(failedAttemptView([])).toBeNull();
  });

  it("never carries identifiers, evidence or the description", () => {
    const view = failedAttemptView([reported]);
    expect(JSON.stringify(view)).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}-/);
    expect(Object.keys(view ?? {}).sort()).toEqual(["attendBy", "decisionPending", "nextStepLabel", "reasonLabel"]);
  });
});

describe("failed attempt loader", () => {
  it("lists only the incidents of the order, newest first as the server returns them", async () => {
    const calls: unknown[] = [];
    const state = await loadFailedAttempt(api({ items: [reported], next_cursor: null }, calls), orderId);
    expect(state).toEqual({ kind: "ready", view: failedAttemptView([reported]) });
    expect(calls).toEqual([[{ orderId }, null]]);
  });

  it.each<TenantApiErrorCategory>(["forbidden", "unauthorized", "not_found"])(
    "shows nothing to a caller without the read (%s)",
    async (category) => {
      expect(await loadFailedAttempt(api(category), orderId)).toEqual({ kind: "hidden" });
    },
  );

  it.each<TenantApiErrorCategory>(["network", "unavailable", "invalid", "conflict"])(
    "says the reason could not be read on %s",
    async (category) => {
      expect(await loadFailedAttempt(api(category), orderId)).toEqual({ kind: "unavailable" });
    },
  );

  it("shows nothing when the order has no incident", async () => {
    expect(await loadFailedAttempt(api({ items: [], next_cursor: null }), orderId)).toEqual({ kind: "hidden" });
  });
});
