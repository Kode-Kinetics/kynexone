import client from './client';

/** P2 — ESS "Complete your profile" (sections) and the HR review inbox. The legacy blob lives in employeeCompletion.ts, untouched. */

export type SectionKey = 'person' | 'emergency' | 'arabicName' | 'id' | 'bank' | 'documents';
export type SectionState = 'Missing' | 'Confirm' | 'Pending' | 'ChangesRequested' | 'Accepted' | 'AwaitingFinance';
export type DocumentKind = 'national_id_copy' | 'iqama_copy' | 'passport_copy' | 'bank_letter' | 'other';

export const SECTION_ORDER: SectionKey[] = ['person', 'emergency', 'arabicName', 'id', 'bank', 'documents'];
export const DOCUMENT_KINDS: DocumentKind[] = ['national_id_copy', 'iqama_copy', 'passport_copy', 'bank_letter', 'other'];
export const MAX_FILE_BYTES = 5 * 1024 * 1024;
export const MAX_ATTACHMENTS = 3;
export const ACCEPTED_TYPES = '.pdf,.jpg,.jpeg,.png';

export interface EssField {
  key: string;
  /** Dictionary key (`ess.completion.field.*`); `label` is the English fallback. */
  labelKey: string;
  label?: string;
  /** Masked when `sensitive`; the editable value otherwise. */
  value: string;
  required: boolean;
  kind: 'Text' | 'Email' | 'Phone' | 'Enum' | 'NationalId' | 'Iqama' | 'Border' | 'Iban' | 'BankName' | 'GccId' | 'Passport' | 'Date';
  maxLength: number;
  sensitive: boolean;
  options?: string[];
}
export interface EssDocument { id: string; kind: DocumentKind; scanStatus: 'Clean' | 'PendingScan' | 'Quarantined'; fileName?: string }
export interface EssSection {
  key: SectionKey;
  state: SectionState;
  requiresStepUp: boolean;
  reviewNote?: string | null;
  requestId?: string | null;
  submittedAtUtc?: string | null;
  fields: EssField[];
  documents: EssDocument[];
}
export interface EssCompletion {
  progress: { done: number; total: number };
  noticeVersion: string;
  hosting: string;
  sections: EssSection[];
}
export interface SubmitBody { noticeVersion: string; confirm: boolean; values: Record<string, string>; documentIds: string[] }

/** The shape of the server's problem+json for this flow. */
export interface EssProblem { code?: string; titleKey?: string; status?: number; errors?: Record<string, string[]> }

export function problemOf(error: unknown): EssProblem {
  const data = (error as { response?: { data?: unknown } } | null)?.response?.data;
  return data && typeof data === 'object' ? (data as EssProblem) : {};
}
export const isStepUpRequired = (error: unknown) => (error as { response?: { status?: number } } | null)?.response?.status === 401 && problemOf(error).code === 'step_up_required';

/**
 * Calls that can be answered 401 on purpose (a step-up challenge, a wrong password) must not enter the session-refresh path:
 * the interceptor would refresh, replay the request and count a second failed attempt. `_retry` is the interceptor's own flag.
 */
const noRefresh = { _retry: true } as Record<string, unknown>;
const stepUpHeaders = (token?: string) => (token ? { 'X-Step-Up': token } : undefined);

export const essCompletionApi = {
  get: () => client.get<EssCompletion>('/api/ess/completion').then(r => r.data),
  submit: (section: SectionKey, body: SubmitBody, stepUp?: string) =>
    client.post<EssSection>(`/api/ess/completion/${section}/submit`, body, { ...noRefresh, headers: stepUpHeaders(stepUp) }).then(r => r.data),
  upload: (section: SectionKey, kind: DocumentKind, file: File, stepUp?: string) => {
    const form = new FormData();
    form.append('section', section); form.append('kind', kind); form.append('file', file);
    return client.post<EssDocument>('/api/ess/completion/documents', form, { ...noRefresh, headers: stepUpHeaders(stepUp) }).then(r => r.data);
  },
  stepUp: (password: string, mfaCode?: string) =>
    client.post<{ stepUpToken: string; expiresAtUtc: string }>('/api/auth/step-up', { purpose: 'ess.sensitive', password, mfaCode: mfaCode || undefined }, noRefresh).then(r => r.data),
};

// ── HR ───────────────────────────────────────────────────────────────────────

export interface InboxDiff { field: string; labelKey: string; before: string; after: string; changed: boolean }
export interface InboxItem {
  requestId: string;
  employee: { id: number; code: string; name: string };
  section: SectionKey;
  status: string;
  submittedAtUtc: string;
  goesToFinance: boolean;
  diff: InboxDiff[];
  documents: Array<{ id: string; kind: DocumentKind; scanStatus: string; fileName: string }>;
}
export interface InboxQuery { status?: string; section?: SectionKey | ''; companyId?: string; page?: number }

export const hrCompletionApi = {
  inbox: (q: InboxQuery = {}) => client.get<InboxItem[]>('/api/hr/profile-completion/inbox', { params: { status: q.status ?? 'PendingHR', section: q.section || undefined, companyId: q.companyId || undefined, page: q.page } }).then(r => r.data),
  accept: (id: string, notes: string, overrideReason?: string) => client.post<{ outcome?: string; changeRequestId?: string }>(`/api/ess/profile-change-requests/${id}/approve`, { notes, overrideReason }).then(r => r.data),
  requestChanges: (id: string, notes: string) => client.post(`/api/ess/profile-change-requests/${id}/reject`, { notes }),
  documentUrl: (id: string) => `/api/hr/profile-completion/documents/${id}/download`,
};
