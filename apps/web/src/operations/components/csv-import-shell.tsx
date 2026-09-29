"use client";

import Link from "next/link";
import {
  csvCodColumn,
  csvFileErrorLabels,
  csvHeader,
  csvRowErrorLabels,
  csvRowOutcomeLabels,
  isCommittable,
  type CsvImportCommit,
  type CsvImportPreview,
} from "../contracts/csv-import";
import { formatMxnCentsWithCurrency } from "../contracts/money";
import { operationsOrderHref } from "../routing/operations-routing";
import type { CsvImportController, CsvImportState } from "../state/csv-import-controller";
import { useCsvImport } from "../state/use-csv-import";
import { ScreenGate, TenantFeedback } from "./tenant-feedback";

export function CsvImportShell() {
  const { state, controller } = useCsvImport();
  return (
    <main className="opsShell" aria-busy={state.phase === "loading" || state.busy}>
      <header className="opsHeader">
        <div>
          <p className="opsEyebrow">Despacho</p>
          <h1>Importar órdenes por CSV</h1>
          <p>El servidor prevalida el archivo fila por fila; ninguna orden se crea hasta que confirmas el lote.</p>
        </div>
        <div className="opsHeaderStatus">
          <Link className="opsPrimary" href="/ops/dashboard">Volver a Operaciones</Link>
        </div>
      </header>

      <ScreenGate phase={state.phase} accessMessage="Tu rol en la organización activa no importa órdenes." />
      <TenantFeedback errors={state.errors} message={state.message} stepUpHref={state.stepUpHref} />

      {state.phase === "ready" && (
        <section className="opsFormLayout">
          <UploadForm key={`upload-${state.formKey}`} state={state} controller={controller} />
          <section>
            {state.commit !== null
              ? <CommitReport commit={state.commit} controller={controller} />
              : state.preview !== null && <PreviewReport preview={state.preview} state={state} controller={controller} />}
          </section>
        </section>
      )}
    </main>
  );
}

function UploadForm({
  state,
  controller,
}: {
  readonly state: CsvImportState;
  readonly controller: CsvImportController;
}) {
  return (
    <form className="opsForm" autoComplete="off" onSubmit={(event) => {
      event.preventDefault();
      void controller.preview();
    }}>
      <fieldset>
        <legend>Archivo</legend>
        <p>
          Formato CSV-001 en UTF-8 con encabezado <code>{csvHeader}</code>;
          hasta 500 filas y 1 MiB. Cada fila usa una cotización vigente de esta organización.
        </p>
        <p>
          Cobro contra entrega (opcional): agrega al final la columna <code>{csvCodColumn}</code> con el monto en
          centavos enteros, sin punto, comas ni signos (por ejemplo <code>15050</code> para $150.50). Deja la celda
          vacía si la orden no lleva cobro.
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
          <button className="opsPrimary" type="submit" disabled={state.busy || state.fileBytes === null}>
            {state.busy && state.commit === null ? "Procesando…" : "Previsualizar"}
          </button>
          <button className="opsSecondary" type="button" disabled={state.busy} onClick={() => controller.reset()}>
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
}: {
  readonly preview: CsvImportPreview;
  readonly state: CsvImportState;
  readonly controller: CsvImportController;
}) {
  const committable = isCommittable(preview);
  return (
    <>
      <h2>Prevalidación</h2>
      <p>{preview.total_rows} fila(s): {preview.valid_rows} válida(s), {preview.invalid_rows} con error.</p>
      {preview.file_errors.length > 0 && (
        <ul className="opsAlert" role="alert">
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
        <div className="opsFormActions">
          <button className="opsPrimary" type="button" disabled={state.busy} onClick={() => void controller.commit()}>
            {state.busy ? "Confirmando…" : `Confirmar lote de ${preview.valid_rows} orden(es)`}
          </button>
        </div>
      )}
    </>
  );
}

/** Server-reported integer cents only; the client never parses the COD cell. */
function codLabel(cents: number | null): string {
  if (cents === null) return "—";
  return cents === 0 ? "Sin cobro" : formatMxnCentsWithCurrency(cents);
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
      <button className="opsSecondary" type="button" onClick={() => controller.reset()}>Importar otro archivo</button>
    </>
  );
}
