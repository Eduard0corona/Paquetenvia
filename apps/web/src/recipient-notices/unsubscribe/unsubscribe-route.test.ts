import { describe, expect, it } from "vitest";
import { parseRecipientUnsubscribePathname } from "./unsubscribe-route";

const token = `AAE${"A".repeat(83)}-_`;

describe("the /avisos/baja/:token address", () => {
  it("reads one 88-character Base64URL token", () => {
    expect(token).toHaveLength(88);
    expect(parseRecipientUnsubscribePathname(`/avisos/baja/${token}`)).toEqual({ kind: "unsubscribe", token });
    expect(parseRecipientUnsubscribePathname(`/avisos/baja/${token}/`)).toEqual({ kind: "unsubscribe", token });
  });

  it("is an invalid link for anything else, with no request to make", () => {
    for (const pathname of [
      "/avisos/baja",
      "/avisos/baja/",
      `/avisos/baja/${token.slice(1)}`,
      `/avisos/baja/${token}A`,
      `/avisos/baja/${token.slice(1)}=`,
      `/avisos/baja/${token}/extra`,
      `/track/${token}`,
    ]) {
      expect(parseRecipientUnsubscribePathname(pathname), pathname).toEqual({ kind: "invalid" });
    }
  });
});
