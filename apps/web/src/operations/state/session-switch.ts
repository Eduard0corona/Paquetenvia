/**
 * Z15 ("Ambos, panel fijo (Recommended)"): on a session or organization change a screen drops everything it showed for
 * the previous organization at once, and only then closes the previous realtime connection. Closing it can take a
 * network round trip (or wait for a connection still being negotiated); meanwhile nothing of the previous organization
 * stays on screen, and nothing the screen holds for it is sent under the new organization.
 */
export async function clearThenStopPrevious(clear: () => void, stopPrevious: () => Promise<void>): Promise<void> {
  clear();
  await stopPrevious();
}
