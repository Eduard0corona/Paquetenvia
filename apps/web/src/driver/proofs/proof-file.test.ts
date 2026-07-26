import { describe, expect, it } from "vitest";
import {
  MaximumDriverProofBytes,
  validateDriverProof,
} from "./proof-file";

describe("driver proof validation", () => {
  it.each([
    ["image/png", new Uint8Array([0x89, 0x50, 0x4e, 0x47])],
    ["image/jpeg", new Uint8Array([0xff, 0xd8, 0xff, 0xe0])],
  ] as const)("hashes the original %s Blob without transforming or encoding it", async (
    contentType,
    bytes,
  ) => {
    const blob = new Blob([bytes], { type: contentType });
    const result = await validateDriverProof(blob);
    expect(result.blob).toBe(blob);
    expect(result.sha256).toMatch(/^[a-f0-9]{64}$/);
    expect(result).not.toHaveProperty("base64");
    expect(result).not.toHaveProperty("fileName");
  });

  it("accepts a file exactly at the technical limit", async () => {
    const blob = new Blob([new Uint8Array(MaximumDriverProofBytes)], {
      type: "image/jpeg",
    });
    await expect(validateDriverProof(blob)).resolves.toMatchObject({
      blob,
      sizeBytes: MaximumDriverProofBytes,
      contentType: "image/jpeg",
    });
  });

  it.each([
    [new Blob([], { type: "image/png" }), "empty"],
    [new Blob(["x"], { type: "image/gif" }), "unsupported-type"],
    [
      new Blob([new Uint8Array(MaximumDriverProofBytes + 1)], {
        type: "image/jpeg",
      }),
      "too-large",
    ],
  ] as const)("rejects invalid evidence without partial output", async (blob, category) => {
    await expect(validateDriverProof(blob)).rejects.toMatchObject({ category });
  });
});
