/**
 * The one client-side copy of the OPS-003 offline operation age rule
 * (decisions OPS-003-OFFLINE-72H and OPS-003-SERVER-72H-REJECTION, AI-05
 * `x-offline-operation-age`, `maximum_age: PT72H`). A queued driver operation
 * may be replayed for up to 72 hours after it was captured; after that it is
 * discarded and the driver is told. The server applies the same bound and
 * answers 409 `OFFLINE_OPERATION_EXPIRED`, so every 72-hour check in the PWA
 * goes through this module instead of repeating the number.
 */

export const OfflineOperationMaximumAgeHours = 72;

export const OfflineOperationMaximumAgeMilliseconds =
  OfflineOperationMaximumAgeHours * 60 * 60 * 1_000;

/** Problem Details `code` AI-05 returns for an operation older than the maximum age. */
export const OfflineOperationExpiredCode = "OFFLINE_OPERATION_EXPIRED";

/**
 * Mirrors the server comparison: exactly 72 hours old is still accepted and
 * only an older capture is expired. An unreadable timestamp is never treated
 * as expired here; the queue contract rejects it elsewhere.
 */
export function isOfflineOperationExpired(
  capturedAt: string,
  now: Date,
): boolean {
  const captured = Date.parse(capturedAt);
  if (!Number.isFinite(captured)) return false;
  return now.getTime() - captured > OfflineOperationMaximumAgeMilliseconds;
}

/** True only for the stable AI-05 code, never for other 409 conflicts. */
export function isOfflineOperationExpiredCode(
  code: string | null | undefined,
): boolean {
  return code === OfflineOperationExpiredCode;
}
