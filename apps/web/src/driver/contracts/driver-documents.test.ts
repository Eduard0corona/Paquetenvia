import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { driverDocumentsFixedLine, parseMyDriverDocuments } from "./driver-documents";

/** DRV-DOC-001-IN-PERSON-2026-11-02: AI-05 MyDriverDocuments and the AI-07 driver_account documents text. */
const aiSeven = readFileSync(resolve(process.cwd(), "../../docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml"), "utf8");
const openApi = readFileSync(resolve(process.cwd(), "../../docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml"), "utf8");

const valid = {
  id: "d0c00000-0000-4000-8000-0000000000d1",
  document_type: "DRIVER_LICENSE",
  status: "VALID",
  rejection_code: null,
  expires_at: "2027-03-02T07:00:00+00:00",
  valid_through: "2027-03-01",
  expiry_basis: "PRINTED_EXPIRY",
  recorded_at: "2026-11-02T18:00:00+00:00",
  revoked_at: null,
};

describe("my driver documents contract", () => {
  it("reads the published shape and the fixed AI-07 line", () => {
    const answer = {
      documents_enabled: true,
      documents: [
        { document_type: "IDENTITY", document: null },
        { document_type: "DRIVER_LICENSE", document: valid },
      ],
    };
    expect(parseMyDriverDocuments(answer)).toEqual(answer);
    expect(parseMyDriverDocuments({ documents_enabled: false, documents: [] })).toEqual({
      documents_enabled: false,
      documents: [],
    });
    const account = aiSeven.slice(aiSeven.indexOf("\n  driver_account:\n"), aiSeven.indexOf("\n  external_driver_offers:\n"));
    expect(account.replace(/\s+/g, " ")).toContain(`"${driverDocumentsFixedLine}"`);
    const schema = openApi.slice(openApi.indexOf("\n    MyDriverDocuments:\n"));
    expect(schema).toContain("- documents_enabled\n      - documents\n");
  });

  it("fails closed on anything else", () => {
    for (const value of [
      { documents_enabled: true },
      { documents_enabled: true, documents: [], driver_id: "e1a00000-0000-4000-8000-0000000000a1" },
      { documents_enabled: false, documents: [{ document_type: "IDENTITY", document: null }] },
      { documents_enabled: "true", documents: [] },
      {
        documents_enabled: true,
        documents: [
          { document_type: "DRIVER_LICENSE", document: valid },
          { document_type: "IDENTITY", document: null },
        ],
      },
      { documents_enabled: true, documents: [{ document_type: "IDENTITY", document: valid }] },
      { documents_enabled: true, documents: [{ document_type: "DRIVER_LICENSE", document: { ...valid, object_key: "k" } }] },
      { documents_enabled: true, documents: [{ document_type: "DRIVER_LICENSE", document: { ...valid, reviewed_by: "x" } }] },
      { documents_enabled: true, documents: [{ document_type: "BACKGROUND_CHECK", document: null }] },
    ])
      expect(() => parseMyDriverDocuments(value)).toThrow();
  });
});
