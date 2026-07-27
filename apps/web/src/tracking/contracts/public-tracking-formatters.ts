export const PUBLIC_TRACKING_TIME_ZONE = "America/Mazatlan";

const publicTrackingDateTimeFormatter = new Intl.DateTimeFormat("es-MX", {
  dateStyle: "medium",
  timeStyle: "short",
  timeZone: PUBLIC_TRACKING_TIME_ZONE,
  hourCycle: "h23",
});

export function formatPublicTrackingTimestamp(value: string | Date): string {
  const date = typeof value === "string" ? new Date(value) : value;

  if (!Number.isFinite(date.getTime())) {
    throw new Error("Invalid public tracking timestamp.");
  }

  return publicTrackingDateTimeFormatter.format(date);
}

export function formatPublicTrackingEstimatedWindow(
  window: Readonly<Record<string, string | null>> | null,
): string | null {
  const from = window?.from;
  const to = window?.to;
  if (typeof from !== "string" || typeof to !== "string") {
    return null;
  }

  try {
    return `${formatPublicTrackingTimestamp(from)} – ${formatPublicTrackingTimestamp(to)}`;
  } catch {
    return null;
  }
}
