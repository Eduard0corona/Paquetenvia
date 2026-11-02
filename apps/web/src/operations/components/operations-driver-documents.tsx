"use client";

import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import type { ConfirmRequest } from "../../components/confirm-dialog";
import { Field } from "../../components/ui/field";
import {
  documentsColumn,
  documentWarning,
  driverDocumentRejectionCodes,
  driverDocumentRejectionLabels,
  driverDocumentTypeLabels,
  formatCalendarDate,
  inPersonReviewLine,
  isCalendarDate,
  isRevocable,
  mazatlanToday,
  permanentLicenseReviewDate,
  slotDateText,
  slotStatusText,
  type DocumentIndicator,
  type DriverDocumentRejectionCode,
  type DriverDocumentSlot,
  type DriverDocumentType,
  type RecordDriverDocumentInput,
} from "../contracts/driver-document";
import type { DocumentWriteResult } from "../state/drivers-controller";
import { useMinuteClock } from "../state/use-minute-clock";

const toneClass = {
  muted: "statusToneMuted",
  ok: "statusToneOk",
  warn: "statusToneWarn",
  crit: "statusToneCrit",
} as const;

/** A state as a dot next to its text, never color alone. */
function Indicator({ indicator }: { readonly indicator: DocumentIndicator }) {
  return (
    <span className={`statusBadge ${toneClass[indicator.tone]}`}>
      <span className="statusDot" aria-hidden="true" />
      <span className="statusBadgeLabel">{indicator.text}</span>
    </span>
  );
}

/**
 * DRV-DOC-001-IN-PERSON-2026-11-02 (AI-07 driver_roster documents.column): the driver's documents in one state -
 * «Vencido», "Incompletos", «Vence en N días» or "Completos" - with the "Documentos" button that opens them.
 */
export function DriverDocumentsCell({
  reference,
  slots,
  now,
  onOpen,
}: {
  readonly reference: string;
  readonly slots: readonly DriverDocumentSlot[] | undefined;
  readonly now: number;
  readonly onOpen: () => void;
}) {
  if (slots === undefined) {
    // The documents page of this driver was not read (yet): nothing to show or record.
    return <td>Sin datos de documentos</td>;
  }
  return (
    <td>
      <span className="opsDocumentsCell">
        <Indicator indicator={documentsColumn(slots, now)} />
        <button type="button" className="btn btnSecondary" onClick={onOpen} aria-label={`Documentos de ${reference}`}>
          Documentos
        </button>
      </span>
    </td>
  );
}

type View = { readonly kind: "list" } | { readonly kind: "record" };

/**
 * The driver's documents (AI-07 driver_roster documents.dialog): one line per type its vehicle requires with its
 * status, reason, date and warning, "Registrar documento" and, on the current VALID one of a type, "Revocar". Built on
 * the native modal dialog (the browser keeps focus inside it and Escape closes it). Nothing typed is kept after it
 * closes, logged or stored.
 */
