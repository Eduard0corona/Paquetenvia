// Fails a production build that still contains the synthetic /dev portal.
// Runs after `next build` (package.json "build"). A DevSynthetic image that
// opts in with PAQUETERIA_DEV_PORTAL_BUILD=true is exempt by design.
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";

const root = join(process.cwd(), ".next");
if (process.env.PAQUETERIA_DEV_PORTAL_BUILD === "true") {
  console.log("verify-production-bundle: /dev portal explicitly included.");
  process.exit(0);
}
if (!existsSync(root)) {
  console.error("verify-production-bundle: .next does not exist.");
  process.exit(1);
}

// Values that only the /dev portal ships to the browser.
const markers = [
  "local-dispatcher-mfa",
  "Synthetic dispatcher (MFA)",
  "local-driver-session",
];
const failures = [];

for (const manifest of [
  "app-path-routes-manifest.json",
  "server/app-paths-manifest.json",
]) {
  const path = join(root, manifest);
  if (existsSync(path) && /"\/dev(?:\/page)?"/.test(readFileSync(path, "utf8"))) {
    failures.push(`${manifest} declares the /dev route`);
  }
}

function scan(directory) {
  for (const entry of readdirSync(directory)) {
    const path = join(directory, entry);
    if (statSync(path).isDirectory()) {
      // cache/ holds compiler state and dev/ belongs to `next dev`.
      if (entry === "cache" || entry === "dev") continue;
      scan(path);
    } else if (/\.(js|mjs|cjs|json|html|rsc|body|txt|map)$/.test(entry)) {
      const text = readFileSync(path, "utf8");
      for (const marker of markers) {
        if (text.includes(marker)) failures.push(`${path} contains "${marker}"`);
      }
    }
  }
}
scan(root);

if (failures.length > 0) {
  console.error("verify-production-bundle: the /dev portal leaked into the build:");
  for (const failure of failures) console.error(`  ${failure}`);
  process.exit(1);
}
console.log("verify-production-bundle: no /dev portal in the production build.");
