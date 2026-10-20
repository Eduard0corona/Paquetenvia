import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it, vi } from "vitest";
import { readJpegStructure } from "./jpeg-structure";
import {
  decodeDriverProofPhoto,
  DriverProofPhotoError,
  DriverProofPhotoJpegQuality,
  DriverProofPhotoMaximumSide,
  driverProofPhotoTargetSize,
  reduceDriverProofPhoto,
  type DriverProofPhotoSize,
  type DriverProofPhotoSource,
} from "./proof-photo-reduce";
import { ascii, exifGpsFixture, latin1, segment, syntheticJpeg } from "./proof-photo.fixtures";

/**
 * A stand-in decoder: the browser's decoding and canvas encoding run in the real
 * Chromium of the DriverOfflineOperationsPwa Playwright suite. `encoded` is what the
 * browser's encoder would return for the requested size.
 */
function decoder(
  natural: DriverProofPhotoSize,
  encoded: (size: DriverProofPhotoSize) => Blob | Promise<Blob>,
) {
  const encodeJpeg = vi.fn<(size: DriverProofPhotoSize, quality: number) => Promise<Blob>>(
    async (size) => encoded(size),
  );
  const close = vi.fn();
  const decode = vi.fn<(photo: Blob) => Promise<DriverProofPhotoSource>>(async () => ({
    ...natural,
    encodeJpeg,
    close,
  }));
  return { decode, encodeJpeg, close };
}

const picked = new Blob([exifGpsFixture], { type: "image/jpeg" });

function jpegBlob(size: DriverProofPhotoSize, before: readonly number[] = []): Blob {
  return new Blob([syntheticJpeg({ ...size, before })], { type: "image/jpeg" });
}

describe("proof photo size (DRV-PROOF-PHOTO-REDUCE-2026-10-20)", () => {
  it.each([
    [{ width: 4000, height: 3000 }, { width: 1600, height: 1200 }],
    [{ width: 3024, height: 4032 }, { width: 1200, height: 1600 }],
    [{ width: 1000, height: 2000 }, { width: 800, height: 1600 }],
    [{ width: 1601, height: 1601 }, { width: 1600, height: 1600 }],
    [{ width: 1600, height: 900 }, { width: 1600, height: 900 }],
    [{ width: 800, height: 600 }, { width: 800, height: 600 }],
    [{ width: 1, height: 1 }, { width: 1, height: 1 }],
    [{ width: 1, height: 5000 }, { width: 1, height: 1600 }],
    [{ width: 9000, height: 2 }, { width: 1600, height: 1 }],
  ])("reduces %o to %o: longest side at most 1600 px, never enlarged, same aspect", (natural, reduced) => {
    expect(driverProofPhotoTargetSize(natural)).toEqual(reduced);
  });

  it.each([
    { width: 0, height: 10 },
    { width: 10, height: -1 },
    { width: 10.5, height: 10 },
    { width: Number.NaN, height: 10 },
  ])("refuses a decoded size of %o", (natural) => {
    expect(() => driverProofPhotoTargetSize(natural)).toThrow(DriverProofPhotoError);
  });

  it("matches the owner's answer: 1600 px per side at quality 80 %", () => {
    expect(DriverProofPhotoMaximumSide).toBe(1600);
    expect(DriverProofPhotoJpegQuality).toBe(0.8);
  });
});

