import { asUuid } from "../../realtime/envelope";

export const DriverOfflineOperationSchemaVersion = 1 as const;

export const driverOperationKinds = [
  "CHECK_IN",
  "PICKUP_PROOF",
  "START_TRANSIT",
  "START_DELIVERY",
  "DELIVERY_PROOF",
] as const;

export const driverOperationStatuses = [
  "PENDING",
  "PROCESSING",
  "RETRY_WAIT",
  "WAITING_VALIDATION",
  "AWAITING_REST_CONFIRMATION",
  "NEEDS_ATTENTION",
  "BLOCKED",
] as const;

export type DriverOperationKind = (typeof driverOperationKinds)[number];
export type DriverOperationStatus = (typeof driverOperationStatuses)[number];

export type DriverOperationalStatus =
  | "ASSIGNED"
  | "AT_PICKUP"
  | "PICKED_UP"
  | "IN_TRANSIT"
  | "DELIVERING"
  | "DELIVERED";

export type DriverProofType = "PICKUP_PHOTO" | "DELIVERY_PHOTO";

export interface DriverOperationDefinition {
  readonly sourceStatus: DriverOperationalStatus;
  readonly targetStatus: DriverOperationalStatus;
  readonly reason: string;
  readonly proofType: DriverProofType | null;
}

export const driverOperationDefinitions: Readonly<
  Record<DriverOperationKind, DriverOperationDefinition>
> = Object.freeze({
  CHECK_IN: Object.freeze({
    sourceStatus: "ASSIGNED",
    targetStatus: "AT_PICKUP",
    reason: "DRIVER_CHECK_IN",
    proofType: null,
  }),
  PICKUP_PROOF: Object.freeze({
    sourceStatus: "AT_PICKUP",
    targetStatus: "PICKED_UP",
    reason: "DRIVER_PICKUP_CONFIRMED",
    proofType: "PICKUP_PHOTO",
  }),
  START_TRANSIT: Object.freeze({
    sourceStatus: "PICKED_UP",
    targetStatus: "IN_TRANSIT",
    reason: "DRIVER_TRANSIT_STARTED",
    proofType: null,
  }),
  START_DELIVERY: Object.freeze({
    sourceStatus: "IN_TRANSIT",
    targetStatus: "DELIVERING",
    reason: "DRIVER_DELIVERY_STARTED",
    proofType: null,
  }),
  DELIVERY_PROOF: Object.freeze({
    sourceStatus: "DELIVERING",
    targetStatus: "DELIVERED",
    reason: "DRIVER_DELIVERY_CONFIRMED",
    proofType: "DELIVERY_PHOTO",
  }),
});

export type DriverOperationSafeError =
  | "NETWORK"
  | "SESSION_EXPIRED"
  | "VERSION_CONFLICT"
  | "EVIDENCE_REJECTED"
  | "RESOURCE_UNAVAILABLE"
  | null;

export interface DriverOfflineOperation {
  readonly schemaVersion: 1;
  readonly partitionKey: string;
  readonly id: string;
  readonly orderId: string;
  readonly kind: DriverOperationKind;
  readonly sourceStatus: DriverOperationalStatus;
  readonly targetStatus: DriverOperationalStatus;
  readonly expectedVersion: number;
  readonly reason: string;
  readonly clientOccurredAt: string;
  readonly createdAt: string;
  readonly transitionIdempotencyKey: string;
  readonly sessionIdempotencyKey: string | null;
  readonly finalizeIdempotencyKey: string | null;
  readonly proofType: DriverProofType | null;
  readonly contentType: "image/jpeg" | "image/png" | null;
  readonly sizeBytes: number | null;
  readonly sha256: string | null;
  readonly capturedAt: string | null;
  readonly status: DriverOperationStatus;
  readonly attemptCount: number;
  readonly nextAttemptAt: string;
  readonly sessionAttempt: number;
  readonly uploadSessionId: string | null;
  readonly uploadSessionExpiresAt: string | null;
  readonly uploadAccepted: boolean;
  readonly proofId: string | null;
  readonly safeError: DriverOperationSafeError;
}

export interface DriverProofBlobRecord {
  readonly schemaVersion: 1;
  readonly partitionKey: string;
  readonly operationId: string;
  readonly blob: Blob;
  readonly contentType: "image/jpeg" | "image/png";
  readonly sizeBytes: number;
  readonly sha256: string;
}

export interface DriverSyncLease {
  readonly schemaVersion: 1;
  readonly partitionKey: string;
  readonly ownerId: string;
  readonly expiresAt: string;
  readonly updatedAt: string;
}

