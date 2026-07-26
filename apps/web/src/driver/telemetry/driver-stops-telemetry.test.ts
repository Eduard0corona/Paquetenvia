import { describe, expect, it } from "vitest";
import { driverStopCountBucket } from "./driver-stops-telemetry";

describe("driver stop telemetry", () => {
  it.each([
    [0, "0"],
    [1, "1"],
    [2, "2-5"],
    [5, "2-5"],
    [6, "6-20"],
    [20, "6-20"],
    [21, "21+"],
    [500, "21+"],
  ])("buckets %i without identifiers", (count, expected) => {
    expect(driverStopCountBucket(count)).toBe(expected);
  });
});
