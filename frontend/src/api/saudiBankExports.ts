import client from './client';

// Saudi bank-instruction exports (IMPLEMENTATION-CONTRACT, 2026-09-26).
// Only format currently supported by the backend: `anb-connect-csv-v1` — the ANB
// Connect payroll-payment channel (header.csv + body.csv). It is NOT an ANB
// corporate-portal WPY upload and NOT a generic all-bank Saudi format.
//
// Nothing in this module logs, caches or persists bank data (no localStorage).

const BASE = '/api/payroll/bank-exports';

export const ANB_CONNECT_FORMAT_ID = 'anb-connect-csv-v1';
export const CUSTOM_FORMAT_PREFIX = 'custom:';
export const isCustomFormatId = (id: string | null | undefined): boolean => !!id && id.startsWith(CUSTOM_FORMAT_PREFIX);
export const SAUDI_BANK_BATCH_TYPES = ['PAYROLL', 'BENEFIT', 'BONUS', 'WELFARE'] as const;
export type SaudiBankBatchType = (typeof SAUDI_BANK_BATCH_TYPES)[number];

export interface SaudiBankExportFormat {
  id: string;
  name: string;
  bank: string;
  channel: string;
  sourceUrl: string;
  reviewedOn: string;
  acceptanceStatus: string;
}

export interface SaudiBankExportSettings {
  formatId: string;
  molEstablishmentId: string;
  mainAccountNumber: string;
  organizationName: string;
  organizationAddress1: string;
  organizationAddress2: string;
  organizationAddress3: string;
  companyName: string;
  narrative: string;
  batchType: string;
  /** ANB auto-WPS: ANB builds and uploads the signed WPS file to Mudad. Off by default. */
  autoWpsUpload: boolean;
  /** Establishment national unified number (10 digits). Required when autoWpsUpload is on. */
  nationalUnifiedNo: string;
  /** Custom-template files only: the company's salary (debit) account as a Saudi IBAN. */
  debitAccountIban: string;
}

export interface SaudiBankExportFile {
  name: string;
  sha256: string;
}

export interface SaudiBankExistingExport {
  id: string;
  formatId: string;
  files: SaudiBankExportFile[];
  batchReference: string;
  paymentDate: string;
  employeeCount: number;
  totalAmount: number;
}

export interface SaudiBankExportContext {
  companyId: string;
  companyName: string;
  countryCode: string;
  runStatus: string;
  existingExport: SaudiBankExistingExport | null;
}

export interface SaudiBankExportRequest {
  batchReference: string;
  /** YYYY-MM-DD */
  paymentDate: string;
}

export interface SaudiBankExportIssue {
  code: string;
  message: string;
  employeeId?: string | number | null;
  field?: string | null;
}

export interface SaudiBankExportValidation {
  canExport: boolean;
  errors: SaudiBankExportIssue[];
  warnings: { code: string; message: string }[];
  employeeCount: number;
  totalAmount: number;
  currency: string;
  formatId: string;
  /** Employees left out of this bank file (cash/cheque or zero net pay), by name and reason. */
  exclusions?: SaudiBankExportExclusion[];
  /** totalAmount reconciles as runNetTotal − excludedTotal. */
  excludedTotal?: number;
  runNetTotal?: number;
}

export interface SaudiBankExportExclusion {
  employeeId: number;
  employeeCode: string;
  amount: number;
  reasonCode: string;
  reason: string;
}

export interface SaudiBankExportArtifact extends SaudiBankExistingExport {
  downloadUrl: string;
}

export interface SaudiBankExportDownload {
  blob: Blob;
  filename: string;
}

type Opts = { signal?: AbortSignal };

function filenameFromDisposition(header: unknown): string | null {
  if (typeof header !== 'string') return null;
  const star = /filename\*\s*=\s*UTF-8''([^;]+)/i.exec(header);
  if (star) {
    try { return decodeURIComponent(star[1].trim().replace(/^"|"$/g, '')); } catch { /* fall through */ }
  }
  const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(header);
  return plain ? plain[1].trim() : null;
}

// ── Custom bank templates (customer-configured layout; maker-checker) ───────────────────────────
// A template decides LAYOUT only. The Saudi wage-file rules (IBAN, ID, amounts, totals, count) run on every
// row regardless. A template is never bank-certified: acceptance is unverified until the bank accepts a test file.

