"use client";

import { useCallback, useSyncExternalStore } from "react";
import {
  rateLimitMessage,
  rateLimitWaits,
  watchWait,
  type RateLimitedOperation,
  type RateLimitWaits,
} from "../../lib/rate-limit-wait";

export interface RateLimitWaitView {
  /** Whole seconds left of the server's wait for any of the operations; 0 when none waits. */
  readonly secondsLeft: number;
}

/**
 * SEC-004 (owner answers Q6, Q11, Q14): the server's wait for any of the operations, re-rendered once per second
 * while it lasts, so the countdown ticks and the buttons that read `secondsLeft > 0` re-enable themselves when it
 * ends. Pass a module-level constant as `operations`. Its one timer stops when the wait ends or on unmount.
 */
export function useRateLimitWait(
  operations: readonly RateLimitedOperation[],
  waits: RateLimitWaits = rateLimitWaits,
): RateLimitWaitView {
  const subscribe = useCallback(
    (listener: () => void) => watchWait(waits, () => waits.millisecondsLeft(operations), true, listener),
    [operations, waits],
  );
  const secondsLeft = useSyncExternalStore(
    subscribe,
    () => waits.secondsLeft(operations),
    () => 0,
  );
  return { secondsLeft };
}

/**
 * Owner answers Q6 and Q12: "Demasiadas solicitudes. Espera N segundos e intenta de nuevo." ("1 segundo" at one)
 * while the server's wait lasts, counting down; nothing once it ends. A `timer`: assistive technology reads it when
 * asked but never announces it by itself, so the ticking seconds are not read out. The message is announced once,
 * by the shell's {@link RateLimitAnnouncer}.
 */
export function RateLimitNoticeView({
  secondsLeft,
  className = "notice noticeWarn",
  id,
}: RateLimitWaitView & { readonly className?: string; readonly id?: string }) {
  if (secondsLeft <= 0) return null;
  return (
    <p className={className} id={id} role="timer">
      {rateLimitMessage(secondsLeft)}
    </p>
  );
}

/** The common message of the server's wait for the operations, counting down. */
export function RateLimitNotice({
  operations,
  waits,
  className,
  id,
}: {
  readonly operations: readonly RateLimitedOperation[];
  readonly waits?: RateLimitWaits;
  readonly className?: string;
  readonly id?: string;
}) {
  const view = useRateLimitWait(operations, waits);
  return <RateLimitNoticeView {...view} className={className} id={id} />;
}

/** What the announcer says: the message with the seconds the newest 429 asked for, or nothing between waits. */
export function rateLimitAnnouncement(waits: RateLimitWaits): string {
  const newest = waits.newest();
  return newest === null ? "" : rateLimitMessage(newest.seconds);
}

/**
 * The one polite announcement of every wait in the tab (accessibility rule of the task: announced once, not every
 * second). It lives in the application shell, so a page that shows the countdown in two places (the top-bar search
 * and "Órdenes", both listOrders) still says it once. Its region is always present, so the text that appears in it
 * is read; the text does not change while the wait lasts, and it empties when the wait ends, so the next wait is
 * announced again.
 */
export function RateLimitAnnouncer({ waits = rateLimitWaits }: { readonly waits?: RateLimitWaits }) {
  const subscribe = useCallback(
    (listener: () => void) =>
      watchWait(
        waits,
        () => {
          const newest = waits.newest();
          return newest === null ? 0 : Math.max(0, newest.until - waits.now());
        },
        false,
        listener,
      ),
    [waits],
  );
  const text = useSyncExternalStore(subscribe, () => rateLimitAnnouncement(waits), () => "");
  return (
    <p className="srOnly" role="status" aria-live="polite">
      {text}
    </p>
  );
}
