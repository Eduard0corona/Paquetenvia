import { describe, expect, it, vi } from "vitest";
import type { DriverSession } from "../session/driver-session";
import { createDriverDispatchContactApi } from "./dispatch-contact-api";
import { createDriverStopsApi } from "./driver-stops-api";
import { createExternalOffersApi } from "./external-offers-api";
import { createDriverOrganizationRolesApi } from "./organization-roles-api";
import { createDriverVoiceApi } from "./voice-api";

/**
 * DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 (owner answer E3): a session the phone only remembers is
 * not confirmed by the server, and the browser's cookie may belong to someone else, so no driver
 * client ever reaches the network with it. Each one fails the way a lost connection does, so the
 * screen falls back to what the partition saved.
 */
const remembered: DriverSession = {
  organizationId: "11111111-1111-4111-8111-111111111111",
  cacheNamespace: "N".repeat(32),
  credentialMode: "cookie",
  getCsrfToken: () => {
    throw new Error("a remembered session has no CSRF token");
  },
  remembered: true,
};
const baseUrl = "https://api.synthetic.test";

describe("driver clients with a remembered session", () => {
  it("never fetch and fail as a lost connection", async () => {
    const fetch = vi.fn<typeof globalThis.fetch>();

    await expect(
      createDriverStopsApi({ baseUrl, session: remembered, fetch }).listStops(),
    ).rejects.toMatchObject({ category: "recoverable" });
    await expect(
      createDriverStopsApi({ baseUrl, session: remembered, fetch }).listStops(undefined, { details: false }),
    ).rejects.toMatchObject({ category: "recoverable" });
    await expect(
      createDriverOrganizationRolesApi({ baseUrl, session: remembered, fetch }).getMyRoles(),
    ).rejects.toMatchObject({ category: "recoverable" });
    await expect(
      createDriverDispatchContactApi({ baseUrl, session: remembered, fetch }).getMyDispatchContact(),
    ).rejects.toMatchObject({ category: "recoverable" });
    await expect(createExternalOffersApi(baseUrl, remembered, fetch).list()).rejects.toMatchObject({
      category: "recoverable",
    });
    await expect(
      createExternalOffersApi(baseUrl, remembered, fetch).accept(
        "22222222-2222-4222-8222-222222222222",
        "33333333-3333-4333-8333-333333333333",
      ),
    ).rejects.toMatchObject({ category: "recoverable" });
    const voice = createDriverVoiceApi(baseUrl, remembered, fetch);
    await expect(voice.getPhone()).rejects.toMatchObject({ category: "network" });
    await expect(
      voice.requestRecipientCall(
        "22222222-2222-4222-8222-222222222222",
        "33333333-3333-4333-8333-333333333333",
      ),
    ).rejects.toMatchObject({ category: "network" });

    expect(fetch).not.toHaveBeenCalled();
  });
});
