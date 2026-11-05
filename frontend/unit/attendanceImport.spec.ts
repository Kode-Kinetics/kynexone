import { expect, test } from '@playwright/test';
import { translate } from '../src/i18n/translations';
import {
  buildImportErrorReportCsv,
  IMPORT_ALL_KEY,
  IMPORT_PARTIAL_KEY,
  importButtonLabel,
  importGate,
  neutraliseCsvCell,
  type ImportPreview,
} from '../src/lib/attendanceImport';

const headers = { row: 'Row', column: 'Column', reason: 'Reason', original: 'Original line' };
const preview = (over: Partial<ImportPreview> = {}): ImportPreview => ({
  fileName: 'p.csv', totalRows: 10, validRows: 8, duplicateRows: 0, failedRows: 2, importedRows: 0, timeZone: 'Asia/Riyadh', errors: [], ...over,
});

test('CSV-injection: cells starting with = + - @ (or tab / CR) are neutralised with a leading quote', () => {
  for (const evil of ['=HYPERLINK("http://x")', '+1+1', '-2+3', '@SUM(A1)', '\tcmd', '\rcmd']) {
    expect(neutraliseCsvCell(evil).startsWith("'")).toBe(true);
  }
  expect(neutraliseCsvCell('EMP-001')).toBe('EMP-001');
  expect(neutraliseCsvCell('Unknown employee code')).toBe('Unknown employee code');
  expect(neutraliseCsvCell('')).toBe('');
});

test('error report: header, one line per error, real row number, quoted when needed', () => {
  const csv = buildImportErrorReportCsv([
    { rowNumber: 4, column: 'punchTimestamp', message: 'Could not read the date, "2026-13-01".', rawRow: 'EMP-1,2026-13-01,In' },
    { rowNumber: 9, message: 'Unknown employee code', rawRow: 'X,2026-06-16 09:00,In' },
  ], headers);
  const lines = csv.replace(/^\uFEFF/, '').split('\r\n').filter(Boolean);
  expect(lines[0]).toBe('Row,Column,Reason,Original line');
  expect(lines[1]).toBe('4,punchTimestamp,"Could not read the date, ""2026-13-01"".","EMP-1,2026-13-01,In"');
  expect(lines[2]).toBe('9,,Unknown employee code,"X,2026-06-16 09:00,In"');
});

test('error report: a hostile raw row cannot become a formula in Excel', () => {
  const csv = buildImportErrorReportCsv([{ rowNumber: 2, message: '=cmd|calc', rawRow: '@evil,2026-06-16 09:00,In' }], headers);
  expect(csv).toContain("'=cmd|calc");
  expect(csv).toContain('"\'@evil,2026-06-16 09:00,In"');
  // No cell of any data line starts with a formula character.
  const dataLine = csv.replace(/^\uFEFF/, '').split('\r\n')[1];
  expect(dataLine).not.toMatch(/(^|,)"?[=+\-@]/);
});

test('error report starts with a BOM so Excel reads Arabic text as UTF-8; empty list is header only', () => {
  const csv = buildImportErrorReportCsv([], headers);
  expect(csv.startsWith('\uFEFF')).toBe(true);
  expect(csv.replace('\uFEFF', '').trim()).toBe('Row,Column,Reason,Original line');
});

test('button label: "Import N valid rows (skip M)" when some rows fail', () => {
  expect(importButtonLabel(preview({ validRows: 8, failedRows: 2 }))).toEqual({ mode: 'partial', valid: 8, skipped: 2 });
  expect(translate('en', IMPORT_PARTIAL_KEY, { count: 8, skipped: 2 })).toBe('Import 8 valid rows (skip 2)');
  expect(translate('en', IMPORT_PARTIAL_KEY, { count: 1, skipped: 5 })).toBe('Import 1 valid row (skip 5)');
});

test('button label: "Import N rows" when nothing is skipped', () => {
  expect(importButtonLabel(preview({ validRows: 10, failedRows: 0 }))).toEqual({ mode: 'all', valid: 10, skipped: 0 });
  expect(translate('en', IMPORT_ALL_KEY, { count: 10 })).toBe('Import 10 rows');
  expect(translate('en', IMPORT_ALL_KEY, { count: 1 })).toBe('Import 1 row');
});

test('button label: nothing valid means nothing to import', () => {
  expect(importButtonLabel(preview({ validRows: 0, failedRows: 3 })).mode).toBe('none');
  expect(importButtonLabel(null).mode).toBe('none');
});

test('gate: Validate needs content; Import is disabled until THIS content has been validated', () => {
  const none = importGate({ content: '', validatedContent: null, preview: null, busy: false });
  expect(none).toMatchObject({ canValidate: false, canImport: false, stale: false });

  const typed = importGate({ content: 'a,b', validatedContent: null, preview: null, busy: false });
  expect(typed).toMatchObject({ canValidate: true, canImport: false });

  const validated = importGate({ content: 'a,b', validatedContent: 'a,b', preview: preview(), busy: false });
  expect(validated).toMatchObject({ canValidate: true, canImport: true, stale: false });
});

test('gate: editing the file after validation makes the result stale and blocks Import', () => {
  const g = importGate({ content: 'a,b,c', validatedContent: 'a,b', preview: preview(), busy: false });
  expect(g).toMatchObject({ canImport: false, stale: true });
});

test('gate: zero valid rows or a request in flight block Import', () => {
  expect(importGate({ content: 'x', validatedContent: 'x', preview: preview({ validRows: 0 }), busy: false }).canImport).toBe(false);
  expect(importGate({ content: 'x', validatedContent: 'x', preview: preview(), busy: true })).toMatchObject({ canImport: false, canValidate: false });
});

test('formula triggers after leading spaces are neutralised too (Excel evaluates " =cmd")', () => {
  expect(neutraliseCsvCell(' =cmd')).toBe("' =cmd");
  expect(neutraliseCsvCell('   @SUM(A1)')).toBe("'   @SUM(A1)");
  expect(neutraliseCsvCell('  +1+1')).toBe("'  +1+1");
  expect(neutraliseCsvCell(' plain text')).toBe(' plain text');
});

test('same rule as the server: a plain signed number is data, not a formula', () => {
  expect(neutraliseCsvCell('-125.50')).toBe('-125.50');
  expect(neutraliseCsvCell('+966501234567')).toBe('+966501234567');
  expect(neutraliseCsvCell(' -5')).toBe("' -5"); // the server only exempts a cell that is a number from its first character
  expect(neutraliseCsvCell('-5+3')).toBe("'-5+3");
});

test('direction-control characters are stripped from a cell, as the server does', () => {
  expect(neutraliseCsvCell('a\u202Eb\u200Fc')).toBe('abc');
  expect(neutraliseCsvCell('\u202E=cmd')).toBe("'=cmd");
});