export type CustomBankLayout = 'delimited' | 'fixed-width';
export type CustomBankFieldKind = 'text' | 'amount' | 'date' | 'integer';

export interface CustomBankColumn {
  field: string;
  literal?: string | null;
  title: string;
  width?: number | null;
  align: 'left' | 'right';
  pad: string;
  amountDecimals: number;
  decimalSeparator: string;
  thousandsSeparator: string;
  dateFormat: string;
  upperCase: boolean;
  maxLength?: number | null;
  allowBlank: boolean;
}

export interface CustomBankTemplateDefinition {
  name: string;
  sampleReference: string;
  layout: CustomBankLayout;
  encoding: 'utf-8' | 'utf-8-bom' | 'windows-1256';
  lineEnding: 'crlf' | 'lf';
  endWithLineBreak: boolean;
  fileExtension: string;
  delimiter: string;
  quoteRule: 'none' | 'minimal' | 'always';
  headerRow: boolean;
  headerRecord: CustomBankColumn[];
  columns: CustomBankColumn[];
  trailerRecord: CustomBankColumn[];
}

export interface CustomBankFieldEntry { key: string; kind: CustomBankFieldKind; detail: boolean; envelope: boolean }

export interface CustomBankStarter {
  id: string; name: string; verifyWithBank: boolean; acceptanceStatus: string; definition: CustomBankTemplateDefinition;
}

export interface CustomBankCatalog {
  fields: CustomBankFieldEntry[];
  layouts: string[]; encodings: string[]; lineEndings: string[]; extensions: string[]; delimiters: string[];
  quoteRules: string[]; dateFormats: string[]; decimalSeparators: string[]; thousandsSeparators: string[];
  starters: CustomBankStarter[]; acceptanceStatus: string;
}

export interface CustomBankTemplateVersion {
  version: number;
  status: 'Pending' | 'Approved' | 'Rejected' | 'Superseded' | 'Retired';
  definitionHash: string;
  proposedBy: string; proposedAtUtc: string; proposeNote: string;
  decidedBy?: string | null; decidedAtUtc?: string | null; decisionNote: string;
  proposedByCurrentUser: boolean;
  definition: CustomBankTemplateDefinition;
}

export interface CustomBankTemplate {
  id: string; companyId: string; name: string; formatId: string;
  state: 'Active' | 'ActivePendingChange' | 'PendingApproval' | 'Rejected';
  activeVersion: number | null; pendingVersion: number | null; starterId?: string | null;
  acceptanceStatus: string; updatedAtUtc: string; versions: CustomBankTemplateVersion[];
}

export interface CustomBankPreviewRequest {
  batchReference: string; paymentDate: string; templateId?: string; version?: number; definition?: CustomBankTemplateDefinition;
}

export interface CustomBankPreview {
  canExport: boolean; errors: SaudiBankExportIssue[]; warnings: { code: string; message: string }[];
  recordCount: number; totalAmount: number; currency: string; templateName: string; version: number | null;
  approvedVersion: boolean; encoding: string; lineEnding: string; lines: string[]; redacted: boolean; purpose: string; acceptanceStatus: string;
}

const TPL = (companyId: string) => `${BASE}/companies/${encodeURIComponent(companyId)}/templates`;

export const customBankTemplatesApi = {
  catalog: (opts: Opts = {}) => client.get<CustomBankCatalog>(`${BASE}/templates/catalog`, { signal: opts.signal }).then((r) => r.data),
  list: (companyId: string, opts: Opts = {}) => client.get<CustomBankTemplate[]>(TPL(companyId), { signal: opts.signal }).then((r) => r.data),
  create: (companyId: string, body: { definition?: CustomBankTemplateDefinition; starterId?: string; note?: string }, opts: Opts = {}) =>
    client.post<{ template: CustomBankTemplate }>(TPL(companyId), body, { signal: opts.signal }).then((r) => r.data.template),
  update: (companyId: string, id: string, body: { definition: CustomBankTemplateDefinition; note?: string }, opts: Opts = {}) =>
    client.put<{ template: CustomBankTemplate }>(`${TPL(companyId)}/${encodeURIComponent(id)}`, body, { signal: opts.signal }).then((r) => r.data.template),
  approve: (companyId: string, id: string, body: { version: number; definitionHash: string; note?: string }, opts: Opts = {}) =>
    client.post<{ template: CustomBankTemplate }>(`${TPL(companyId)}/${encodeURIComponent(id)}/approve`, body, { signal: opts.signal }).then((r) => r.data.template),
  reject: (companyId: string, id: string, body: { version: number; definitionHash: string; note: string }, opts: Opts = {}) =>
    client.post<{ template: CustomBankTemplate }>(`${TPL(companyId)}/${encodeURIComponent(id)}/reject`, body, { signal: opts.signal }).then((r) => r.data.template),
  preview: (batchId: string, body: CustomBankPreviewRequest, opts: Opts = {}) =>
    client.post<CustomBankPreview>(`${BASE}/batches/${encodeURIComponent(batchId)}/preview`, body, { signal: opts.signal }).then((r) => r.data),
};

