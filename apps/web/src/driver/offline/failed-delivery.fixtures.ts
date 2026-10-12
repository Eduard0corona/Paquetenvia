import { vi } from "vitest";
import type { DriverCachePartition } from "../cache/driver-stops-cache";
import type { DriverStop, DriverStopList } from "../contracts/driver-stop";
import { stopList, withStopDetails } from "../contracts/driver-stop.fixtures";
import type { DriverFailedDeliveryReason } from "../contracts/failed-delivery";
import type { DriverOfflineQueue } from "./driver-offline-queue";
import {
  DriverSyncApiError,
  type DriverIncidentReceipt,
  type DriverSyncApi,
  type DriverUploadGrant,
  uploadDriverProof,
} from "./driver-sync-api";
import {
  DriverSyncScheduler,
  type DriverSyncSchedulerOptions,
} from "./driver-sync-scheduler";
import {
  createDriverOfflineOperation,
  type DriverOfflineOperation,
  type DriverProofBlobRecord,
} from "./operation-contract";

/**
 * Test support only (UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12): synthetic
 * identifiers, a FAILED_DELIVERY factory, an in-memory queue and a scripted
 * server that applies each of the five AI-05 steps once per Idempotency-Key,
 * like the API, so retries and lost answers can be asserted.
 */
export const partition: DriverCachePartition = { key: "J".repeat(43) };
export const orderId = "22222222-2222-4222-8222-222222222222";
export const reportId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
export const collectionId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
export const reportedAt = new Date("2026-10-12T18:30:00.000Z");
export const photoSha256 = "f".repeat(64);
export const amountCents = 125_000;

export function failedDeliveryOperation(
  patch: Partial<DriverOfflineOperation> = {},
  reasonCode: DriverFailedDeliveryReason = "RECIPIENT_ABSENT",
): DriverOfflineOperation {
  return Object.freeze({
    ...createDriverOfflineOperation({
      partitionKey: partition.key,
      orderId,
      kind: "FAILED_DELIVERY",
      expectedVersion: 8,
      proof: { contentType: "image/png", sizeBytes: 3, sha256: photoSha256 },
      failedDelivery: { reasonCode },
      now: () => reportedAt,
      randomUuid: () => reportId,
    }),
    ...patch,
  });
}

export function collectionOperation(
  patch: Partial<DriverOfflineOperation> = {},
): DriverOfflineOperation {
  return Object.freeze({
    ...createDriverOfflineOperation({
      partitionKey: partition.key,
      orderId,
      kind: "COD_COLLECTION",
      expectedVersion: 8,
      cod: { amountCents, reference: "ORD_SYNTHETIC 2026-10-12T11:29:00-07:00" },
      now: () => new Date(reportedAt.getTime() - 60_000),
      randomUuid: () => collectionId,
    }),
    ...patch,
  });
}

export function photoRecord(operationId = reportId): DriverProofBlobRecord {
  return {
    schemaVersion: 1,
    partitionKey: partition.key,
    operationId,
    blob: new Blob([new Uint8Array([1, 2, 3])], { type: "image/png" }),
    contentType: "image/png",
    sizeBytes: 3,
    sha256: photoSha256,
  };
}

export function deliveryStop(
  patch: Partial<DriverStop> = {},
): DriverStop {
  return withStopDetails({
    order_id: orderId,
    aggregate_version: 8,
    order_public_id: "ORD_SYNTHETIC",
    stop_type: "DELIVERY",
    status: "DELIVERING",
    address_summary: "Zona sintética",
    contact_short_name: "M. Sintética",
    ...patch,
  });
}

export function cashStop(recorded: boolean, patch: Partial<DriverStop> = {}): DriverStop {
  return deliveryStop({
    cod: { expected: { currency: "MXN", amount_cents: amountCents }, recorded },
    ...patch,
  });
}

/** The five server steps of a report, in AI-05 order. */
export type ReportStep = "session" | "upload" | "finalize" | "incident" | "transition";

/**
 * One scripted failure: `before` answers the error without applying the step;
 * `after` applies it and then loses the answer (the phone sees the error).
 */
export interface ScriptedFailure {
  readonly step: ReportStep;
  readonly when: "before" | "after";
  readonly error: DriverSyncApiError;
}

