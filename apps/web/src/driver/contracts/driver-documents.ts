import {
  driverDocumentTypes,
  parseDriverDocumentSlot,
  type DriverDocumentSlot,
} from "../../operations/contracts/driver-document";
import { array, boolean, exactObject, fail } from "../../operations/contracts/strict-json";

/**
 * DRV-DOC-001-IN-PERSON-2026-11-02 (AI-07 driver_account documents; Y4, Y6, Y8): AI-05 listMyDriverDocuments, the
 * driver's own documents for the read-only "Cuenta > Documentos" list. Dispatch reviews them in person (Y1); the app
 * never adds, edits or deletes one, never shows a file, a number, a key, a hash or who reviewed it (none exists), and
 * never keeps them in browser storage or the offline queue. The reader fails closed on the first value outside the
 * contract.
 */
export interface MyDriverDocuments {
  /** False while Drivers:Documents:Enabled is off (the pilot until GATE-007, Y2): no "Documentos" section. */
  readonly documents_enabled: boolean;
  /** One slot per document type the driver's vehicle requires, in the published order. */
  readonly documents: readonly DriverDocumentSlot[];
}

export function parseMyDriverDocuments(value: unknown): MyDriverDocuments {
  const object = exactObject(value, ["documents_enabled", "documents"]);
  const enabled = boolean(object.documents_enabled);
  const documents = array(object.documents, driverDocumentTypes.length).map(parseDriverDocumentSlot);
  const order = documents.map((slot) => driverDocumentTypes.indexOf(slot.document_type));
  if (order.some((index, position) => position > 0 && index <= order[position - 1]!)) fail();
  // Off means nothing was read.
  if (!enabled && documents.length > 0) fail();
  return { documents_enabled: enabled, documents };
}

/** The fixed line of "Cuenta > Documentos" (AI-07 driver_account documents). */
export const driverDocumentsFixedLine = "Despacho revisa tus documentos en persona. Lleva el original cuando te lo pidan.";