export function DriverDocumentsDialog({
  reference,
  slots,
  onClose,
  onRecord,
  onRevoke,
  confirm,
}: {
  readonly reference: string;
  readonly slots: readonly DriverDocumentSlot[];
  readonly onClose: () => void;
  readonly onRecord: (input: RecordDriverDocumentInput, idempotencyKey: string) => Promise<DocumentWriteResult>;
  readonly onRevoke: (documentId: string) => Promise<DocumentWriteResult>;
  readonly confirm: (request: ConfirmRequest) => void;
}) {
  const dialogRef = useRef<HTMLDialogElement>(null);
  const returnFocusRef = useRef<HTMLElement | null>(null);
  const titleId = useId();
  const [view, setView] = useState<View>({ kind: "list" });
  const [status, setStatus] = useState<string | null>(null);
  const [revokeError, setRevokeError] = useState<string | null>(null);
  const [revoking, setRevoking] = useState(false);
  const now = useMinuteClock();

  useEffect(() => {
    const dialog = dialogRef.current;
    if (dialog === null) return;
    const active = document.activeElement;
    returnFocusRef.current = active instanceof HTMLElement ? active : null;
    if (!dialog.open) dialog.showModal();
    return () => {
      if (dialog.open) dialog.close();
      const target = returnFocusRef.current;
      if (target !== null && target.isConnected) target.focus();
    };
  }, []);

  const revoke = (slot: DriverDocumentSlot, button: HTMLElement) => {
    const document = slot.document;
    if (document === null) return;
    confirm({
      title: "¿Revocar documento?",
      description:
        "El repartidor ya no recibirá pedidos nuevos; lo que lleva sigue y puedes reasignarlo desde la orden.",
      confirmLabel: "Revocar",
      returnFocus: button,
      onConfirm: () => {
        setRevoking(true);
        setRevokeError(null);
        setStatus(null);
        void onRevoke(document.id).then((result) => {
          setRevoking(false);
          if (result.ok) setStatus(`${driverDocumentTypeLabels[slot.document_type]}: documento revocado.`);
          else setRevokeError(result.message);
        });
      },
    });
  };

  return (
    <dialog
      ref={dialogRef}
      className="confirmDialog documentsDialog"
      aria-labelledby={titleId}
      onClose={onClose}
    >
      <h2 id={titleId}>{view.kind === "list" ? `Documentos de ${reference}` : "Registrar documento"}</h2>
      {view.kind === "list" ? (
        <>
          <p>Despacho revisa los documentos en persona; aquí solo se anota el tipo, la fecha y la decisión.</p>
          <ul className="documentsList">
            {slots.map((slot) => {
              const warning = slot.document === null ? null : documentWarning(slot.document, now);
              const date = slotDateText(slot);
              return (
                <li key={slot.document_type}>
                  <span className="documentsListType">{driverDocumentTypeLabels[slot.document_type]}</span>
                  <span>{slotStatusText(slot)}</span>
                  {date !== null && <span>{date}</span>}
                  {warning !== null && <Indicator indicator={warning} />}
                  {isRevocable(slot) && (
                    <button
                      type="button"
                      className="btn btnSecondary"
                      disabled={revoking}
                      onClick={(event) => revoke(slot, event.currentTarget)}
                      aria-label={`Revocar ${driverDocumentTypeLabels[slot.document_type]}`}
                    >
                      Revocar
                    </button>
                  )}
                </li>
              );
            })}
          </ul>
          {revokeError !== null && (
            <p className="notice noticeCrit" role="alert">
              {revokeError}
            </p>
          )}
          <p className="live" aria-live="polite">
            {status ?? ""}
          </p>
          <div className="confirmDialogActions">
            <button type="button" className="btn btnSecondary" onClick={() => dialogRef.current?.close()}>
              Cerrar
            </button>
            {slots.length > 0 && (
              <button
                type="button"
                className="btn btnPrimary"
                onClick={() => {
                  setStatus(null);
                  setRevokeError(null);
                  setView({ kind: "record" });
                }}
              >
                Registrar documento
              </button>
            )}
          </div>
        </>
      ) : (
        <RecordDocumentForm
          slots={slots}
          now={now}
          onCancel={() => setView({ kind: "list" })}
          onRecord={onRecord}
          onRecorded={(type) => {
            setStatus(`${driverDocumentTypeLabels[type]}: documento registrado.`);
            setView({ kind: "list" });
          }}
        />
      )}
    </dialog>
  );
}

type Decision = "" | "VALID" | "REJECTED";

/**
 * "Registrar documento" (AI-07 driver_roster documents.record_dialog): the type among the ones the vehicle requires
 * (Y11), "Licencia permanente de Sinaloa" for a license (Y5), the printed date (Y12), Aprobado or Rechazado and, for
 * Rechazado, one reason of the fixed list (Y7). No photo, copy or document number is asked (Y3). One Idempotency-Key per
 * submission, reused only to retry the same one after an answer that applied nothing for sure; on an error the form
 * keeps what was typed.
 */
