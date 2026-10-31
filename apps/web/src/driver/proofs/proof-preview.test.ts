import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it, vi } from "vitest";
import { MaximumDriverProofBytes } from "./proof-file";
import {
  DriverProofPreview,
  driverProofPreviewProblemMessage,
  driverProofPreviewStatus,
  type DriverProofPreviewState,
} from "./proof-preview";

function photo(bytes: readonly number[] = [0x89, 0x50, 0x4e, 0x47], type = "image/png") {
  return new Blob([new Uint8Array(bytes)], { type });
}

/**
 * Records every object URL handed out and revoked, like the browser would, and every
 * reduction: the stand-in reducer returns a new JPEG Blob for each picked file
 * (DRV-PROOF-PHOTO-REDUCE-2026-10-20), so a test can tell the picked file from the
 * reduced photo.
 */
function harness(
  reduce: (photo: Blob) => Promise<Blob> = async (picked) =>
    new Blob([await picked.arrayBuffer()], { type: "image/jpeg" }),
) {
  const created: { readonly url: string; readonly photo: Blob }[] = [];
  const revoked: string[] = [];
  const reduced = new Map<Blob, Blob>();
  const preview = new DriverProofPreview({
    createObjectUrl: (value) => {
      const url = `blob:paquetenvia/${created.length + 1}`;
      created.push({ url, photo: value });
      return url;
    },
    revokeObjectUrl: (url) => {
      revoked.push(url);
    },
    reduce: async (picked) => {
      const result = await reduce(picked);
      reduced.set(picked, result);
      return result;
    },
  });
  const live = () => created.map(({ url }) => url).filter((url) => !revoked.includes(url));
  /** The reduced photo of `picked`; fails when it was never reduced. */
  const reducedOf = (picked: Blob) => {
    const result = reduced.get(picked);
    if (result === undefined) throw new Error("the photo was not reduced");
    return result;
  };
  return { preview, created, revoked, live, reducedOf };
}

function deferred() {
  let settle!: (value: boolean) => void;
  let fail!: (error: unknown) => void;
  const promise = new Promise<boolean>((resolvePromise, rejectPromise) => {
    settle = resolvePromise;
    fail = rejectPromise;
  });
  return { promise, settle, fail };
}

