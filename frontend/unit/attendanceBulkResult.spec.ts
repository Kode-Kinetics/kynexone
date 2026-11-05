import { expect, test } from '@playwright/test';
import { outcomeTone, processedMismatch } from '../src/lib/attendanceBulkResult';
import { errorReportScope, normaliseImportResult } from '../src/lib/attendanceImport';
import { normaliseBulkEntryResult, tenantToday } from '../src/lib/attendanceBulkEntry';

test('outcome tone: rose when nothing succeeded and something failed, amber when partial, emerald otherwise', () => {
  expect(outcomeTone(0, 3)).toBe('rose');
  expect(outcomeTone(5, 3)).toBe('amber');
  expect(outcomeTone(5, 0)).toBe('emerald');
  expect(outcomeTone(0, 0)).toBe('emerald');
});

test('deploy-skew guard: a selection whose result count differs is a mismatch', () => {
  expect(processedMismatch({ scope: 'selected', expected: 3, result: { employees: 120 } })).toEqual({ processed: 120, expected: 3 });
  expect(processedMismatch({ scope: 'selected', expected: 3, result: { employees: 3 } })).toBeNull();
});

test('deploy-skew guard: "all" scope, or a result that does not say, is not judged', () => {
  expect(processedMismatch({ scope: 'all', expected: 0, result: { employees: 120 } })).toBeNull();
  expect(processedMismatch({ scope: 'selected', expected: 3, result: null })).toBeNull();
  expect(processedMismatch({ scope: 'selected', expected: 3, result: {} })).toBeNull();
});

test('error report scope: partial when the server listed fewer errors than rows that failed', () => {
  expect(errorReportScope({ failedRows: 1200, errors: new Array(500).fill(0) as never })).toEqual({ partial: true, shown: 500, total: 1200 });
  expect(errorReportScope({ failedRows: 3, errors: new Array(3).fill(0) as never })).toEqual({ partial: false, shown: 3, total: 3 });
  expect(errorReportScope({ failedRows: 0, errors: [] })).toEqual({ partial: false, shown: 0, total: 0 });
});

test('import result is read defensively: missing errors and counts become empty / zero', () => {
  const r = normaliseImportResult({ fileName: 'a.csv', totalRows: 4 });
  expect(r.errors).toEqual([]);
  expect(r).toMatchObject({ totalRows: 4, validRows: 0, duplicateRows: 0, failedRows: 0, importedRows: 0, timeZone: '' });
  expect(normaliseImportResult(null).errors).toEqual([]);
  expect(normaliseImportResult({ errors: 'oops', validRows: 'x' })).toMatchObject({ errors: [], validRows: 0 });
});

test('mass entry result is read defensively', () => {
  expect(normaliseBulkEntryResult({ created: 4 })).toEqual({ requested: 0, created: 4, duplicates: 0, failed: 0, errors: [] });
  expect(normaliseBulkEntryResult(undefined).errors).toEqual([]);
});

test('tenant day: 00:30 in Riyadh is already the next calendar day (UTC is not)', () => {
  const now = new Date('2026-06-15T21:30:00Z'); // 00:30 on 16 June in Riyadh (UTC+3)
  expect(tenantToday('Asia/Riyadh', now)).toBe('2026-06-16');
  expect(now.toISOString().slice(0, 10)).toBe('2026-06-15');
});

test('tenant day: a zone behind UTC, and an unknown zone falls back to the UTC day', () => {
  expect(tenantToday('America/New_York', new Date('2026-06-16T02:00:00Z'))).toBe('2026-06-15');
  expect(tenantToday('Not/AZone', new Date('2026-06-16T02:00:00Z'))).toBe('2026-06-16');
  expect(tenantToday('', new Date('2026-06-16T02:00:00Z'))).toBe('2026-06-16');
});
