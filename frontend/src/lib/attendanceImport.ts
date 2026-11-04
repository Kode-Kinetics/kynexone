/**
 * Attendance CSV import: the pure rules behind Validate -> Import -> Download error report.
 *
 * Partial import is an explicit choice made AFTER a dry run (greytHR / Zoho / ZenHR behave this way; Oracle
 * rolls the whole batch back, which we do not). So Import is only offered for exactly the content that was
 * validated, and its label says how many rows go in and how many are skipped.
 */

export interface ImportRowError {
  /** The real line number in the file the person uploaded (header = 1). */
  rowNumber: number;
  column?: string | null;
  message: string;
  rawRow?: string | null;
}

/** Response of POST /api/attendance/events/import-preview and /import. */
export interface ImportPreview {
  batchId?: string;
  status?: string;
  fileName: string;
  totalRows: number;
  validRows: number;
  importedRows: number;
  duplicateRows: number;
  failedRows: number;
  /** IANA name of the zone zone-less timestamps were read in (the tenant zone). */
  timeZone: string;
  errors: ImportRowError[];
}

/** The rows the server could not read are not returned beyond its cap; this is the cap it reports against. */
export const IMPORT_MAX_ROWS = 5000;

/**
 * Spreadsheet-formula injection: a cell that starts with = + - @ (or a tab / carriage return that Excel
 * trims before evaluating) is run as a formula when the report is opened. A leading apostrophe makes the
 * spreadsheet treat it as text. The values here come from an uploaded file, so they are hostile by default.
 */
export function neutraliseCsvCell(value: string): string {
  return /^[=+\-@\t\r]/.test(value) ? `'${value}` : value;
}

function csvCell(value: string | number | null | undefined): string {
  if (typeof value === 'number') return String(value);
  const s = neutraliseCsvCell(value ?? '');
  return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

export interface ErrorReportHeaders { row: string; column: string; reason: string; original: string }

/**
 * CSV of the rows that could not be imported, with the reason for each, so HR can fix the file and upload it
 * again. UTF-8 with a BOM so Excel shows Arabic text correctly; CRLF line ends.
 */
export function buildImportErrorReportCsv(errors: readonly ImportRowError[], headers: ErrorReportHeaders): string {
  const lines = [[headers.row, headers.column, headers.reason, headers.original].map(csvCell).join(',')];
  for (const e of errors) lines.push([e.rowNumber, e.column, e.message, e.rawRow].map(csvCell).join(','));
  return `\uFEFF${lines.join('\r\n')}\r\n`;
}

/** Message keys of the two button labels (ICU plurals; the Arabic is in i18n/attendanceBulk.ts). */
export const IMPORT_ALL_KEY = 'Import {count, plural, one {# row} other {# rows}}';
export const IMPORT_PARTIAL_KEY = 'Import {count, plural, one {# valid row} other {# valid rows}} (skip {skipped})';

export type ImportLabel =
  | { mode: 'none'; valid: 0; skipped: 0 }
  | { mode: 'all'; valid: number; skipped: 0 }
  | { mode: 'partial'; valid: number; skipped: number };

/** "Import N valid rows (skip M)" when M > 0, "Import N rows" when M = 0, nothing when no row is valid. */
export function importButtonLabel(preview: Pick<ImportPreview, 'validRows' | 'failedRows'> | null): ImportLabel {
  if (!preview || preview.validRows <= 0) return { mode: 'none', valid: 0, skipped: 0 };
  return preview.failedRows > 0
    ? { mode: 'partial', valid: preview.validRows, skipped: preview.failedRows }
    : { mode: 'all', valid: preview.validRows, skipped: 0 };
}

export interface ImportGateInput {
  /** What is in the box / the chosen file right now. */
  content: string;
  /** The exact content the shown preview was computed from. */
  validatedContent: string | null;
  preview: ImportPreview | null;
  busy: boolean;
}

export interface ImportGate {
  canValidate: boolean;
  canImport: boolean;
  /** A preview exists but the content changed since: its counts no longer describe what would be imported. */
  stale: boolean;
}

export function importGate({ content, validatedContent, preview, busy }: ImportGateInput): ImportGate {
  const hasContent = content.trim().length > 0;
  const fresh = preview !== null && validatedContent !== null && validatedContent === content;
  return {
    canValidate: hasContent && !busy,
    canImport: fresh && preview.validRows > 0 && !busy,
    stale: preview !== null && !fresh,
  };
}
