"use client";

import Link from "next/link";
import { recipientWhatsAppConsentText } from "../contracts/create-order";
import {
  csvCodColumn,
  csvFileErrorLabels,
  csvRecipientConsentField,
  csvRecipientConsentHint,
  csvHeader,
  csvRowErrorLabels,
  csvRowOutcomeLabels,
  isCommittable,
  type CsvImportCommit,
  type CsvImportPreview,
} from "../contracts/csv-import";
import { operationsOrderHref } from "../routing/operations-routing";
import { csvImportRateLimit, type CsvImportController, type CsvImportState } from "../state/csv-import-controller";
import { useCsvImport } from "../state/use-csv-import";
import { Feedback, ScreenGate } from "../../components/ui/feedback";
import { RateLimitNoticeView, useRateLimitWait } from "../../components/ui/rate-limit-notice";
import { Money } from "../../components/ui/money";
import { PageHeader } from "../../components/ui/page-header";

export function CsvImportShell() {
  const { state, controller } = useCsvImport();
  // SEC-004 (owner answers Q1, Q6): preview and commit share one budget, so during the server's wait both
  // "Previsualizar" and "Confirmar" are disabled; the file and the report stay and nothing is sent by itself.
  const rateLimit = useRateLimitWait(csvImportRateLimit);
  const waiting = rateLimit.secondsLeft > 0;
  return (
    <div className="page" aria-busy={state.phase === "loading" || state.busy}>
      <PageHeader
        eyebrow="Despacho"
        title="Importar órdenes por CSV"
        description="Revisamos el archivo fila por fila; ninguna orden se crea hasta que confirmas el lote."
      />

      <ScreenGate phase={state.phase} accessMessage="Tu rol en la organización activa no importa órdenes." />
      <Feedback errors={state.errors} message={state.message} stepUpHref={state.stepUpHref} />
      <RateLimitNoticeView {...rateLimit} />

      {state.phase === "ready" && (
        <section className="opsFormLayout">
          <UploadForm key={`upload-${state.formKey}`} state={state} controller={controller} waiting={waiting} />
          <section>
            {state.commit !== null
              ? <CommitReport commit={state.commit} controller={controller} />
              : state.preview !== null && (
                <PreviewReport preview={state.preview} state={state} controller={controller} waiting={waiting} />
              )}
          </section>
        </section>
      )}
    </div>
  );
}

function UploadForm({
  state,
  controller,
  waiting,
}: {
  readonly state: CsvImportState;
  readonly controller: CsvImportController;
  /** SEC-004: the server's wait for the CSV budget is running. */
  readonly waiting: boolean;
}) {
  return (
    <form className="opsForm" autoComplete="off" onSubmit={(event) => {
      event.preventDefault();
      void controller.preview();
    }}>
      <fieldset>
        <legend>Archivo</legend>
        <p>
          Archivo CSV en UTF-8 con encabezado <code>{csvHeader}</code>;
          hasta 500 filas y 1 MiB. Cada fila usa una cotización vigente de esta organización.
        </p>
        <p>
          Cobro contra entrega (opcional): agrega al final la columna <code>{csvCodColumn}</code> con el monto en
          centavos enteros, sin punto, comas ni signos (por ejemplo <code>15050</code> para $150.50). Deja la celda
          vacía si la orden no lleva cobro. El tope es $20,000.00 MXN por pedido (<code>2000000</code> centavos); una
          fila con más se marca inválida y el lote no se puede confirmar.
        </p>
        <label>Archivo CSV
          <input
            name="file"
            type="file"
            accept=".csv,text/csv"
            disabled={state.busy}
            onChange={(event) => void controller.selectFile(event.currentTarget.files?.item(0) ?? null)}
          />
        </label>
        {state.fileBytes !== null && <p>Archivo listo en memoria ({state.fileBytes} bytes).</p>}
        <div className="opsFormActions">
          <button className="btn btnPrimary" type="submit" disabled={state.busy || state.fileBytes === null || waiting}>
            {state.busy && state.commit === null ? "Procesando…" : "Previsualizar"}
          </button>
          <button className="btn btnSecondary" type="button" disabled={state.busy} onClick={() => controller.reset()}>
            Empezar de nuevo
          </button>
        </div>
      </fieldset>
    </form>
  );
}

