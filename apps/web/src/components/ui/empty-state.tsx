import type { ReactNode } from "react";

/** What a list or panel shows when there is nothing in it yet. */
export function EmptyState({
  title,
  children,
}: {
  readonly title?: ReactNode;
  readonly children?: ReactNode;
}) {
  return (
    <div className="emptyState">
      {title !== undefined && <strong>{title}</strong>}
      {children}
    </div>
  );
}
