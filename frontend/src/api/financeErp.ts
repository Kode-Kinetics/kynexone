import client from './client';

// ─────────────────────────────────────────────────────────────────────────────
// GL → ERP hand-off (GlJournalExportsController + FinanceGlController periods).
//
// An export is a frozen, tied-out journal file. The server refuses rather than misleads:
//   • rows an ERP already CONFIRMED are never re-sent (the new file is "Supplementary");
//   • each company's journal must balance on its own (byCompany);
//   • the person who exported cannot confirm or reject it (maker / checker).
// Outcomes are recorded per journal (one ERP document number each) or for the whole file.
// ─────────────────────────────────────────────────────────────────────────────

const BASE = '/api/finance/gl';

export type JournalExportStatus = 'Exported' | 'Confirmed' | 'Rejected' | 'Superseded' | 'PartiallyConfirmed';
export type JournalExportKind = 'Full' | 'Supplementary' | 'Replacement';
export type JournalOutcome = 'Pending' | 'Confirmed' | 'Rejected' | JournalExportStatus;

export interface JournalExportOption {
  key: string;
  label: string;
  defaultValue: string;
  description: string;
  maxLength: number;
}

export interface JournalExportFormat {
  key: string;
  displayName: string;
  extension: string;
  contentType: string;
  disclaimer: string;
  /** "Verified" = pinned to a documented spec; "Template" = validate in the ERP's test instance first. */
  status: 'Verified' | 'Template';
  options: JournalExportOption[];
}

export interface JournalExportScope {
  period?: string;
  runId?: string;
  companyId?: string | null;
  currency?: string;
  format: string;
  includeUnattributed?: boolean;
  /** Only with replacesExportId: re-send the rows of ONE confirmed export the ERP reversed. */
  includePreviouslyConfirmed?: boolean;
  replacesExportId?: string;
  options?: Record<string, string>;
}

export interface CompanyBalance {
  companyCode: string;
  companyId: string | null;
  debits: number;
  credits: number;
  balanced: boolean;
  lines: number;
}

export interface JournalExportManifest {
  exportId: string | null;
  status: JournalExportStatus | null;
  kind: JournalExportKind;
  replacesExportId: string | null;
  format: { key: string; displayName: string; disclaimer: string; extension: string; status: string };
  fileName: string;
  fileHash: string;
  currency: string;
  companyCode: string;
  entryCount: number;
  lineCount: number;
  totalDebits: number;
  totalCredits: number;
  byCompany: CompanyBalance[];
  journalCount: number;
  previouslyConfirmedExcluded: { count: number; total: number; exportIds: string[] };
  warnings: string[];
  unattributedExcluded?: { excluded: boolean; count: number | null; total: number | null; note: string | null };
  outOfPeriodEntryDateCount: number;
  downloadUrl: string | null;
}

export interface JournalExportSummary {
  id: string;
  companyId: string | null;
  companyCode: string;
  period: string;
  payrollRunId: string | null;
  formatKey: string;
  currency: string;
  fileName: string;
  fileHash: string;
  lineCount: number;
  entryCount: number;
  totalDebits: number;
  totalCredits: number;
  includeUnattributed: boolean;
  status: JournalExportStatus;
  exportedByName: string;
  exportedAtUtc: string;
  erpDocumentNumber: string | null;
  erpPostedAtUtc: string | null;
  confirmedByName: string | null;
  confirmedAtUtc: string | null;
  rejectionReason: string | null;
  notes: string | null;
  kind: JournalExportKind;
  replacesExportId: string | null;
  formatVersion: number;
  confirmedJournalCount: number;
}

export interface JournalExportJournal {
  journalRef: string;
  companyCode: string;
  composite: boolean;
  lineCount: number;
  debits: number;
  credits: number;
  accountingDate: string;
  description: string;
  status: JournalOutcome;
  erpDocumentNumber: string | null;
  erpPostedAtUtc: string | null;
  reason: string | null;
  decidedByName: string | null;
}

export interface JournalExportDetail {
  export: JournalExportSummary;
  formatter: { key: string; displayName: string; disclaimer: string } | null;
  newRowsInScopeSinceExport: { count: number; total: number; message: string | null };
  journals: JournalExportJournal[];
}

export interface JournalConfirmation {
  journalRef: string;
  erpDocumentNumber: string;
  postedAtUtc?: string | null;
}

export interface GlPeriod {
  id: string;
  companyId: string | null;
  period: string;
  status: 'Open' | 'Closed' | string;
  closedAtUtc: string | null;
  closedByName: string | null;
  closedReason: string | null;
  reopenedAtUtc: string | null;
  reopenedByName: string | null;
  reopenReason: string | null;
}

