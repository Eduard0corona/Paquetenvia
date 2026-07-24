import type { PublicOrderId, RealtimeEnvelope, Uuid } from "./envelope";

export interface RealtimeDeduplicationOptions {
  readonly maximumEventIds: number;
  readonly maximumAggregates: number;
  readonly eventTtlMilliseconds: number;
}

const defaultOptions: RealtimeDeduplicationOptions = {
  maximumEventIds: 2_048,
  maximumAggregates: 1_024,
  eventTtlMilliseconds: 15 * 60 * 1_000,
};

export class RealtimeEventGuard {
  private readonly eventIds = new Map<Uuid, number>();
  private readonly aggregateVersions = new Map<string, number>();

  public constructor(
    private readonly options: RealtimeDeduplicationOptions = defaultOptions,
    private readonly now: () => number = Date.now,
  ) {
    if (
      !Number.isInteger(options.maximumEventIds) ||
      options.maximumEventIds < 1 ||
      !Number.isInteger(options.maximumAggregates) ||
      options.maximumAggregates < 1 ||
      !Number.isFinite(options.eventTtlMilliseconds) ||
      options.eventTtlMilliseconds <= 0
    ) {
      throw new Error("Realtime deduplication limits must be positive and bounded.");
    }
  }

  public shouldApply<TAggregateId extends Uuid | PublicOrderId, TPayload>(
    event: RealtimeEnvelope<TAggregateId, TPayload>,
  ): boolean {
    this.removeExpiredEventIds();
    if (this.eventIds.has(event.event_id)) {
      return false;
    }

    const previousVersion = this.aggregateVersions.get(event.aggregate_id);
    if (previousVersion !== undefined && event.aggregate_version < previousVersion) {
      this.rememberEventId(event.event_id);
      return false;
    }

    this.rememberEventId(event.event_id);
    this.rememberAggregateVersion(event.aggregate_id, event.aggregate_version);
    return true;
  }

  public replaceAggregateVersions(versions: Readonly<Record<string, number>>): void {
    this.aggregateVersions.clear();
    for (const [aggregateId, version] of Object.entries(versions)) {
      if (!Number.isSafeInteger(version) || version < 0) {
        throw new Error("REST synchronization returned an invalid aggregate version.");
      }

      this.rememberAggregateVersion(aggregateId, version);
    }
  }

  public clear(): void {
    this.eventIds.clear();
    this.aggregateVersions.clear();
  }

  private rememberEventId(eventId: Uuid): void {
    this.eventIds.delete(eventId);
    this.eventIds.set(eventId, this.now());
    while (this.eventIds.size > this.options.maximumEventIds) {
      const oldest = this.eventIds.keys().next().value;
      if (oldest === undefined) {
        break;
      }

      this.eventIds.delete(oldest);
    }
  }

  private rememberAggregateVersion(aggregateId: string, version: number): void {
    this.aggregateVersions.delete(aggregateId);
    this.aggregateVersions.set(aggregateId, version);
    while (this.aggregateVersions.size > this.options.maximumAggregates) {
      const oldest = this.aggregateVersions.keys().next().value;
      if (oldest === undefined) {
        break;
      }

      this.aggregateVersions.delete(oldest);
    }
  }

  private removeExpiredEventIds(): void {
    const cutoff = this.now() - this.options.eventTtlMilliseconds;
    for (const [eventId, seenAt] of this.eventIds) {
      if (seenAt > cutoff) {
        break;
      }

      this.eventIds.delete(eventId);
    }
  }
}
