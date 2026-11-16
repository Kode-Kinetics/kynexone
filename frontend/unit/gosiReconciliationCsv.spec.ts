import { test, expect } from '@playwright/test';
import { buildGosiReconciliationCsv, type GosiReconciliationRow } from '../src/lib/gosiReconciliationCsv';

// Launch wave L4.3: a reconciliation CSV of what is on the GOSI screen. UTF-8 with a BOM so Excel shows Arabic names,
// point decimals at 2 dp, a header in English and Arabic, one totals row, and a footer saying it is not a GOSI upload.

const rows: GosiReconciliationRow[] = [
  { employeeCode: 'E-001', employeeName: 'Ahmad Al-Qahtani', classification: 'Saudi', coveredWage: 12500.5, expectedEmployee: 1125.05, actualEmployee: 1125.05, expectedEmployer: 1500.06, actualEmployer: 1500.06 },
  { employeeCode: 'E-002', employeeName: 'سارة العتيبي', classification: 'Saudi', coveredWage: 8000, expectedEmployee: 720, actualEmployee: 720.01, expectedEmployer: 960, actualEmployer: 960 },
  { employeeCode: 'E-003', employeeName: '=cmd|"/c calc"!A1', classification: 'Expat', coveredWage: 6000, expectedEmployee: 0, actualEmployee: 0, expectedEmployer: 120, actualEmployer: 120 },
];
const headers = ['Employee code', 'Employee', 'Classification', 'Covered wage', 'Expected employee', 'Actual employee', 'Employee variance', 'Expected employer', 'Actual employer', 'Employer variance'];
const meta = { headers, totalsLabel: 'Total', footer: 'Reconciliation report, not a GOSI upload.', period: '2026-11' };

test('starts with a byte-order mark and the header row, ends with the footer', () => {
  const csv = buildGosiReconciliationCsv(rows, meta);
  expect(csv.startsWith('﻿')).toBe(true);
  const lines = csv.slice(1).split('\r\n');
  expect(lines[0]).toBe(headers.join(','));
  expect(lines.at(-2)).toBe('"Reconciliation report, not a GOSI upload."');
  expect(lines.at(-1)).toBe('');
});

test('amounts are point decimals at two places, and variance is actual minus expected', () => {
  const lines = buildGosiReconciliationCsv(rows, meta).slice(1).split('\r\n');
  expect(lines[1]).toBe('E-001,Ahmad Al-Qahtani,Saudi,12500.50,1125.05,1125.05,0.00,1500.06,1500.06,0.00');
  expect(lines[2]).toBe('E-002,سارة العتيبي,Saudi,8000.00,720.00,720.01,0.01,960.00,960.00,0.00');
});

test('one totals row sums the columns', () => {
  const lines = buildGosiReconciliationCsv(rows, meta).slice(1).split('\r\n');
  expect(lines[4]).toBe('Total,,,26500.50,1845.05,1845.06,0.01,2580.06,2580.06,0.00');
});

test('a name that starts like a formula is neutralised', () => {
  const csv = buildGosiReconciliationCsv(rows, meta);
  expect(csv).not.toMatch(/,=cmd/);
  expect(csv).toContain(`"'=cmd|""/c calc""!A1"`);
});

test('no rows still gives a header, a zero totals row and the footer', () => {
  const lines = buildGosiReconciliationCsv([], meta).slice(1).split('\r\n');
  expect(lines[0]).toBe(headers.join(','));
  expect(lines[1]).toBe('Total,,,0.00,0.00,0.00,0.00,0.00,0.00,0.00');
});