describe("reduceDriverProofPhoto (DRV-PROOF-PHOTO-REDUCE-2026-10-20)", () => {
  it("decodes the picked photo once and encodes it at the reduced size and quality 0.8", async () => {
    const { decode, encodeJpeg, close } = decoder({ width: 1000, height: 2000 }, (size) => jpegBlob(size));

    const reduced = await reduceDriverProofPhoto(picked, decode);

    expect(decode).toHaveBeenCalledExactlyOnceWith(picked);
    expect(encodeJpeg).toHaveBeenCalledExactlyOnceWith({ width: 800, height: 1600 }, 0.8);
    expect(close).toHaveBeenCalledOnce();
    expect(reduced.type).toBe("image/jpeg");
    expect(readJpegStructure(new Uint8Array(await reduced.arrayBuffer()))).toEqual({
      width: 800,
      height: 1600,
      metadataMarkers: [],
      trailingBytes: 0,
    });
  });

  it("removes the metadata a browser encoder writes (Chromium ICC, Safari EXIF) before anything hashes it", async () => {
    const encoderMetadata = [
      ...segment(0xe1, ascii("Exif\0\0MM\0*")),
      ...segment(0xe2, ascii("ICC_PROFILE\0")),
    ];
    const { decode } = decoder({ width: 1600, height: 1200 }, (size) =>
      jpegBlob(size, encoderMetadata),
    );

    const reduced = new Uint8Array(await (await reduceDriverProofPhoto(picked, decode)).arrayBuffer());

    expect(reduced).toEqual(syntheticJpeg({ width: 1600, height: 1200 }));
    expect(latin1(reduced)).not.toContain("Exif");
    expect(latin1(reduced)).not.toContain("ICC_PROFILE");
  });

  it("refuses an encoder result of another size, such as the picked EXIF GPS file itself", async () => {
    // Whatever the browser does, the result must be a new JPEG of the reduced size.
    const { decode } = decoder({ width: 2000, height: 1000 }, () => picked);

    await expect(reduceDriverProofPhoto(picked, decode)).rejects.toBeInstanceOf(DriverProofPhotoError);
  });

  it.each([
    ["the decoder refuses the photo", () => Promise.reject(new DOMException("bad", "InvalidStateError"))],
    ["the phone runs out of memory", () => Promise.reject(new RangeError("memory"))],
  ])("fails with DriverProofPhotoError when %s", async (_case, decode) => {
    await expect(reduceDriverProofPhoto(picked, decode)).rejects.toBeInstanceOf(DriverProofPhotoError);
  });

  it.each([
    ["writes PNG instead of JPEG", (size: DriverProofPhotoSize) =>
      new Blob([syntheticJpeg(size)], { type: "image/png" })],
    ["writes a frame of another size", () => jpegBlob({ width: 10, height: 10 })],
    ["writes bytes that are not a JPEG", () =>
      new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], { type: "image/jpeg" })],
    ["throws", () => Promise.reject(new Error("encoder"))],
  ])("fails with DriverProofPhotoError and frees the photo when the encoder %s", async (_case, encoded) => {
    const { decode, close } = decoder({ width: 3000, height: 2000 }, encoded);

    await expect(reduceDriverProofPhoto(picked, decode)).rejects.toBeInstanceOf(DriverProofPhotoError);
    expect(close).toHaveBeenCalledOnce();
  });

  it("fails closed where the platform cannot decode images at all", async () => {
    expect(typeof globalThis.createImageBitmap).toBe("undefined");

    await expect(reduceDriverProofPhoto(picked)).rejects.toBeInstanceOf(DriverProofPhotoError);
    await expect(decodeDriverProofPhoto(picked)).rejects.toBeDefined();
  });
});

describe("proof photo reduction policy", () => {
  const source = readFileSync(
    resolve(process.cwd(), "src/driver/proofs/proof-photo-reduce.ts"),
    "utf8",
  );

  it("decodes in the photo's visual orientation and encodes JPEG through a canvas", () => {
    expect(source).toContain('createImageBitmap(photo, { imageOrientation: "from-image" })');
    expect(source).toContain("new OffscreenCanvas(size.width, size.height)");
    expect(source).toContain("canvas.convertToBlob({ type: DriverProofPhotoContentType, quality })");
    expect(source).toContain("canvas.toBlob(");
    expect(source).toContain('export const DriverProofPhotoContentType = "image/jpeg";');
  });

  it("keeps the photo in memory only, never logs it and never returns the picked file", () => {
    expect(source).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\.|console\./);
    expect(source).not.toMatch(/FileReader|readAsDataURL|toDataURL/);
    expect(source).not.toMatch(/return photo\b/);
  });
});