export class DriverOfflineContractError extends Error {
  public constructor() {
    super("La cola local no cumple el contrato esperado.");
    this.name = "DriverOfflineContractError";
  }
}

const operationProperties = new Set([
  "schemaVersion",
  "partitionKey",
  "id",
  "orderId",
  "kind",
  "sourceStatus",
  "targetStatus",
  "expectedVersion",
  "reason",
  "clientOccurredAt",
  "createdAt",
  "transitionIdempotencyKey",
  "sessionIdempotencyKey",
  "finalizeIdempotencyKey",
  "proofType",
  "contentType",
  "sizeBytes",
  "sha256",
  "capturedAt",
  "status",
  "attemptCount",
  "nextAttemptAt",
  "sessionAttempt",
  "uploadSessionId",
  "uploadSessionExpiresAt",
  "uploadAccepted",
  "proofId",
  "safeError",
]);

export function createDriverOfflineOperation(input: {
  readonly partitionKey: string;
  readonly orderId: string;
  readonly kind: DriverOperationKind;
  readonly expectedVersion: number;
  readonly proof?: {
    readonly contentType: "image/jpeg" | "image/png";
    readonly sizeBytes: number;
    readonly sha256: string;
  };
  readonly now?: () => Date;
  readonly randomUuid?: () => string;
}): DriverOfflineOperation {
  const definition = driverOperationDefinitions[input.kind];
  const timestamp = (input.now ?? (() => new Date()))().toISOString();
  const id = asUuid((input.randomUuid ?? (() => crypto.randomUUID()))());
  const proof = input.proof;
  if ((definition.proofType === null) !== (proof === undefined)) {
    throw new DriverOfflineContractError();
  }
  return Object.freeze({
    schemaVersion: DriverOfflineOperationSchemaVersion,
    partitionKey: readPartitionKey(input.partitionKey),
    id,
    orderId: asUuid(input.orderId),
    kind: input.kind,
    sourceStatus: definition.sourceStatus,
    targetStatus: definition.targetStatus,
    expectedVersion: readPositiveSafeInteger(input.expectedVersion),
    reason: definition.reason,
    clientOccurredAt: timestamp,
    createdAt: timestamp,
    transitionIdempotencyKey: transitionIdempotencyKey(id),
    sessionIdempotencyKey:
      definition.proofType === null ? null : sessionIdempotencyKey(id, 1),
    finalizeIdempotencyKey:
      definition.proofType === null ? null : finalizeIdempotencyKey(id, 1),
    proofType: definition.proofType,
    contentType: proof?.contentType ?? null,
    sizeBytes: proof ? readPositiveSafeInteger(proof.sizeBytes) : null,
    sha256: proof ? readSha256(proof.sha256) : null,
    capturedAt: definition.proofType === null ? null : timestamp,
    status: "PENDING",
    attemptCount: 0,
    nextAttemptAt: timestamp,
    sessionAttempt: 1,
    uploadSessionId: null,
    uploadSessionExpiresAt: null,
    uploadAccepted: false,
    proofId: null,
    safeError: null,
  });
}

