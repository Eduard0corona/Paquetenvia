import { describe, expect, it } from "vitest";
import {
  checkCsvFile,
  isCommittable,
  maximumCsvBytes,
  parseCsvImportCommit,
  parseCsvImportPreview,
} from "./csv-import";
import { ContractViolationError } from "./strict-json";
import {
  commitResponse,
  invalidPreviewResponse,
  orderId,
  previewResponse,
  quoteId,
  syntheticDigest,
} from "./ui-001-screens.fixtures";

describe("CSV-001 preview parser", () => {
  it("accepts the AI-05 CsvImportPreview shape", () => {
    const preview = parseCsvImportPreview(previewResponse());
    expect(preview).toMatchObject({ total_rows: 1, valid_rows: 1, invalid_rows: 0 });
    expect(isCommittable(preview)).toBe(true);
  });

  it("reports invalid rows and never offers their commit", () => {
    const preview = parseCsvImportPreview(invalidPreviewResponse());
    expect(preview.rows[1].errors[0].code).toBe("QUOTE_ID_INVALID");
    expect(isCommittable(preview)).toBe(false);
  });

  it("accepts a file-level rejection without rows and never offers its commit", () => {
    const preview = parseCsvImportPreview(
      previewResponse({ total_rows: 0, valid_rows: 0, invalid_rows: 0, file_errors: ["HEADER_INVALID"], rows: [] }),
    );
    expect(isCommittable(preview)).toBe(false);
  });

  it("accepts an invalid row the server reports without column errors", () => {
    const preview = parseCsvImportPreview(
      previewResponse({
        valid_rows: 0,
        invalid_rows: 1,
        rows: [{ row_number: 2, quote_id: quoteId, payer_type: null, valid: false, errors: [] }],
      }),
    );
    expect(preview.rows[0].valid).toBe(false);
  });

  it.each([
    ["an extra key", previewResponse({ extra: true })],
    ["counts that do not add up", previewResponse({ valid_rows: 2 })],
    ["totals that differ from the rows", previewResponse({ total_rows: 3 })],
    ["an unknown file error", previewResponse({ file_errors: ["VIRUS"], rows: [], total_rows: 0, valid_rows: 0 })],
    ["file errors together with rows", previewResponse({ file_errors: ["FILE_EMPTY"] })],
    ["a malformed digest", previewResponse({ content_digest: "short" })],
    ["a padded digest", previewResponse({ content_digest: `${syntheticDigest().slice(0, 42)}=` })],
    [
      "a valid row with errors",
      previewResponse({
        rows: [{ row_number: 2, quote_id: quoteId, payer_type: "SENDER", valid: true, errors: [{ column: "x", code: "QUOTE_ID_INVALID" }] }],
      }),
    ],
    [
      "an unknown row error",
      previewResponse({
        valid_rows: 0,
        invalid_rows: 1,
        rows: [{ row_number: 2, quote_id: null, payer_type: null, valid: false, errors: [{ column: "x", code: "NOPE" }] }],
      }),
    ],
    [
      "a non-UUID quote",
      previewResponse({ rows: [{ row_number: 2, quote_id: "not-a-uuid", payer_type: "SENDER", valid: true, errors: [] }] }),
    ],
    [
      "a fractional row number",
      previewResponse({ rows: [{ row_number: 2.5, quote_id: quoteId, payer_type: "SENDER", valid: true, errors: [] }] }),
    ],
  ])("fails closed on %s", (_label, body) => {
    expect(() => parseCsvImportPreview(body)).toThrow(ContractViolationError);
  });
});

describe("CSV-001 commit parser", () => {
  it("accepts created and failed rows", () => {
    const commit = parseCsvImportCommit(
      commitResponse({
        total_rows: 2,
        created_rows: 1,
        failed_rows: 1,
        rows: [
          ...(commitResponse().rows as unknown[]),
          { row_number: 3, quote_id: quoteId, status: "FAILED", order_id: null, public_id: null, error_code: "QUOTE_UNAVAILABLE" },
        ],
      }),
    );
    expect(commit.rows.map((row) => row.status)).toEqual(["CREATED", "FAILED"]);
    expect(commit.rows[0].order_id).toBe(orderId);
  });

  it.each([
    ["created without order", { status: "CREATED", order_id: null, public_id: null, error_code: null }],
    ["created with an error", { status: "CREATED", order_id: orderId, public_id: "PQ-1", error_code: "INVALID_REQUEST" }],
    ["failed with an order", { status: "FAILED", order_id: orderId, public_id: "PQ-1", error_code: "INVALID_REQUEST" }],
    ["an unknown error code", { status: "FAILED", order_id: null, public_id: null, error_code: "OTHER" }],
  ])("fails closed on a row %s", (_label, row) => {
    const body = commitResponse({
      created_rows: row.status === "CREATED" ? 1 : 0,
      failed_rows: row.status === "CREATED" ? 0 : 1,
      rows: [{ row_number: 2, quote_id: quoteId, ...row }],
    });
    expect(() => parseCsvImportCommit(body)).toThrow(ContractViolationError);
  });

  it("fails closed when counts disagree", () => {
    expect(() => parseCsvImportCommit(commitResponse({ created_rows: 0, failed_rows: 1 }))).toThrow(
      ContractViolationError,
    );
  });
});

describe("CSV-001 client file checks", () => {
  it("refuses empty and oversized files before any request", () => {
    expect(checkCsvFile(0)).toBe("empty");
    expect(checkCsvFile(maximumCsvBytes + 1)).toBe("too_large");
    expect(checkCsvFile(maximumCsvBytes)).toBeNull();
    expect(checkCsvFile(1)).toBeNull();
  });
});
