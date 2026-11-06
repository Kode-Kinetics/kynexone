import { expect, test } from '@playwright/test';
import { canConfirm, confirmMissing, showsDialog, UNDO_WINDOW_MS } from '../src/lib/confirmTier';

/** The three confirmation tiers (Screen Standard section 5) as pure rules. */

test('tier 1 shows no dialog and tier 2 and 3 do; undo lasts 8 seconds', () => {
  expect(showsDialog(1)).toBe(false);
  expect(showsDialog(2)).toBe(true);
  expect(showsDialog(3)).toBe(true);
  expect(UNDO_WINDOW_MS).toBe(8000);
});

test('tier 2 confirms at once; a reason is needed only when asked for', () => {
  expect(canConfirm({ tier: 2 }, { typed: '', reason: '' })).toBe(true);
  expect(confirmMissing({ tier: 2, reasonRequired: true }, { typed: '', reason: '   ' })).toEqual(['reason']);
  expect(canConfirm({ tier: 2, reasonRequired: true }, { typed: '', reason: 'cheque bounced' })).toBe(true);
});

test('a reason can have a minimum length (the leave void reason is at least 5 characters)', () => {
  const req = { tier: 2 as const, reasonRequired: true, reasonMinLength: 5 };
  expect(confirmMissing(req, { typed: '', reason: 'oops' })).toEqual(['reason']);
  expect(confirmMissing(req, { typed: '', reason: '  oops ' })).toEqual(['reason']); // trimmed: 4 characters
  expect(canConfirm(req, { typed: '', reason: 'duplicate request' })).toBe(true);
});

test('tier 3 needs the name typed back exactly and a reason', () => {
  const req = { tier: 3 as const, typeToConfirm: 'RUN-2026-10' };
  expect(confirmMissing(req, { typed: '', reason: '' })).toEqual(['typed', 'reason']);
  expect(confirmMissing(req, { typed: 'run-2026-10', reason: 'month end' })).toEqual(['typed']); // case matters
  expect(confirmMissing(req, { typed: 'RUN-2026-1', reason: 'month end' })).toEqual(['typed']);
  expect(confirmMissing(req, { typed: 'RUN-2026-10', reason: '' })).toEqual(['reason']);
  expect(canConfirm(req, { typed: '  RUN-2026-10 ', reason: 'month end' })).toBe(true);
});

test('tier 3 without a name to type can never be confirmed (a missing name is a bug, not a bypass)', () => {
  expect(canConfirm({ tier: 3 }, { typed: '', reason: 'x' })).toBe(false);
});

test('the reason box invents no length limit: the cap is the caller\'s option, from the server\'s own limit', async () => {
  const { readFileSync } = await import('node:fs');
  const { join } = await import('node:path');
  const dialog = readFileSync(join(__dirname, '..', 'src', 'components', 'ui', 'ConfirmDialog.tsx'), 'utf8');
  expect(dialog).not.toMatch(/maxLength=\{\d+\}/);
  expect(dialog).toContain('maxLength={state?.reason?.maxLength}');
});
