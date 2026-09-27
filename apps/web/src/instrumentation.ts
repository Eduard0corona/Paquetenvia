import { resolveConfiguredConnectSources } from "./security/security-headers";

/**
 * Runs once when the server starts. Validates the runtime CSP configuration so
 * an invalid PAQUETERIA_CSP_CONNECT_SOURCES is reported once, at startup, with
 * a clear message instead of on every request. Next.js then answers every
 * request with 500 (fail closed), and src/proxy.ts also refuses to load with
 * it, so the web app never serves pages under a weaker policy.
 */
export function register(): void {
  resolveConfiguredConnectSources();
}