describe("driver proof photo preview", () => {
  it("previews the reduced photo of exactly one acceptable file without storing anything", async () => {
    const { preview, created, revoked, reducedOf } = harness();
    const picked = photo();

    expect(preview.getSnapshot()).toEqual({ phase: "capture", problem: null });
    await preview.select([picked]);

    expect(preview.getSnapshot()).toEqual({
      phase: "preview",
      objectUrl: "blob:paquetenvia/1",
      problem: null,
    });
    // The preview shows the reduced photo, never the picked file.
    expect(created).toEqual([{ url: "blob:paquetenvia/1", photo: reducedOf(picked) }]);
    expect(reducedOf(picked)).not.toBe(picked);
    expect(revoked).toEqual([]);
  });

  it("shows Preparando la foto while the phone reduces it and creates no URL before", async () => {
    let finish!: (photo: Blob) => void;
    const reduced = new Blob([new Uint8Array([0xff, 0xd8, 0xff])], { type: "image/jpeg" });
    const { preview, created } = harness(
      () => new Promise<Blob>((resolvePhoto) => {
        finish = resolvePhoto;
      }),
    );

    const selecting = preview.select([photo()]);

    expect(preview.getSnapshot()).toEqual({ phase: "preparing" });
    expect(created).toEqual([]);
    finish(reduced);
    await selecting;
    expect(created).toEqual([{ url: "blob:paquetenvia/1", photo: reduced }]);
  });

  it.each([
    ["the photo cannot be decoded", () => Promise.reject(new Error("decode"))],
    ["the phone runs out of memory", () => Promise.reject(new RangeError("memory"))],
  ])("keeps nothing and asks for a new photo when %s, never falling back to the file", async (
    _case,
    reduce,
  ) => {
    const { preview, created } = harness(reduce);

    await preview.select([photo()]);

    expect(preview.getSnapshot()).toEqual({ phase: "capture", problem: "unprocessable" });
    expect(created).toEqual([]);
    const enqueue = vi.fn(async () => true);
    await expect(preview.use(enqueue)).resolves.toBe(false);
    expect(enqueue).not.toHaveBeenCalled();
  });

  it("ignores a reduction that finishes after release or after a newer pick", async () => {
    const pending: ((photo: Blob) => void)[] = [];
    const { preview, created, live } = harness(
      () => new Promise<Blob>((resolvePhoto) => {
        pending.push(resolvePhoto);
      }),
    );
    const late = new Blob([new Uint8Array([1])], { type: "image/jpeg" });
    const newer = new Blob([new Uint8Array([2])], { type: "image/jpeg" });

    const released = preview.select([photo()]);
    preview.release();
    pending[0](late);
    await released;
    expect(created).toEqual([]);
    expect(preview.getSnapshot()).toEqual({ phase: "capture", problem: null });

    const superseded = preview.select([photo()]);
    const current = preview.select([photo()]);
    pending[2](newer);
    pending[1](late);
    await Promise.all([superseded, current]);
    expect(created).toEqual([{ url: "blob:paquetenvia/1", photo: newer }]);
    expect(live()).toEqual(["blob:paquetenvia/1"]);
  });

  it("Repetir revokes the preview, forgets the photo and waits for a new one", async () => {
    const { preview, revoked, live } = harness();
    await preview.select([photo()]);

    preview.retake();

    expect(preview.getSnapshot()).toEqual({ phase: "capture", problem: null });
    expect(revoked).toEqual(["blob:paquetenvia/1"]);
    expect(live()).toEqual([]);
    const enqueue = vi.fn(async () => true);
    await expect(preview.use(enqueue)).resolves.toBe(false);
    expect(enqueue).not.toHaveBeenCalled();
  });

  it("queues only the reduced photo shown after a retake", async () => {
    const { preview, live, reducedOf } = harness();
    const first = photo([0x89, 0x50, 0x4e, 0x47, 1]);
    const second = photo([0xff, 0xd8, 0xff, 0xe0, 2], "image/jpeg");
    await preview.select([first]);
    preview.retake();
    await preview.select([second]);
    const enqueue = vi.fn<(photo: Blob) => Promise<boolean>>(async () => true);

    await preview.use(enqueue);

    expect(enqueue).toHaveBeenCalledTimes(1);
    expect(enqueue.mock.calls[0][0]).toBe(reducedOf(second));
    expect(live()).toEqual([]);
  });

  it("Usar esta foto revokes the preview before saving and hands over the reduced Blob once", async () => {
    const { preview, revoked, live, reducedOf } = harness();
    const picked = photo();
    await preview.select([picked]);
    const save = deferred();
    const enqueue = vi.fn<(photo: Blob) => Promise<boolean>>(() => save.promise);

    const using = preview.use(enqueue);
    // A second tap while saving does nothing.
    const again = preview.use(enqueue);

    expect(preview.getSnapshot()).toEqual({ phase: "saving" });
    expect(revoked).toEqual(["blob:paquetenvia/1"]);
    expect(live()).toEqual([]);
    expect(enqueue).toHaveBeenCalledTimes(1);
    // The evidence is the reduced photo the preview showed, never the picked file.
    expect(enqueue.mock.calls[0][0]).toBe(reducedOf(picked));
    expect(enqueue.mock.calls[0][0]).not.toBe(picked);
    await expect(again).resolves.toBe(false);

    save.settle(true);
    await expect(using).resolves.toBe(true);
    expect(preview.getSnapshot()).toEqual({ phase: "queued" });
    expect(live()).toEqual([]);
    // Nothing is left to use again.
    await expect(preview.use(enqueue)).resolves.toBe(false);
    expect(enqueue).toHaveBeenCalledTimes(1);
  });

  it.each([
    ["the queue does not store it", () => Promise.resolve(false)],
    ["the queue fails", () => Promise.reject(new Error("quota"))],
  ])("previews the same reduced photo again when %s", async (_case, outcome) => {
    const { preview, created, revoked, live, reducedOf } = harness();
    const picked = photo();
    await preview.select([picked]);

    await expect(preview.use(outcome)).resolves.toBe(false);

    expect(preview.getSnapshot()).toEqual({
      phase: "preview",
      objectUrl: "blob:paquetenvia/2",
      problem: "not-saved",
    });
    expect(created.map((entry) => entry.photo)).toEqual([reducedOf(picked), reducedOf(picked)]);
    expect(revoked).toEqual(["blob:paquetenvia/1"]);
    expect(live()).toEqual(["blob:paquetenvia/2"]);

    preview.retake();
    expect(live()).toEqual([]);
  });

  it("release revokes the preview on unmount", async () => {
    const { preview, live } = harness();
    await preview.select([photo()]);

    preview.release();
    preview.release();

    expect(preview.getSnapshot()).toEqual({ phase: "capture", problem: null });
    expect(live()).toEqual([]);
  });

  it("never creates a URL after release, even when a save fails later", async () => {
    const { preview, created, live } = harness();
    await preview.select([photo()]);
    const save = deferred();
    const using = preview.use(() => save.promise);

    preview.release();
    save.settle(false);

    await expect(using).resolves.toBe(false);
    expect(created).toHaveLength(1);
    expect(live()).toEqual([]);
    expect(preview.getSnapshot()).toEqual({ phase: "capture", problem: null });
  });

  it.each([
    ["no file", [] as Blob[], "select-one"],
    ["two files", [photo(), photo()], "select-one"],
    ["an empty file", [photo([])], "empty"],
    ["a file that is not JPEG or PNG", [photo([1], "image/gif")], "unsupported-type"],
  ] as const)("does not reduce or preview %s", async (_case, files, problem) => {
    const reduce = vi.fn(async (picked: Blob) => picked);
    const { preview, created } = harness(reduce);

    await preview.select(files);

    expect(preview.getSnapshot()).toEqual({ phase: "capture", problem });
    expect(created).toEqual([]);
    expect(reduce).not.toHaveBeenCalled();
  });

  it("turns away a photo over the technical limit without reading or reducing it", async () => {
    const reduce = vi.fn(async (picked: Blob) => picked);
    const { preview, created } = harness(reduce);
    const arrayBuffer = vi.fn();
    const oversized = {
      size: MaximumDriverProofBytes + 1,
      type: "image/jpeg",
      arrayBuffer,
    } as unknown as Blob;

    await preview.select(null);
    expect(preview.getSnapshot()).toEqual({ phase: "capture", problem: "select-one" });

    await preview.select([oversized]);
    expect(preview.getSnapshot()).toEqual({ phase: "capture", problem: "too-large" });
    expect(created).toEqual([]);
    expect(arrayBuffer).not.toHaveBeenCalled();
    expect(reduce).not.toHaveBeenCalled();
  });

  it("drops the earlier preview when the input changes again", async () => {
    const { preview, live } = harness();
    await preview.select([photo()]);

    await preview.select([photo([0xff, 0xd8, 0xff], "image/jpeg")]);

    expect(live()).toEqual(["blob:paquetenvia/2"]);
  });

  it("notifies subscribers on every step until they unsubscribe", async () => {
    const { preview } = harness();
    const seen: DriverProofPreviewState["phase"][] = [];
    const unsubscribe = preview.subscribe(() => seen.push(preview.getSnapshot().phase));

    await preview.select([photo()]);
    preview.retake();
    await preview.select([photo()]);
    await preview.use(async () => true);
    unsubscribe();
    preview.release();

    expect(seen).toEqual([
      "preparing",
      "preview",
      "capture",
      "preparing",
      "preview",
      "saving",
      "queued",
    ]);
  });
});

