import { canDownloadScanned, scanState } from './scanState';

export type DocumentDownloadState = 'available' | 'needs_documents_permission' | 'needs_sensitive_permission' | 'scan_blocked';

/**
 * What the employee workspace offers for one document. Mirrors GET /api/employees/documents/{id}/download:
 * employees.documents is required (the route answers 403 otherwise), a sensitive file also needs employees.sensitive
 * (the list masks such a file's name, so an empty name means "masked"), and a file the upload scan has not passed is
 * refused with 409. The server stays the authority; this only decides whether to show the action or the reason.
 */
export function documentDownloadState(
  doc: { fileName?: string | null; scanStatus?: string | null },
  can: { documents: boolean; sensitive: boolean },
): DocumentDownloadState {
  if (!can.documents) return 'needs_documents_permission';
  if (!doc.fileName && !can.sensitive) return 'needs_sensitive_permission';
  if (!canDownloadScanned(scanState(doc.scanStatus))) return 'scan_blocked';
  return 'available';
}
