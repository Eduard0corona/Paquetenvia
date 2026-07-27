export interface OperationsDashboardTelemetry {
  lookupCompleted(source: "rest"): void;
  lookupFailed(category: string): void;
  realtimeStateChanged(state: string): void;
  refreshTriggered(trigger: string): void;
  filterChanged(filter: string): void;
}

export const noOpOperationsTelemetry: OperationsDashboardTelemetry = {
  lookupCompleted: () => undefined,
  lookupFailed: () => undefined,
  realtimeStateChanged: () => undefined,
  refreshTriggered: () => undefined,
  filterChanged: () => undefined,
};
