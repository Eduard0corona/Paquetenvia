/**
 * DRV-PROOF-PHOTO-REDUCE-2026-10-20: reads and cleans the marker structure of a
 * JPEG (ITU-T T.81 annex B) without decoding its pixels. The proof photo the
 * phone stores keeps only what a decoder needs - the JFIF APP0 header, tables,
 * frame, scans and EOI - and no metadata: no APP1 to APP15 segment (EXIF with
 * GPS, XMP, ICC profiles, Photoshop/IPTC), no comment and nothing after EOI.
 * Any structure it does not understand fails closed with JpegStructureError.
 */

const SOI = 0xd8;
const EOI = 0xd9;
const SOS = 0xda;
const APP0 = 0xe0;
const APP15 = 0xef;
const COM = 0xfe;
const JFIF = [0x4a, 0x46, 0x49, 0x46, 0x00]; // "JFIF\0"

export class JpegStructureError extends Error {
  public constructor() {
    super("The photo is not a well-formed JPEG.");
    this.name = "JpegStructureError";
  }
}

export interface JpegStructure {
  /** Frame size from the first SOFn header, in pixels. */
  readonly width: number;
  readonly height: number;
  /** Every metadata segment marker found, in file order (for example 0xe1 for EXIF or XMP). */
  readonly metadataMarkers: readonly number[];
  /** Bytes after EOI (camera trailers can carry metadata there). */
  readonly trailingBytes: number;
}

interface JpegPart {
  /** null for entropy-coded scan data. */
  readonly marker: number | null;
  readonly start: number;
  readonly end: number;
}

function isFrameMarker(marker: number): boolean {
  // SOF0-SOF15 except DHT (C4), JPG (C8) and DAC (CC).
  return (
    marker >= 0xc0 &&
    marker <= 0xcf &&
    marker !== 0xc4 &&
    marker !== 0xc8 &&
    marker !== 0xcc
  );
}

function isMetadata(bytes: Uint8Array, part: JpegPart): boolean {
  const marker = part.marker;
  if (marker === null) return false;
  if (marker === COM || (marker > APP0 && marker <= APP15)) return true;
  if (marker !== APP0) return false;
  // Only the JFIF header stays; any other APP0 (JFXX thumbnails, AVI1, ...) is metadata.
  const payload = part.start + 4;
  return (
    part.end - payload < JFIF.length ||
    !JFIF.every((value, offset) => bytes[payload + offset] === value)
  );
}

/** Splits a whole JPEG into its parts; throws on anything malformed or truncated. */
function readParts(bytes: Uint8Array): { parts: JpegPart[]; end: number } {
  if (bytes.length < 4 || bytes[0] !== 0xff || bytes[1] !== SOI) {
    throw new JpegStructureError();
  }
  const parts: JpegPart[] = [{ marker: SOI, start: 0, end: 2 }];
  let index = 2;
  let inScan = false;
  for (;;) {
    if (inScan) {
      // Entropy-coded data runs to the next marker that is neither a stuffed
      // 0xFF00, a restart marker (RST0-RST7) nor a fill byte.
      const dataStart = index;
      for (;;) {
        if (index + 1 >= bytes.length) throw new JpegStructureError();
        if (bytes[index] !== 0xff) {
          index += 1;
          continue;
        }
        const next = bytes[index + 1];
        if (next === 0x00 || (next >= 0xd0 && next <= 0xd7)) {
          index += 2;
          continue;
        }
        if (next === 0xff) {
          index += 1;
          continue;
        }
        break;
      }
      if (index > dataStart) parts.push({ marker: null, start: dataStart, end: index });
      inScan = false;
    }
    if (index + 1 >= bytes.length || bytes[index] !== 0xff) {
      throw new JpegStructureError();
    }
    // Fill bytes (0xFF) may precede a marker; they carry nothing and are dropped.
    while (index + 1 < bytes.length && bytes[index + 1] === 0xff) index += 1;
    if (index + 1 >= bytes.length) throw new JpegStructureError();
    const marker = bytes[index + 1];
    if (marker === EOI) {
      parts.push({ marker, start: index, end: index + 2 });
      return { parts, end: index + 2 };
    }
    // SOI again, TEM, RSTn outside a scan or a reserved 0x00 marker are not valid here.
    if (marker === SOI || marker === 0x01 || marker === 0x00 || (marker >= 0xd0 && marker <= 0xd7)) {
      throw new JpegStructureError();
    }
    if (index + 3 >= bytes.length) throw new JpegStructureError();
    const length = (bytes[index + 2] << 8) | bytes[index + 3];
    const end = index + 2 + length;
    if (length < 2 || end > bytes.length) throw new JpegStructureError();
    parts.push({ marker, start: index, end });
    index = end;
    if (marker === SOS) inScan = true;
  }
}

/** The frame size and the metadata a JPEG carries, without decoding pixels. */
export function readJpegStructure(bytes: Uint8Array): JpegStructure {
  const { parts, end } = readParts(bytes);
  const frame = parts.find((part) => part.marker !== null && isFrameMarker(part.marker));
  if (!frame || frame.end - frame.start < 2 + 8 || !parts.some((part) => part.marker === SOS)) {
    throw new JpegStructureError();
  }
  // Frame header: Lf (2), P (1), Y (2), X (2), Nf (1), ...
  const height = (bytes[frame.start + 5] << 8) | bytes[frame.start + 6];
  const width = (bytes[frame.start + 7] << 8) | bytes[frame.start + 8];
  if (width < 1 || height < 1) throw new JpegStructureError();
  return Object.freeze({
    width,
    height,
    metadataMarkers: Object.freeze(
      parts.filter((part) => isMetadata(bytes, part)).map((part) => part.marker as number),
    ),
    trailingBytes: bytes.length - end,
  });
}

/**
 * The same JPEG without metadata: every APP1-APP15 segment, every comment, any
 * APP0 that is not JFIF, fill bytes and anything after EOI are left out; all
 * other bytes are copied unchanged, so the pixels decode exactly as before.
 */
export function stripJpegMetadata(bytes: Uint8Array): Uint8Array<ArrayBuffer> {
  const { parts } = readParts(bytes);
  const kept = parts.filter((part) => !isMetadata(bytes, part));
  const output = new Uint8Array(
    kept.reduce((total, part) => total + part.end - part.start, 0),
  );
  let offset = 0;
  for (const part of kept) {
    output.set(bytes.subarray(part.start, part.end), offset);
    offset += part.end - part.start;
  }
  return output;
}
