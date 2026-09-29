import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import {
  DRAFT_FILTERS,
  approveDisabledReason,
  draftRequestFailureReason,
  draftStatusChip,
  isClosedDraftError,
  isOpenDraft,
  nextDraftAction,
  rejectionReasonError,
} from '../src/lib/newHireReview';

const read = (relative: string) => fs.readFileSync(path.join(process.cwd(), relative), 'utf8');

const row = (overrides: Partial<Parameters<typeof nextDraftAction>[0]> = {}) => ({
  status: 'PendingHrApproval',
  isMine: false,
  canApprove: true,
  approveBlockedReason: null,
  activatedEmployeeId: null,
  ...overrides,
});

/**
 * F08 — before this screen an employee draft (every accepted offer) was reachable only by its id:
 * no list, no review, no way to activate it or to see why activation would fail. These tests pin the
 * rules the New hires screen acts on, both ways, and that the screen uses them instead of
 * re-deriving them.
 */
test.describe('New hires — review rules', () => {
  test('each row offers exactly one next action, and it is the right one', () => {
    expect(nextDraftAction(row()).kind).toBe('review');
    // The maker never gets the decision; they are told who has to act.
    const mine = nextDraftAction(row({ isMine: true, canApprove: false, approveBlockedReason: 'You created this draft, so another HR approver has to approve it.' }));
    expect(mine).toMatchObject({ kind: 'waiting', reason: expect.stringContaining('another HR approver') });
    // A draft still being prepared is sent by its maker, not approved around them.
    expect(nextDraftAction(row({ status: 'Draft', isMine: true, canApprove: false })).kind).toBe('submit');
    // ...and a checker is not offered a decision on a draft nobody has sent yet.
    expect(nextDraftAction(row({ status: 'Draft', isMine: false, canApprove: true })))
      .toMatchObject({ kind: 'waiting', reason: expect.stringContaining('not yet sent') });
    // A closed draft offers nothing that changes it.
    expect(nextDraftAction(row({ status: 'Activated', canApprove: false, activatedEmployeeId: 42 })))
      .toEqual({ kind: 'openEmployee', label: 'Open employee', employeeId: 42 });
    expect(nextDraftAction(row({ status: 'Rejected', canApprove: false })).kind).toBe('closed');
    expect(nextDraftAction(row({ status: 'Cancelled', canApprove: false })).kind).toBe('closed');
  });

  test('open and closed states match the server', () => {
    for (const s of ['Draft', 'Submitted', 'PendingHrApproval']) expect(isOpenDraft(s)).toBe(true);
    for (const s of ['Activated', 'Rejected', 'Cancelled']) expect(isOpenDraft(s)).toBe(false);
    // Submitted (what older offer acceptances wrote) reads the same as PendingHrApproval.
    expect(draftStatusChip('Submitted')).toEqual(draftStatusChip('PendingHrApproval'));
    expect(draftStatusChip('Cancelled').label).toBe('Withdrawn');
  });

  test('a rejection needs a reason, and the rule is the server rule', () => {
    expect(rejectionReasonError('')).toContain('at least 5');
    expect(rejectionReasonError('   no ')).toContain('at least 5');
    expect(rejectionReasonError('Salary above band')).toBeNull();
    expect(rejectionReasonError('x'.repeat(1001))).toContain('under');
  });

  test('approve is offered only when the server allows it and nothing would refuse activation', () => {
    expect(approveDisabledReason(true, null, 0)).toBeNull();
    expect(approveDisabledReason(true, null, 2)).toContain('Fix the 2 problems');
    expect(approveDisabledReason(true, null, 1)).toContain('Fix the problem');
    expect(approveDisabledReason(true, null, null)).toContain('Checking');
    expect(approveDisabledReason(false, 'You changed this draft, so another HR approver has to approve it.', 0))
      .toContain('You changed this draft');
  });

  test('a failure is reported as a reason, never as an empty result', () => {
    expect(draftRequestFailureReason({ isAxiosError: true })).toContain('could not be reached');
    expect(draftRequestFailureReason({ response: { status: 500 } })).toContain('HTTP 500');
    expect(draftRequestFailureReason({ response: { status: 403, data: { message: 'another HR approver has to approve or reject it.' } } }))
      .toContain('another HR approver');
    expect(draftRequestFailureReason({ response: { status: 403 } })).toBe('You do not have permission for this.');
    expect(isClosedDraftError({ response: { status: 409, data: { error: 'draft_closed' } } })).toBe(true);
    expect(isClosedDraftError({ response: { status: 409, data: { error: 'ESTABLISHMENT_BUDGET_EXCEEDED' } } })).toBe(false);
  });

  test('the default view is the exception list: what waits on a checker', () => {
    expect(DRAFT_FILTERS[0]).toMatchObject({ key: 'awaiting', countKey: 'awaitingApproval' });
  });
});

test.describe('New hires — the screen uses those rules', () => {
  const page = () => read('src/views/NewHiresPage.tsx');

  test('it acts on the shared rules instead of re-deriving them', () => {
    const source = page();
    expect(source).toContain('nextDraftAction(row)');
    expect(source).toContain('approveDisabledReason(');
    expect(source).toContain('rejectionReasonError(reason)');
    expect(source).toContain('disabled={!!deciding || !!disabledReason}');
  });

  test('a load failure renders an error with a retry, not "nothing waiting"', () => {
    const source = page();
    expect(source).toContain('setData(null);');
    expect(source).toContain('setLoadError(draftRequestFailureReason(err));');
    // The empty state renders only when there is no error.
    expect(source).toMatch(/!loading && !loadError && data && data\.items\.length === 0/);
    expect(source).toContain('role="alert"');
    expect(source).not.toMatch(/catch\s*\{\s*\}/);
  });

  test('the screen is reachable: a route gated on the draft permissions and a menu entry', () => {
    expect(read('app/(dashboard)/people/new-hires/page.tsx'))
      .toContain("permissions={['employees.write', 'employees.approve']}");
    expect(read('src/routes/navigation.ts')).toContain("path: '/people/new-hires'");
  });
});
