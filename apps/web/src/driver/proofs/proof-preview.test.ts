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

/** Records every object URL handed out and revoked, like the browser would. */
function harness() {
  const created: { readonly url: string; readonly photo: Blob }[] = [];
  const revoked: string[] = [];
  const preview = new DriverProofPreview({
    createObjectUrl: (value) => {
      const url = `blob:paquetenvia/${created.length + 1}`;
      created.push({ url, photo: value });
      return url;
    },
    revokeObjectUrl: (url) => {
      revoked.push(url);
    },
  });
  const live = () => created.map(({ url }) => url).filter((url) => !revoked.includes(url));
  return { preview, created, revoked, live };
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
  it("previews exactly one acceptable photo from an object URL without storing anything", () => {
    const { preview, created, revoked } = harness();
    const picked = photo();

    expect(preview.getSnapshot()).toEqual({ phase: "capture", problem: null });
    preview.select([picked]);

    expect(preview.getSnapshot()).toEqual({
      phase: "preview",
      objectUrl: "blob:paquetenvia/1",
      problem: null,
    });
    expect(created).toEqual([{ url: "blob:paquetenvia/1", photo: picked }]);
    expect(revoked).toEqual([]);
  });

  it("Repetir revokes the preview, forgets the photo and waits for a new one", async () => {
    const { preview, revoked, live } = harness();
    preview.select([photo()]);

    preview.retake();

    expect(preview.getSnapshot()).toEqual({ phase: "capture", problem: null });
    expect(revoked).toEqual(["blob:paquetenvia/1"]);
    expect(live()).toEqual([]);
    const enqueue = vi.fn(async () => true);
    await expect(preview.use(enqueue)).resolves.toBe(false);
    expect(enqueue).not.toHaveBeenCalled();
  });

  it("queues only the photo shown after a retake", async () => {
    const { preview, live } = harness();
    const first = photo([0x89, 0x50, 0x4e, 0x47, 1]);
    const second = photo([0xff, 0xd8, 0xff, 0xe0, 2], "image/jpeg");
    preview.select([first]);
    preview.retake();
    preview.select([second]);
    const enqueue = vi.fn<(photo: Blob) => Promise<boolean>>(async () => true);

    await preview.use(enqueue);

    expect(enqueue).toHaveBeenCalledTimes(1);
    expect(enqueue.mock.calls[0][0]).toBe(second);
    expect(live()).toEqual([]);
  });

  it("Usar esta foto revokes the preview before saving and hands over the same Blob once", async () => {
    const { preview, revoked, live } = harness();
    const picked = photo();
    preview.select([picked]);
    const save = deferred();
    const enqueue = vi.fn<(photo: Blob) => Promise<boolean>>(() => save.promise);

    const using = preview.use(enqueue);
    // A second tap while saving does nothing.
    const again = preview.use(enqueue);

    expect(preview.getSnapshot()).toEqual({ phase: "saving" });
    expect(revoked).toEqual(["blob:paquetenvia/1"]);
    expect(live()).toEqual([]);
    expect(enqueue).toHaveBeenCalledTimes(1);
    expect(enqueue.mock.calls[0][0]).toBe(picked);
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
  ])("previews the same photo again when %s", async (_case, outcome) => {
    const { preview, created, revoked, live } = harness();
    const picked = photo();
    preview.select([picked]);

    await expect(preview.use(outcome)).resolves.toBe(false);

    expect(preview.getSnapshot()).toEqual({
      phase: "preview",
      objectUrl: "blob:paquetenvia/2",
      problem: "not-saved",
    });
    expect(created.map((entry) => entry.photo)).toEqual([picked, picked]);
    expect(revoked).toEqual(["blob:paquetenvia/1"]);
    expect(live()).toEqual(["blob:paquetenvia/2"]);

    preview.retake();
    expect(live()).toEqual([]);
  });

  it("release revokes the preview on unmount", () => {
    const { preview, live } = harness();
    preview.select([photo()]);

    preview.release();
    preview.release();

    expect(preview.getSnapshot()).toEqual({ phase: "capture", problem: null });
    expect(live()).toEqual([]);
  });

  it("never creates a URL after release, even when a save fails later", async () => {
    const { preview, created, live } = harness();
    preview.select([photo()]);
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
  ] as const)("does not preview %s", (_case, files, problem) => {
    const { preview, created } = harness();

    preview.select(files);

    expect(preview.getSnapshot()).toEqual({ phase: "capture", problem });
    expect(created).toEqual([]);
  });

  it("turns away a photo over the technical limit without reading it", () => {
    const { preview, created } = harness();
    const arrayBuffer = vi.fn();
    const oversized = {
      size: MaximumDriverProofBytes + 1,
      type: "image/jpeg",
      arrayBuffer,
    } as unknown as Blob;

    preview.select(null);
    expect(preview.getSnapshot()).toEqual({ phase: "capture", problem: "select-one" });

    preview.select([oversized]);
    expect(preview.getSnapshot()).toEqual({ phase: "capture", problem: "too-large" });
    expect(created).toEqual([]);
    expect(arrayBuffer).not.toHaveBeenCalled();
  });

  it("drops the earlier preview when the input changes again", () => {
    const { preview, live } = harness();
    preview.select([photo()]);

    preview.select([photo([0xff, 0xd8, 0xff], "image/jpeg")]);

    expect(live()).toEqual(["blob:paquetenvia/2"]);
  });

  it("notifies subscribers on every step until they unsubscribe", async () => {
    const { preview } = harness();
    const seen: DriverProofPreviewState["phase"][] = [];
    const unsubscribe = preview.subscribe(() => seen.push(preview.getSnapshot().phase));

    preview.select([photo()]);
    preview.retake();
    preview.select([photo()]);
    await preview.use(async () => true);
    unsubscribe();
    preview.release();

    expect(seen).toEqual(["preview", "capture", "preview", "saving", "queued"]);
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
    expect(driverProofPreviewProblemMessage("not-saved")).toBe(
      "No se pudo guardar la foto en el dispositivo. Vuelve a intentarlo o repite la foto.",
    );
  });

  it("announces each step politely without repeating an alert", () => {
    expect(driverProofPreviewStatus({ phase: "capture", problem: null })).toBe("");
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

  it("keeps the photo in memory only and never logs it", () => {
    for (const source of [component, machine]) {
      expect(source).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\.|console\./);
      expect(source).not.toMatch(/FileReader|readAsDataURL|toDataURL/);
    }
    expect(machine).toContain("URL.createObjectURL(photo)");
    expect(machine).toContain("URL.revokeObjectURL(url)");
  });
});
