import { NextResponse } from "next/server";

export function proxy() {
  const response = NextResponse.next();
  response.headers.set("Cache-Control", "no-store, private");
  response.headers.set("Pragma", "no-cache");
  response.headers.set("Referrer-Policy", "no-referrer");
  response.headers.set("X-Robots-Tag", "noindex, nofollow, noarchive");
  response.headers.set("X-Content-Type-Options", "nosniff");
  response.headers.set(
    "Permissions-Policy",
    "geolocation=(), camera=(), microphone=(), payment=(), usb=()",
  );
  return response;
}

export const config = {
  matcher: ["/track", "/track/:path*"],
};
