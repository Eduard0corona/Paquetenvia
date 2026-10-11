import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

/**
 * UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 rules that live in component wiring:
 * the stop details leave the phone on logout, the delivery never goes before
 * the cash, and the cash step and "Cómo llegar" appear where the design puts them.
 */
const read = (path: string) => readFileSync(path, "utf8");
const account = read("src/driver/components/driver-account.tsx");
const experience = read("src/driver/components/driver-stops-experience.tsx");
const cash = read("src/driver/components/driver-cod-collection.tsx");
const details = read("src/driver/components/driver-stop-details.tsx");

describe("driver stop details retention", () => {
  it("stops every controller, signs out, then wipes the saved details before leaving", () => {
    const started = account.indexOf("window.dispatchEvent(new Event(driverLogoutStartedEvent))");
    const signOut = account.indexOf("await signOutInstalledSession(");
    expect(started).toBeGreaterThan(0);
    expect(signOut).toBeGreaterThan(started);
    expect(account).toMatch(
      /assign: \(url: string\) => \{\s*void forgetDriverStopDetails\(\)\s*\.catch\(\(\) => undefined\)\s*\.finally\(\(\) => window\.location\.assign\(url\)\);/,
    );
    expect(account).toMatch(/signOutInstalledSession\(\s*current,\s*window,\s*leave,/);

    const listener = experience.indexOf("window.addEventListener(driverLogoutStartedEvent, stopControllers)");
    expect(listener).toBeGreaterThan(0);
    for (const dispose of [
      "void controllerRef.current?.dispose();",
      "void operationsControllerRef.current?.dispose();",
      "externalOffersControllerRef.current?.dispose();",
    ]) {
      expect(experience).toContain(dispose);
    }
  });

  it("keeps the wipe to stop snapshots: the offline queue survives a logout", () => {
    const cache = read("src/driver/cache/driver-stops-cache.ts");
    const forget = cache.slice(
      cache.indexOf("export async function forgetDriverStopDetails"),
      cache.indexOf("export function openDriverDatabase"),
    );
    expect(forget).toContain("database.transaction(DriverStopsStoreName, \"readwrite\")");
    expect(forget).toContain(".objectStore(DriverStopsStoreName).clear()");
    expect(forget).not.toContain("DriverOfflineOperationsStoreName");
    expect(forget).not.toContain("DriverProofBlobsStoreName");
  });
});

describe("cash before delivery", () => {
  it("shows the cash step first and keeps the delivery photo disabled until the cash is saved", () => {
    const cashStep = experience.indexOf("<DriverCodCollection");
    const photoStep = experience.indexOf("<DriverProofCapture");
    expect(cashStep).toBeGreaterThan(0);
    expect(photoStep).toBeGreaterThan(cashStep);
    expect(experience).toMatch(
      /const cashFirst =\s*kind === "DELIVERY_PROOF" &&\s*stop\.cod !== null &&\s*stop\.codCollection !== "recorded" &&\s*stop\.codCollection !== "queued";/,
    );
    expect(experience).toContain("disabled={cashFirst}");
    expect(experience).toContain("Registra el cobro antes de confirmar la entrega.");
  });

  it("offers Recibí $X only at the door, with the amount in the button and the confirmation", () => {
    expect(cash).toContain('stop.projectedStatus !== "DELIVERING"');
    expect(cash).toContain("{codReceivedLabel(amountCents)}");
    expect(cash).toContain("...codCollectionConfirmation(amountCents),");
    expect(cash).toContain("onConfirm: () => void onCollect(),");
    // The driver never types an amount or a reference.
    expect(cash).not.toContain("<input");
  });
});

describe("design placement", () => {
  it("leads the detail with who and where, then Cómo llegar", () => {
    const lead = experience.indexOf("<StopLead stop={stop} />");
    const heading = experience.indexOf("{stop.order_public_id}", lead);
    const navigation = experience.indexOf("<NavigationLink stop={stop} />");
    expect(lead).toBeGreaterThan(0);
    expect(heading).toBeGreaterThan(lead);
    expect(navigation).toBeGreaterThan(heading);
    expect(details).toContain(">\n        Cómo llegar\n      </a>");
  });

  it("never shows internal codes, ids or the legacy summary on the stop", () => {
    expect(experience).not.toContain("{stop.address_summary}");
    expect(details).not.toContain("order_id}<");
    expect(details).not.toMatch(/>\s*COD\s*</);
  });
});

describe("AI-07 record", () => {
  it("declares the stop actions, the retention and the cash step under the owner decision", () => {
    const aiSeven = read("../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml");
    const route = aiSeven.slice(
      aiSeven.indexOf("\n  /driver/stops/:id:\n"),
      aiSeven.indexOf("\n  /track/:token:\n") + 1,
    );
    expect(route).toContain("- UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11");
    expect(route).toContain("    - navigate\n");
    expect(route).toContain("    - cod_collection\n");
    const screen = aiSeven.slice(
      aiSeven.indexOf("\n  driver_stop:\n"),
      aiSeven.indexOf("\n  external_driver_offers:\n"),
    );
    for (const section of ["    details:\n", "    navigation:\n", "    cod_collection:\n"]) {
      expect(screen).toContain(section);
    }
    expect(screen).toContain("https://www.google.com/maps/search/?api=1&query=<latitude>,<longitude>");
    expect(screen).toContain("logging out wipes every saved snapshot of the phone before leaving");
  });
});
