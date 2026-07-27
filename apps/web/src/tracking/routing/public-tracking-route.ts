export type PublicTrackingRoute =
  | { readonly kind: "tracking"; readonly token: string }
  | { readonly kind: "not-found" };

const TRACKING_PATH = /^\/track\/([A-Za-z0-9_-]{43})\/?$/;

export function parsePublicTrackingPathname(
  pathname: string,
): PublicTrackingRoute {
  const match = TRACKING_PATH.exec(pathname);
  return match === null
    ? { kind: "not-found" }
    : { kind: "tracking", token: match[1] };
}
