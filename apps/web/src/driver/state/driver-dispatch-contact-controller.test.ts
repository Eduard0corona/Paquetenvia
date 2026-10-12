import { describe, expect, it, vi } from "vitest";
import { createDriverDispatchContactApi } from "../api/dispatch-contact-api";
import type {
  DriverDispatchContactCache,
  SavedDriverDispatchContact,
} from "../cache/driver-dispatch-contact-cache";
import {
  createDriverCachePartition,
  type DriverCachePartition,
} from "../cache/driver-stops-cache";
import type { DriverSession } from "../session/driver-session";
import {
  DriverDispatchContactController,
  DriverDispatchContactMaximumRetries,
} from "./driver-dispatch-contact-controller";
import { testWindow } from "./driver-roles.fixtures";

/**
 * DRV-DISPATCH-CONTACT-2026-10-12: the number comes from getMyDispatchContact
 * when the session starts; the answer saved for the partition keeps "Llamar a
 * despacho" working offline, a failed call keeps it and is asked again when
 * the connection returns or the app is shown again, and 401 or 403 removes it
 * for good. The answers go through the real client and its strict parser. The
 * numbers are synthetic.
 */
const syntheticNumber = "+522000000001";
const newerNumber = "+522000000002";
const session: DriverSession = {
  organizationId: "33333333-3333-4333-8333-333333333333",
  cacheNamespace: "opaque-driver-dispatch-session",
  getAccessToken: () => "memory-only",
};

interface Answer {
  readonly status: number;
  readonly body: string;
  readonly contentType?: string;
}

const numberAnswer = (phone: string | null): Answer => ({
  status: 200,
  body: JSON.stringify({ phone_e164: phone }),
});
const problem = (status: number): Answer => ({
  status,
  body: JSON.stringify({ title: "Problem", status }),
  contentType: "application/problem+json",
});

/** Each call takes the next answer; the last one repeats. */
function answersFetch(...answers: (Answer | Error)[]) {
  let call = 0;
  return vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>(async () => {
    const answer = answers[Math.min(call, answers.length - 1)];
    call += 1;
    if (answer instanceof Error) throw answer;
    return new Response(answer.body, {
      status: answer.status,
      headers: { "Content-Type": answer.contentType ?? "application/json; charset=utf-8" },
    });
  });
}

/** The dispatch contact store of one phone, in memory. */
class MemoryDispatchContactCache implements DriverDispatchContactCache {
  public readonly saved = new Map<string, SavedDriverDispatchContact>();
  public failWrites = false;

  public async readContact(partition: DriverCachePartition) {
    return this.saved.get(partition.key) ?? null;
  }

  public async replaceContact(partition: DriverCachePartition, phoneE164: string | null) {
    if (this.failWrites) throw new Error("quota");
    this.saved.set(partition.key, { phoneE164 });
  }

  public async clearContact(partition: DriverCachePartition) {
    this.saved.delete(partition.key);
  }
}

function controllerFor(
  fetcher: ReturnType<typeof answersFetch>,
  options: { readonly cache?: MemoryDispatchContactCache; readonly page?: ReturnType<typeof testWindow> } = {},
) {
  const cache = options.cache ?? new MemoryDispatchContactCache();
  const page = options.page ?? testWindow();
  const controller = new DriverDispatchContactController({
    session,
    api: createDriverDispatchContactApi({
      baseUrl: "https://api.synthetic.test",
      session,
      fetch: fetcher as unknown as typeof fetch,
    }),
    cache,
    window: page.window,
  });
  return { controller, cache, fetcher, page };
}

async function saved(cache: MemoryDispatchContactCache, phoneE164: string | null) {
  cache.saved.set((await createDriverCachePartition(session)).key, { phoneE164 });
}

