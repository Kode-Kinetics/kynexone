import client from './client';

// Saudi bank-instruction exports (IMPLEMENTATION-CONTRACT, 2026-09-26).
// Only format currently supported by the backend: `anb-connect-csv-v1` — the ANB
// Connect payroll-payment channel (header.csv + body.csv). It is NOT an ANB
// corporate-portal WPY upload and NOT a generic all-bank Saudi format.
//
// Nothing in this module logs, caches or persists bank data (no localStorage).

const BASE = '/api/payroll/bank-exports';

export const ANB_CONNECT_FORMAT_ID = 'anb-connect-csv-v1';
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
