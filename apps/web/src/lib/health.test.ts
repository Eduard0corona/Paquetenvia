import { describe, expect, it } from "vitest";
import { getLocalHealthState } from "./health";

describe("getLocalHealthState", () => {
  it("returns the stable local readiness state", () => {
    expect(getLocalHealthState()).toEqual({
      label: "listo",
      status: "healthy",
    });
  });
});
