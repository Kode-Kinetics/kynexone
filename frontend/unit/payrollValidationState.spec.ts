import { test, expect } from '@playwright/test';
import { validationPanelState } from '../src/lib/payrollValidationState';
import { readFileSync } from 'node:fs';

// The Validation tab used to read "No saved validation issues" for a run that had never been validated, and for a read
// that had failed. Each situation now says what is true.

test('no run chosen asks for one', () => {
  expect(validationPanelState({ runId: '', status: undefined, loading: false, failed: false })).toBe('select_run');
});

test('a Draft run has never been validated: it says so and the Run action waits for Process', () => {
  expect(validationPanelState({ runId: 'r1', status: 'Draft', loading: false, failed: false })).toBe('not_run');
});

test('a failed read is never shown as a clean result', () => {
  expect(validationPanelState({ runId: 'r1', status: 'Processed', loading: false, failed: true })).toBe('unavailable');
});

test('a processed run with no stored findings is the only "none saved" state', () => {
  expect(validationPanelState({ runId: 'r1', status: 'Processed', loading: false, failed: false })).toBe('none_saved');
  expect(validationPanelState({ runId: 'r1', status: 'Locked', loading: false, failed: false })).toBe('none_saved');
});

test('while the read is in flight there is no empty state at all', () => {
  expect(validationPanelState({ runId: 'r1', status: 'Processed', loading: true, failed: false })).toBe('loading');
});

test('the payslips table scrolls sideways instead of clipping its columns', () => {
  const source = readFileSync('src/views/PayrollPage.tsx', 'utf8');
  const start = source.indexOf('function PayslipsTab');
  const table = source.slice(start, source.indexOf('</table>', start));
  expect(table).toContain('<div className="surface overflow-x-auto">');
  expect(table).not.toContain('<div className="surface overflow-hidden">');
});
