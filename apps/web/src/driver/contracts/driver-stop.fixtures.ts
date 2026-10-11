import type { DriverStop, DriverStopNavigation } from "./driver-stop";

/** Synthetic navigation target for tests: no real address or person. */
export const syntheticNavigation: DriverStopNavigation = Object.freeze({
  latitude: 24.8,
  longitude: -107.39,
  address_text: "Calle Sintética 100, Colonia de Prueba",
  references: null,
});

type StopDetails =
  | "contact_short_name"
  | "contact_full_name"
  | "zone_name"
  | "service_window"
  | "cod"
  | "navigation";

/**
 * A DRV-001 style stop completed with the UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11
 * members (no contact, zone, window or cash, synthetic navigation), unless the
 * test overrides them.
 */
export function withStopDetails(
  stop: Omit<DriverStop, StopDetails> & Partial<Pick<DriverStop, StopDetails>>,
): DriverStop {
  return {
    contact_short_name: null,
    contact_full_name: null,
    zone_name: null,
    service_window: null,
    cod: null,
    navigation: syntheticNavigation,
    ...stop,
  };
}