export class FailedDeliveryServer {
  public status: DriverStop["status"] = "DELIVERING";
  public version = 8;
  public cod: DriverStop["cod"] = null;
  public readonly calls: string[] = [];
  public readonly keys: Record<ReportStep, string[]> = {
    session: [],
    upload: [],
    finalize: [],
    incident: [],
    transition: [],
  };
  public readonly incidentBodies: DriverOfflineOperation[] = [];
  public readonly sessions = new Map<string, string>();
  public readonly proofs = new Map<string, string>();
  public readonly incidents = new Map<string, string>();
  public transitionsApplied = 0;
  public stopsReads = 0;
  public failures: ScriptedFailure[] = [];
  /** Called on every read of the stops, before it answers (to change the server meanwhile). */
  public onStopsRead: (() => void) | null = null;
  public grantExpiresAt = "2026-10-13T00:00:00.000Z";

  public stop(): DriverStop {
    return deliveryStop({
      status: this.status,
      aggregate_version: this.version,
      cod: this.cod,
    });
  }

  public readonly refreshStops = async (): Promise<DriverStopList> => {
    this.stopsReads += 1;
    this.onStopsRead?.();
    return stopList([this.stop()]);
  };

  public api(): DriverSyncApi {
    return {
      createProofUploadSession: vi.fn(async (operation: DriverOfflineOperation) =>
        this.step("session", operation.sessionIdempotencyKey ?? "", () => {
          const key = operation.sessionIdempotencyKey ?? "";
          if (!this.sessions.has(key)) {
            this.sessions.set(key, uuid(0x500 + this.sessions.size));
          }
          return this.grant(this.sessions.get(key)!);
        }),
      ),
      finalizeProof: vi.fn(async (operation: DriverOfflineOperation, sessionId: string) =>
        this.step("finalize", operation.finalizeIdempotencyKey ?? "", () => {
          const key = operation.finalizeIdempotencyKey ?? "";
          if (!this.proofs.has(key)) this.proofs.set(key, uuid(0x600 + this.proofs.size));
          expectEqual(sessionId, this.sessions.get(operation.sessionIdempotencyKey ?? ""));
          return {
            id: this.proofs.get(key)!,
            proofType: "DELIVERY_PHOTO" as const,
            sha256: photoSha256,
            capturedAt: operation.clientOccurredAt,
          };
        }),
      ),
      openFailedDeliveryIncident: vi.fn(async (operation: DriverOfflineOperation) => {
        const report = operation.failedDelivery!;
        return this.step("incident", report.incidentIdempotencyKey, (): DriverIncidentReceipt => {
          if (!this.incidents.has(report.incidentIdempotencyKey)) {
            this.incidents.set(report.incidentIdempotencyKey, uuid(0x700 + this.incidents.size));
            this.incidentBodies.push(operation);
          }
          return {
            id: this.incidents.get(report.incidentIdempotencyKey)!,
            orderId: operation.orderId,
            reasonCode: report.reasonCode,
            nextAction: null,
            occurredAt: operation.clientOccurredAt,
            evidenceProofIds: [operation.proofId!],
          };
        });
      }),
      transitionOrder: vi.fn(async (operation: DriverOfflineOperation) =>
        this.step("transition", operation.transitionIdempotencyKey, () => {
          if (this.status === "DELIVERING" && this.version === operation.expectedVersion) {
            this.status = "FAILED_ATTEMPT";
            this.version += 1;
            this.transitionsApplied += 1;
          } else if (this.status !== "FAILED_ATTEMPT") {
            throw new DriverSyncApiError("conflict", "VERSION_CONFLICT");
          }
          return { id: operation.orderId, status: "FAILED_ATTEMPT" as const, version: this.version };
        }),
      ),
      recordCodCollection: vi.fn(async (operation: DriverOfflineOperation) => {
        this.calls.push("cod");
        this.cod = {
          expected: { currency: "MXN", amount_cents: operation.cod!.amountCents },
          recorded: true,
        };
        return {
          id: uuid(0x800),
          orderId: operation.orderId,
          amountCents: operation.cod!.amountCents,
          status: "RECORDED" as const,
        };
      }),
    };
  }

  public readonly upload: typeof uploadDriverProof = vi.fn(async (grant: DriverUploadGrant) => {
    await this.step("upload", grant.id, () => undefined);
  });

