export interface DriverStopsTelemetry {
  loadCompleted(source: "rest" | "offline", countBucket: string): void;
  loadFailed(category: string): void;
  realtimeStateChanged(state: string): void;
  /**
   * UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: stops of an answer that broke the
   * contract, left out ("dropped") or kept without their details ("degraded").
   * Only a count bucket is reported, never a value of the stop.
   */
  stopsRejected?(outcome: "dropped" | "degraded", countBucket: string): void;
}

export const disabledDriverStopsTelemetry: DriverStopsTelemetry = Object.freeze({
  loadCompleted: () => undefined,
  loadFailed: () => undefined,
  realtimeStateChanged: () => undefined,
  stopsRejected: () => undefined,
});

export function driverStopCountBucket(count: number): string {
  if (count <= 0) return "0";
  if (count === 1) return "1";
  if (count <= 5) return "2-5";
  if (count <= 20) return "6-20";
  return "21+";
}
