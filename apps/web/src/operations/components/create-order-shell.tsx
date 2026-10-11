"use client";

import type { AcceptanceVersions } from "../contracts/acceptance-versions";
import { useCreateOrder } from "../state/use-create-order";
import { ScreenGate } from "../../components/ui/feedback";
import { PageHeader } from "../../components/ui/page-header";
import { CreateOrderOutcomePanel } from "./create-order-summary";
import { CreateOrderWizard } from "./create-order-wizard";

/**
 * /ops/orders/new (AI-07 create_order, UI-PHASE3-ORDER-WIZARD-2026-10-10): "Nueva orden" in four
 * steps. When the person finishes, the order is created and then confirmed (project owner,
 * 2026-10-10: "Confirmada (Recommended)"); the screen reports exactly what the API answered.
 */
export function CreateOrderShell({
  acceptanceVersions,
}: {
  readonly acceptanceVersions: AcceptanceVersions | null;
}) {
  const { state, controller } = useCreateOrder(acceptanceVersions);
  return (
    <div className="page" aria-busy={state.phase === "loading" || state.busy !== null}>
      <PageHeader
        eyebrow="Despacho"
        title="Nueva orden"
        description="Captura el envío en cuatro pasos: dónde, qué se envía, servicio y precio, y confirmar."
      />

      <ScreenGate
        phase={state.phase}
        accessMessage="Tu rol en la organización activa no puede crear cotizaciones ni órdenes."
      />

      {state.phase === "ready" &&
        (state.outcome !== null ? (
          <CreateOrderOutcomePanel
            key={state.outcome.kind}
            outcome={state.outcome}
            busy={state.busy !== null}
            controller={controller}
          />
        ) : (
          <CreateOrderWizard
            state={state}
            controller={controller}
            versionsConfigured={acceptanceVersions !== null}
          />
        ))}
    </div>
  );
}
