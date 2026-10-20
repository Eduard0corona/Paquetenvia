import { describe, expect, it } from "vitest";
import {
  JpegStructureError,
  readJpegStructure,
  stripJpegMetadata,
} from "./jpeg-structure";
import { ascii, exifGpsFixture, latin1, segment, syntheticJpeg } from "./proof-photo.fixtures";

/** The EXIF fields of the fixture's APP1, read with a minimal little-endian TIFF reader. */
function readExif(bytes: Uint8Array) {
  const text = latin1(bytes);
  const start = text.indexOf("Exif\0\0");
  if (start < 0) return null;
  const tiff = new DataView(bytes.buffer, bytes.byteOffset + start + 6);
  expect(String.fromCharCode(tiff.getUint8(0), tiff.getUint8(1))).toBe("II");
  const entries = (offset: number) => {
    const values = new Map<number, { type: number; count: number; at: number }>();
    for (let index = 0; index < tiff.getUint16(offset, true); index += 1) {
      const entry = offset + 2 + index * 12;
      values.set(tiff.getUint16(entry, true), {
        type: tiff.getUint16(entry + 2, true),
        count: tiff.getUint32(entry + 4, true),
        at: entry + 8,
      });
    }
    return values;
  };
  const ascii = (value: { count: number; at: number }) => {
    const at = value.count > 4 ? tiff.getUint32(value.at, true) : value.at;
    return Array.from({ length: value.count - 1 }, (_, index) =>
      String.fromCharCode(tiff.getUint8(at + index)),
    ).join("");
  };
  const rationals = (value: { count: number; at: number }) => {
    const at = tiff.getUint32(value.at, true);
    return Array.from(
      { length: value.count },
      (_, index) => tiff.getUint32(at + index * 8, true) / tiff.getUint32(at + index * 8 + 4, true),
    );
  };
  const ifd0 = entries(tiff.getUint32(4, true));
  const gps = entries(tiff.getUint32(ifd0.get(0x8825)!.at, true));
  return {
    make: ascii(ifd0.get(0x010f)!),
    model: ascii(ifd0.get(0x0110)!),
    orientation: tiff.getUint16(ifd0.get(0x0112)!.at, true),
    latitudeRef: ascii(gps.get(0x0001)!),
    latitude: rationals(gps.get(0x0002)!),
    longitudeRef: ascii(gps.get(0x0003)!),
    longitude: rationals(gps.get(0x0004)!),
  };
}

describe("the EXIF GPS fixture (DRV-PROOF-PHOTO-REDUCE-2026-10-20)", () => {
  it("carries synthetic EXIF GPS, orientation, XMP, a comment and an ICC profile", () => {
    expect(readExif(exifGpsFixture)).toEqual({
      make: "Paquetenvia sintetica",
      model: "Fixture sin camara",
      orientation: 6,
      latitudeRef: "N",
      latitude: [11, 22, 77],
      longitudeRef: "W",
      longitude: [44, 55, 88],
    });
    const text = latin1(exifGpsFixture);
    expect(text).toContain("http://ns.adobe.com/xap/1.0/\0");
    expect(text).toContain('exif:GPSLatitude="11,22.77N"');
    expect(text).toContain("ICC_PROFILE\0");
    expect(readJpegStructure(exifGpsFixture)).toEqual({
      width: 2000,
      height: 1000,
      // EXIF, XMP, comment, then the encoder's ICC profile.
      metadataMarkers: [0xe1, 0xe1, 0xfe, 0xe2],
      trailingBytes: 0,
    });
  });

  it("loses every metadata segment and keeps every other byte when stripped", () => {
    const stripped = stripJpegMetadata(exifGpsFixture);

    expect(readJpegStructure(stripped)).toEqual({
      width: 2000,
      height: 1000,
      metadataMarkers: [],
      trailingBytes: 0,
    });
    expect(readExif(stripped)).toBeNull();
    const text = latin1(stripped);
    for (const marker of ["Exif", "GPS", "xap", "xmpmeta", "ICC_PROFILE", "Paquetenvia", "Fixture"]) {
      expect(text).not.toContain(marker);
    }
    // SOI and the JFIF header come first; the scan and EOI are untouched.
    expect([...stripped.subarray(0, 4)]).toEqual([0xff, 0xd8, 0xff, 0xe0]);
    expect(text.slice(6, 11)).toBe("JFIF\0");
    const scan = latin1(exifGpsFixture).lastIndexOf("\xff\xda");
    expect(text.endsWith(latin1(exifGpsFixture).slice(scan))).toBe(true);
    // Stripping again changes nothing.
    expect(stripJpegMetadata(stripped)).toEqual(stripped);
  });
});

