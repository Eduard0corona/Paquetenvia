export const publicOrderStatuses = [
  "CREATED",
  "SCHEDULED",
  "IN_TRANSIT",
  "OUT_FOR_DELIVERY",
  "DELIVERY_EXCEPTION",
  "DELIVERED",
  "RETURNING",
  "RETURNED",
  "CANCELLED",
] as const;

export const publicTimelineCodes = [
  "ORDER_CREATED",
  "PICKUP_SCHEDULED",
  "PICKED_UP",
  "IN_TRANSIT",
  "OUT_FOR_DELIVERY",
  "DELIVERY_ATTEMPTED",
  "RESCHEDULED",
  "DELIVERED",
  "RETURNING",
  "RETURNED",
  "CANCELLED",
] as const;

export type PublicOrderStatus = (typeof publicOrderStatuses)[number];
export type PublicTimelineCode = (typeof publicTimelineCodes)[number];

export interface PublicTimelineItem {
  readonly code: PublicTimelineCode;
  readonly occurred_at: string;
}

export interface PublicTrackingProjection {
  readonly public_id: string;
  readonly public_status: PublicOrderStatus;
  readonly aggregate_version: number;
  readonly estimated_window: Readonly<Record<string, string | null>> | null;
  readonly timeline: readonly PublicTimelineItem[];
}

const ROOT_PROPERTIES = [
  "public_id",
  "public_status",
  "aggregate_version",
  "estimated_window",
  "timeline",
] as const;
const TIMELINE_PROPERTIES = ["code", "occurred_at"] as const;
const statusSet = new Set<string>(publicOrderStatuses);
const timelineCodeSet = new Set<string>(publicTimelineCodes);

export function parsePublicTrackingProjection(
  value: unknown,
): PublicTrackingProjection {
  requireExactObject(value, ROOT_PROPERTIES);
  const publicId = value.public_id;
  if (
    typeof publicId !== "string" ||
    publicId.length < 1 ||
    publicId.length > 128
  ) {
    throw new PublicTrackingContractError();
  }

  const publicStatus = value.public_status;
  if (typeof publicStatus !== "string" || !statusSet.has(publicStatus)) {
    throw new PublicTrackingContractError();
  }

  const aggregateVersion = value.aggregate_version;
  if (
    !Number.isSafeInteger(aggregateVersion) ||
    (aggregateVersion as number) < 1
  ) {
    throw new PublicTrackingContractError();
  }

  const estimatedWindow = parseEstimatedWindow(value.estimated_window);
  if (!Array.isArray(value.timeline) || value.timeline.length > 200) {
    throw new PublicTrackingContractError();
  }

  let previousTimestamp = Number.NEGATIVE_INFINITY;
  const timeline = value.timeline.map((item): PublicTimelineItem => {
    requireExactObject(item, TIMELINE_PROPERTIES);
    if (typeof item.code !== "string" || !timelineCodeSet.has(item.code)) {
      throw new PublicTrackingContractError();
    }

    if (typeof item.occurred_at !== "string" || item.occurred_at.length > 64) {
      throw new PublicTrackingContractError();
    }

    const timestamp = Date.parse(item.occurred_at);
    if (!Number.isFinite(timestamp) || timestamp < previousTimestamp) {
      throw new PublicTrackingContractError();
    }

    previousTimestamp = timestamp;
    return {
      code: item.code as PublicTimelineCode,
      occurred_at: item.occurred_at,
    };
  });

  return {
    public_id: publicId,
    public_status: publicStatus as PublicOrderStatus,
    aggregate_version: aggregateVersion as number,
    estimated_window: estimatedWindow,
    timeline,
  };
}

function parseEstimatedWindow(
  value: unknown,
): Readonly<Record<string, string | null>> | null {
  if (value === null) return null;
  if (!isRecord(value)) throw new PublicTrackingContractError();
  const entries = Object.entries(value);
  if (entries.length > 8) throw new PublicTrackingContractError();

  const parsed: Record<string, string | null> = {};
  for (const [key, item] of entries) {
    if (
      key.length < 1 ||
      key.length > 64 ||
      (item !== null && (typeof item !== "string" || item.length > 256))
    ) {
      throw new PublicTrackingContractError();
    }

    parsed[key] = item;
  }

  return parsed;
}

function requireExactObject<const T extends readonly string[]>(
  value: unknown,
  properties: T,
): asserts value is Record<T[number], unknown> {
  if (!isRecord(value)) throw new PublicTrackingContractError();
  const actual = Object.keys(value);
  if (
    actual.length !== properties.length ||
    properties.some((property) => !Object.hasOwn(value, property))
  ) {
    throw new PublicTrackingContractError();
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export class PublicTrackingContractError extends Error {
  public constructor() {
    super("Invalid public tracking response.");
    this.name = "PublicTrackingContractError";
  }
}
