import type { NextConfig } from "next";

const apiBaseUrl = process.env.NEXT_PUBLIC_API_BASE_URL;
if (
  apiBaseUrl !== undefined &&
  process.env.NODE_ENV === "production" &&
  new URL(apiBaseUrl).protocol !== "https:"
) {
  throw new Error("NEXT_PUBLIC_API_BASE_URL must use HTTPS in production.");
}
const supportUrl = process.env.NEXT_PUBLIC_TRACKING_SUPPORT_URL;
if (supportUrl !== undefined) {
  const parsedSupportUrl = new URL(supportUrl);
  if (
    (parsedSupportUrl.protocol !== "https:" &&
      parsedSupportUrl.protocol !== "mailto:") ||
    parsedSupportUrl.searchParams.has("token") ||
    parsedSupportUrl.searchParams.has("access_token")
  ) {
    throw new Error("NEXT_PUBLIC_TRACKING_SUPPORT_URL is not safe.");
  }
}
const apiOrigin = apiBaseUrl === undefined ? undefined : new URL(apiBaseUrl).origin;
const websocketOrigin =
  apiOrigin === undefined
    ? undefined
    : apiOrigin.replace(/^http:/, "ws:").replace(/^https:/, "wss:");
const connectSources = ["'self'", apiOrigin, websocketOrigin]
  .filter((value): value is string => value !== undefined)
  .join(" ");
const trackingCsp = [
  "default-src 'self'",
  `script-src 'self' 'unsafe-inline'${process.env.NODE_ENV === "development" ? " 'unsafe-eval'" : ""}`,
  "style-src 'self' 'unsafe-inline'",
  "object-src 'none'",
  "base-uri 'none'",
  "frame-ancestors 'none'",
  "form-action 'none'",
  "img-src 'self' data:",
  "font-src 'self'",
  `connect-src ${connectSources}`,
].join("; ");
const operationsCsp = [
  "default-src 'self'",
  `script-src 'self' 'unsafe-inline'${process.env.NODE_ENV === "development" ? " 'unsafe-eval'" : ""}`,
  "style-src 'self' 'unsafe-inline'",
  "object-src 'none'",
  "base-uri 'none'",
  "frame-ancestors 'none'",
  "form-action 'self'",
  "img-src 'self' data:",
  "font-src 'self'",
  `connect-src ${connectSources}`,
].join("; ");

const nextConfig: NextConfig = {
  reactStrictMode: true,
  logging: {
    incomingRequests: {
      ignore: [/^\/track(?:\/|$)/],
    },
  },
  async rewrites() {
    return [
      {
        source: "/track/:token",
        destination: "/track",
      },
    ];
  },
  async headers() {
    return [
      {
        source: "/track/:path*",
        headers: [
          { key: "Cache-Control", value: "no-store, private" },
          { key: "Pragma", value: "no-cache" },
          { key: "Referrer-Policy", value: "no-referrer" },
          { key: "X-Robots-Tag", value: "noindex, nofollow, noarchive" },
          { key: "X-Content-Type-Options", value: "nosniff" },
          {
            key: "Permissions-Policy",
            value:
              "geolocation=(), camera=(), microphone=(), payment=(), usb=()",
          },
          { key: "Content-Security-Policy", value: trackingCsp },
        ],
      },
      {
        source: "/ops/:path*",
        headers: [
          { key: "Cache-Control", value: "no-store, private" },
          { key: "Pragma", value: "no-cache" },
          { key: "Referrer-Policy", value: "no-referrer" },
          { key: "X-Robots-Tag", value: "noindex, nofollow, noarchive" },
          { key: "X-Content-Type-Options", value: "nosniff" },
          {
            key: "Permissions-Policy",
            value:
              "geolocation=(), camera=(), microphone=(), payment=(), usb=()",
          },
          { key: "Content-Security-Policy", value: operationsCsp },
        ],
      },
    ];
  },
};

export default nextConfig;