describe("driver proof photo preview copy", () => {
  it("explains each problem in es-MX", () => {
    expect(driverProofPreviewProblemMessage("select-one")).toBe(
      "Selecciona exactamente una foto.",
    );
    expect(driverProofPreviewProblemMessage("empty")).toBe(
      "La foto está vacía. Tómala de nuevo.",
    );
    expect(driverProofPreviewProblemMessage("too-large")).toBe(
      "La foto pesa más de 10 MiB. Tómala de nuevo con menor resolución.",
    );
    expect(driverProofPreviewProblemMessage("unsupported-type")).toBe(
      "La foto debe ser JPEG o PNG. Tómala de nuevo.",
    );
    expect(driverProofPreviewProblemMessage("unprocessable")).toBe(
      "No se pudo procesar la foto en el teléfono. Tómala de nuevo.",
    );
    expect(driverProofPreviewProblemMessage("not-saved")).toBe(
      "No se pudo guardar la foto en el dispositivo. Vuelve a intentarlo o repite la foto.",
    );
  });

  it("announces each step politely without repeating an alert", () => {
    expect(driverProofPreviewStatus({ phase: "capture", problem: null })).toBe("");
    expect(driverProofPreviewStatus({ phase: "preparing" })).toBe("Preparando la foto…");
    expect(
      driverProofPreviewStatus({ phase: "preview", objectUrl: "blob:x", problem: null }),
    ).toBe("Revisa la foto: elige «Usar esta foto» o «Repetir».");
    expect(
      driverProofPreviewStatus({ phase: "preview", objectUrl: "blob:x", problem: "not-saved" }),
    ).toBe("");
    expect(driverProofPreviewStatus({ phase: "saving" })).toBe(
      "Guardando la foto en el dispositivo…",
    );
    expect(driverProofPreviewStatus({ phase: "queued" })).toBe(
      "Foto guardada en el dispositivo.",
    );
  });
});

