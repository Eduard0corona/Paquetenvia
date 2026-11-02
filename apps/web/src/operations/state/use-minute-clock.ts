"use client";

import { useSyncExternalStore } from "react";

/**
 * DRV-DOC-001-IN-PERSON-2026-11-02: the device clock, read again every minute, for the document warnings of
 * "Repartidores" («Vence en N días», «Vencido», Y6) so they move on while the screen stays open. The instant is
 * rounded down to the minute so every read within a minute agrees.
 */
const tickMilliseconds = 60_000;

function subscribe(onChange: () => void): () => void {
  const timer = setInterval(onChange, tickMilliseconds);
  return () => clearInterval(timer);
}

function snapshot(): number {
  return Math.floor(Date.now() / tickMilliseconds) * tickMilliseconds;
}

export function useMinuteClock(): number {
  return useSyncExternalStore(subscribe, snapshot, snapshot);
}
