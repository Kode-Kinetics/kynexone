import client from './client';

// ── Types ──────────────────────────────────────────────────────────────────────

export interface LetterTypeInfo {
  letterType: string;
  referencePrefix: string;
  nameEn: string;
  nameAr: string;
  employeeRequestable: boolean;
  /** False when this tenant has no template for the type — the UI must not offer it. */
  isConfigured: boolean;
  languages: string;
}

export interface LetterTemplate {
  id: string;
  companyId?: string;
  letterType: string;
  nameEn: string;
  nameAr: string;
  language: string;
  titleEn: string;
  titleAr: string;
  bodyEn: string;
  bodyAr: string;
  closingEn: string;
  closingAr: string;
  isActive: boolean;
  isSystemDefault: boolean;
  version: number;
  updatedAtUtc?: string;
}

export interface IssuedLetter {
  id: string;
  referenceNumber: string;
  letterType: string;
  employeeId: number;
  employeeCode: string;
  employeeName: string;
  language: string;
  purpose: string;
  addresseeName: string;
  issuedByName: string;
  issuedByTitle: string;
  issuedAtUtc: string;
  fileHash: string;
  fileSizeBytes: number;
  fromEmployeeRequest: boolean;
}

export interface DocumentRequest {
  id: string;
  employeeId: number;
  employeeCode: string;
  employeeName: string;
  letterType: string;
  language: string;
  purpose: string;
  addresseeName: string;
  status: string;
  createdAtUtc: string;
  decidedAtUtc?: string;
  decisionNote: string;
  issuedLetterId?: string;
  hrRequestId?: string;
}

export interface Paged<T> { total: number; page: number; pageSize: number; items: T[] }

/** A refusal the UI must show verbatim: HR needs to know WHICH field is empty. */
export interface LetterIssueRefusal {
  code?: string;
  message?: string;
  unresolvedFields?: string[];
}

export function letterRefusalFrom(err: unknown): LetterIssueRefusal | null {
  const e = err as { response?: { status?: number; data?: LetterIssueRefusal } };
  if (e?.response?.status === 409 || e?.response?.status === 400) return e.response?.data ?? null;
  return null;
}

// ── Download helper ───────────────────────────────────────────────────────────

async function downloadPdf(url: string, method: 'get' | 'post', body?: unknown) {
  const response = method === 'post'
    ? await client.post(url, body, { responseType: 'blob' })
    : await client.get(url, { responseType: 'blob' });

  const reference = String(response.headers['x-letter-reference'] ?? '');
  const disposition = String(response.headers['content-disposition'] ?? '');
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
  const filename = match ? decodeURIComponent(match[1]) : `${reference || 'letter'}.pdf`;

  const objectUrl = URL.createObjectURL(new Blob([response.data], { type: 'application/pdf' }));
  const anchor = document.createElement('a');
  anchor.href = objectUrl;
  anchor.download = filename;
  anchor.click();
  URL.revokeObjectURL(objectUrl);
  return { reference, filename };
}

// ── API ───────────────────────────────────────────────────────────────────────

export const hrLettersApi = {
  types: () => client.get<LetterTypeInfo[]>('/api/hr-letters/types').then((r) => r.data),

  templates: () =>
    client.get<{ items: LetterTemplate[]; knownTokens: string[] }>('/api/hr-letters/templates').then((r) => r.data),

  updateTemplate: (id: string, body: Partial<LetterTemplate> & { language: string }) =>
    client.put<LetterTemplate>(`/api/hr-letters/templates/${id}`, body).then((r) => r.data),

  seedDefaults: () =>
    client.post<{ added: number }>('/api/hr-letters/templates/seed-defaults').then((r) => r.data),

  issue: (body: { employeeId: number; letterType: string; language: string; purpose: string; addresseeName: string }) =>
    downloadPdf('/api/hr-letters/issue', 'post', body),

  register: (params: { employeeId?: number; letterType?: string; reference?: string; page?: number; pageSize?: number } = {}) =>
    client.get<Paged<IssuedLetter>>('/api/hr-letters/register', { params: { page: 1, pageSize: 25, ...params } }).then((r) => r.data),

  reprint: (id: string) => downloadPdf(`/api/hr-letters/register/${id}/pdf`, 'get'),

  requests: (params: { status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<Paged<DocumentRequest>>('/api/hr-letters/requests', { params: { status: 'Pending', page: 1, pageSize: 25, ...params } }).then((r) => r.data),

  issueForRequest: (id: string, body?: { letterType?: string; language?: string }) =>
    downloadPdf(`/api/hr-letters/requests/${id}/issue`, 'post', body ?? {}),

  decline: (id: string, reason: string) =>
    client.post(`/api/hr-letters/requests/${id}/decline`, { reason }).then((r) => r.data),
};

// ── Employee self-service ─────────────────────────────────────────────────────

export interface EssLetterType { letterType: string; nameEn: string; nameAr: string }

export interface EssDocumentRequest {
  id: string;
  letterType: string;
  language: string;
  purpose: string;
  addresseeName: string;
  status: string;
  createdAtUtc: string;
  decidedAtUtc?: string;
  decisionNote: string;
  referenceNumber?: string;
  isIssued: boolean;
}

export const essDocumentsApi = {
  types: () => client.get<EssLetterType[]>('/api/ess/document-requests/types').then((r) => r.data),

  list: () => client.get<EssDocumentRequest[]>('/api/ess/document-requests').then((r) => r.data),

  create: (body: { letterType: string; language: string; purpose: string; addresseeName: string }) =>
    client.post<EssDocumentRequest>('/api/ess/document-requests', body).then((r) => r.data),

  download: (id: string) => downloadPdf(`/api/ess/document-requests/${id}/pdf`, 'get'),
};
