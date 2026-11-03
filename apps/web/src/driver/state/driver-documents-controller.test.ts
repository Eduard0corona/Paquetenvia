import { describe, expect, it, vi } from "vitest";
import { DriverDocumentsApiError, type DriverDocumentsApi } from "../api/driver-documents-api";
import type { MyDriverDocuments } from "../contracts/driver-documents";
import { DriverDocumentsController, driverDocumentsMessages } from "./driver-documents-controller";

/** DRV-DOC-001-IN-PERSON-2026-11-02 (Y8): read on open and on refresh, nothing else; read-only. */
const enabled: MyDriverDocuments = {
  documents_enabled: true,
  documents: [{ document_type: "IDENTITY", document: null }],
};

function setup(getMyDocuments: DriverDocumentsApi["getMyDocuments"]) {
  const api = { getMyDocuments: vi.fn(getMyDocuments) };
  const controller = new DriverDocumentsController(api, () => 1_000_000);
  return { api, controller };
}

describe("driver documents controller", () => {
  it("shows the list as read, with the time it was read", async () => {
    const { api, controller } = setup(async () => enabled);
    await controller.load(true);
    expect(api.getMyDocuments).toHaveBeenCalledWith(expect.any(AbortSignal));
    expect(controller.state).toEqual({ phase: "ready", documents: enabled.documents, readAt: 1_000_000, refreshing: false, message: null });
  });

  it("shows nothing while documents are off or the account is not a driver here", async () => {
    for (const answer of [
      async () => ({ documents_enabled: false, documents: [] }),
      async () => {
        throw new DriverDocumentsApiError("forbidden");
      },
      async () => {
        throw new DriverDocumentsApiError("unauthorized");
      },
    ] satisfies DriverDocumentsApi["getMyDocuments"][]) {
      const { controller } = setup(answer);
      await controller.load(true);
      expect(controller.state.phase).toBe("hidden");
    }
  });

  it("asks for a connection offline and keeps the list a failed refresh already showed", async () => {
    const offline = setup(async () => enabled);
    await offline.controller.load(false);
    expect(offline.api.getMyDocuments).not.toHaveBeenCalled();
    expect(offline.controller.state).toMatchObject({ phase: "error", message: driverDocumentsMessages.offline });

    const get = vi
      .fn<DriverDocumentsApi["getMyDocuments"]>()
      .mockResolvedValueOnce(enabled)
      .mockRejectedValueOnce(new DriverDocumentsApiError("unavailable"))
      .mockRejectedValueOnce(new DriverDocumentsApiError("network"));
    const { controller } = setup(get);
    await controller.load(true);
    await controller.load(true);
    expect(controller.state).toMatchObject({ phase: "ready", documents: enabled.documents, message: driverDocumentsMessages.loadFailed });
    await controller.load(false);
    expect(controller.state).toMatchObject({ phase: "ready", message: driverDocumentsMessages.offline });

    const failing = setup(async () => {
      throw new DriverDocumentsApiError("invalid-contract");
    });
    await failing.controller.load(true);
    expect(failing.controller.state).toMatchObject({ phase: "error", message: driverDocumentsMessages.loadFailed });
  });

  it("starts no second read while one is in flight and forgets everything once disposed", async () => {
    let release: (value: MyDriverDocuments) => void = () => undefined;
    const { api, controller } = setup(() => new Promise<MyDriverDocuments>((resolve) => { release = resolve; }));
    const first = controller.load(true);
    await controller.load(true);
    expect(api.getMyDocuments).toHaveBeenCalledOnce();
    expect(controller.state.refreshing).toBe(true);
    controller.dispose();
    release(enabled);
    await first;
    expect(controller.state.phase).toBe("loading");
  });
});