export function parseDriverOfflineOperation(
  value: unknown,
  expectedPartitionKey?: string,
): DriverOfflineOperation {
  const record = readExactRecord(value, operationProperties);
  const partitionKey = readPartitionKey(record.partitionKey);
  if (expectedPartitionKey !== undefined && partitionKey !== expectedPartitionKey) {
    throw new DriverOfflineContractError();
  }
  const kind = readEnum(record.kind, driverOperationKinds);
  const definition = driverOperationDefinitions[kind];
  const sourceStatus = readOperationalStatus(record.sourceStatus);
  const targetStatus = readOperationalStatus(record.targetStatus);
  const reason = readText(record.reason, 64);
  if (
    sourceStatus !== definition.sourceStatus ||
    targetStatus !== definition.targetStatus ||
    reason !== definition.reason
  ) {
    throw new DriverOfflineContractError();
  }
  const id = readUuid(record.id);
  const sessionAttempt = readPositiveSafeInteger(record.sessionAttempt);
  const transitionKey = readText(record.transitionIdempotencyKey, 128);
  const sessionKey = readNullableText(record.sessionIdempotencyKey, 128);
  const finalizeKey = readNullableText(record.finalizeIdempotencyKey, 128);
  const proofType =
    record.proofType === null
      ? null
      : readEnum(record.proofType, [
          "PICKUP_PHOTO",
          "DELIVERY_PHOTO",
        ] as const);
  const contentType =
    record.contentType === null
      ? null
      : readEnum(record.contentType, ["image/jpeg", "image/png"] as const);
  const sizeBytes =
    record.sizeBytes === null
      ? null
      : readPositiveSafeInteger(record.sizeBytes);
  const sha256 = record.sha256 === null ? null : readSha256(record.sha256);
  const capturedAt = readNullableUtcTimestamp(record.capturedAt);
  const proofFields = [
    proofType,
    contentType,
    sizeBytes,
    sha256,
    capturedAt,
    sessionKey,
    finalizeKey,
  ];
  const expectsProof = definition.proofType !== null;
  if (
    transitionKey !== transitionIdempotencyKey(id) ||
    (expectsProof &&
      (proofFields.some((value) => value === null) ||
        proofType !== definition.proofType ||
        capturedAt !== record.clientOccurredAt ||
        sessionKey !== sessionIdempotencyKey(id, sessionAttempt) ||
        finalizeKey !== finalizeIdempotencyKey(id, sessionAttempt))) ||
    (!expectsProof && proofFields.some((value) => value !== null))
  ) {
    throw new DriverOfflineContractError();
  }

  return Object.freeze({
    schemaVersion: readSchemaVersion(record.schemaVersion),
    partitionKey,
    id,
    orderId: readUuid(record.orderId),
    kind,
    sourceStatus,
    targetStatus,
    expectedVersion: readPositiveSafeInteger(record.expectedVersion),
    reason,
    clientOccurredAt: readUtcTimestamp(record.clientOccurredAt),
    createdAt: readUtcTimestamp(record.createdAt),
    transitionIdempotencyKey: transitionKey,
    sessionIdempotencyKey: sessionKey,
    finalizeIdempotencyKey: finalizeKey,
    proofType,
    contentType,
    sizeBytes,
    sha256,
    capturedAt,
    status: readEnum(record.status, driverOperationStatuses),
    attemptCount: readNonNegativeSafeInteger(record.attemptCount),
    nextAttemptAt: readUtcTimestamp(record.nextAttemptAt),
    sessionAttempt,
    uploadSessionId: readNullableUuid(record.uploadSessionId),
    uploadSessionExpiresAt: readNullableUtcTimestamp(
      record.uploadSessionExpiresAt,
    ),
    uploadAccepted: readBoolean(record.uploadAccepted),
    proofId: readNullableUuid(record.proofId),
    safeError: readSafeError(record.safeError),
  });
}

export function parseDriverProofBlob(
  value: unknown,
  expectedPartitionKey?: string,
): DriverProofBlobRecord {
  const record = readExactRecord(
    value,
    new Set([
      "schemaVersion",
      "partitionKey",
      "operationId",
      "blob",
      "contentType",
      "sizeBytes",
      "sha256",
    ]),
  );
  const partitionKey = readPartitionKey(record.partitionKey);
  if (expectedPartitionKey !== undefined && partitionKey !== expectedPartitionKey) {
    throw new DriverOfflineContractError();
  }
  if (!(record.blob instanceof Blob)) throw new DriverOfflineContractError();
  const contentType = readEnum(record.contentType, [
    "image/jpeg",
    "image/png",
  ] as const);
  const sizeBytes = readPositiveSafeInteger(record.sizeBytes);
  const sha256 = readSha256(record.sha256);
  if (
    record.blob.size !== sizeBytes ||
    record.blob.type !== contentType
  ) {
    throw new DriverOfflineContractError();
  }
  return Object.freeze({
    schemaVersion: readSchemaVersion(record.schemaVersion),
    partitionKey,
    operationId: readUuid(record.operationId),
    blob: record.blob,
    contentType,
    sizeBytes,
    sha256,
  });
}

export function parseDriverSyncLease(
  value: unknown,
  expectedPartitionKey?: string,
): DriverSyncLease {
  const record = readExactRecord(
    value,
    new Set([
      "schemaVersion",
      "partitionKey",
      "ownerId",
      "expiresAt",
      "updatedAt",
    ]),
  );
  const partitionKey = readPartitionKey(record.partitionKey);
  if (expectedPartitionKey !== undefined && partitionKey !== expectedPartitionKey) {
    throw new DriverOfflineContractError();
  }
  return Object.freeze({
    schemaVersion: readSchemaVersion(record.schemaVersion),
    partitionKey,
    ownerId: readUuid(record.ownerId),
    expiresAt: readUtcTimestamp(record.expiresAt),
    updatedAt: readUtcTimestamp(record.updatedAt),
  });
}