describe("dispatch contact of the driver session", () => {
  it("knows no number until the API answers, then saves the answer for the partition", async () => {
    const { controller, cache, fetcher } = controllerFor(answersFetch(numberAnswer(syntheticNumber)));
    expect(controller.current).toEqual({ phoneE164: null });

    await controller.start();

    expect(fetcher).toHaveBeenCalledOnce();
    expect(String(fetcher.mock.calls[0][0])).toBe("https://api.synthetic.test/api/v1/driver/me/dispatch-contact");
    expect(controller.current).toEqual({ phoneE164: syntheticNumber });
    expect([...cache.saved.values()]).toEqual([{ phoneE164: syntheticNumber }]);
  });

  it("saves an organization without a number as none, replacing a number saved before", async () => {
    const cache = new MemoryDispatchContactCache();
    await saved(cache, syntheticNumber);
    const { controller } = controllerFor(answersFetch(numberAnswer(null)), { cache });
    const seen: (string | null)[] = [];
    controller.subscribe((state) => seen.push(state.phoneE164));

    await controller.start();

    expect(seen).toEqual([null, syntheticNumber, null]);
    expect(controller.current).toEqual({ phoneE164: null });
    expect([...cache.saved.values()]).toEqual([{ phoneE164: null }]);
  });

  it.each<[string, Answer | Error]>([
    ["offline", new TypeError("Failed to fetch")],
    ["a 503", problem(503)],
    ["the 404 of an API older than this app", problem(404)],
    ["an answer that breaks the contract", numberAnswer("+52 200 000 0001")],
  ])("keeps the saved number when %s, so the link works offline", async (_name, failure) => {
    const cache = new MemoryDispatchContactCache();
    await saved(cache, syntheticNumber);
    const { controller } = controllerFor(answersFetch(failure), { cache });

    await controller.start();

    expect(controller.current).toEqual({ phoneE164: syntheticNumber });
    expect([...cache.saved.values()]).toEqual([{ phoneE164: syntheticNumber }]);
  });

  it("knows no number when the first start fails with nothing saved", async () => {
    const { controller, cache } = controllerFor(answersFetch(new TypeError("Failed to fetch")));
    await controller.start();
    expect(controller.current).toEqual({ phoneE164: null });
    expect(cache.saved.size).toBe(0);
  });

  it.each([401, 403])("removes the saved number on %i and never asks again", async (status) => {
    const cache = new MemoryDispatchContactCache();
    await saved(cache, syntheticNumber);
    const page = testWindow();
    const { controller, fetcher } = controllerFor(answersFetch(problem(status), numberAnswer(newerNumber)), {
      cache,
      page,
    });

    await controller.start();
    expect(controller.current).toEqual({ phoneE164: null });
    expect(cache.saved.size).toBe(0);

    page.goOnline();
    page.show();
    await Promise.resolve();
    expect(fetcher).toHaveBeenCalledOnce();
    expect(controller.current).toEqual({ phoneE164: null });
  });

  it("asks again after a failure when the connection returns or the app is shown, at most the retry limit", async () => {
    const page = testWindow();
    const { controller, fetcher, cache } = controllerFor(answersFetch(new TypeError("Failed to fetch")), { page });
    await controller.start();
    expect(fetcher).toHaveBeenCalledTimes(1);

    for (let attempt = 0; attempt < DriverDispatchContactMaximumRetries + 3; attempt += 1) {
      if (attempt % 2 === 0) page.goOnline();
      else page.show();
      await vi.waitFor(() => expect(fetcher.mock.calls.length).toBe(Math.min(attempt + 2, DriverDispatchContactMaximumRetries + 1)));
    }
    expect(fetcher).toHaveBeenCalledTimes(DriverDispatchContactMaximumRetries + 1);
    expect(controller.current).toEqual({ phoneE164: null });
    expect(cache.saved.size).toBe(0);
  });

  it("gets the number on a retry and stops retrying after an answer", async () => {
    const page = testWindow();
    const { controller, fetcher, cache } = controllerFor(
      answersFetch(new TypeError("Failed to fetch"), numberAnswer(syntheticNumber)),
      { page },
    );
    await controller.start();
    page.goOnline();
    await vi.waitFor(() => expect(controller.current).toEqual({ phoneE164: syntheticNumber }));
    expect([...cache.saved.values()]).toEqual([{ phoneE164: syntheticNumber }]);

    page.goOnline();
    page.show();
    await Promise.resolve();
    expect(fetcher).toHaveBeenCalledTimes(2);
  });

  it("does not ask again while the phone is still offline", async () => {
    const page = testWindow({ online: false });
    const { controller, fetcher } = controllerFor(answersFetch(new TypeError("Failed to fetch")), { page });
    await controller.start();
    page.show();
    await Promise.resolve();
    expect(fetcher).toHaveBeenCalledOnce();
  });

  it("uses the answer even when the phone cannot save it", async () => {
    const cache = new MemoryDispatchContactCache();
    cache.failWrites = true;
    const { controller } = controllerFor(answersFetch(numberAnswer(syntheticNumber)), { cache });
    await controller.start();
    expect(controller.current).toEqual({ phoneE164: syntheticNumber });
  });

  it("writes and asks nothing after dispose, and cancels the request on its way", async () => {
    const pending: { signal?: AbortSignal | null } = {};
    const fetcher = vi.fn(
      (_input: RequestInfo | URL, init?: RequestInit) =>
        new Promise<Response>((_resolve, reject) => {
          pending.signal = init?.signal;
          init?.signal?.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")));
        }),
    );
    const cache = new MemoryDispatchContactCache();
    const page = testWindow();
    const controller = new DriverDispatchContactController({
      session,
      api: createDriverDispatchContactApi({
        baseUrl: "https://api.synthetic.test",
        session,
        fetch: fetcher as unknown as typeof fetch,
      }),
      cache,
      window: page.window,
    });
    const seen: (string | null)[] = [];
    controller.subscribe((state) => seen.push(state.phoneE164));

    const started = controller.start();
    await vi.waitFor(() => expect(fetcher).toHaveBeenCalledOnce());
    controller.dispose();
    await started;

    expect(pending.signal?.aborted).toBe(true);
    page.goOnline();
    await Promise.resolve();
    expect(fetcher).toHaveBeenCalledOnce();
    expect(cache.saved.size).toBe(0);
    expect(seen).toEqual([null]);
  });
});
