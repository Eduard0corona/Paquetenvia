export interface PublicTrackingTelemetry {
  lookupCompleted(source: "rest"): void;
  lookupFailed(category: string): void;
  realtimeStateChanged(state: string): void;
  refreshTriggered(trigger: string): void;
}

export const noOpPublicTrackingTelemetry: PublicTrackingTelemetry = {
  lookupCompleted() {},
  lookupFailed() {},
  realtimeStateChanged() {},
  refreshTriggered() {},
};