export function transitionIdempotencyKey(operationId: string): string {
  return validateIdempotencyKey(`drv2-${asUuid(operationId)}-transition`);
}

export function sessionIdempotencyKey(
  operationId: string,
  attempt: number,
): string {
  return validateIdempotencyKey(
    `drv2-${asUuid(operationId)}-session-${readPositiveSafeInteger(attempt)}`,
  );
}

export function finalizeIdempotencyKey(
  operationId: string,
  attempt: number,
): string {
  return validateIdempotencyKey(
    `drv2-${asUuid(operationId)}-finalize-${readPositiveSafeInteger(attempt)}`,
  );
}

function validateIdempotencyKey(value: string): string {
  if (value.length < 16 || value.length > 128) {
    throw new DriverOfflineContractError();
  }
  return value;
}

function readExactRecord(
  value: unknown,
  properties: ReadonlySet<string>,
): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new DriverOfflineContractError();
  }
  const record = value as Record<string, unknown>;
  const keys = Object.keys(record);
  if (
    keys.length !== properties.size ||
    keys.some((key) => !properties.has(key))
  ) {
    throw new DriverOfflineContractError();
  }
  return record;
}

function readSchemaVersion(value: unknown): 1 {
  if (value !== DriverOfflineOperationSchemaVersion) {
    throw new DriverOfflineContractError();
  }
  return value;
}

function readUuid(value: unknown): string {
  if (typeof value !== "string") throw new DriverOfflineContractError();
  try {
    return asUuid(value);
  } catch {
    throw new DriverOfflineContractError();
  }
}

function readNullableUuid(value: unknown): string | null {
  return value === null ? null : readUuid(value);
}

function readPartitionKey(value: unknown): string {
  if (
    typeof value !== "string" ||
    !/^[A-Za-z0-9_-]{43}$/.test(value)
  ) {
    throw new DriverOfflineContractError();
  }
  return value;
}

function readOperationalStatus(value: unknown): DriverOperationalStatus {
  return readEnum(value, [
    "ASSIGNED",
    "AT_PICKUP",
    "PICKED_UP",
    "IN_TRANSIT",
    "DELIVERING",
    "DELIVERED",
  ] as const);
}

function readEnum<T extends string>(
  value: unknown,
  values: readonly T[],
): T {
  if (typeof value !== "string" || !values.includes(value as T)) {
    throw new DriverOfflineContractError();
  }
  return value as T;
}

function readPositiveSafeInteger(value: unknown): number {
  if (!Number.isSafeInteger(value) || (value as number) < 1) {
    throw new DriverOfflineContractError();
  }
  return value as number;
}

function readNonNegativeSafeInteger(value: unknown): number {
  if (!Number.isSafeInteger(value) || (value as number) < 0) {
    throw new DriverOfflineContractError();
  }
  return value as number;
}

function readText(value: unknown, maximumLength: number): string {
  if (
    typeof value !== "string" ||
    value.length < 1 ||
    value.length > maximumLength ||
    value.trim() !== value
  ) {
    throw new DriverOfflineContractError();
  }
  return value;
}

function readNullableText(
  value: unknown,
  maximumLength: number,
): string | null {
  return value === null ? null : readText(value, maximumLength);
}

function readUtcTimestamp(value: unknown): string {
  if (
    typeof value !== "string" ||
    !value.endsWith("Z") ||
    !Number.isFinite(Date.parse(value))
  ) {
    throw new DriverOfflineContractError();
  }
  return value;
}

function readNullableUtcTimestamp(value: unknown): string | null {
  return value === null ? null : readUtcTimestamp(value);
}

function readBoolean(value: unknown): boolean {
  if (typeof value !== "boolean") throw new DriverOfflineContractError();
  return value;
}

function readSha256(value: unknown): string {
  if (typeof value !== "string" || !/^[a-f0-9]{64}$/.test(value)) {
    throw new DriverOfflineContractError();
  }
  return value;
}

function readSafeError(value: unknown): DriverOperationSafeError {
  if (
    value !== null &&
    ![
      "NETWORK",
      "SESSION_EXPIRED",
      "VERSION_CONFLICT",
      "EVIDENCE_REJECTED",
      "RESOURCE_UNAVAILABLE",
    ].includes(value as string)
  ) {
    throw new DriverOfflineContractError();
  }
  return value as DriverOperationSafeError;
}
