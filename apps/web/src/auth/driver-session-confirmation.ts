import {
  driverSessionCheckRequestedEvent,
  isConfirmedDriverSession,
  type DriverSession,
} from "../driver/session/driver-session";

/** Every this often, while a remembered session waits, the server is asked again (online and shown only). */
export const DriverSessionRecheckMilliseconds = 30_000;

export interface DriverSessionConfirmationHost {
  __paquetenviaDriverSession?: DriverSession;
  readonly navigator: Pick<Navigator, "onLine">;
  readonly document: Pick<Document, "visibilityState"> & {
    addEventListener(type: "visibilitychange", listener: () => void): void;
    removeEventListener(type: "visibilitychange", listener: () => void): void;
  };
  addEventListener(type: string, listener: () => void): void;
  removeEventListener(type: string, listener: () => void): void;
}

export interface DriverSessionConfirmationOptions {
  readonly host: DriverSessionConfirmationHost;
  /** The page's first session check; nothing runs before it ends. */
  readonly initial: Promise<unknown>;
  /** Asks `GET /auth/session` again (bootstrapBffSession with rememberDriver). */
  readonly check: () => Promise<unknown>;
  readonly retryMilliseconds?: number;
  readonly setTimer?: (callback: () => void, milliseconds: number) => unknown;
  readonly clearTimer?: (timer: unknown) => void;
}

/**
 * DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (owner answer E3 "Sí, y envía al validar (Recommended)":
 * "se envían cuando vuelve la señal y el servidor confirma la sesión"): on the driver pages, while
 * no confirmed driver session is installed, the session is checked again when the connection
 * returns (`online`), when the page is shown again, and when the stops screen asks
 * (driverSessionCheckRequestedEvent). While the installed session is only remembered it is also
 * checked every {@link DriverSessionRecheckMilliseconds}, online and shown. One check at a time;
 * a confirmed session stops all of it. Returns the function that stops watching.
 */
export function watchDriverSessionConfirmation(
  options: DriverSessionConfirmationOptions,
): () => void {
  const host = options.host;
  const retryMilliseconds = options.retryMilliseconds ?? DriverSessionRecheckMilliseconds;
  const setTimer =
    options.setTimer ?? ((callback, milliseconds) => globalThis.setTimeout(callback, milliseconds));
  const clearTimer =
    options.clearTimer ??
    ((timer) => globalThis.clearTimeout(timer as ReturnType<typeof setTimeout>));
  let stopped = false;
  let ready = false;
  let running: Promise<void> | null = null;
  let timer: unknown = null;

  const confirmed = () => isConfirmedDriverSession(host.__paquetenviaDriverSession ?? null);
  const remembered = () => host.__paquetenviaDriverSession?.remembered === true;
  const reachable = () =>
    host.navigator.onLine !== false && host.document.visibilityState !== "hidden";

  const cancelRetry = () => {
    if (timer !== null) {
      clearTimer(timer);
      timer = null;
    }
  };
  const scheduleRetry = () => {
    cancelRetry();
    if (stopped || !remembered() || !reachable()) return;
    timer = setTimer(() => {
      timer = null;
      void run();
    }, retryMilliseconds);
  };
  const run = (): Promise<void> => {
    if (stopped || !ready || confirmed() || !reachable()) return Promise.resolve();
    if (running) return running;
    cancelRetry();
    const pending = options
      .check()
      .catch(() => undefined)
      .then(() => {
        if (running === pending) running = null;
        scheduleRetry();
      });
    running = pending;
    return pending;
  };
  const onVisibility = () => {
    if (host.document.visibilityState === "hidden") cancelRetry();
    else void run();
  };
  const onRequest = () => void run();

  host.addEventListener("online", onRequest);
  host.addEventListener(driverSessionCheckRequestedEvent, onRequest);
  host.document.addEventListener("visibilitychange", onVisibility);
  void options.initial
    .catch(() => undefined)
    .then(() => {
      ready = true;
      scheduleRetry();
    });

  return () => {
    stopped = true;
    cancelRetry();
    host.removeEventListener("online", onRequest);
    host.removeEventListener(driverSessionCheckRequestedEvent, onRequest);
    host.document.removeEventListener("visibilitychange", onVisibility);
  };
}
