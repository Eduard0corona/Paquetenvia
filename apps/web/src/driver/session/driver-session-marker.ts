import { asUuid } from "../../realtime/envelope";

/**
 * DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (owner answer E3 "Sí, y envía al
 * validar (Recommended)": "El teléfono recuerda qué repartidor y empresa
 * abrieron la sesión (sin contraseña ni token)"): which driver and which
 * organization opened the last driver session on this phone, so a page load
 * without network can open their saved stops and queue.
 *
 * It holds exactly what the IndexedDB partition key is made of
 * (createDriverCachePartition): the API's `sessionNamespace` (the
 * non-reversible per-identity cache key AuthCenterBffEndpoints.SessionNamespace
 * derives from SHA-256 of the AuthCenter `sub`) and the organization id. Never
 * a token, a cookie, the CSRF token, a password, a name, an e-mail or a phone,
 * and it is never logged. It authorizes nothing: it only chooses which local
 * partition to open while the server cannot be asked, and nothing of that
 * partition is sent until `GET /auth/session` confirms the same namespace and
 * the organization contexts include the same organization.
 *
 * It lives in localStorage, not IndexedDB: it needs no schema, so the driver
 * database stays at version 5 (a new store would need version 6, whose upgrade
 * waits for every other open tab and which an older build can never open
 * again, so a rollback would lock the queue), an older build simply ignores
 * the key, and it is read synchronously when the page starts.
 */
export const driverSessionMarkerKey = "paquetenvia:driver-session-marker";

/** Version of the marker's shape; a marker of any other shape is dropped. */
export const DriverSessionMarkerVersion = 1 as const;

export interface DriverSessionMarker {
  readonly sessionNamespace: string;
  readonly organizationId: string;
}

/** The part of `window` the marker uses; every access may throw (blocked storage). */
export interface DriverSessionMarkerHost {
  readonly localStorage?: Pick<Storage, "getItem" | "setItem" | "removeItem">;
}

const namespacePattern = /^[A-Za-z0-9_-]{16,128}$/;

/**
 * The saved marker, or null when there is none, storage is unavailable or the
 * value is not exactly a version 1 marker (such a value is removed).
 */
export function readDriverSessionMarker(
  host: DriverSessionMarkerHost,
): DriverSessionMarker | null {
  let raw: string | null;
  try {
    raw = host.localStorage?.getItem(driverSessionMarkerKey) ?? null;
  } catch {
    return null;
  }
  if (raw === null) return null;
  const marker = parseDriverSessionMarker(raw);
  if (marker === null) clearDriverSessionMarker(host);
  return marker;
}

export function writeDriverSessionMarker(
  host: DriverSessionMarkerHost,
  marker: DriverSessionMarker,
): void {
  if (!isValidMarker(marker)) return;
  try {
    host.localStorage?.setItem(
      driverSessionMarkerKey,
      JSON.stringify({
        v: DriverSessionMarkerVersion,
        sessionNamespace: marker.sessionNamespace,
        organizationId: marker.organizationId,
      }),
    );
  } catch {
    // Without storage the phone simply remembers nobody; nothing else changes.
  }
}

export function clearDriverSessionMarker(host: DriverSessionMarkerHost): void {
  try {
    host.localStorage?.removeItem(driverSessionMarkerKey);
  } catch {
    // Nothing is remembered when storage is unavailable.
  }
}

/** Whether the marker names this driver in this organization. */
export function driverSessionMarkerMatches(
  marker: DriverSessionMarker | null,
  sessionNamespace: string,
  organizationId: string,
): boolean {
  return (
    marker !== null &&
    marker.sessionNamespace === sessionNamespace &&
    marker.organizationId === organizationId
  );
}

/**
 * Removes the marker only when it names this driver in this organization:
 * after a 403 or a session without a usable credential, which wipe that
 * partition, the phone stops remembering that session.
 */
export function forgetDriverSessionMarker(
  host: DriverSessionMarkerHost,
  sessionNamespace: string,
  organizationId: string,
): void {
  if (
    driverSessionMarkerMatches(
      readDriverSessionMarker(host),
      sessionNamespace,
      organizationId,
    )
  ) {
    clearDriverSessionMarker(host);
  }
}

export function parseDriverSessionMarker(raw: string): DriverSessionMarker | null {
  if (raw.length > 512) return null;
  let value: unknown;
  try {
    value = JSON.parse(raw);
  } catch {
    return null;
  }
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    return null;
  }
  const record = value as Record<string, unknown>;
  const keys = Object.keys(record).sort();
  if (
    keys.length !== 3 ||
    keys[0] !== "organizationId" ||
    keys[1] !== "sessionNamespace" ||
    keys[2] !== "v" ||
    record.v !== DriverSessionMarkerVersion
  ) {
    return null;
  }
  const marker = {
    sessionNamespace: record.sessionNamespace,
    organizationId: record.organizationId,
  };
  return isValidMarker(marker)
    ? Object.freeze({
        sessionNamespace: marker.sessionNamespace,
        organizationId: marker.organizationId,
      })
    : null;
}

function isValidMarker(value: {
  readonly sessionNamespace: unknown;
  readonly organizationId: unknown;
}): value is DriverSessionMarker {
  if (
    typeof value.sessionNamespace !== "string" ||
    !namespacePattern.test(value.sessionNamespace) ||
    typeof value.organizationId !== "string"
  ) {
    return false;
  }
  try {
    asUuid(value.organizationId);
    return true;
  } catch {
    return false;
  }
}
