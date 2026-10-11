// HR letters — pure rules for the Letters screen. Import-free so node --test can load it.
//
// Backend: EmployeeSelfServiceController, "HR document requests (B6)".
//   GET  /api/ess/document-requests/types   → EssLetterTypeDto[] (only types the tenant has a template for)
//   POST /api/ess/document-requests          ← { letterType, language, purpose, addresseeName }
//   GET  /api/ess/document-requests          → EssDocumentRequestResponse[] (newest first, max 50)
//   GET  /api/ess/document-requests/{id}/pdf → the issued letter (409 not_issued_yet before HR issues)
// The employee asks; HR issues. Nothing here produces a letter.

export interface LetterType {
  letterType: string;
  nameEn: string;
  nameAr: string;
}

export type LetterLanguage = 'bilingual' | 'en' | 'ar';

export interface LetterRequest {
  id: string;
  letterType: string;
  language: string;
  purpose: string;
  addresseeName: string;
  status: string;
  createdAtUtc: string;
  decidedAtUtc: string | null;
  decisionNote: string;
  referenceNumber: string | null;
  isIssued: boolean;
}

export interface LetterDraft {
  letterType: string | null;
  language: LetterLanguage;
  purpose: string;
  addresseeName: string;
}

/** AddresseeName is varchar(200) on employee_document_requests; Purpose is unbounded but becomes ticket text. */
export const ADDRESSEE_MAX = 200;
export const PURPOSE_MAX = 500;

export const LETTER_LANGUAGES: readonly LetterLanguage[] = ['bilingual', 'ar', 'en'];

export type LetterFieldError = 'required' | 'alreadyOpen' | 'notOffered' | 'tooLong';

export interface LetterValidation {
  ok: boolean;
  errors: Partial<Record<'letterType' | 'purpose' | 'addresseeName', LetterFieldError>>;
}

/** Letter types with a request still waiting on HR. The server refuses a second one (duplicate_pending_request). */
export function openLetterTypes(requests: readonly LetterRequest[]): Set<string> {
  return new Set(requests.filter((r) => r.status === 'Pending').map((r) => r.letterType));
}

export function validateLetterDraft(
  draft: LetterDraft,
  offered: readonly LetterType[],
  open: ReadonlySet<string>,
): LetterValidation {
  const errors: LetterValidation['errors'] = {};
  if (!draft.letterType) errors.letterType = 'required';
  else if (!offered.some((t) => t.letterType === draft.letterType)) errors.letterType = 'notOffered';
  else if (open.has(draft.letterType)) errors.letterType = 'alreadyOpen';
  if (draft.purpose.trim().length > PURPOSE_MAX) errors.purpose = 'tooLong';
  if (draft.addresseeName.trim().length > ADDRESSEE_MAX) errors.addresseeName = 'tooLong';
  return { ok: Object.keys(errors).length === 0, errors };
}

/** The body POST /api/ess/document-requests expects. Empty optional text is sent as null. */
export function letterRequestBody(draft: LetterDraft): {
  letterType: string;
  language: LetterLanguage;
  purpose: string | null;
  addresseeName: string | null;
} {
  if (!draft.letterType) throw new Error('letterType is required');
  const purpose = draft.purpose.trim();
  const addressee = draft.addresseeName.trim();
  return {
    letterType: draft.letterType,
    language: draft.language,
    purpose: purpose || null,
    addresseeName: addressee || null,
  };
}

export type LetterStatusKey = 'pending' | 'issued' | 'declined' | 'cancelled' | 'unknown';
export type Tone = 'success' | 'warning' | 'danger' | 'info' | 'neutral';

/** Server statuses (EmployeeDocumentRequestStatuses): Pending, Issued, Declined, Cancelled. */
export function letterStatus(request: Pick<LetterRequest, 'status' | 'isIssued'>): { key: LetterStatusKey; tone: Tone } {
  if (request.isIssued || request.status === 'Issued') return { key: 'issued', tone: 'success' };
  switch (request.status) {
    case 'Pending': return { key: 'pending', tone: 'warning' };
    case 'Declined': return { key: 'declined', tone: 'danger' };
    case 'Cancelled': return { key: 'cancelled', tone: 'neutral' };
    default: return { key: 'unknown', tone: 'neutral' };
  }
}

/** The letter's name in the reader's language; the English name when no Arabic one is configured. */
export function letterTypeName(
  letterType: string,
  types: readonly LetterType[],
  language: 'en' | 'ar',
): string | null {
  const match = types.find((t) => t.letterType === letterType);
  if (!match) return null;
  return language === 'ar' && match.nameAr.trim() ? match.nameAr : match.nameEn;
}

export type LetterServerError =
  | 'duplicate'
  | 'notConfigured'
  | 'notIssuedYet'
  | 'reprintUnavailable'
  | 'busy'
  | 'notLinked'
  | 'generic';

/** Maps a refusal from the document-request endpoints to a plain-language message key. */
export function letterServerError(status: number | undefined, code: string | undefined): LetterServerError {
  if (code === 'duplicate_pending_request') return 'duplicate';
  if (code === 'template_not_configured') return 'notConfigured';
  if (code === 'not_issued_yet') return 'notIssuedYet';
  if (code === 'reprint_unavailable') return 'reprintUnavailable';
  if (status === 503) return 'busy';
  // GetEssContextAsync answers 400 with { message, messageAr } when the login has no employee link.
  if (status === 400) return 'notLinked';
  return 'generic';
}

/** A file name for the cache copy: the reference number when there is one, never path characters. */
export function letterFileName(request: Pick<LetterRequest, 'id' | 'referenceNumber'>): string {
  const base = (request.referenceNumber || request.id).replace(/[^A-Za-z0-9_-]+/g, '-').replace(/^-+|-+$/g, '');
  return `${base || 'letter'}.pdf`;
}
