const CACHE_NAME = "paquetenvia-driver-shell-v3";
const DRIVER_STOPS_SHELL_KEY = "/driver/stops";
const OWNED_CACHE_PREFIXES = [
  "paquetenvia-driver-shell-",
  "paquetenvia-foundation-",
];
const INSTALL_ASSETS = ["/manifest.webmanifest"];

self.addEventListener("install", (event) => {
  event.waitUntil(
    caches
      .open(CACHE_NAME)
      .then((cache) =>
        Promise.allSettled(INSTALL_ASSETS.map((asset) => cache.add(asset))),
      )
      .then(() => self.skipWaiting()),
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(
          keys
            .filter(
              (key) =>
                key !== CACHE_NAME &&
                OWNED_CACHE_PREFIXES.some((prefix) => key.startsWith(prefix)),
            )
            .map((key) => caches.delete(key)),
        ),
      )
      .then(() => self.clients.claim()),
  );
});

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (request.method !== "GET" || mustUseNetworkOnly(request)) {
    return;
  }

  const url = new URL(request.url);
  if (request.mode === "navigate" && isDriverStopsNavigation(url)) {
    event.respondWith(driverStopsNetworkFirst(request));
    return;
  }

  if (isStaticAsset(url)) {
    event.respondWith(cacheFirst(request));
  }
});

function mustUseNetworkOnly(request) {
  const url = new URL(request.url);
  return (
    url.origin !== self.location.origin ||
    request.headers.has("Authorization") ||
    url.pathname.startsWith("/api/v1/") ||
    url.pathname.startsWith("/hubs/") ||
    url.pathname.includes("/proofs/") ||
    hasSignedUrlParameters(url)
  );
}

function hasSignedUrlParameters(url) {
  const sensitiveNames = [
    "x-amz-algorithm",
    "x-amz-credential",
    "x-amz-signature",
    "x-goog-signature",
    "signature",
    "token",
  ];
  const names = [...url.searchParams.keys()].map((name) => name.toLowerCase());
  return sensitiveNames.some((name) => names.includes(name));
}

function isDriverStopsNavigation(url) {
  return (
    url.origin === self.location.origin &&
    /^\/driver\/stops(?:\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})?\/?$/.test(
      url.pathname,
    )
  );
}

function isStaticAsset(url) {
  return (
    url.origin === self.location.origin &&
    (url.pathname.startsWith("/_next/static/") ||
      url.pathname === "/manifest.webmanifest" ||
      url.pathname.startsWith("/icons/"))
  );
}

async function driverStopsNetworkFirst(request) {
  const cache = await caches.open(CACHE_NAME);
  try {
    const response = await fetch(request);
    if (isSafeCacheableResponse(response)) {
      await cache.put(request, response.clone());
      if (isDriverStopsListNavigation(new URL(request.url))) {
        await cache.put(DRIVER_STOPS_SHELL_KEY, response.clone());
      }
    }
    return response;
  } catch {
    const cached = await cache.match(request);
    if (cached) return cached;
    const shell = await cache.match(DRIVER_STOPS_SHELL_KEY);
    if (shell) return shell;
    return new Response("Offline", {
      status: 503,
      headers: { "Content-Type": "text/plain; charset=utf-8" },
    });
  }
}

function isDriverStopsListNavigation(url) {
  return (
    url.origin === self.location.origin &&
    /^\/driver\/stops\/?$/.test(url.pathname)
  );
}

async function cacheFirst(request) {
  const cache = await caches.open(CACHE_NAME);
  const cached = await cache.match(request);
  if (cached) return cached;
  const response = await fetch(request);
  if (isSafeCacheableResponse(response)) {
    await cache.put(request, response.clone());
  }
  return response;
}

function isSafeCacheableResponse(response) {
  return (
    response.ok &&
    response.type !== "opaque" &&
    !response.headers.has("Set-Cookie")
  );
}
