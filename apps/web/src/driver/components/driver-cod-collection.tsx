"use client";

import type { ConfirmRequest } from "../../components/confirm-dialog";
import { Money } from "../../components/ui/money";
import { formatMxnCents } from "../../operations/contracts/money";
import type { ProjectedDriverStop } from "../offline/operation-projection";
import { codReceivedLabel } from "../offline/rejection-codes";
import styles from "./driver-stops.module.css";

/**
 * UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 confirmation (owner answer "Solo
 * confirma el monto"): it names the exact amount; the reference (tracking
 * number and phone time) is added by the app and never typed.
 */
export function codCollectionConfirmation(
  amountCents: number,
): Omit<ConfirmRequest, "onConfirm"> {
  const amount = formatMxnCents(amountCents);
  return {
    title: `¿Recibiste ${amount}?`,
    description:
      `Confirma que te pagaron ${amount} en efectivo por esta entrega. ` +
      "El cobro se registra con la guía y la hora de tu teléfono.",
    confirmLabel: `Sí, recibí ${amount}`,
  };
}

/**
 * The cash step of a DELIVERY stop: "Cobro $1,250.00" and, while the stop is
 * DELIVERING and nothing is recorded or saved yet, "Recibí $1,250.00". The
 * collection is queued only from the confirmation; afterwards the step tells
 * the driver it is saved, so the delivery can be confirmed.
 */
export function DriverCodCollection({
  stop,
  disabled,
  confirm,
  onCollect,
}: Readonly<{
  stop: ProjectedDriverStop;
  disabled: boolean;
  confirm: (request: ConfirmRequest) => void;
  onCollect: () => Promise<boolean>;
}>) {
  if (stop.cod === null) return null;
  const amountCents = stop.cod.expected.amount_cents;
  const headingId = `cod-heading-${stop.order_id}`;
  return (
    <section className={styles.codStep} aria-labelledby={headingId}>
      <h3 id={headingId}>
        Cobro <Money cents={amountCents} currency={false} />
      </h3>
      {stop.codCollection === "recorded" ? (
        <p>El cobro ya está registrado.</p>
      ) : stop.codCollection === "queued" ? (
        <p>Cobro guardado en el teléfono; se registrará al sincronizar.</p>
      ) : stop.codCollection === "attention" ? (
        <p>El cobro requiere atención; revisa el aviso de esta parada.</p>
      ) : stop.projectedStatus !== "DELIVERING" ? (
        <p>Cobra este monto en efectivo al entregar.</p>
      ) : (
        <button
          type="button"
          className={styles.codButton}
          disabled={disabled}
          onClick={() =>
            confirm({
              ...codCollectionConfirmation(amountCents),
              onConfirm: () => void onCollect(),
            })
          }
        >
          {codReceivedLabel(amountCents)}
        </button>
      )}
    </section>
  );
}
