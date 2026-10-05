"use client";

import { useId, type ReactNode } from "react";

/** Props a Field hands to its control so the label, hint and error stay linked. */
export interface FieldControlProps {
  readonly id: string;
  readonly "aria-describedby"?: string;
  readonly "aria-invalid"?: true;
}

/**
 * Label + control + optional hint + optional error. The hint and the error are linked to
 * the control through aria-describedby; an error also sets aria-invalid.
 */
export function Field({
  label,
  hint,
  error,
  children,
}: {
  readonly label: ReactNode;
  readonly hint?: ReactNode;
  readonly error?: ReactNode;
  readonly children: (control: FieldControlProps) => ReactNode;
}) {
  const id = useId();
  const hintId = hint === undefined ? undefined : `${id}-hint`;
  const errorId = error === undefined || error === null ? undefined : `${id}-error`;
  const describedBy = [hintId, errorId].filter((value) => value !== undefined).join(" ");
  return (
    <div className="field">
      <label htmlFor={id} className="fieldLabel">{label}</label>
      {children({
        id,
        ...(describedBy === "" ? {} : { "aria-describedby": describedBy }),
        ...(errorId === undefined ? {} : { "aria-invalid": true as const }),
      })}
      {hintId !== undefined && <p id={hintId} className="fieldHint">{hint}</p>}
      {errorId !== undefined && <p id={errorId} className="fieldError">{error}</p>}
    </div>
  );
}
