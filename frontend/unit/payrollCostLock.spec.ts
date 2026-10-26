import { expect, test } from '@playwright/test';
import {
  acknowledgeUncosted,
  blockerCards,
  classifyLockError,
  groupJournal,
  hiddenEmployeeCount,
  readOnlyLockNote,
  type JournalLine,
  type LockBlocker,
  type LockGroup,
} from '../src/lib/payrollCostLock';

const line = (over: Partial<JournalLine>): JournalLine => ({
  componentCode: 'BASIC', componentName: 'Basic', glAccount: '5001 — Basic salary', glAccountName: 'Basic salary',
  entryType: 'DR', amount: 100, costCentreCode: null, costCentreName: null, ...over,
});

test.describe('groupJournal', () => {
  const entries: JournalLine[] = [
    line({ costCentreCode: 'SALES', costCentreName: 'Sales', amount: 180000 }),
    line({ costCentreCode: 'SALES', costCentreName: 'Sales', entryType: 'CR', glAccount: '5009 — Recovery', amount: 2000 }),
    line({ costCentreCode: 'OPS', costCentreName: 'Operations', amount: 50 }),
    line({ glAccount: '5003 — Overtime', amount: 12000 }),
    line({ glAccount: '2100 — Salaries payable', entryType: 'CR', amount: 412300 }),
  ];

  test('cost centre mode: missing group first, then by code, then Not costed last', () => {
    const groups = groupJournal(entries, 'cc', new Set(['5003']));
    expect(groups.map((g) => g.key)).toEqual(['missing', 'OPS', 'SALES', 'none']);
    const sales = groups.find((g) => g.key === 'SALES')!;
    expect(sales.debit).toBe(180000);
    expect(sales.credit).toBe(2000);
    expect(sales.net).toBe(178000);
    expect(groups[0].debit).toBe(12000);
    expect(groups[3].credit).toBe(412300);
  });

  test('without blockers nothing is "missing": null cost centres are simply not costed', () => {
    const groups = groupJournal(entries, 'cc', new Set());
    expect(groups.map((g) => g.key)).toEqual(['OPS', 'SALES', 'none']);
    expect(groups.find((g) => g.key === 'none')!.lines).toHaveLength(2);
  });

  test('account mode groups by account code and keeps the cost centre on each line', () => {
    const groups = groupJournal(entries, 'account', new Set());
    expect(groups.map((g) => g.key)).toEqual(['2100', '5001', '5003', '5009']);
    expect(groups.find((g) => g.key === '5001')!.lines.map((l) => l.costCentreCode)).toEqual(['SALES', 'OPS']);
  });

  test('subtotals do not carry float noise', () => {
    const groups = groupJournal([line({ amount: 0.1, costCentreCode: 'A' }), line({ amount: 0.2, costCentreCode: 'A' })], 'cc', new Set());
    expect(groups[0].debit).toBe(0.3);
  });
});

const blocker = (over: Partial<LockBlocker>): LockBlocker => ({
  employeeId: 412, employeeCode: 'E-0412', level: 'company', departmentId: 'dep-1', positionId: 'pos-1', parentDepartmentId: null,
  reason: 'cost_centre_required', accountCode: '5001', ...over,
});