function RecordDocumentForm({
  slots,
  now,
  onCancel,
  onRecord,
  onRecorded,
}: {
  readonly slots: readonly DriverDocumentSlot[];
  readonly now: number;
  readonly onCancel: () => void;
  readonly onRecord: (input: RecordDriverDocumentInput, idempotencyKey: string) => Promise<DocumentWriteResult>;
  readonly onRecorded: (type: DriverDocumentType) => void;
}) {
  const types = slots.map((slot) => slot.document_type);
  const firstPending = slots.find((slot) => slot.document === null || slot.document.status !== "VALID");
  const [type, setType] = useState<DriverDocumentType>((firstPending ?? slots[0]!).document_type);
  const [decision, setDecision] = useState<Decision>("");
  const [permanent, setPermanent] = useState(false);
  const [printed, setPrinted] = useState("");
  const [reason, setReason] = useState<DriverDocumentRejectionCode | "">("");
  const [fieldError, setFieldError] = useState<{ readonly field: "decision" | "date" | "reason"; readonly text: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const submission = useRef<{ readonly key: string; readonly fingerprint: string; retryable: boolean } | null>(null);
  const decisionName = useId();
  const permanentAllowed = type === "DRIVER_LICENSE";
  const usesPermanent = permanentAllowed && permanent;

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (pending) return;
    setError(null);
    let input: RecordDriverDocumentInput;
    if (decision === "") {
      setFieldError({ field: "decision", text: "Elige si lo apruebas o lo rechazas." });
      return;
    }
    if (decision === "REJECTED") {
      if (reason === "") {
        setFieldError({ field: "reason", text: "Elige el motivo del rechazo." });
        return;
      }
      input = { documentType: type, decision: "REJECTED", rejectionCode: reason };
    } else if (usesPermanent) {
      input = { documentType: "DRIVER_LICENSE", decision: "VALID", permanentLicense: true };
    } else {
      if (!isCalendarDate(printed)) {
        setFieldError({ field: "date", text: "Escribe la fecha de vencimiento impresa completa (día, mes y año)." });
        return;
      }
      input = { documentType: type, decision: "VALID", permanentLicense: false, printedExpiry: printed };
    }
    setFieldError(null);
    const fingerprint = JSON.stringify(input);
    const previous = submission.current;
    const key = previous !== null && previous.fingerprint === fingerprint && previous.retryable ? previous.key : crypto.randomUUID();
    submission.current = { key, fingerprint, retryable: false };
    setPending(true);
    const result = await onRecord(input, key);
    setPending(false);
    if (result.ok) {
      submission.current = null;
      onRecorded(type);
      return;
    }
    submission.current = { key, fingerprint, retryable: result.retryable };
    setError(result.message);
  };

  return (
    <form className="documentsForm" onSubmit={(event) => void submit(event)} noValidate>
      <p className="notice">{inPersonReviewLine}</p>
      <Field label="Documento">
        {(control) => (
          <select
            {...control}
            value={type}
            onChange={(event) => {
              const next = event.target.value as DriverDocumentType;
              setType(next);
              if (next !== "DRIVER_LICENSE") setPermanent(false);
            }}
          >
            {types.map((value) => (
              <option key={value} value={value}>
                {driverDocumentTypeLabels[value]}
              </option>
            ))}
          </select>
        )}
      </Field>

      <fieldset className="documentsDecision" aria-describedby={fieldError?.field === "decision" ? `${decisionName}-error` : undefined}>
        <legend>Decisión</legend>
        {(["VALID", "REJECTED"] as const).map((value) => (
          <label key={value}>
            <input
              type="radio"
              name={decisionName}
              value={value}
              checked={decision === value}
              onChange={() => setDecision(value)}
            />{" "}
            {value === "VALID" ? "Aprobado" : "Rechazado"}
          </label>
        ))}
        {fieldError?.field === "decision" && (
          <p id={`${decisionName}-error`} className="fieldError">
            {fieldError.text}
          </p>
        )}
      </fieldset>

      {decision === "VALID" && permanentAllowed && (
        <label className="documentsCheck">
          <input type="checkbox" checked={permanent} onChange={(event) => setPermanent(event.target.checked)} /> Licencia
          permanente de Sinaloa
        </label>
      )}

      {decision === "VALID" && usesPermanent && (
        <p>Revisión el {formatCalendarDate(permanentLicenseReviewDate(mazatlanToday(now)))}</p>
      )}

      {decision === "VALID" && !usesPermanent && (
        <Field
          label="Fecha de vencimiento impresa"
          hint="Vale todo ese día, hora de Culiacán."
          error={fieldError?.field === "date" ? fieldError.text : undefined}
        >
          {(control) => <input {...control} type="date" value={printed} onChange={(event) => setPrinted(event.target.value)} />}
        </Field>
      )}

      {decision === "REJECTED" && (
        <Field label="Motivo" error={fieldError?.field === "reason" ? fieldError.text : undefined}>
          {(control) => (
            <select
              {...control}
              value={reason}
              onChange={(event) => setReason(event.target.value as DriverDocumentRejectionCode | "")}
            >
              <option value="">Elige un motivo</option>
              {driverDocumentRejectionCodes.map((code) => (
                <option key={code} value={code}>
                  {driverDocumentRejectionLabels[code]}
                </option>
              ))}
            </select>
          )}
        </Field>
      )}

      {error !== null && (
        <p className="notice noticeCrit" role="alert">
          {error}
        </p>
      )}

      <div className="confirmDialogActions">
        <button type="button" className="btn btnSecondary" onClick={onCancel} disabled={pending}>
          Cancelar
        </button>
        <button type="submit" className="btn btnPrimary" disabled={pending}>
          {pending ? "Registrando…" : "Registrar"}
        </button>
      </div>
    </form>
  );
}
