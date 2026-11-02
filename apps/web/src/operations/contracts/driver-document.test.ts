import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import {
  daysBetween,
  documentsColumn,
  documentWarning,
  driverDocumentConflictMessages,
  driverDocumentExpiryBases,
  driverDocumentRejectionCodes,
  driverDocumentRejectionLabels,
  driverDocumentSearch,
  driverDocumentStatuses,
  driverDocumentTypeLabels,
  driverDocumentTypes,
  formatCalendarDate,
  inPersonReviewLine,
  isCalendarDate,
  isRevocable,
  mazatlanToday,
  parseDriverDocument,
  parseDriverDocumentPage,
  permanentLicenseReviewDate,
  recordDriverDocumentBody,
  slotDateText,
  slotStatusText,
  withDocument,
  type DriverDocument,
  type DriverDocumentSlot,
} from "./driver-document";

/** DRV-DOC-001-IN-PERSON-2026-11-02: the panel's reading of AI-05 and AI-07 for in-person driver documents. */
const openApi = readFileSync(resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"), "utf8");
const aiSeven = readFileSync(resolve(process.cwd(), "../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml"), "utf8");

/** 15:00 on 2 November 2026 in Culiacán (UTC-7), already 3 November in Tokyo. */
const now = Date.parse("2026-11-02T22:00:00Z");

function document(overrides: Partial<DriverDocument> = {}): DriverDocument {
  return {
    id: "d0c00000-0000-4000-8000-0000000000d1",
    document_type: "DRIVER_LICENSE",
    status: "VALID",
    rejection_code: null,
    expires_at: "2027-03-02T07:00:00+00:00",
    valid_through: "2027-03-01",
    expiry_basis: "PRINTED_EXPIRY",
    recorded_at: "2026-11-02T18:00:00+00:00",
    revoked_at: null,
    ...overrides,
  };
}

/** A VALID document whose last valid day is `validThrough`, expiring at 00:00 Culiacán of the next day. */
function lastDay(validThrough: string, overrides: Partial<DriverDocument> = {}): DriverDocument {
  const next = new Date(Date.parse(`${validThrough}T07:00:00Z`) + 86_400_000).toISOString();
  return document({ valid_through: validThrough, expires_at: next, ...overrides });
}

function slot(type: DriverDocumentSlot["document_type"], value: DriverDocument | null): DriverDocumentSlot {
  return { document_type: type, document: value === null ? null : { ...value, document_type: type } };
}

/** The enum of one property of the AI-05 DriverDocument schema, without null. */
function enumOf(property: string): string[] {
  const schema = openApi.slice(openApi.indexOf("\n    DriverDocument:\n"), openApi.indexOf("\n    DriverDocumentSlot:\n"));
  const lines = schema.slice(schema.indexOf(`\n        ${property}:\n`) + 1).split("\n");
  const start = lines.findIndex((line) => line === "          enum:");
  const values: string[] = [];
  for (const line of lines.slice(start + 1)) {
    const match = /^ {10}- (\S+)$/.exec(line);
    if (match === null) break;
    if (match[1] !== "null") values.push(match[1]!);
  }
  return values;
}

describe("driver documents contract (DRV-DOC-001-IN-PERSON-2026-11-02)", () => {
  it("uses the AI-05 vocabularies and the AI-07 labels", () => {
    expect(enumOf("document_type")).toEqual([...driverDocumentTypes]);
    expect(enumOf("status")).toEqual([...driverDocumentStatuses]);
    expect(enumOf("rejection_code")).toEqual([...driverDocumentRejectionCodes]);
    expect(enumOf("expiry_basis")).toEqual([...driverDocumentExpiryBases]);
    const labels = aiSeven.slice(aiSeven.indexOf("      type_labels:\n"));
    for (const type of driverDocumentTypes) expect(labels).toContain(`        ${type}: ${driverDocumentTypeLabels[type]}\n`);
    const record = aiSeven.slice(aiSeven.indexOf("      record_dialog:"), aiSeven.indexOf("      revoke_dialog:"));
    expect(record.replace(/\s+/g, " ")).toContain(
      "Ilegible, Vencido, No es el pedido, No es del repartidor, Incompleto, No aceptado (Y7)",
    );
    expect(Object.values(driverDocumentRejectionLabels)).toEqual([
      "Ilegible",
      "Vencido",
      "No es el pedido",
      "No es del repartidor",
      "Incompleto",
      "No aceptado",
    ]);
    expect(record.replace(/\s+/g, " ")).toContain(inPersonReviewLine);
    expect(record.replace(/\s+/g, " ")).toContain(
      `EXPIRY_DATE_OUT_OF_RANGE ("${driverDocumentConflictMessages.EXPIRY_DATE_OUT_OF_RANGE}")`,
    );
    expect(driverDocumentTypes).not.toContain("BACKGROUND_CHECK");
    expect(driverDocumentTypes).not.toContain("OTHER");
  });

  it("reads a page and fails closed on anything outside the contract", () => {
    const page = {
      documents_enabled: true,
      items: [
        {
          driver_id: "e1a00000-0000-4000-8000-0000000000a1",
          documents: [
            { document_type: "IDENTITY", document: null },
            { document_type: "DRIVER_LICENSE", document: document() },
          ],
        },
      ],
      next_cursor: "BAAAAAAAAAAAAAAAAAAAAAA",
    };
    expect(parseDriverDocumentPage(page)).toEqual(page);
    expect(parseDriverDocumentPage({ documents_enabled: false, items: [], next_cursor: null })).toEqual({
      documents_enabled: false,
      items: [],
      next_cursor: null,
    });

    const invalid: unknown[] = [
      { ...page, extra: 1 },
      { documents_enabled: false, items: page.items, next_cursor: null },
      { ...page, next_cursor: "a&b" },
      { ...page, items: [page.items[0], page.items[0]] },
      { ...page, items: [{ ...page.items[0], documents: [...page.items[0]!.documents].reverse() }] },
      { ...page, items: [{ ...page.items[0], documents: [{ document_type: "IDENTITY", document: document() }] }] },
      { ...page, items: [{ ...page.items[0], documents: [{ document_type: "OTHER", document: null }] }] },
    ];
    for (const value of invalid) expect(() => parseDriverDocumentPage(value)).toThrow();

    for (const value of [
      { ...document(), object_key: "k" },
      { ...document(), sha256: "00" },
      { ...document(), document_number: "123" },
      { ...document(), reviewed_by: "e1a00000-0000-4000-8000-0000000000a1" },
      document({ rejection_code: "ILLEGIBLE" }),
      document({ valid_through: null }),
      document({ valid_through: "2027-02-30" }),
      document({ document_type: "IDENTITY", expiry_basis: "PERMANENT_LICENSE_REVIEW" }),
      document({ revoked_at: "2026-11-02T18:00:00+00:00" }),
      document({ status: "EXPIRED_SOON" as DriverDocument["status"] }),
    ])
      expect(() => parseDriverDocument(value)).toThrow();
    // Older FILE rows: rejected without a reason, revoked without a time, a date without a basis.
    expect(parseDriverDocument(document({ status: "REJECTED", expires_at: null, valid_through: null, expiry_basis: null })))
      .toMatchObject({ status: "REJECTED", rejection_code: null });
    expect(parseDriverDocument(document({ status: "REVOKED", expiry_basis: null }))).toMatchObject({ revoked_at: null });
  });

  it("sends exactly one of the three request shapes and only a cursor", () => {
    expect(
      recordDriverDocumentBody({ documentType: "INSURANCE", decision: "VALID", permanentLicense: false, printedExpiry: "2027-03-01" }),
    ).toEqual({ document_type: "INSURANCE", decision: "VALID", expiry_basis: "PRINTED_EXPIRY", expires_on: "2027-03-01" });
    expect(recordDriverDocumentBody({ documentType: "DRIVER_LICENSE", decision: "VALID", permanentLicense: true })).toEqual({
      document_type: "DRIVER_LICENSE",
      decision: "VALID",
      expiry_basis: "PERMANENT_LICENSE_REVIEW",
    });
    expect(recordDriverDocumentBody({ documentType: "IDENTITY", decision: "REJECTED", rejectionCode: "HOLDER_MISMATCH" })).toEqual({
      document_type: "IDENTITY",
      decision: "REJECTED",
      rejection_code: "HOLDER_MISMATCH",
    });
    expect(() =>
      recordDriverDocumentBody({ documentType: "IDENTITY", decision: "VALID", permanentLicense: false, printedExpiry: "2027-13-01" }),
    ).toThrow();
    expect(() =>
      recordDriverDocumentBody({ documentType: "IDENTITY" as "DRIVER_LICENSE", decision: "VALID", permanentLicense: true }),
    ).toThrow();
    expect(() =>
      recordDriverDocumentBody({ documentType: "IDENTITY", decision: "REJECTED", rejectionCode: "OTHER" as "ILLEGIBLE" }),
    ).toThrow();
    expect(driverDocumentSearch().toString()).toBe("");
    expect(driverDocumentSearch("BAAAAAAAAAAAAAAAAAAAAAA").toString()).toBe("cursor=BAAAAAAAAAAAAAAAAAAAAAA");
    expect(() => driverDocumentSearch("a&b")).toThrow();
  });

  it("computes dates in Culiacán whatever the device zone (Y5, Y12)", () => {
    expect(mazatlanToday(now)).toBe("2026-11-02");
    expect(mazatlanToday(Date.parse("2026-11-03T06:59:59Z"))).toBe("2026-11-02");
    expect(mazatlanToday(Date.parse("2026-11-03T07:00:00Z"))).toBe("2026-11-03");
    expect(daysBetween("2026-11-02", "2026-12-02")).toBe(30);
    expect(permanentLicenseReviewDate("2026-11-02")).toBe("2027-11-02");
    expect(permanentLicenseReviewDate("2028-02-29")).toBe("2029-02-28");
    expect(permanentLicenseReviewDate("2026-12-31")).toBe("2027-12-31");
    expect(formatCalendarDate("2027-03-01")).toMatch(/^1 mar\.? 2027$/);
    expect(isCalendarDate("2027-02-29")).toBe(false);
    expect(isCalendarDate("2028-02-29")).toBe(true);
  });

  it("warns from 30 days before the last valid day and says Vencido once it passed (Y6)", () => {
    expect(documentWarning(lastDay("2026-12-03"), now)).toBeNull();
    expect(documentWarning(lastDay("2026-12-02"), now)).toEqual({ text: "Vence en 30 días", tone: "warn" });
    expect(documentWarning(lastDay("2026-11-03"), now)).toEqual({ text: "Vence en 1 día", tone: "warn" });
    expect(documentWarning(lastDay("2026-11-02"), now)).toEqual({ text: "Vence hoy", tone: "warn" });
    // The last valid day is valid all of it: at 00:00 Culiacán of the next day it is expired.
    expect(documentWarning(lastDay("2026-11-02"), Date.parse("2026-11-03T06:59:59Z"))).toEqual({ text: "Vence hoy", tone: "warn" });
    expect(documentWarning(lastDay("2026-11-02"), Date.parse("2026-11-03T07:00:00Z"))).toEqual({ text: "Vencido", tone: "crit" });
    const review = { expiry_basis: "PERMANENT_LICENSE_REVIEW" } as const;
    expect(documentWarning(lastDay("2026-11-20", review), now)).toEqual({ text: "Revisión en 18 días", tone: "warn" });
    expect(documentWarning(lastDay("2026-11-02", review), now)).toEqual({ text: "Revisión hoy", tone: "warn" });
    expect(documentWarning(lastDay("2026-11-01", review), now)).toEqual({ text: "Revisión vencida", tone: "crit" });
    // Only a VALID document warns.
    expect(documentWarning(lastDay("2026-11-01", { status: "REVOKED", revoked_at: "2026-11-01T18:00:00+00:00" }), now)).toBeNull();
  });

  it("puts Vencido first, then Incompletos, then the nearest warning, then Completos", () => {
    const valid = lastDay("2027-06-30");
    const soon = lastDay("2026-11-20");
    const sooner = lastDay("2026-11-10", { expiry_basis: "PERMANENT_LICENSE_REVIEW" });
    const expired = lastDay("2026-11-01");
    const rejected = document({ status: "REJECTED", rejection_code: "ILLEGIBLE", expires_at: null, valid_through: null, expiry_basis: null });
    expect(documentsColumn([], now)).toEqual({ text: "Sin documentos exigidos", tone: "muted" });
    expect(documentsColumn([slot("IDENTITY", valid), slot("DRIVER_LICENSE", valid)], now)).toEqual({ text: "Completos", tone: "ok" });
    expect(documentsColumn([slot("IDENTITY", valid), slot("DRIVER_LICENSE", soon)], now)).toEqual({ text: "Vence en 18 días", tone: "warn" });
    expect(documentsColumn([slot("IDENTITY", soon), slot("DRIVER_LICENSE", sooner)], now)).toEqual({ text: "Revisión en 8 días", tone: "warn" });
    for (const missing of [null, rejected, document({ status: "PENDING" }), document({ status: "EXPIRED" })])
      expect(documentsColumn([slot("IDENTITY", missing), slot("DRIVER_LICENSE", soon)], now)).toEqual({ text: "Incompletos", tone: "crit" });
    expect(documentsColumn([slot("IDENTITY", null), slot("DRIVER_LICENSE", expired)], now)).toEqual({ text: "Vencido", tone: "crit" });
  });

  it("describes each slot of the dialog and revokes only a VALID one", () => {
    expect(slotStatusText(slot("IDENTITY", null))).toBe("Falta");
    const rejected = document({ status: "REJECTED", rejection_code: "WRONG_DOCUMENT", expires_at: null, valid_through: null, expiry_basis: null });
    expect(slotStatusText(slot("IDENTITY", rejected))).toBe("Rechazado: No es el pedido");
    expect(slotStatusText(slot("IDENTITY", document()))).toBe("Aprobado");
    expect(slotStatusText(slot("IDENTITY", document({ status: "REVOKED", revoked_at: "2026-11-02T18:00:00+00:00" })))).toBe("Revocado");
    expect(slotStatusText(slot("IDENTITY", document({ status: "EXPIRED" })))).toBe("Vencido");
    expect(slotStatusText(slot("IDENTITY", document({ status: "PENDING" })))).toBe("Pendiente");
    expect(slotDateText(slot("IDENTITY", document()))).toMatch(/^Vence el 1 mar\.? 2027$/);
    expect(slotDateText(slot("DRIVER_LICENSE", document({ expiry_basis: "PERMANENT_LICENSE_REVIEW" })))).toMatch(/^Revisión el 1 mar\.? 2027$/);
    expect(slotDateText(slot("IDENTITY", rejected))).toBeNull();
    expect(isRevocable(slot("IDENTITY", document()))).toBe(true);
    expect(isRevocable(slot("IDENTITY", rejected))).toBe(false);
    expect(isRevocable(slot("IDENTITY", null))).toBe(false);
    const slots = [slot("IDENTITY", null), slot("DRIVER_LICENSE", document())];
    const revoked = document({ status: "REVOKED", revoked_at: "2026-11-02T19:00:00+00:00" });
    expect(withDocument(slots, revoked)).toEqual([slots[0], { document_type: "DRIVER_LICENSE", document: revoked }]);
  });
});
