/**
 * Test support only (DRV-PROOF-PHOTO-REDUCE-2026-10-20): the EXIF GPS photo fixture and
 * builders of structurally valid JPEG bytes for the reduction and JPEG structure tests.
 */
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/**
 * DRV-PROOF-PHOTO-REDUCE-2026-10-20 fixture: a 2000x1000 two-colour JPEG written by
 * Chromium's canvas encoder (which adds an ICC profile), with an EXIF APP1 (Make,
 * Model, Orientation 6 and a GPS IFD), an XMP APP1 and a comment added. The make
 * and model are invented and the coordinates are impossible (77 and 88 seconds of
 * arc): no real camera, photo or place.
 */
export const exifGpsFixture = new Uint8Array(
  readFileSync(resolve(process.cwd(), "src/driver/proofs/proof-photo-exif-gps.fixture.jpg")),
);

/** The bytes as a string of one character per byte, to search for markers and text. */
export function latin1(bytes: Uint8Array): string {
  return new TextDecoder("latin1").decode(bytes);
}

export function segment(marker: number, payload: readonly number[] | Uint8Array): number[] {
  const length = payload.length + 2;
  return [0xff, marker, length >> 8, length & 0xff, ...payload];
}

const jfif = [0x4a, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00];

/** A structurally valid baseline JPEG (pixels are not decodable; nothing here decodes them). */
export function syntheticJpeg(
  options: {
    readonly width?: number;
    readonly height?: number;
    readonly before?: readonly number[];
    readonly scan?: readonly number[];
    readonly after?: readonly number[];
  } = {},
): Uint8Array<ArrayBuffer> {
  const width = options.width ?? 3;
  const height = options.height ?? 2;
  return new Uint8Array([
    0xff, 0xd8,
    ...segment(0xe0, jfif),
    ...(options.before ?? []),
    ...segment(0xdb, [0x00, ...new Array<number>(64).fill(1)]),
    ...segment(0xc0, [0x08, height >> 8, height & 0xff, width >> 8, width & 0xff, 0x01, 0x01, 0x11, 0x00]),
    ...segment(0xc4, [0x00, ...new Array<number>(16).fill(0)]),
    ...segment(0xda, [0x01, 0x01, 0x00, 0x00, 0x3f, 0x00]),
    ...(options.scan ?? [0x12, 0x34]),
    0xff, 0xd9,
    ...(options.after ?? []),
  ]);
}

export const ascii = (text: string): number[] => [...new TextEncoder().encode(text)];
