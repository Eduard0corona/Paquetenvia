export interface DriverStopsTelemetry {
  loadCompleted(source: "rest" | "offline", countBucket: string): void;
  loadFailed(category: string): void;
  realtimeStateChanged(state: string): void;
}

export const disabledDriverStopsTelemetry: DriverStopsTelemetry = Object.freeze({
  loadCompleted: () => undefined,
  loadFailed: () => undefined,
  realtimeStateChanged: () => undefined,
});

export function driverStopCountBucket(count: number): string {
  if (count <= 0) return "0";
  if (count === 1) return "1";
  if (count <= 5) return "2-5";
  if (count <= 20) return "6-20";
  return "21+";
}
