"use client";

import { useCallback, useEffect, useId, useRef, useState, type ReactNode } from "react";

/** What a risky action asks the person to confirm before it runs. */
export interface ConfirmRequest {
  readonly title: string;
  readonly description: string;
  readonly confirmLabel: string;
  readonly cancelLabel?: string;
  readonly onConfirm: () => void;
}

/**
 * In-page confirmation built on the native modal `<dialog>` (showModal): the browser keeps
 * focus inside it and Escape closes it. Focus starts on "Cancelar" so Enter never confirms
 * by accident, and returns to the control that opened it.
 */
export function ConfirmDialog({
  request,
  onDismiss,
}: {
  readonly request: ConfirmRequest | null;
  readonly onDismiss: () => void;
}) {
  const dialogRef = useRef<HTMLDialogElement>(null);
  const cancelRef = useRef<HTMLButtonElement>(null);
  const returnFocusRef = useRef<HTMLElement | null>(null);
  const titleId = useId();
  const descriptionId = useId();
  const open = request !== null;

  useEffect(() => {
    const dialog = dialogRef.current;
    if (dialog === null) return;
    if (open && !dialog.open) {
      const active = document.activeElement;
      returnFocusRef.current = active instanceof HTMLElement ? active : null;
      dialog.showModal();
      cancelRef.current?.focus();
    } else if (!open && dialog.open) {
      dialog.close();
    }
  }, [open]);

  const restoreFocus = useCallback(() => {
    const target = returnFocusRef.current;
    returnFocusRef.current = null;
    if (target !== null && target.isConnected) target.focus();
  }, []);

  const finish = useCallback(
    (confirmed: boolean) => {
      const action = request?.onConfirm;
      dialogRef.current?.close();
      restoreFocus();
      onDismiss();
      if (confirmed && action !== undefined) action();
    },
    [request, onDismiss, restoreFocus],
  );

  return (
    <dialog
      ref={dialogRef}
      className="confirmDialog"
      aria-labelledby={titleId}
      aria-describedby={descriptionId}
      onClose={() => {
        // Escape (native "cancel") ends here too.
        restoreFocus();
        onDismiss();
      }}
    >
      {request !== null && (
        <>
          <h2 id={titleId}>{request.title}</h2>
          <p id={descriptionId}>{request.description}</p>
          <div className="confirmDialogActions">
            <button ref={cancelRef} type="button" className="opsSecondary" onClick={() => finish(false)}>
              {request.cancelLabel ?? "Cancelar"}
            </button>
            <button type="button" className="opsPrimary" onClick={() => finish(true)}>
              {request.confirmLabel}
            </button>
          </div>
        </>
      )}
    </dialog>
  );
}

/** One confirmation slot per screen: `confirm(request)` opens it, `dialog` renders it. */
export function useConfirmDialog(): {
  readonly confirm: (request: ConfirmRequest) => void;
  readonly dialog: ReactNode;
} {
  const [request, setRequest] = useState<ConfirmRequest | null>(null);
  const dismiss = useCallback(() => setRequest(null), []);
  return {
    confirm: setRequest,
    dialog: <ConfirmDialog request={request} onDismiss={dismiss} />,
  };
}