describe("JPEG structure", () => {
  it("drops APP1 to APP15, comments, non-JFIF APP0 and trailers, and keeps the rest verbatim", () => {
    const metadata = [
      ...segment(0xe1, ascii("Exif\0\0GPS")),
      ...segment(0xe2, ascii("ICC_PROFILE\0")),
      ...segment(0xed, ascii("Photoshop 3.0\0")),
      ...segment(0xee, ascii("Adobe")),
      ...segment(0xef, [1]),
      ...segment(0xfe, ascii("comment")),
      ...segment(0xe0, ascii("JFXX\0thumbnail")),
    ];
    const input = syntheticJpeg({ before: metadata, after: ascii("trailer") });

    expect(readJpegStructure(input)).toEqual({
      width: 3,
      height: 2,
      metadataMarkers: [0xe1, 0xe2, 0xed, 0xee, 0xef, 0xfe, 0xe0],
      trailingBytes: 7,
    });
    expect(stripJpegMetadata(input)).toEqual(syntheticJpeg());
  });

  it("walks scan data with stuffed bytes, restart markers and fill bytes", () => {
    const scan = [0x01, 0xff, 0x00, 0x02, 0xff, 0xd0, 0x03, 0xff, 0xd7, 0x04, 0xff];
    const input = syntheticJpeg({ width: 1600, height: 1200, scan });

    expect(readJpegStructure(input)).toEqual({
      width: 1600,
      height: 1200,
      metadataMarkers: [],
      trailingBytes: 0,
    });
    expect(stripJpegMetadata(input)).toEqual(input);
  });

  it("finds metadata written between the scans of a progressive JPEG", () => {
    const progressive = new Uint8Array([
      ...syntheticJpeg().subarray(0, -2),
      ...segment(0xe1, ascii("Exif\0\0")),
      ...segment(0xda, [0x01, 0x01, 0x00, 0x00, 0x3f, 0x00]),
      0x56,
      0xff, 0xd9,
    ]);

    expect(readJpegStructure(progressive).metadataMarkers).toEqual([0xe1]);
    expect(readJpegStructure(stripJpegMetadata(progressive)).metadataMarkers).toEqual([]);
  });

  it.each([
    ["an empty file", new Uint8Array()],
    ["a PNG", new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])],
    ["a JPEG without EOI", syntheticJpeg().subarray(0, -2)],
    ["a segment running past the end", new Uint8Array([0xff, 0xd8, 0xff, 0xe1, 0x00, 0x40, 0x00])],
    ["a segment shorter than its length field", new Uint8Array([0xff, 0xd8, 0xff, 0xe1, 0x00, 0x01, 0xff, 0xd9])],
    ["bytes where a marker belongs", new Uint8Array([0xff, 0xd8, 0x00, 0x00, 0xff, 0xd9])],
    ["a second SOI", new Uint8Array([0xff, 0xd8, 0xff, 0xd8, 0xff, 0xd9])],
  ])("fails closed on %s", (_case, bytes) => {
    expect(() => readJpegStructure(bytes)).toThrow(JpegStructureError);
    expect(() => stripJpegMetadata(bytes)).toThrow(JpegStructureError);
  });

  it.each([
    ["no frame header", new Uint8Array([0xff, 0xd8, ...segment(0xda, [0x00]), 0x01, 0xff, 0xd9])],
    ["no scan", new Uint8Array([0xff, 0xd8, ...segment(0xc0, [8, 0, 1, 0, 1, 1, 1, 0x11, 0]), 0xff, 0xd9])],
    ["a frame of zero pixels", syntheticJpeg({ width: 0 })],
  ])("refuses to read a size from %s", (_case, bytes) => {
    expect(() => readJpegStructure(bytes)).toThrow(JpegStructureError);
  });
});
