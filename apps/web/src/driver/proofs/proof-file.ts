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

export async function validateDriverProof(
  blob: Blob | null | undefined,
): Promise<ValidatedDriverProof> {
  if (!blob) throw new DriverProofFileError("missing");
  if (blob.size < 1) throw new DriverProofFileError("empty");
  if (blob.size > MaximumDriverProofBytes) {
    throw new DriverProofFileError("too-large");
  }
  if (blob.type !== "image/jpeg" && blob.type !== "image/png") {
    throw new DriverProofFileError("unsupported-type");
  }

  const digest = await crypto.subtle.digest("SHA-256", await blob.arrayBuffer());
  const sha256 = [...new Uint8Array(digest)]
    .map((byte) => byte.toString(16).padStart(2, "0"))
    .join("");
  return Object.freeze({
    blob,
    contentType: blob.type,
    sizeBytes: blob.size,
    sha256,
  });
}
