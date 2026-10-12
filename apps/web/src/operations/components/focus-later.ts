/** The title of the current "Nueva orden" step (UI-PHASE3-ORDER-WIZARD-2026-10-10). */
export const orderStepTitleId = "order-step-title";

/**
 * Moves focus once React has rendered what the last action changed: the first element found
 * among `ids`. Nothing happens when none of them is on the page.
 */
export function focusLater(...ids: readonly string[]): void {
  window.setTimeout(() => {
    for (const id of ids) {
      const element = document.getElementById(id);
      if (element !== null) {
        element.focus();
        return;
      }
    }
  }, 0);
}