  public scheduler(
    queue: DriverOfflineQueue,
    patch: Partial<DriverSyncSchedulerOptions> = {},
  ): DriverSyncScheduler {
    return new DriverSyncScheduler({
      partition,
      queue,
      api: this.api(),
      refreshStops: this.refreshStops,
      onQueueChanged: vi.fn(),
      onAccessRevoked: vi.fn(),
      now: () => new Date(reportedAt.getTime() + 60_000),
      randomUuid: () => "99999999-9999-4999-8999-999999999999",
      upload: this.upload,
      ...patch,
    });
  }

  private async step<T>(step: ReportStep, key: string, apply: () => T): Promise<T> {
    this.calls.push(step);
    this.keys[step].push(key);
    const index = this.failures.findIndex((failure) => failure.step === step);
    const failure = index >= 0 ? this.failures.splice(index, 1)[0] : null;
    if (failure?.when === "before") throw failure.error;
    const result = apply();
    if (failure?.when === "after") throw failure.error;
    return result;
  }

  private grant(id: string): DriverUploadGrant {
    return {
      id,
      status: "CREATED",
      uploadUrl: `https://objects.synthetic.test/quarantine/${id}`,
      expiresAt: this.grantExpiresAt,
      requiredHeaders: { "Content-Type": "image/png" },
    };
  }
}

export class MemoryQueue implements DriverOfflineQueue {
  public operations: DriverOfflineOperation[];
  public readonly proofs = new Map<string, DriverProofBlobRecord>();
  public clearCount = 0;

  public constructor(operations: readonly DriverOfflineOperation[]) {
    this.operations = [...operations];
  }

  public async listOperations(): Promise<readonly DriverOfflineOperation[]> {
    return Object.freeze(
      [...this.operations].sort(
        (left, right) =>
          left.createdAt.localeCompare(right.createdAt) ||
          left.id.localeCompare(right.id),
      ),
    );
  }

  public async readProof(
    _partition: DriverCachePartition,
    operationId: string,
  ): Promise<DriverProofBlobRecord | null> {
    return this.proofs.get(operationId) ?? null;
  }

  public async enqueue(
    _partition: DriverCachePartition,
    operation: DriverOfflineOperation,
    proof?: { blob: Blob; contentType: "image/jpeg" | "image/png"; sizeBytes: number; sha256: string },
  ): Promise<void> {
    this.operations.push(operation);
    if (proof) {
      this.proofs.set(operation.id, {
        schemaVersion: 1,
        partitionKey: partition.key,
        operationId: operation.id,
        ...proof,
      });
    }
  }

  public async replaceOperation(
    _partition: DriverCachePartition,
    operation: DriverOfflineOperation,
  ): Promise<void> {
    this.operations = this.operations.map((candidate) =>
      candidate.id === operation.id ? operation : candidate,
    );
  }

  public async replaceOperationAndDeleteProof(
    partitionValue: DriverCachePartition,
    operation: DriverOfflineOperation,
  ): Promise<void> {
    await this.replaceOperation(partitionValue, operation);
    this.proofs.delete(operation.id);
  }

  public async deleteOperation(
    _partition: DriverCachePartition,
    operationId: string,
  ): Promise<void> {
    this.operations = this.operations.filter((candidate) => candidate.id !== operationId);
    this.proofs.delete(operationId);
  }

  public async rebuildOperation(
    _partition: DriverCachePartition,
    replacedOperationId: string,
    operation: DriverOfflineOperation,
  ): Promise<void> {
    const proof = this.proofs.get(replacedOperationId);
    await this.deleteOperation(partition, replacedOperationId);
    this.operations.push(operation);
    if (proof) this.proofs.set(operation.id, { ...proof, operationId: operation.id });
  }

  public async clearPartition(): Promise<void> {
    this.clearCount += 1;
    this.operations = [];
    this.proofs.clear();
  }

  public async acquireLease(): Promise<boolean> {
    return true;
  }

  public async renewLease(): Promise<boolean> {
    return true;
  }

  public async releaseLease(): Promise<void> {}

  public only(): DriverOfflineOperation {
    if (this.operations.length !== 1) {
      throw new Error(`expected one queued operation, found ${this.operations.length}`);
    }
    return this.operations[0];
  }
}

export function uuid(tail: number): string {
  return `00000000-0000-4000-8000-${tail.toString(16).padStart(12, "0")}`;
}

function expectEqual(actual: unknown, expected: unknown): void {
  if (actual !== expected) {
    throw new Error(`expected ${String(expected)} but received ${String(actual)}`);
  }
}
