export function resolveDriverApiBaseUrl(
  browserOrigin: string,
  configuredValue: string | undefined,
  environment: string,
): string {
  const candidate = configuredValue?.trim() || browserOrigin;
  let url: URL;
  try {
    url = new URL(candidate);
  } catch {
    throw new Error("NEXT_PUBLIC_API_BASE_URL must be an absolute URL.");
  }

  if (
    url.username ||
    url.password ||
    url.search ||
    url.hash ||
    (url.protocol !== "https:" && url.protocol !== "http:")
  ) {
    throw new Error("NEXT_PUBLIC_API_BASE_URL is not a safe API origin.");
  }
  if (environment === "production" && url.protocol !== "https:") {
    throw new Error("NEXT_PUBLIC_API_BASE_URL must use HTTPS in Production.");
  }

  url.pathname = url.pathname.replace(/\/+$/, "") || "/";
  return url.origin + (url.pathname === "/" ? "" : url.pathname);
}