export const saudiBankExportsApi = {
  availability: (batchId: string, opts: Opts = {}) =>
    client.get<{ enabled: boolean }>(`${BASE}/batches/${encodeURIComponent(batchId)}/availability`, { signal: opts.signal }).then(r => r.data),
  listFormats: (opts: Opts = {}) =>
    client.get<SaudiBankExportFormat[]>(`${BASE}/formats`, { signal: opts.signal }).then((r) => r.data),

  getSettings: (companyId: string, opts: Opts = {}) =>
    client
      .get<SaudiBankExportSettings>(`${BASE}/companies/${encodeURIComponent(companyId)}/settings`, { signal: opts.signal })
      .then((r) => r.data),

  saveSettings: (companyId: string, settings: SaudiBankExportSettings, opts: Opts = {}) =>
    client
      .put<SaudiBankExportSettings>(`${BASE}/companies/${encodeURIComponent(companyId)}/settings`, settings, { signal: opts.signal })
      .then((r) => r.data),

  getContext: (batchId: string, opts: Opts = {}) =>
    client
      .get<SaudiBankExportContext>(`${BASE}/batches/${encodeURIComponent(batchId)}/context`, { signal: opts.signal })
      .then((r) => r.data),

  validate: (batchId: string, body: SaudiBankExportRequest, opts: Opts = {}) =>
    client
      .post<SaudiBankExportValidation>(`${BASE}/batches/${encodeURIComponent(batchId)}/validate`, body, { signal: opts.signal })
      .then((r) => r.data),

  generate: (batchId: string, body: SaudiBankExportRequest, opts: Opts = {}) =>
    client
      .post<SaudiBankExportArtifact>(`${BASE}/batches/${encodeURIComponent(batchId)}/generate`, body, { signal: opts.signal })
      .then((r) => r.data),

  /** Fetches the frozen zip (exact persisted CSV bytes). Filename is server-generated. */
  download: async (batchId: string, opts: Opts = {}): Promise<SaudiBankExportDownload> => {
    const r = await client.get<Blob>(`${BASE}/batches/${encodeURIComponent(batchId)}/download`, {
      responseType: 'blob',
      signal: opts.signal,
    });
    const filename = filenameFromDisposition(r.headers?.['content-disposition']) ?? 'bank-export.zip';
    return { blob: r.data, filename };
  },
};

/** Save a downloaded blob via a temporary object URL (revoked immediately after click). */
export function saveBlob({ blob, filename }: SaudiBankExportDownload): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 0);
}

export function isAbortError(err: unknown): boolean {
  const e = err as { code?: string; name?: string };
  return e?.code === 'ERR_CANCELED' || e?.name === 'CanceledError' || e?.name === 'AbortError';
}

/**
 * Pull the server message out of an axios error. Blob responses (download) carry
 * their JSON error body as a Blob, so it is read as text first. Never includes
 * request payloads (which may contain account numbers).
 */
export async function extractApiError(err: unknown, fallback: string): Promise<{ status?: number; message: string; issues?: SaudiBankExportIssue[] }> {
  const e = err as { response?: { status?: number; data?: unknown } };
  const status = e?.response?.status;
  let data = e?.response?.data as unknown;
  if (typeof Blob !== 'undefined' && data instanceof Blob) {
    try {
      const text = await data.text();
      data = text ? JSON.parse(text) : undefined;
    } catch {
      data = undefined;
    }
  }
  const d = (data ?? {}) as { message?: string; error?: string; title?: string; errors?: unknown };
  const issues = Array.isArray(d.errors) ? (d.errors as SaudiBankExportIssue[]).filter((i) => i && typeof i.message === 'string') : undefined;
  const message = d.message ?? (typeof d.error === 'string' ? d.error : undefined) ?? d.title ?? fallback;
  return { status, message, issues };
}