describe("driver proof photo step policy", () => {
  const component = readFileSync(
    resolve(process.cwd(), "src/driver/components/driver-proof-capture.tsx"),
    "utf8",
  );
  const machine = readFileSync(
    resolve(process.cwd(), "src/driver/proofs/proof-preview.ts"),
    "utf8",
  );
  const stops = readFileSync(
    resolve(process.cwd(), "src/driver/components/driver-stops-experience.tsx"),
    "utf8",
  );
  const styles = readFileSync(
    resolve(process.cwd(), "src/driver/components/driver-stops.module.css"),
    "utf8",
  );

  it("queues the photo only from Usar esta foto, after Vista previa de la foto", () => {
    expect(component).toContain("Vista previa de la foto");
    expect(component).toMatch(/>\s*Repetir\s*</);
    expect(component).toMatch(/>\s*Usar esta foto\s*</);
    // onUse (the queue) runs only through the preview's "Usar esta foto".
    expect(component).not.toMatch(/onUse\(/);
    expect(component.split("preview.use(onUse)")).toHaveLength(2);
    expect(component).toContain("onClick={() => void preview.use(onUse)}");
    expect(stops).toContain("onUse={(photo) => onEnqueue(kind, photo)}");
    expect(stops).not.toMatch(/type="file"/);
  });

  it("keeps the camera input, an accessible step and 44 px controls", () => {
    expect(component).toContain('capture="environment"');
    expect(component).toContain('accept="image/jpeg,image/png"');
    expect(component).toContain('aria-live="polite"');
    expect(component).toContain('role="alert"');
    expect(component).toContain("tabIndex={-1}");
    expect(component).toContain('alt="Foto de evidencia tomada"');
    expect(component).toContain("useEffect(() => () => preview.release(), [preview]);");
    // Both preview buttons sit in the step's fieldset, whose buttons are 44 px targets.
    expect(stops).toContain("<fieldset className={styles.actions}");
    expect(styles).toMatch(
      /\.actions button,[^{]*\{[^}]*min-width: 44px;[^}]*min-height: 44px;/,
    );
    expect(styles).toContain(".proofCapture[hidden] {\n  display: none;");
  });

  it("reduces every picked photo on the phone before it is previewed or queued", () => {
    // DRV-PROOF-PHOTO-REDUCE-2026-10-20: the default reducer, never an override, in the app.
    expect(machine).toContain(
      "this.reduce = options.reduce ?? ((photo) => reduceDriverProofPhoto(photo));",
    );
    expect(component).toContain("useState(() => new DriverProofPreview())");
    expect(component).toContain("void preview.select(event.currentTarget.files)");
    // Only the reduced photo is previewed; the picked file is never shown or kept.
    expect(machine.match(/this\.showPreview\(/g)).toHaveLength(2);
    expect(machine).toContain("this.showPreview(reduced, null);");
    expect(machine).toContain('this.showPreview(photo, "not-saved");');
    expect(machine).not.toMatch(/this\.photo = picked|showPreview\(picked/);
  });

  it("keeps the photo in memory only and never logs it", () => {
    for (const source of [component, machine]) {
      expect(source).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\.|console\./);
      expect(source).not.toMatch(/FileReader|readAsDataURL|toDataURL/);
    }
    expect(machine).toContain("URL.createObjectURL(photo)");
    expect(machine).toContain("URL.revokeObjectURL(url)");
  });
});