/** Query string for an export scope. Options travel as options[key]=value (ASP.NET dictionary binding). */
function scopeParams(scope: JournalExportScope): Record<string, string | boolean> {
  const p: Record<string, string | boolean> = { format: scope.format };
  if (scope.period) p.period = scope.period;
  if (scope.runId) p.runId = scope.runId;
  if (scope.companyId) p.companyId = scope.companyId;
  if (scope.currency) p.currency = scope.currency;
  if (scope.includeUnattributed) p.includeUnattributed = true;
  if (scope.includePreviouslyConfirmed) p.includePreviouslyConfirmed = true;
  if (scope.replacesExportId) p.replacesExportId = scope.replacesExportId;
  for (const [k, v] of Object.entries(scope.options ?? {})) if (v.trim()) p[`options[${k}]`] = v.trim();
  return p;
}

function filenameFromDisposition(header: unknown): string | null {
  if (typeof header !== 'string') return null;
  const star = /filename\*=UTF-8''([^;]+)/i.exec(header);
  if (star) return decodeURIComponent(star[1]);
  const plain = /filename="?([^";]+)"?/i.exec(header);
  return plain ? plain[1] : null;
}

export const financeErpApi = {
  formats: () => client.get<JournalExportFormat[]>(`${BASE}/journal-exports/formats`).then(r => r.data),

  /** Dry run: same gates as create, nothing persisted. */
  preview: (scope: JournalExportScope) =>
    client.get<JournalExportManifest>(`${BASE}/journal-exports/preview`, { params: scopeParams(scope) }).then(r => r.data),

  create: (scope: JournalExportScope) =>
    client.post<JournalExportManifest>(`${BASE}/journal-exports`, null, { params: scopeParams(scope) }).then(r => r.data),

  list: (filter: { period?: string; companyId?: string | null; status?: string } = {}) =>
    client.get<JournalExportSummary[]>(`${BASE}/journal-exports`, {
      params: {
        ...(filter.period ? { period: filter.period } : {}),
        ...(filter.companyId ? { companyId: filter.companyId } : {}),
        ...(filter.status ? { status: filter.status } : {}),
      },
    }).then(r => r.data),

  get: (id: string) => client.get<JournalExportDetail>(`${BASE}/journal-exports/${encodeURIComponent(id)}`).then(r => r.data),

  /** The frozen file, regenerated server-side and re-verified against its recorded hash. */
  download: async (id: string, fallbackName: string) => {
    const r = await client.get<Blob>(`${BASE}/journal-exports/${encodeURIComponent(id)}/download`, { responseType: 'blob' });
    return { blob: r.data, filename: filenameFromDisposition(r.headers?.['content-disposition']) ?? fallbackName };
  },

  confirm: (id: string, body: { erpDocumentNumber: string; postedAtUtc?: string | null; notes?: string | null }) =>
    client.post(`${BASE}/journal-exports/${encodeURIComponent(id)}/confirm`, body).then(r => r.data),

  reject: (id: string, reason: string) =>
    client.post(`${BASE}/journal-exports/${encodeURIComponent(id)}/reject`, { reason }).then(r => r.data),

  confirmJournals: (id: string, journals: JournalConfirmation[], notes?: string) =>
    client.post(`${BASE}/journal-exports/${encodeURIComponent(id)}/journals/confirm`, { journals, notes: notes ?? null }).then(r => r.data),

  rejectJournals: (id: string, journals: { journalRef: string; reason: string }[]) =>
    client.post(`${BASE}/journal-exports/${encodeURIComponent(id)}/journals/reject`, { journals }).then(r => r.data),

  listPeriods: (companyId?: string | null) =>
    client.get<GlPeriod[]>(`${BASE}/periods`, { params: companyId ? { companyId } : undefined }).then(r => r.data),

  closePeriod: (period: string, companyId: string | null, reason?: string) =>
    client.post<GlPeriod>(`${BASE}/periods/${encodeURIComponent(period)}/close`, { reason: reason?.trim() || null },
      { params: companyId ? { companyId } : undefined }).then(r => r.data),

  reopenPeriod: (period: string, companyId: string | null, reason: string) =>
    client.post<GlPeriod>(`${BASE}/periods/${encodeURIComponent(period)}/reopen`, { reason: reason.trim() },
      { params: companyId ? { companyId } : undefined }).then(r => r.data),
};

/** Save a downloaded blob via a temporary object URL. */
export function saveJournalFile({ blob, filename }: { blob: Blob; filename: string }): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 0);
}
