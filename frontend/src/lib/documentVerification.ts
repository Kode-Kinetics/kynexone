import type { EmployeeDocument } from '../api/employees';

/**
 * G-004: where an employee document stands with HR's verification, and what HR may do about it. The server owns the
 * rules (employees.documents, data scope, and employees.sensitive for a protected category); this only decides which words and
 * buttons to show.
 */
export type DocumentVerificationState = 'pending' | 'verified' | 'rejected' | 'approved' | 'expired' | 'other';

export function documentVerificationState(raw?: string | null): DocumentVerificationState {
  switch ((raw ?? '').trim().toLowerCase()) {
    case 'pending': return 'pending';
    case 'verified': return 'verified';
    case 'rejected': return 'rejected';
    case 'approved': return 'approved';
    case 'expired': return 'expired';
    default: return 'other';
  }
}

/** The status in words, for the employee and for HR. A key so the translation layer owns the wording. */
export function documentStatusWords(raw?: string | null): string {
  switch (documentVerificationState(raw)) {
    case 'pending': return 'Waiting for HR to verify';
    case 'verified': return 'Verified by HR';
    case 'rejected': return 'Rejected by HR';
    case 'approved': return 'Accepted by HR';
    case 'expired': return 'Expired';
    default: return raw?.trim() || 'Waiting for HR to verify';
  }
}

export const MIN_REJECT_REASON = 3;
export const MAX_REJECT_REASON = 500;

/** A rejection needs a reason the employee can read. Returns the trimmed text, or null when it is too short or too long. */
export function rejectReasonOrNull(text: string): string | null {
  const trimmed = text.trim();
  return trimmed.length >= MIN_REJECT_REASON && trimmed.length <= MAX_REJECT_REASON ? trimmed : null;
}

/** HR decides on a document that is waiting. A verified or rejected one is history here (a re-upload is a new document). */
export const canDecideDocument = (doc: Pick<EmployeeDocument, 'approvalStatus'>, holdsDocuments: boolean): boolean =>
  holdsDocuments && documentVerificationState(doc.approvalStatus) === 'pending';
