import { neutraliseCsvCell } from './attendanceImport';

/**
 * The CSV behind "Export CSV (for reconciliation, not a GOSI upload)" on the GOSI reconciliation screen. It is the screen's
 * own figures per employee — deducted vs recomputed — for the user's spreadsheet. It is not a GOSI file: GOSI publishes no
 * upload layout for it, so nothing here follows one. UTF-8 with a byte-order mark (Excel shows Arabic names), CRLF line
 * ends, point decimals at two places, one totals row, a footer that says what the file is not.
 */
export interface GosiReconciliationRow {
  employeeCode: string;
  employeeName: string;
  classification: string;
  coveredWage: number;
  expectedEmployee: number;
  actualEmployee: number;
  expectedEmployer: number;
  actualEmployer: number;
}

export interface GosiReconciliationCsvMeta {
  /** Ten column headings, in the order of the row below. The page passes them in English and Arabic together. */
  headers: readonly string[];
  totalsLabel: string;
  footer: string;
  /** yyyy-MM, for the file name the page picks; kept here so the file and its name cannot disagree. */
  period: string;
}

const cell = (value: string | number): string => {
  if (typeof value === 'number') return value.toFixed(2);
  const s = neutraliseCsvCell(value);
  return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
};

/** Money in halalas, so sums and differences are exact before they are printed (0.1 + 0.2 never shows as 0.30000000000000004). */
const halalas = (n: number) => Math.round(n * 100);
const money = (h: number) => h / 100;

export function buildGosiReconciliationCsv(rows: readonly GosiReconciliationRow[], meta: GosiReconciliationCsvMeta): string {
  const lines: string[] = [meta.headers.map(cell).join(',')];
  const totals = { wage: 0, ee: 0, eeA: 0, er: 0, erA: 0 };
  for (const r of rows) {
    const [wage, ee, eeA, er, erA] = [r.coveredWage, r.expectedEmployee, r.actualEmployee, r.expectedEmployer, r.actualEmployer].map(halalas);
    totals.wage += wage; totals.ee += ee; totals.eeA += eeA; totals.er += er; totals.erA += erA;
    lines.push([
      r.employeeCode, r.employeeName, r.classification,
      money(wage), money(ee), money(eeA), money(eeA - ee), money(er), money(erA), money(erA - er),
    ].map(cell).join(','));
  }
  lines.push([
    meta.totalsLabel, '', '',
    money(totals.wage), money(totals.ee), money(totals.eeA), money(totals.eeA - totals.ee), money(totals.er), money(totals.erA), money(totals.erA - totals.er),
  ].map(cell).join(','));
  lines.push(cell(meta.footer));
  return `﻿${lines.join('\r\n')}\r\n`;
}

/** File name for the download: names the period and what the file is. */
export const gosiReconciliationCsvName = (period: string) => `gosi-reconciliation-${period}.csv`;
