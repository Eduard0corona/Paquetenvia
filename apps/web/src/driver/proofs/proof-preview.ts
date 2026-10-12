import { checkDriverProofFile, MaximumDriverProofBytes } from "./proof-file";

/** Why the photo step shows a message; each value has es-MX copy below. */
export type DriverProofPreviewProblem =
  | "select-one"
  | "empty"
  | "too-large"
  | "unsupported-type"
  | "not-saved";

export type DriverProofPreviewState =
  /** Waiting for a photo from the camera or the file picker. */
  | {
      readonly phase: "capture";
      readonly problem: DriverProofPreviewProblem | null;
    }
  /** "Vista previa de la foto": shown from an in-memory object URL; nothing is stored yet. */
  | {
      readonly phase: "preview";
      readonly objectUrl: string;
      readonly problem: "not-saved" | null;
    }
  /** "Usar esta foto" handed the photo to the offline queue; the preview URL is revoked. */
  | { readonly phase: "saving" }
  /** The offline queue stored the action with this photo. */
  | { readonly phase: "queued" };

export interface DriverProofPreviewOptions {
  readonly createObjectUrl?: (photo: Blob) => string;
  readonly revokeObjectUrl?: (url: string) => void;
}

const capturing: DriverProofPreviewState = Object.freeze({
  phase: "capture",
  problem: null,
});

/**
 * UI-001 phase 3: the driver sees the proof photo before it is queued. The photo exists
 * only in memory (the picked Blob and one object URL at a time); nothing new is persisted
 * or logged. "Usar esta foto" hands that same Blob to the offline queue, which validates,
 * hashes and stores it exactly as before. Every object URL created here is revoked on
 * "Repetir", on "Usar esta foto" and on `release` (unmount).
 */
export class DriverProofPreview {
  private readonly listeners = new Set<() => void>();
  private readonly createObjectUrl: (photo: Blob) => string;
  private readonly revokeObjectUrl: (url: string) => void;
  private state: DriverProofPreviewState = capturing;
  private photo: Blob | null = null;
  private objectUrl: string | null = null;
  private generation = 0;

  public constructor(options: DriverProofPreviewOptions = {}) {
    this.createObjectUrl =
      options.createObjectUrl ?? ((photo) => URL.createObjectURL(photo));
    this.revokeObjectUrl =
      options.revokeObjectUrl ?? ((url) => URL.revokeObjectURL(url));
  }

  public readonly getSnapshot = (): DriverProofPreviewState => this.state;

  public readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  /** The file input changed: exactly one acceptable JPEG or PNG opens the preview. */
  public select(files: ArrayLike<Blob> | null): void {
    if (this.state.phase === "saving" || this.state.phase === "queued") return;
    this.forgetPhoto();
    if (!files || files.length !== 1) {
      this.setState({ phase: "capture", problem: "select-one" });
      return;
    }
    const photo = files[0];
    const failure = checkDriverProofFile(photo);
    if (failure !== null) {
      this.setState({
        phase: "capture",
        problem: failure === "missing" ? "select-one" : failure,
      });
      return;
    }
    this.showPreview(photo, null);
  }

  /** "Repetir": drop the previewed photo and wait for a new one. */
  public retake(): void {
    if (this.state.phase !== "preview") return;
    this.forgetPhoto();
    this.setState(capturing);
  }

  /**
   * "Usar esta foto": revoke the preview and hand the same photo to `enqueue` once. When
   * the queue does not store it, the same photo is previewed again so the driver can try
   * again or retake it. Resolves whether the queue stored it.
   */
  public async use(
    enqueue: (photo: Blob) => Promise<boolean>,
  ): Promise<boolean> {
    const photo = this.photo;
    if (this.state.phase !== "preview" || photo === null) return false;
    const generation = this.generation;
    this.revokePreviewUrl();
    this.setState({ phase: "saving" });
    let stored: boolean;
    try {
      stored = await enqueue(photo);
    } catch {
      stored = false;
    }
    // Released while saving (the step unmounted): show nothing and create no URL.
    if (generation !== this.generation) return stored;
    if (stored) {
      this.photo = null;
      this.setState({ phase: "queued" });
    } else {
      this.showPreview(photo, "not-saved");
    }
    return stored;
  }

  /**
   * Unmount: revoke the object URL, forget the photo and ignore a save still in flight.
   * Safe to call more than once; the instance stays usable (React may re-run effects).
   */
  public release(): void {
    this.generation += 1;
    this.forgetPhoto();
    this.setState(capturing);
  }

  private showPreview(photo: Blob, problem: "not-saved" | null): void {
    this.photo = photo;
    const objectUrl = this.createObjectUrl(photo);
    this.objectUrl = objectUrl;
    this.setState({ phase: "preview", objectUrl, problem });
  }

  private forgetPhoto(): void {
    this.revokePreviewUrl();
    this.photo = null;
  }

  private revokePreviewUrl(): void {
    const objectUrl = this.objectUrl;
    if (objectUrl === null) return;
    this.objectUrl = null;
    this.revokeObjectUrl(objectUrl);
  }

  private setState(next: DriverProofPreviewState): void {
    if (next === this.state) return;
    this.state = Object.freeze(next);
    for (const listener of this.listeners) listener();
  }
}

const maximumMebibytes = MaximumDriverProofBytes / (1024 * 1024);

export function driverProofPreviewProblemMessage(
  problem: DriverProofPreviewProblem,
): string {
  switch (problem) {
    case "select-one":
      return "Selecciona exactamente una foto.";
    case "empty":
      return "La foto está vacía. Tómala de nuevo.";
    case "too-large":
      return `La foto pesa más de ${maximumMebibytes} MiB. Tómala de nuevo con menor resolución.`;
    case "unsupported-type":
      return "La foto debe ser JPEG o PNG. Tómala de nuevo.";
    case "not-saved":
      return "No se pudo guardar la foto en el dispositivo. Vuelve a intentarlo o repite la foto.";
  }
}

/** Short status for the step's polite live region; problems use their own alert. */
export function driverProofPreviewStatus(state: DriverProofPreviewState): string {
  switch (state.phase) {
    case "capture":
      return "";
    case "preview":
      return state.problem === null
        ? "Revisa la foto: elige «Usar esta foto» o «Repetir»."
        : "";
    case "saving":
      return "Guardando la foto en el dispositivo…";
    case "queued":
      return "Foto guardada en el dispositivo.";
  }
}
