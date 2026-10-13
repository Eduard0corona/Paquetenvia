"use client";

import {
  useEffect,
  useId,
  useRef,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from "react";
import {
  useConfirmDialog,
  type ConfirmRequest,
} from "../../components/confirm-dialog";
import {
  driverFailedDeliveryReasonLabel,
  driverFailedDeliveryReasonsFor,
  type DriverFailedDeliveryReason,
} from "../contracts/failed-delivery";
import {
  driverDispatchRoleReportMessage,
  driverFailedDeliveryAvailability,
  driverRoleUnknownReportMessage,
  type DriverFailedDeliveryAvailability,
} from "../offline/failed-delivery-policy";
import type { DriverOfflineOperation } from "../offline/operation-contract";
import type { ProjectedDriverStop } from "../offline/operation-projection";
import type { DriverReportRole } from "../contracts/organization-roles";
import {
  driverMessageCallsDispatch,
  type DriverFailedDeliveryEnqueueResult,
} from "../state/driver-operations-controller";
import {
  DriverProofPreview,
  driverProofPreviewProblemMessage,
} from "../proofs/proof-preview";
import { DriverDispatchCall } from "./driver-dispatch-call";
import styles from "./driver-stops.module.css";

/**
 * UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: "No se pudo entregar" on a
 * delivery stop at the door. The driver picks a reason from the closed list
 * ("¿Qué pasó?", "Otro…" reveals three more) and takes exactly one photo
 * ("Foto del lugar (obligatoria)"); "Enviar reporte" queues one offline
 * operation and dispatch decides what follows. There is no text field: the
 * server builds the description and the severity from the reason.
 */

/** Copy of the approved design. */
export const failedDeliveryCopy = Object.freeze({
  open: "No se pudo entregar",
  question: "¿Qué pasó?",
  questionHint: "El despacho decidirá si se reprograma o regresa",
  other: "Otro…",
  otherLegend: "Otro motivo",
  photo: "Foto del lugar (obligatoria)",
  photoHint: "Una sola foto JPEG o PNG, máximo 10 MiB.",
  send: "Enviar reporte",
  sendHint: "Elige qué pasó y toma la foto del lugar para enviar el reporte.",
  cancel: "Cancelar",
  photoChosen: "Foto lista para el reporte.",
  notSaved: "No se pudo guardar el reporte en el teléfono. Vuelve a intentarlo.",
  queued: "Reporte guardado en el teléfono. Se enviará cuando haya conexión.",
  codSaved:
    "Hay un cobro guardado en el teléfono. Si fue un error, descártalo para poder reportar que no se pudo entregar.",
  codRecorded:
    "El cobro ya está registrado. Si no puedes entregar, llama a despacho: ellos deciden y coordinan la devolución del cobro.",
  discardCollection: "Descartar cobro guardado",
  dispatchRole: driverDispatchRoleReportMessage,
  roleUnknown: driverRoleUnknownReportMessage,
});

/**
 * The confirmation of the existing discard path ("Descartar" in the stop's
 * attention notice), used as it is for a saved collection.
 */
export const discardOperationConfirmation: Omit<ConfirmRequest, "onConfirm"> =
  Object.freeze({
    title: "Confirma la acción",
    description:
      "Se eliminarán esta operación y su evidencia local del dispositivo.",
    confirmLabel: "Confirmar",
  });

/** "Otro…" as the choice of the first radio group. */
export const OtherReasonChoice = "OTHER" as const;
export type FailedDeliveryPrimaryChoice =
  | DriverFailedDeliveryReason
  | typeof OtherReasonChoice
  | null;

/** The reason a choice reports, or null while it is incomplete. */
export function chosenFailedDeliveryReason(
  primary: FailedDeliveryPrimaryChoice,
  other: DriverFailedDeliveryReason | null,
): DriverFailedDeliveryReason | null {
  return primary === OtherReasonChoice ? other : primary;
}

export function DriverFailedDelivery({
  stop,
  operations,
  reportRole,
  dispatchPhone,
  disabled,
  onReport,
  onDiscardCollection,
}: Readonly<{
  stop: ProjectedDriverStop;
  /** The projected operations of the stop's order. */
  operations: readonly DriverOfflineOperation[];
  /** driverReportRole of the roles getMyOrganizationRoles answered (ORG-MY-ROLES-2026-10-12). */
  reportRole: DriverReportRole;
  /** The organization's dispatch number getMyDispatchContact answered, or null (DRV-DISPATCH-CONTACT-2026-10-12). */
  dispatchPhone: string | null;
  disabled: boolean;
  onReport: (
    reason: DriverFailedDeliveryReason,
    photo: Blob,
  ) => Promise<DriverFailedDeliveryEnqueueResult>;
  onDiscardCollection: (operationId: string) => void;
}>) {
  const availability = driverFailedDeliveryAvailability(
    stop,
    operations,
    reportRole,
  );
  return (
    <FailedDeliverySection
      availability={availability}
      orderId={stop.order_id}
      dispatchPhone={dispatchPhone}
      disabled={disabled}
      onReport={onReport}
      onDiscardCollection={onDiscardCollection}
    />
  );
}

/** What the stop shows for each availability (see driverFailedDeliveryAvailability). */
export function FailedDeliverySection({
  availability,
  orderId,
  dispatchPhone = null,
  disabled,
  onReport,
  onDiscardCollection,
}: Readonly<{
  availability: DriverFailedDeliveryAvailability;
  orderId: string;
  /**
   * DRV-DISPATCH-CONTACT-2026-10-12: with a number, "Llamar a despacho" goes
   * next to every text here that asks to call dispatch; null keeps the text
   * alone.
   */
  dispatchPhone?: string | null;
  disabled: boolean;
  onReport: (
    reason: DriverFailedDeliveryReason,
    photo: Blob,
  ) => Promise<DriverFailedDeliveryEnqueueResult>;
  onDiscardCollection: (operationId: string) => void;
}>) {
  switch (availability.kind) {
    case "hidden":
    case "dispatch-decides":
      // A confirmed failed attempt reads "Intento fallido · El despacho
      // decide" in the stop's confirmed status.
      return null;
    case "queued":
      return (
        <section className={styles.failedDelivery} aria-label={failedDeliveryCopy.open}>
          <p className={styles.failedDeliveryNote} role="status">
            {failedDeliveryCopy.queued}
          </p>
        </section>
      );
    case "attention":
      // The report's own notice explains why it stopped; here only the
      // collection that holds it may be discarded.
      return availability.discardable ? (
        <SavedCollectionNotice
          collectionId={availability.discardable.id}
          disabled={disabled}
          onDiscard={onDiscardCollection}
        />
      ) : null;
    case "cod-saved":
      return (
        <SavedCollectionNotice
          collectionId={availability.discardable?.id ?? null}
          disabled={disabled}
          onDiscard={onDiscardCollection}
        />
      );
    case "cod-recorded":
      return (
        <section className={styles.failedDelivery} aria-label={failedDeliveryCopy.open}>
          <p className={styles.failedDeliveryNote}>{failedDeliveryCopy.codRecorded}</p>
          <DriverDispatchCall phoneE164={dispatchPhone} />
        </section>
      );
    case "dispatch-role":
      // Owner answer "Registrar desde Despacho (Recommended)" (2026-10-12).
      return (
        <section className={styles.failedDelivery} aria-label={failedDeliveryCopy.open}>
          <p className={styles.failedDeliveryNote}>{failedDeliveryCopy.dispatchRole}</p>
        </section>
      );
    case "role-unknown":
      // ORG-MY-ROLES-2026-10-12: the roles are unknown, so the report fails closed.
      return (
        <section className={styles.failedDelivery} aria-label={failedDeliveryCopy.open}>
          <p className={styles.failedDeliveryNote}>{failedDeliveryCopy.roleUnknown}</p>
        </section>
      );
    case "offered":
      return (
        <FailedDeliveryReport
          key={orderId}
          orderId={orderId}
          paymentReason={availability.paymentReason}
          dispatchPhone={dispatchPhone}
          disabled={disabled}
          onReport={onReport}
        />
      );
  }
}

/**
 * Owner answer "No, llama a despacho (Recommended)": with a collection saved
 * on the phone the report is not offered; a collection saved by mistake is
 * discarded first, with the same confirmation as every discard.
 */
function SavedCollectionNotice({
  collectionId,
  disabled,
  onDiscard,
}: Readonly<{
  collectionId: string | null;
  disabled: boolean;
  onDiscard: (operationId: string) => void;
}>) {
  const { confirm, dialog } = useConfirmDialog();
  return (
    <section className={styles.failedDelivery} aria-label={failedDeliveryCopy.open}>
      <p className={styles.failedDeliveryNote}>{failedDeliveryCopy.codSaved}</p>
      {collectionId !== null ? (
        <button
          type="button"
          className={styles.secondaryAction}
          disabled={disabled}
          onClick={() =>
            confirm({
              ...discardOperationConfirmation,
              onConfirm: () => onDiscard(collectionId),
            })
          }
        >
          {failedDeliveryCopy.discardCollection}
        </button>
      ) : null}
      {dialog}
    </section>
  );
}

/** The closed "No se pudo entregar" form behind its button. */
function FailedDeliveryReport({
  orderId,
  paymentReason,
  dispatchPhone,
  disabled,
  onReport,
}: Readonly<{
  orderId: string;
  paymentReason: boolean;
  dispatchPhone: string | null;
  disabled: boolean;
  onReport: (
    reason: DriverFailedDeliveryReason,
    photo: Blob,
  ) => Promise<DriverFailedDeliveryEnqueueResult>;
}>) {
  const [open, setOpen] = useState(false);
  const [primary, setPrimary] = useState<FailedDeliveryPrimaryChoice>(null);
  const [other, setOther] = useState<DriverFailedDeliveryReason | null>(null);
  const [photo, setPhoto] = useState<Blob | null>(null);
  const [sending, setSending] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const toggleRef = useRef<HTMLButtonElement>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const wasOpen = useRef(open);

  // Focus follows the form: into it when it opens, back to the button when it closes.
  useEffect(() => {
    if (wasOpen.current === open) return;
    wasOpen.current = open;
    if (open) headingRef.current?.focus();
    else toggleRef.current?.focus();
  }, [open]);

  const reset = () => {
    setOpen(false);
    setPrimary(null);
    setOther(null);
    setPhoto(null);
    setProblem(null);
  };
  const reason = chosenFailedDeliveryReason(primary, other);
  const send = async () => {
    if (reason === null || photo === null || sending) return;
    setSending(true);
    setProblem(null);
    let result: DriverFailedDeliveryEnqueueResult;
    try {
      result = await onReport(reason, photo);
    } catch {
      result = { stored: false, message: failedDeliveryCopy.notSaved };
    }
    setSending(false);
    // A refusal says why (for example the cash is already recorded); "try
    // again" only when saving itself failed.
    if (result.stored) reset();
    else setProblem(result.message);
  };

  if (!open) {
    return (
      <section className={styles.failedDelivery} aria-label={failedDeliveryCopy.open}>
        <button
          ref={toggleRef}
          type="button"
          className={styles.failedDeliveryOpen}
          disabled={disabled}
          onClick={() => setOpen(true)}
        >
          {failedDeliveryCopy.open}
        </button>
      </section>
    );
  }

  return (
    <FailedDeliveryForm
      orderId={orderId}
      reasons={driverFailedDeliveryReasonsFor(paymentReason)}
      primary={primary}
      other={other}
      photoChosen={photo !== null}
      disabled={disabled || sending}
      problem={problem}
      dispatchPhone={dispatchPhone}
      headingRef={headingRef}
      onChoosePrimary={(value) => {
        setPrimary(value);
        if (value !== OtherReasonChoice) setOther(null);
      }}
      onChooseOther={setOther}
      onSend={() => void send()}
      onCancel={reset}
      photoStep={
        <FailedDeliveryPhoto
          orderId={orderId}
          disabled={disabled || sending}
          onChoose={(value) => {
            setProblem(null);
            setPhoto(value);
          }}
          onForget={() => setPhoto(null)}
        />
      }
    />
  );
}

/**
 * The open form, without state: "¿Qué pasó?" with its reasons as 44 px radio
 * targets, "Otro…" revealing three more, the photo step and "Enviar reporte",
 * disabled until a reason and the photo are chosen.
 */
export function FailedDeliveryForm({
  orderId,
  reasons,
  primary,
  other,
  photoChosen,
  disabled,
  problem,
  dispatchPhone = null,
  headingRef,
  onChoosePrimary,
  onChooseOther,
  onSend,
  onCancel,
  photoStep,
}: Readonly<{
  orderId: string;
  reasons: ReturnType<typeof driverFailedDeliveryReasonsFor>;
  primary: FailedDeliveryPrimaryChoice;
  other: DriverFailedDeliveryReason | null;
  photoChosen: boolean;
  disabled: boolean;
  problem: string | null;
  /** DRV-DISPATCH-CONTACT-2026-10-12: "Llamar a despacho" next to a refusal that asks to call dispatch. */
  dispatchPhone?: string | null;
  headingRef?: React.RefObject<HTMLHeadingElement | null>;
  onChoosePrimary: (value: DriverFailedDeliveryReason | typeof OtherReasonChoice) => void;
  onChooseOther: (value: DriverFailedDeliveryReason) => void;
  onSend: () => void;
  onCancel: () => void;
  photoStep: ReactNode;
}>) {
  const headingId = `failed-delivery-heading-${orderId}`;
  const hintId = `failed-delivery-hint-${orderId}`;
  const sendHintId = `failed-delivery-send-hint-${orderId}`;
  const otherId = `failed-delivery-other-${orderId}`;
  const ready =
    chosenFailedDeliveryReason(primary, other) !== null && photoChosen;
  return (
    <section
      className={styles.failedDelivery}
      aria-labelledby={headingId}
    >
      <h3 id={headingId} ref={headingRef} tabIndex={-1}>
        {failedDeliveryCopy.open}
      </h3>
      <fieldset
        className={styles.reasonGroup}
        aria-describedby={hintId}
        disabled={disabled}
      >
        <legend>{failedDeliveryCopy.question}</legend>
        <p id={hintId} className={styles.reasonHint}>
          {failedDeliveryCopy.questionHint}
        </p>
        {reasons.primary.map((reason) => (
          <ReasonOption
            key={reason}
            name={`failed-delivery-reason-${orderId}`}
            value={reason}
            label={driverFailedDeliveryReasonLabel(reason)}
            checked={primary === reason}
            onChange={() => onChoosePrimary(reason)}
          />
        ))}
        <ReasonOption
          name={`failed-delivery-reason-${orderId}`}
          value={OtherReasonChoice}
          label={failedDeliveryCopy.other}
          checked={primary === OtherReasonChoice}
          controls={otherId}
          onChange={() => onChoosePrimary(OtherReasonChoice)}
        />
        {primary === OtherReasonChoice ? (
          <fieldset id={otherId} className={styles.otherReasons}>
            <legend>{failedDeliveryCopy.otherLegend}</legend>
            {reasons.other.map((reason) => (
              <ReasonOption
                key={reason}
                name={`failed-delivery-other-${orderId}`}
                value={reason}
                label={driverFailedDeliveryReasonLabel(reason)}
                checked={other === reason}
                onChange={() => onChooseOther(reason)}
              />
            ))}
          </fieldset>
        ) : null}
      </fieldset>
      {photoStep}
      <div className={styles.failedDeliveryActions}>
        <button
          type="button"
          disabled={disabled || !ready}
          aria-describedby={ready ? undefined : sendHintId}
          onClick={onSend}
        >
          {failedDeliveryCopy.send}
        </button>
        <button
          type="button"
          className={styles.retakeButton}
          disabled={disabled}
          onClick={onCancel}
        >
          {failedDeliveryCopy.cancel}
        </button>
      </div>
      {ready ? null : (
        <p id={sendHintId} className={styles.reasonHint}>
          {failedDeliveryCopy.sendHint}
        </p>
      )}
      {problem ? (
        <p className={styles.proofProblem} role="alert">
          {problem}
        </p>
      ) : null}
      {driverMessageCallsDispatch(problem) ? (
        <DriverDispatchCall phoneE164={dispatchPhone} />
      ) : null}
    </section>
  );
}

function ReasonOption({
  name,
  value,
  label,
  checked,
  controls,
  onChange,
}: Readonly<{
  name: string;
  value: string;
  label: string;
  checked: boolean;
  controls?: string;
  onChange: () => void;
}>) {
  const id = useId();
  return (
    <label className={styles.reasonOption} htmlFor={id}>
      <input
        id={id}
        type="radio"
        name={name}
        value={value}
        checked={checked}
        aria-controls={controls}
        onChange={onChange}
      />
      <span>{label}</span>
    </label>
  );
}

/**
 * "Foto del lugar (obligatoria)": exactly one photo, shown in "Vista previa de
 * la foto" from an in-memory object URL before it is used, as the delivery
 * photo is. "Usar esta foto" hands the Blob to the form (nothing is stored
 * until "Enviar reporte"); "Repetir" drops it. The object URL is revoked on
 * "Repetir", on "Usar esta foto" and when the step unmounts.
 */
function FailedDeliveryPhoto({
  orderId,
  disabled,
  onChoose,
  onForget,
}: Readonly<{
  orderId: string;
  disabled: boolean;
  onChoose: (photo: Blob) => void;
  onForget: () => void;
}>) {
  const [preview] = useState(() => new DriverProofPreview());
  const state = useSyncExternalStore(
    preview.subscribe,
    preview.getSnapshot,
    preview.getSnapshot,
  );
  const inputRef = useRef<HTMLInputElement>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const shownPhase = useRef(state.phase);
  const [unrenderableUrl, setUnrenderableUrl] = useState<string | null>(null);

  // Unmount (sent, cancelled or the stop changed): revoke the URL and forget the photo.
  useEffect(() => () => preview.release(), [preview]);

  useEffect(() => {
    if (shownPhase.current === state.phase) return;
    shownPhase.current = state.phase;
    if (state.phase === "preview") headingRef.current?.focus();
    else if (state.phase === "capture") inputRef.current?.focus();
  }, [state.phase]);

  const inputId = `failed-delivery-photo-${orderId}`;
  const hintId = `failed-delivery-photo-hint-${orderId}`;
  const problemId = `failed-delivery-photo-error-${orderId}`;
  const headingId = `failed-delivery-preview-heading-${orderId}`;
  const problem =
    state.phase === "capture" || state.phase === "preview"
      ? state.problem
      : null;

  const reopenCamera = () => {
    const input = inputRef.current;
    if (!input) return;
    input.value = "";
    input.click();
  };

  return (
    <div className={styles.proofStep}>
      <div className={styles.proofCapture} hidden={state.phase !== "capture"}>
        <label htmlFor={inputId}>{failedDeliveryCopy.photo}</label>
        <p id={hintId} className={styles.reasonHint}>
          {failedDeliveryCopy.photoHint}
        </p>
        <input
          ref={inputRef}
          id={inputId}
          type="file"
          accept="image/jpeg,image/png"
          capture="environment"
          required
          disabled={disabled}
          aria-describedby={problem ? `${hintId} ${problemId}` : hintId}
          aria-invalid={state.phase === "capture" && problem ? true : undefined}
          onChange={(event) => preview.select(event.currentTarget.files)}
        />
      </div>
      {state.phase === "preview" ? (
        <section className={styles.proofPreview} aria-labelledby={headingId}>
          <h3 id={headingId} ref={headingRef} tabIndex={-1}>
            Vista previa de la foto
          </h3>
          {unrenderableUrl === state.objectUrl ? (
            <p>No se puede mostrar la vista previa de esta foto.</p>
          ) : (
            // eslint-disable-next-line @next/next/no-img-element -- a local blob: URL that next/image cannot load
            <img
              className={styles.proofImage}
              src={state.objectUrl}
              alt="Foto del lugar tomada"
              onError={() => setUnrenderableUrl(state.objectUrl)}
            />
          )}
          <div className={styles.proofPreviewActions}>
            <button
              type="button"
              className={styles.retakeButton}
              disabled={disabled}
              onClick={() => {
                preview.retake();
                reopenCamera();
              }}
            >
              Repetir
            </button>
            <button
              type="button"
              disabled={disabled}
              onClick={() =>
                void preview.use(async (photo) => {
                  onChoose(photo);
                  return true;
                })
              }
            >
              Usar esta foto
            </button>
          </div>
        </section>
      ) : null}
      {state.phase === "queued" ? (
        <div className={styles.proofPreviewActions}>
          <button
            type="button"
            className={styles.retakeButton}
            disabled={disabled}
            onClick={() => {
              preview.release();
              onForget();
              reopenCamera();
            }}
          >
            Repetir
          </button>
        </div>
      ) : null}
      {problem ? (
        <p id={problemId} className={styles.proofProblem} role="alert">
          {driverProofPreviewProblemMessage(problem)}
        </p>
      ) : null}
      <p className={styles.liveMessage} aria-live="polite">
        {state.phase === "preview" && state.problem === null
          ? "Revisa la foto: elige «Usar esta foto» o «Repetir»."
          : state.phase === "queued"
            ? failedDeliveryCopy.photoChosen
            : ""}
      </p>
    </div>
  );
}
