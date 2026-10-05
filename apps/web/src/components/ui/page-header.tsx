import type { ReactNode } from "react";

/**
 * The header every application screen starts with: optional eyebrow, the page title (the
 * only h1 of the page), an optional description and an actions slot for the page's own
 * primary action and status. Navigation between screens belongs to the app shell.
 */
export function PageHeader({
  eyebrow,
  title,
  description,
  actions,
  live = false,
}: {
  readonly eyebrow?: ReactNode;
  readonly title: ReactNode;
  readonly description?: ReactNode;
  readonly actions?: ReactNode;
  /** Announce changes of the actions slot (connection and refresh status). */
  readonly live?: boolean;
}) {
  return (
    <header className="pageHeader">
      <div className="pageHeaderText">
        {eyebrow !== undefined && <p className="pageEyebrow">{eyebrow}</p>}
        <h1>{title}</h1>
        {description !== undefined && <div className="pageDescription">{description}</div>}
      </div>
      {actions !== undefined && (
        <div className="pageActions" aria-live={live ? "polite" : undefined}>
          {actions}
        </div>
      )}
    </header>
  );
}