test.describe('blockerCards', () => {
  const groups: LockGroup[] = [
    { error: 'cost_centre_required', accountCode: '5001', accountName: 'Basic', employeeCount: 12, employees: [{ id: 412, code: 'E-0412' }, { id: 431, code: 'E-0431' }], amount: 1200, message: 'M1 en', messageAr: 'M1 ar' },
    { error: 'cost_centre_invalid', level: 'Position', employeeCount: 1, employees: [{ id: 77, code: 'E-0077' }], amount: 90, message: 'M2 en', messageAr: 'M2 ar' },
  ];
  const blockers = [
    blocker({}),
    blocker({ employeeId: 412, accountCode: '5002' }), // the same employee on a second account: one row
    blocker({ employeeId: 431, employeeCode: 'E-0431', departmentId: null }),
    blocker({ employeeId: 77, employeeCode: 'E-0077', reason: 'cost_centre_invalid', level: 'position', departmentId: null, positionId: 'pos-9', accountCode: '5001' }),
  ];

  test('one card per server group, locale-picked message, one row per employee with fix links', () => {
    const en = blockerCards(groups, blockers, 'en');
    expect(en).toHaveLength(2);
    expect(en[0].message).toBe('M1 en');
    expect(blockerCards(groups, blockers, 'ar')[0].message).toBe('M1 ar');
    expect(en[0].rows.map((r) => r.code)).toEqual(['E-0412', 'E-0431']);
    // none-at-any-level: department and position first, then the employee
    expect(en[0].rows[0].links.map((l) => l.kind)).toEqual(['department', 'position', 'employee']);
    expect(en[0].rows[1].links.map((l) => l.kind)).toEqual(['position', 'employee']);
    expect(en[0].rows[0].reason).toBe('none');
    // an invalid cost centre at the position level: the position first
    expect(en[1].rows[0].reason).toBe('position');
    expect(en[1].rows[0].links.map((l) => l.kind)).toEqual(['position', 'employee']);
  });

  test('shows at most 10 employees and reports the rest', () => {
    const many: LockGroup = { ...groups[0], employeeCount: 25, employees: Array.from({ length: 10 }, (_, i) => ({ id: i + 1, code: `E-${i + 1}` })) };
    const cards = blockerCards([many], [], 'en');
    expect(cards[0].rows).toHaveLength(10);
    expect(hiddenEmployeeCount(many)).toBe(15);
    expect(hiddenEmployeeCount(groups[1])).toBe(0);
  });

  test('a row with no matching blocker still links to the employee', () => {
    const cards = blockerCards([groups[0]], [], 'en');
    expect(cards[0].rows[0].links.map((l) => l.kind)).toEqual(['employee']);
  });
});

test.describe('classifyLockError', () => {
  const err = (status: number, data: unknown) => ({ response: { status, data } });

  test('422 with blockers is a blocked lock, shown in the viewer language', () => {
    const e = err(422, { error: 'cost_centre_required', message: 'EN', messageAr: 'AR', blockers: [blocker({})], groups: [{ error: 'cost_centre_required', employeeCount: 1, employees: [], amount: 1, message: 'EN', messageAr: 'AR' }] });
    const r = classifyLockError(e, 'ar');
    expect(r.kind).toBe('blocked');
    if (r.kind === 'blocked') {
      expect(r.message).toBe('AR');
      expect(r.blockers).toHaveLength(1);
      expect(r.groups).toHaveLength(1);
    }
  });

  test('the rounding refusal is blocked with no employees', () => {
    const r = classifyLockError(err(422, { error: 'cost_centre_rounding_required', message: 'EN', messageAr: 'AR', accountCode: '5199' }), 'en');
    expect(r).toMatchObject({ kind: 'blocked', message: 'EN', blockers: [], groups: [] });
  });

  test('409 changed after approval means the run was sent back', () => {
    expect(classifyLockError(err(409, { error: 'cost_centre_changed_after_approval', message: 'EN', messageAr: 'AR', status: 'Processed' }), 'ar')).toMatchObject({ kind: 'sent_back', message: 'AR' });
  });

  test('409 locked into an approved run (M-7) and 403 separation of duties', () => {
    expect(classifyLockError(err(409, { error: 'locked_into_approved_run', message: 'EN' }), 'en').kind).toBe('locked_other');
    expect(classifyLockError(err(403, { error: 'separation_of_duties', message: 'EN', messageAr: 'AR' }), 'ar')).toMatchObject({ kind: 'sod', message: 'AR' });
  });

  test('anything else is other, with the server sentence when there is one', () => {
    expect(classifyLockError(err(409, { error: 'x', message: 'Already locked.' }), 'en')).toMatchObject({ kind: 'other', message: 'Already locked.' });
    expect(classifyLockError(new Error('boom'), 'en').kind).toBe('other');
  });
});

test.describe('acknowledgeUncosted', () => {
  test('only a count the approver affirmed is sent', () => {
    expect(acknowledgeUncosted({ uncostedEmployees: 6 }, false)).toBeNull();
    expect(acknowledgeUncosted({ uncostedEmployees: 6 }, true)).toBe(6);
    expect(acknowledgeUncosted({ uncostedEmployees: 0 }, true)).toBeNull();
    expect(acknowledgeUncosted(null, true)).toBeNull();
  });
});

test('readOnlyLockNote is true only when the user may not lock', () => {
  expect(readOnlyLockNote(false)).toBe(true);
  expect(readOnlyLockNote(true)).toBe(false);
});