function PreviewReport({
  preview,
  state,
  controller,
  waiting,
}: {
  readonly preview: CsvImportPreview;
  readonly state: CsvImportState;
  readonly controller: CsvImportController;
  /** SEC-004: the server's wait for the CSV budget is running. */
  readonly waiting: boolean;
}) {
  const committable = isCommittable(preview);
  return (
    <>
      <h2>Prevalidación</h2>
      <p>{preview.total_rows} fila(s): {preview.valid_rows} válida(s), {preview.invalid_rows} con error.</p>
      {preview.file_errors.length > 0 && (
        <ul className="notice noticeCrit" role="alert">
          {preview.file_errors.map((code) => <li key={code}>{csvFileErrorLabels[code]}</li>)}
        </ul>
      )}
      {preview.rows.length > 0 && (
        <table className="opsTable">
          <caption>Resultado por fila (la línea 1 es el encabezado)</caption>
          <thead>
            <tr><th scope="col">Línea</th><th scope="col">Cotización</th><th scope="col">Cobro contra entrega</th><th scope="col">Resultado</th></tr>
          </thead>
          <tbody>
            {preview.rows.map((row) => (
              <tr key={row.row_number}>
                <td>{row.row_number}</td>
                <td>{row.quote_id ?? "—"}</td>
                <td>{codLabel(row.cod_expected_cents)}</td>
                <td>
                  {row.valid
                    ? "Válida"
                    : row.errors.length === 0
                      ? "Rechazada: la aceptación queda fuera de los límites permitidos"
                      : row.errors.map((error) => `${csvRowErrorLabels[error.code]} (${error.column})`).join("; ")}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {committable && state.canCommit && (
        // A new file (another digest) starts from unticked boxes.
        <CsvCommitForm
          key={`commit-${preview.content_digest}`}
          validRows={preview.valid_rows}
          state={state}
          waiting={waiting}
          controller={controller}
        />
      )}
    </>
  );
}

/**
 * The commit form: the required prohibited-goods confirmation (ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02) and the one
 * optional WhatsApp consent box of the file (NTF-RECIPIENT-NOTICES-2026-10-20; AI-07 csv_order_import.recipient_notices),
 * unticked by default, labelled exactly with the AI-24 consent text and followed by the fixed note of
 * NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23 (N2).
 */
export function CsvCommitForm({
  validRows,
  state,
  waiting,
  controller,
}: {
  readonly validRows: number;
  readonly state: Pick<CsvImportState, "busy">;
  /** SEC-004: the server's wait for the CSV budget is running. */
  readonly waiting: boolean;
  readonly controller: Pick<CsvImportController, "commit">;
}) {
  return (
    <form
      className="opsForm"
      autoComplete="off"
      onSubmit={(event) => {
        event.preventDefault();
        const data = new FormData(event.currentTarget);
        void controller.commit(
          data.get("restricted_goods_acknowledged") === "on",
          data.get(csvRecipientConsentField) === "on",
        );
      }}
    >
      <label className="opsCheckbox">
        <input type="checkbox" name="restricted_goods_acknowledged" required /> Confirmo que ningún envío del
        archivo contiene artículos prohibidos
      </label>
      <div className="field">
        <label className="opsCheckbox" htmlFor="csv-recipient-whatsapp-consent">
          <input
            id="csv-recipient-whatsapp-consent"
            type="checkbox"
            name={csvRecipientConsentField}
            aria-describedby="csv-recipient-whatsapp-consent-hint"
          />{" "}
          {recipientWhatsAppConsentText}
        </label>
        <p id="csv-recipient-whatsapp-consent-hint" className="fieldHint">{csvRecipientConsentHint}</p>
      </div>
      <div className="opsFormActions">
        <button className="btn btnPrimary" type="submit" disabled={state.busy || waiting}>
          {state.busy ? "Confirmando…" : `Confirmar lote de ${validRows} orden(es)`}
        </button>
      </div>
    </form>
  );
}

/** Server-reported integer cents only; the client never parses the COD cell. */
function codLabel(cents: number | null) {
  if (cents === null) return "—";
  return cents === 0 ? "Sin cobro" : <Money cents={cents} />;
}

function CommitReport({
  commit,
  controller,
}: {
  readonly commit: CsvImportCommit;
  readonly controller: CsvImportController;
}) {
  return (
    <>
      <h2>Lote confirmado</h2>
      <p>{commit.created_rows} orden(es) creada(s), {commit.failed_rows} fila(s) con error.</p>
      <table className="opsTable">
        <caption>Resultado por fila</caption>
        <thead>
          <tr><th scope="col">Línea</th><th scope="col">Resultado</th><th scope="col">Orden</th></tr>
        </thead>
        <tbody>
          {commit.rows.map((row) => (
            <tr key={row.row_number}>
              <td>{row.row_number}</td>
              <td>{row.status === "CREATED" ? "Creada" : csvRowOutcomeLabels[row.error_code ?? "INVALID_REQUEST"]}</td>
              <td>
                {row.order_id !== null && row.public_id !== null
                  ? <Link href={operationsOrderHref(row.order_id)}>{row.public_id}</Link>
                  : "—"}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <button className="btn btnSecondary" type="button" onClick={() => controller.reset()}>Importar otro archivo</button>
    </>
  );
}
