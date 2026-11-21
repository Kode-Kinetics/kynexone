import { test, expect } from '@playwright/test';
import { translate } from '../src/i18n/translations';
import { nextDraftAction, plainServerReason, approveDisabledReason } from '../src/lib/newHireReview';
import { approverOptionLabel } from '../src/lib/recruitmentJourney';
import { showsPositionsLeft } from '../src/lib/requisitionDecision';

// The exact sentences the API sends (OfferCompensationHandoff.ReviewMessage, ReviewGuidance, DraftMakerChecker.Message, the approve-permission refusal).
const REVIEW = 'Accepted-offer compensation needs review and confirmation by a user with employees.sensitive before activation.';
const LEGACY = 'A group-level HR user must choose an explicit company placement through department or branch and confirm the legacy offer compensation before activation.';
const MAKER = 'You made this hire (you created or changed the draft, or sent or accepted the offer behind it). A second user with employees.approve must activate this hire.';
const NOPERM = "You don't have permission to approve new hires (employees.approve).";
const KEY = /\b(employees|payroll|recruitment)\.[a-z]+/;

test.describe('server sentences are shown in plain words, never with a permission key', () => {
  for (const [name, text] of [['review', REVIEW], ['legacy placement', LEGACY], ['maker', MAKER], ['no permission', NOPERM]] as const) {
    test(`${name}: plain English, no raw key, and a real Arabic sentence`, () => {
      const key = plainServerReason(text);
      expect(key, 'known sentence must be mapped').not.toBeNull();
      const en = translate('en', key!); const ar = translate('ar', key!);
      expect(en).not.toMatch(KEY);
      expect(ar).not.toBe(en);
      expect(ar).not.toMatch(KEY);
      expect(ar).toMatch(/[؀-ۿ]/);
    });
  }
  test('an unknown sentence passes through unchanged (null)', () => {
    expect(plainServerReason('Something else the API said.')).toBeNull();
    expect(plainServerReason(null)).toBeNull();
  });
  test('the review sentence says "a person who may see sensitive employee data"', () => {
    expect(translate('en', plainServerReason(REVIEW)!)).toContain('may see sensitive employee data');
  });
  test('the row next action and the approve gate carry the plain sentence', () => {
    const row = { status: 'Submitted', canApprove: false, approveBlockedReason: REVIEW, isMine: false, activatedEmployeeId: null } as Parameters<typeof nextDraftAction>[0];
    const action = nextDraftAction(row);
    expect(action.kind).toBe('waiting');
    if (action.kind === 'waiting') expect(translate('ar', action.reason)).not.toMatch(/[A-Za-z]{4}/);
    const gate = approveDisabledReason(false, REVIEW, 0, (k, p) => translate('ar', k, p));
    expect(gate).not.toMatch(KEY);
    expect(gate).toMatch(/[؀-ۿ]/);
  });
});

test.describe('offer approver picker', () => {
  test('the role is translated with the supplied translator', () => {
    expect(approverOptionLabel({ name: 'Rana', email: 'r@x.sa', role: 'HR Manager' }, k => translate('ar', k))).toBe(`Rana · ${translate('ar', 'HR Manager')}`);
    expect(translate('ar', 'HR Manager')).toMatch(/[؀-ۿ]/);
  });
});

test.describe('login footer links', () => {
  for (const k of ['Security', 'Privacy', 'Terms']) test(`${k} has an Arabic translation`, () => expect(translate('ar', k)).not.toBe(k));
});

test.describe('positions left', () => {
  test('shown for live openings only', () => {
    for (const s of ['Open', 'InProgress', 'OnHold']) expect(showsPositionsLeft(s)).toBe(true);
    for (const s of ['Closed', 'Cancelled', 'Filled']) expect(showsPositionsLeft(s)).toBe(false);
  });
});
