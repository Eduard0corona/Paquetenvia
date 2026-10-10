import { describe, expect, it } from "vitest";
import {
  checkDriverProofFile,
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

  it.each([
    [undefined, "missing"],
    [new Blob([], { type: "image/png" }), "empty"],
    [new Blob(["x"], { type: "image/gif" }), "unsupported-type"],
    [new Blob(["x"], { type: "image/jpeg;charset=utf-8" }), "unsupported-type"],
    [
      new Blob([new Uint8Array(MaximumDriverProofBytes + 1)], {
        type: "image/png",
      }),
      "too-large",
    ],
  ] as const)(
    "the preview check names the same failure the queue validation throws",
    async (blob, category) => {
      expect(checkDriverProofFile(blob)).toBe(category);
      await expect(validateDriverProof(blob)).rejects.toMatchObject({ category });
    },
  );

  it("the preview check accepts what the queue validation accepts", async () => {
    const blob = new Blob([new Uint8Array([0xff, 0xd8, 0xff])], {
      type: "image/jpeg",
    });
    expect(checkDriverProofFile(blob)).toBeNull();
    await expect(validateDriverProof(blob)).resolves.toMatchObject({
      contentType: "image/jpeg",
      sizeBytes: 3,
    });
  });
});
