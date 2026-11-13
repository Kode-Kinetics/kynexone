import { test, expect } from '@playwright/test';
import { costingNote, erpAccountingDate, hasCostCentres, journalTitle } from '../src/lib/glJournalView';

// The GL Journal tab for a posted run: one block per journal, titled by event; the cost-accounting note comes from what was
// posted, not from the preview plan (always Off for a posted run).

test('each journal is titled by its event, remittances by their group', () => {
  expect(journalTitle({ kind: 'accrual', group: null })).toBe('Payroll accrual');
  expect(journalTitle({ kind: 'settlement', group: null })).toBe('Net pay settlement');
  expect(journalTitle({ kind: 'remittance', group: 'GOSI' })).toBe('GOSI remittance');
  expect(journalTitle({ kind: 'remittance', group: 'LOAN' })).toBe('Loan remittance');
  expect(journalTitle({ kind: 'remittance', group: 'TAX' })).toBe('Tax remittance');
  expect(journalTitle({ kind: 'void', group: null })).toBe('Void');
  expect(journalTitle({ kind: 'settlementReversal', group: null })).toBe('Net pay settlement reversal');
  expect(journalTitle({ kind: 'remittanceReversal', group: 'GOSI' })).toBe('GOSI remittance reversal');
  expect(journalTitle({ kind: 'reclass', group: null })).toBe('Disbursed funds reclassification');
  expect(journalTitle({ kind: 'other', group: null })).toBe('Payroll adjustment');
});

test('the cost-centre column is shown only where a line carries one', () => {
  expect(hasCostCentres([{ costCentreCode: null }, { costCentreCode: undefined }, { costCentreCode: ' ' }])).toBe(false);
  expect(hasCostCentres([{ costCentreCode: null }, { costCentreCode: 'CC-ENG' }])).toBe(true);
  expect(hasCostCentres([])).toBe(false);
});

test('a posted run reports cost centres from what was posted', () => {
  // Costing plan is Off for a posted run, yet the lines carry cost centres: no "off" banner.
  expect(costingNote({ isPosted: true, costing: { enabled: false, postedWithCostCentres: true } })).toBeNull();
  // Posted without any: say so (it is a fact about the posting, not about the company setting today).
  expect(costingNote({ isPosted: true, costing: { enabled: false, postedWithCostCentres: false } })).toBe('postedWithout');
  // Preview with costing off keeps the existing note; costing on has its own lock-preview component.
  expect(costingNote({ isPosted: false, costing: { enabled: false } })).toBe('off');
  expect(costingNote({ isPosted: false, costing: { enabled: true } })).toBeNull();
  // A server that predates the field: never claim cost centres were absent.
  expect(costingNote({ isPosted: true, costing: { enabled: false } })).toBeNull();
  expect(costingNote({ isPosted: true })).toBeNull();
});

// The ERP accounting date of a journal is what the journal export sends (JournalExportBuilder.DeriveAccountingDate): a payroll
// accrual is a month-end journal whatever day Lock ran; any other journal keeps the day it happened if that is inside its period.
test('the accrual carries the last day of its period as the ERP accounting date, not the day Lock ran', () => {
  expect(erpAccountingDate({ kind: 'accrual', period: '2026-11', entryDate: '2026-11-12' })).toBe('2026-11-30');
  expect(erpAccountingDate({ kind: 'accrual', period: '2026-02', entryDate: '2026-03-03' })).toBe('2026-02-28');
  expect(erpAccountingDate({ kind: 'accrual', period: '2028-02', entryDate: '2028-03-03' })).toBe('2028-02-29');
});

test('a payment or remittance keeps its own date when inside the period, else the period end', () => {
  expect(erpAccountingDate({ kind: 'settlement', period: '2026-10', entryDate: '2026-10-28' })).toBe('2026-10-28');
  expect(erpAccountingDate({ kind: 'remittance', period: '2026-10', entryDate: '2026-11-05' })).toBe('2026-10-31');
});

test('a period that cannot be read falls back to the entry date', () => {
  expect(erpAccountingDate({ kind: 'accrual', period: '', entryDate: '2026-11-12' })).toBe('2026-11-12');
});
