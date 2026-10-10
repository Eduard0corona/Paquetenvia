export const MaximumDriverProofBytes = 10 * 1024 * 1024;
export const MaximumQueuedProofBytes = 25 * 1024 * 1024;
export const MaximumDriverOperationsPerPartition = 100;

export type DriverProofContentType = "image/jpeg" | "image/png";

export interface ValidatedDriverProof {
  readonly blob: Blob;
  readonly contentType: DriverProofContentType;
  readonly sizeBytes: number;
  readonly sha256: string;
}

export type DriverProofFileFailure =
  | "missing"
  | "empty"
  | "too-large"
  | "unsupported-type";

export class DriverProofFileError extends Error {
  public constructor(public readonly category: DriverProofFileFailure) {
    super("Selecciona una foto JPEG o PNG válida.");
    this.name = "DriverProofFileError";
  }
}

/**
 * The synchronous part of `validateDriverProof` (presence, size, exact MIME), so the photo
 * preview can turn away a file the queue would refuse without reading or hashing it.
 */
export function checkDriverProofFile(
  blob: Blob | null | undefined,
): DriverProofFileFailure | null {
  if (!blob) return "missing";
  if (blob.size < 1) return "empty";
  if (blob.size > MaximumDriverProofBytes) return "too-large";
  if (blob.type !== "image/jpeg" && blob.type !== "image/png") {
    return "unsupported-type";
  }
  return null;
}

export async function validateDriverProof(
  blob: Blob | null | undefined,
): Promise<ValidatedDriverProof> {
  const failure = checkDriverProofFile(blob);
  if (failure !== null || !blob) {
    throw new DriverProofFileError(failure ?? "missing");
  }

  const digest = await crypto.subtle.digest("SHA-256", await blob.arrayBuffer());
  const sha256 = [...new Uint8Array(digest)]
    .map((byte) => byte.toString(16).padStart(2, "0"))
    .join("");
  return Object.freeze({
    blob,
    // checkDriverProofFile accepted only these two exact types.
    contentType: blob.type as DriverProofContentType,
    sizeBytes: blob.size,
    sha256,
  });
}
