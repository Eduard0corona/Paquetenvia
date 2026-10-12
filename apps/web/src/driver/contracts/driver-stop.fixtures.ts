import type {
  DriverStop,
  DriverStopList,
  DriverStopNavigation,
} from "./driver-stop";

/** Synthetic navigation target for tests: no real address or person. */
export const syntheticNavigation: DriverStopNavigation = Object.freeze({
  latitude: 24.8,
  longitude: -107.39,
  address_text: "Calle Sintética 100, Colonia de Prueba",
  references: null,
});

type StopDetails =
  | "details_available"
  | "contact_short_name"
  | "contact_full_name"
  | "zone_name"
  | "service_window"
  | "cod"
  | "navigation";

/**
 * A DRV-001 style stop completed with the UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11
 * members (details available, no contact, zone, window or cash, synthetic
 * navigation), unless the test overrides them.
 */
export function withStopDetails(
  stop: Omit<DriverStop, StopDetails> & Partial<Pick<DriverStop, StopDetails>>,
): DriverStop {
  return {
    details_available: true,
    contact_short_name: null,
    contact_full_name: null,
    zone_name: null,
    service_window: null,
    cod: null,
    navigation: syntheticNavigation,
    ...stop,
  };
}

/** The same stop as a read without its details (`details=false`) returns it. */
export function withoutStopDetails(stop: DriverStop): DriverStop {
  return {
    ...stop,
    details_available: false,
    contact_short_name: null,
    contact_full_name: null,
    navigation: null,
  };
}

/** A listMyStops answer as the app reads it, with every stop accepted. */
export function stopList(
  stops: readonly DriverStop[],
  completedToday = 0,
): DriverStopList {
  return Object.freeze({
    stops,
    completedToday,
    droppedCount: 0,
    degradedCount: 0,
  });
}
