import { readJpegStructure, stripJpegMetadata } from "./jpeg-structure";

/**
 * DRV-PROOF-PHOTO-REDUCE-2026-10-20 (project owner, "Reducir y quitar metadatos
 * (Recommended)"): "JPEG de máximo 1600 px por lado y calidad 80 % (unos 300-500
 * KB), sin metadatos. La evidencia guardada es esa foto reducida (su huella se
 * calcula después)."
 *
 * The photo the driver picks is decoded on the phone in its visual orientation
 * and re-encoded as a JPEG whose longest side is at most 1600 px (never
 * enlarged, aspect ratio kept) at quality 0.8, with no metadata. That reduced
 * JPEG is the evidence: it is what the preview shows and what the queue hashes,
 * stores, uploads and the server keeps. When the phone cannot decode or encode
 * the photo nothing is kept: the original file is never used instead.
 */
export const DriverProofPhotoMaximumSide = 1600;
export const DriverProofPhotoJpegQuality = 0.8;
export const DriverProofPhotoContentType = "image/jpeg";

export interface DriverProofPhotoSize {
  readonly width: number;
  readonly height: number;
}

/** A decoded photo, already turned the way the camera recorded it. */
export interface DriverProofPhotoSource extends DriverProofPhotoSize {
  /** Draws the whole photo at `size` on an opaque white canvas and encodes it as JPEG. */
  encodeJpeg(size: DriverProofPhotoSize, quality: number): Promise<Blob>;
  /** Frees the decoded pixels; called once, also after a failure. */
  close(): void;
}

export type DriverProofPhotoDecoder = (photo: Blob) => Promise<DriverProofPhotoSource>;

export class DriverProofPhotoError extends Error {
  public constructor() {
    super("The proof photo could not be processed on the device.");
    this.name = "DriverProofPhotoError";
  }
}

/** The reduced size: the longest side at most 1600 px, never enlarged, aspect ratio kept. */
export function driverProofPhotoTargetSize(
  source: DriverProofPhotoSize,
): DriverProofPhotoSize {
  const { width, height } = source;
  if (!Number.isInteger(width) || !Number.isInteger(height) || width < 1 || height < 1) {
    throw new DriverProofPhotoError();
  }
  const longest = Math.max(width, height);
  if (longest <= DriverProofPhotoMaximumSide) return Object.freeze({ width, height });
  const scale = DriverProofPhotoMaximumSide / longest;
  return Object.freeze({
    width: Math.max(1, Math.round(width * scale)),
    height: Math.max(1, Math.round(height * scale)),
  });
}

/**
 * Decodes `photo`, re-encodes it as the reduced JPEG and removes every metadata
 * segment the browser's encoder wrote (Chromium adds an ICC profile, Safari an
 * EXIF block). Throws DriverProofPhotoError and keeps nothing when any step fails.
 */
export async function reduceDriverProofPhoto(
  photo: Blob,
  decode: DriverProofPhotoDecoder = decodeDriverProofPhoto,
): Promise<Blob> {
  let source: DriverProofPhotoSource;
  try {
    source = await decode(photo);
  } catch {
    throw new DriverProofPhotoError();
  }
  try {
    const size = driverProofPhotoTargetSize(source);
    const encoded = await source.encodeJpeg(size, DriverProofPhotoJpegQuality);
    // A browser that cannot write JPEG silently writes PNG instead.
    if (encoded.type !== DriverProofPhotoContentType) throw new DriverProofPhotoError();
    const reduced = stripJpegMetadata(new Uint8Array(await encoded.arrayBuffer()));
    const structure = readJpegStructure(reduced);
    if (
      structure.width !== size.width ||
      structure.height !== size.height ||
      structure.metadataMarkers.length > 0 ||
      structure.trailingBytes > 0
    ) {
      throw new DriverProofPhotoError();
    }
    return new Blob([reduced], { type: DriverProofPhotoContentType });
  } catch {
    throw new DriverProofPhotoError();
  } finally {
    source.close();
  }
}

/**
 * The browser decoder: createImageBitmap with imageOrientation "from-image"
 * (the EXIF orientation applied), or, where that is missing or refuses the
 * option, an <img> element, which browsers draw in its EXIF orientation too.
 */
export async function decodeDriverProofPhoto(
  photo: Blob,
): Promise<DriverProofPhotoSource> {
  if (typeof createImageBitmap === "function") {
    let bitmap: ImageBitmap | null = null;
    try {
      bitmap = await createImageBitmap(photo, { imageOrientation: "from-image" });
    } catch (error) {
      // A TypeError means this browser does not know the option; anything else
      // (an undecodable photo, no memory) is final.
      if (!(error instanceof TypeError)) throw error;
    }
    if (bitmap !== null) {
      const decoded = bitmap;
      return {
        width: decoded.width,
        height: decoded.height,
        encodeJpeg: (size, quality) => encodeOnCanvas(decoded, size, quality),
        close: () => decoded.close(),
      };
    }
  }
  return decodeWithImageElement(photo);
}

async function decodeWithImageElement(photo: Blob): Promise<DriverProofPhotoSource> {
  const url = URL.createObjectURL(photo);
  try {
    const image = new Image();
    image.decoding = "async";
    image.src = url;
    await image.decode();
    return {
      width: image.naturalWidth,
      height: image.naturalHeight,
      encodeJpeg: (size, quality) => encodeOnCanvas(image, size, quality),
      close: () => URL.revokeObjectURL(url),
    };
  } catch (error) {
    URL.revokeObjectURL(url);
    throw error;
  }
}

async function encodeOnCanvas(
  image: CanvasImageSource,
  size: DriverProofPhotoSize,
  quality: number,
): Promise<Blob> {
  if (typeof OffscreenCanvas === "function") {
    const canvas = new OffscreenCanvas(size.width, size.height);
    const context = canvas.getContext("2d");
    if (context !== null) {
      draw(context, image, size);
      return canvas.convertToBlob({ type: DriverProofPhotoContentType, quality });
    }
  }
  const canvas = document.createElement("canvas");
  canvas.width = size.width;
  canvas.height = size.height;
  const context = canvas.getContext("2d");
  if (context === null) throw new DriverProofPhotoError();
  draw(context, image, size);
  return new Promise<Blob>((resolve, reject) => {
    canvas.toBlob(
      (blob) => (blob === null ? reject(new DriverProofPhotoError()) : resolve(blob)),
      DriverProofPhotoContentType,
      quality,
    );
  });
}

function draw(
  context: OffscreenCanvasRenderingContext2D | CanvasRenderingContext2D,
  image: CanvasImageSource,
  size: DriverProofPhotoSize,
): void {
  // JPEG has no transparency: a transparent PNG area becomes white, not black.
  context.fillStyle = "#ffffff";
  context.fillRect(0, 0, size.width, size.height);
  context.imageSmoothingEnabled = true;
  context.imageSmoothingQuality = "high";
  context.drawImage(image, 0, 0, size.width, size.height);
}
