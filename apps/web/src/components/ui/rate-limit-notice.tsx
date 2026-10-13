"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import {
  rateLimitMessage,
  rateLimitWaits,
  type RateLimitedOperation,
  type RateLimitWaits,
} from "../../lib/rate-limit-wait";

/**
 * SEC-004 (owner answer Q6): the whole seconds left of the server's wait for any
 * of the operations, 0 when none waits. It re-renders every second while the
 * wait lasts, so the screen's buttons re-enable themselves when it ends.
 */
export function useRateLimitWait(
  operations: readonly RateLimitedOperation[],
  waits: RateLimitWaits = rateLimitWaits,
): number {
  useSyncExternalStore(
    (listener) => waits.subscribe(listener),
    () => waits.getVersion(),
    () => 0,
  );
  const [, setTick] = useState(0);
  const secondsLeft = waits.secondsLeft(operations);
  const waiting = secondsLeft > 0;
  useEffect(() => {
    if (!waiting) return;
    const timer = window.setInterval(() => setTick((tick) => tick + 1), 1_000);
    return () => window.clearInterval(timer);
  }, [waiting]);
  return secondsLeft;
}

/**
 * "Demasiadas solicitudes. Espera N segundos e intenta de nuevo." while the
 * server's wait for any of the operations lasts, counting down; nothing once it
 * ends. A timer role: assistive technology reads it when asked, without
 * announcing every second.
 */
export function RateLimitNotice({
  operations,
  waits = rateLimitWaits,
}: {
  readonly operations: readonly RateLimitedOperation[];
  readonly waits?: RateLimitWaits;
}) {
  const secondsLeft = useRateLimitWait(operations, waits);
  if (secondsLeft <= 0) return null;
  return (
    <p className="notice noticeWarn" role="timer">
      {rateLimitMessage(secondsLeft)}
    </p>
  );
}
