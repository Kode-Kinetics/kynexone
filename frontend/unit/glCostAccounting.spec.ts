import { expect, test } from '@playwright/test';
import type { CostCenterDto } from '../src/api/organization';
import type { ReadinessCheck } from '../src/api/financeGl';
import {
  addClassRule,
  checkFixTarget,
  orderChecks,
  readinessAllClear,
  sampleHref,
  turnOnGate,
  apiErrorCode,
  canRequireCostCentre,
  cleanRule,
  draftFromRow,
  duplicateClass,
  exclusiveState,
  fixedCostCentreOptions,
  isLockedIntoApprovedRun,
  isPnlType,
  mappingsChanged,
  matchesRuleFilter,
  pickServerMessage,
  removeClassRule,
  ruleBlockedForType,
  setBaseAccount,
  setBaseClass,
  setClassRuleAccount,
  setFixedCostCentre,
  toMappingInputs,
  unusedClasses,
  type DriverDraft,
} from '../src/lib/glCostAccounting';

// Cost accounting v1, WP-F. These are the pure rules behind the GL screens; the screens themselves are
// covered by e2e/gl-cost-centres.spec.ts.

test.describe('account type versus cost centre rule', () => {
  test('only income and expense accounts can carry a cost centre (older rows say Revenue)', () => {
    expect(isPnlType('Expense')).toBe(true);
    expect(isPnlType('Income')).toBe(true);
    expect(isPnlType('Revenue')).toBe(true);
    for (const type of ['Asset', 'Liability', 'Equity', '', 'Other']) expect(isPnlType(type), type).toBe(false);
    expect(canRequireCostCentre('Expense')).toBe(true);
    expect(canRequireCostCentre('Liability')).toBe(false);
  });

  test('Required is blocked on asset, liability and equity accounts; Optional and Not allowed never are', () => {
    expect(ruleBlockedForType('Required', 'Asset')).toBe(true);
    expect(ruleBlockedForType('Required', 'Liability')).toBe(true);
    expect(ruleBlockedForType('Required', 'Equity')).toBe(true);
    expect(ruleBlockedForType('Required', 'Expense')).toBe(false);
    expect(ruleBlockedForType('Optional', 'Liability')).toBe(false);
    expect(ruleBlockedForType('NotAllowed', 'Asset')).toBe(false);
  });

  test('a row from an older server has no rule and reads as Optional, the migration default', () => {
    expect(cleanRule(undefined)).toBe('Optional');
    expect(cleanRule(null)).toBe('Optional');
    expect(cleanRule('Required')).toBe('Required');
    expect(cleanRule('NotAllowed')).toBe('NotAllowed');
    expect(cleanRule('Sometimes')).toBe('Optional');
  });

  test('the rule filter keeps everything for All rules and compares the cleaned rule otherwise', () => {
    expect(matchesRuleFilter({ costCenterRule: 'Required' }, '')).toBe(true);
    expect(matchesRuleFilter({ costCenterRule: 'Required' }, 'Required')).toBe(true);
    expect(matchesRuleFilter({ costCenterRule: 'Required' }, 'Optional')).toBe(false);
    expect(matchesRuleFilter({}, 'Optional')).toBe(true);
  });
});

test.describe('which sentence the user reads (AM-5)', () => {
  const body = { message: 'Enter a reason for this change.', messageAr: 'أدخل سببًا لهذا التغيير.' };

  test('Arabic prefers messageAr, English prefers message', () => {
    expect(pickServerMessage(body, 'ar')).toBe('أدخل سببًا لهذا التغيير.');
    expect(pickServerMessage(body, 'en')).toBe('Enter a reason for this change.');
  });

  test('either side falls back to the other, and blank or missing text is null so the caller can describe the error', () => {
    expect(pickServerMessage({ message: 'English only' }, 'ar')).toBe('English only');
    expect(pickServerMessage({ messageAr: 'عربي فقط' }, 'en')).toBe('عربي فقط');
    expect(pickServerMessage({ message: '  ', messageAr: '' }, 'ar')).toBeNull();
    expect(pickServerMessage(undefined, 'ar')).toBeNull();
    expect(pickServerMessage('<html>', 'ar')).toBeNull();
  });
});

test.describe('server refusals', () => {
  const refusal = (status: number, data: unknown) => ({ response: { status, data } });

  test('the error code comes from code or error', () => {
    expect(apiErrorCode(refusal(409, { code: 'locked_into_approved_run' }))).toBe('locked_into_approved_run');
    expect(apiErrorCode(refusal(409, { error: 'cost_accounting_blockers' }))).toBe('cost_accounting_blockers');
    expect(apiErrorCode(new Error('network'))).toBeNull();
  });

  test('M-7 is a 409 with locked_into_approved_run and nothing else', () => {
    expect(isLockedIntoApprovedRun(refusal(409, { code: 'locked_into_approved_run' }))).toBe(true);
    expect(isLockedIntoApprovedRun(refusal(409, { code: 'cost_accounting_has_costed_runs' }))).toBe(false);
    expect(isLockedIntoApprovedRun(refusal(422, { code: 'locked_into_approved_run' }))).toBe(false);
    expect(isLockedIntoApprovedRun(undefined)).toBe(false);
  });
});


// ═══════════════════════════ Screen 3: mappings ═══════════════════════════

const cc = (id: string, companyId: string | undefined, isActive = true): CostCenterDto => ({ id, companyId, code: id.toUpperCase(), name: id, isActive });

test.describe('which cost centres a fixed posting rule may use (AM-4, AM-5)', () => {
  const all = [cc('sales', 'co-a'), cc('admin', 'co-a'), cc('old', 'co-a', false), cc('pool', undefined), cc('other', 'co-b')];

  test('a company view offers that company\'s active cost centres, never a group (null-company) or another company\'s', () => {
    expect(fixedCostCentreOptions(all, 'co-a').map((c) => c.id)).toEqual(['sales', 'admin']);
    expect(fixedCostCentreOptions(all, 'co-b').map((c) => c.id)).toEqual(['other']);
  });

  test('the group view offers none: a fixed cost centre is set per company', () => {
    expect(fixedCostCentreOptions(all, null)).toEqual([]);
  });
});

const row = (over: Record<string, unknown> = {}) => ({
  driverKey: 'EARN:BASIC', mappedAccountId: 'acc-1', segmentCostCenterId: null as string | null, inherited: false,
  classRules: [] as { functionalClass: string; accountId: string; inherited: boolean }[], ...over,
});

test.describe('the mapping draft', () => {
  test('a company view starts an inherited row unset, so saving never copies the group mapping into the company', () => {
    const d = draftFromRow(row({ inherited: true, classRules: [{ functionalClass: 'SELLING', accountId: 'acc-9', inherited: true }] }), true);
    expect(d).toMatchObject({ accountId: '', fixedCostCentreId: '', cls: '' });
    expect(d.classRules).toEqual([{ cls: 'SELLING', accountId: '', inherited: true, inheritedAccountId: 'acc-9' }]);
    expect(toMappingInputs([d])).toEqual([]);
  });

  test('the group view and an own company mapping start filled', () => {
    const d = draftFromRow(row({ segmentCostCenterId: 'cc-1', classRules: [{ functionalClass: 'COGS', accountId: 'acc-2', inherited: false }] }), true);
    expect(d).toMatchObject({ accountId: 'acc-1', fixedCostCentreId: 'cc-1' });
    expect(d.classRules[0]).toMatchObject({ cls: 'COGS', accountId: 'acc-2', inherited: false });
  });

  test('payload: the base row, then one row per class, each with its own class and the fixed cost centre only on the base', () => {
    const d: DriverDraft = {
      driverKey: 'EARN:BASIC', accountId: 'acc-1', fixedCostCentreId: 'cc-1', cls: '',
      classRules: [{ cls: 'SELLING', accountId: 'acc-5', inherited: false, inheritedAccountId: null }, { cls: 'COGS', accountId: '', inherited: false, inheritedAccountId: null }],
    };
    expect(toMappingInputs([d])).toEqual([
      { driverKey: 'EARN:BASIC', accountId: 'acc-1', segmentCostCenterId: 'cc-1', functionalClass: null },
      { driverKey: 'EARN:BASIC', accountId: 'acc-5', segmentCostCenterId: null, functionalClass: 'SELLING' },
    ]);
  });

  test('a base row set to a class is sent as that class, with no fixed cost centre', () => {
    const d: DriverDraft = { driverKey: 'EARN:BASIC', accountId: 'acc-1', fixedCostCentreId: 'cc-1', cls: 'SELLING', classRules: [] };
    expect(toMappingInputs([d])).toEqual([{ driverKey: 'EARN:BASIC', accountId: 'acc-1', segmentCostCenterId: null, functionalClass: 'SELLING' }]);
  });

  test('changed means the payload differs from what was loaded', () => {
    const base = [draftFromRow(row(), false)];
    expect(mappingsChanged(base, [draftFromRow(row(), false)])).toBe(false);
    expect(mappingsChanged(base, [setFixedCostCentre(base[0], 'cc-3')])).toBe(true);
    expect(mappingsChanged(base, [setBaseAccount(base[0], '')])).toBe(true);
  });
});

test.describe('class and fixed cost centre are mutually exclusive', () => {
  const d = (over: Partial<DriverDraft> = {}): DriverDraft => ({ driverKey: 'X', accountId: 'acc-1', fixedCostCentreId: '', cls: '', classRules: [], ...over });

  test('picking one disables the other, and says which', () => {
    expect(exclusiveState(d(), true)).toEqual({ classDisabled: false, fixedDisabled: false, why: null });
    expect(exclusiveState(d({ cls: 'SELLING' }), true)).toEqual({ classDisabled: false, fixedDisabled: true, why: 'class' });
    expect(exclusiveState(d({ fixedCostCentreId: 'cc-1' }), true)).toEqual({ classDisabled: true, fixedDisabled: false, why: 'fixed' });
  });

  test('without an account nothing can be picked; in the group view the fixed cost centre is always off', () => {
    expect(exclusiveState(d({ accountId: '' }), true)).toEqual({ classDisabled: true, fixedDisabled: true, why: 'account' });
    expect(exclusiveState(d(), false)).toEqual({ classDisabled: false, fixedDisabled: true, why: 'group' });
  });

  test('the setters keep the invariant: a class clears the fixed cost centre, a fixed one clears the class, no account clears both', () => {
    expect(setBaseClass(d({ fixedCostCentreId: 'cc-1' }), 'COGS')).toMatchObject({ cls: 'COGS', fixedCostCentreId: '' });
    expect(setFixedCostCentre(d({ cls: 'COGS' }), 'cc-1')).toMatchObject({ cls: '', fixedCostCentreId: 'cc-1' });
    expect(setBaseAccount(d({ cls: 'COGS', fixedCostCentreId: 'cc-1' }), '')).toMatchObject({ accountId: '', cls: '', fixedCostCentreId: '' });
  });
});

test.describe('class rules', () => {
  const d = (over: Partial<DriverDraft> = {}): DriverDraft => ({ driverKey: 'X', accountId: 'acc-1', fixedCostCentreId: '', cls: '', classRules: [], ...over });

  test('"Add rule for a class" lists the classes not yet used, the reserved one included so it can be shown disabled', () => {
    expect(unusedClasses(d())).toEqual(['COGS', 'SELLING', 'G_AND_A', 'R_AND_D', 'OPERATIONS', 'CAPITALISABLE']);
    const used = d({ cls: 'COGS', classRules: [{ cls: 'SELLING', accountId: '', inherited: false, inheritedAccountId: null }] });
    expect(unusedClasses(used)).toEqual(['G_AND_A', 'R_AND_D', 'OPERATIONS', 'CAPITALISABLE']);
  });

  test('add, set the account of, and remove a class rule', () => {
    let draft = addClassRule(d(), 'SELLING');
    expect(draft.classRules).toEqual([{ cls: 'SELLING', accountId: '', inherited: false, inheritedAccountId: null }]);
    draft = setClassRuleAccount(draft, 'SELLING', 'acc-7');
    expect(toMappingInputs([draft]).at(-1)).toEqual({ driverKey: 'X', accountId: 'acc-7', segmentCostCenterId: null, functionalClass: 'SELLING' });
    expect(removeClassRule(draft, 'SELLING').classRules).toEqual([]);
    // Adding the same class twice is a no-op.
    expect(addClassRule(addClassRule(d(), 'COGS'), 'COGS').classRules).toHaveLength(1);
  });

  test('a base row set to a class that also has its own rule is a duplicate', () => {
    expect(duplicateClass(d({ cls: 'SELLING', classRules: [{ cls: 'SELLING', accountId: 'a', inherited: false, inheritedAccountId: null }] }))).toBe('SELLING');
    expect(duplicateClass(d({ cls: 'COGS', classRules: [{ cls: 'SELLING', accountId: 'a', inherited: false, inheritedAccountId: null }] }))).toBeNull();
    expect(duplicateClass(d())).toBeNull();
  });
});


// ═══════════════════════════ Screen 5: readiness and the switch ═══════════════════════════

const check = (key: string, severity: string, count: number): ReadinessCheck => ({ key, severity, count, sample: [] });

test.describe('readiness checks', () => {
  test('they read in the design order, with the checks the design does not name after them and unknown keys last', () => {
    const keys = ['small-cell', 'zzz', 'mapping-invisible', 'employees-unresolved', 'rounding-default', 'no-company', 'accounts-no-rule', 'invalid-reference']
      .map((k) => check(k, 'ok', 0));
    expect(orderChecks(keys).map((c) => c.key)).toEqual([
      'employees-unresolved', 'invalid-reference', 'accounts-no-rule', 'no-company', 'rounding-default', 'mapping-invisible', 'small-cell', 'zzz',
    ]);
  });

  test('all clear needs every check ok with nothing counted', () => {
    expect(readinessAllClear([check('a', 'ok', 0), check('b', 'ok', 0)])).toBe(true);
    expect(readinessAllClear([check('a', 'ok', 0), check('b', 'warning', 2)])).toBe(false);
    expect(readinessAllClear([])).toBe(false);
  });

  test('each check points at where it is fixed', () => {
    expect(checkFixTarget('employees-unresolved')).toEqual({ kind: 'link', href: '/setup?tab=departments' });
    expect(checkFixTarget('invalid-reference')).toEqual({ kind: 'link', href: '/setup?tab=costCenters' });
    expect(checkFixTarget('no-company')).toEqual({ kind: 'link', href: '/setup?tab=costCenters' });
    expect(checkFixTarget('accounts-no-rule')).toEqual({ kind: 'tab', tab: 'coa' });
    expect(checkFixTarget('mapping-invisible')).toEqual({ kind: 'tab', tab: 'coa' });
    expect(checkFixTarget('rounding-default')).toEqual({ kind: 'default' });
    expect(checkFixTarget('currency-scale')).toBeNull();
  });

  test('a sample row links to the record that holds the problem', () => {
    expect(sampleHref('employees-unresolved', { id: 412, code: 'E-0412', employeeId: 412 })).toContain('412');
    expect(sampleHref('invalid-reference', { id: 'dep-1', code: 'CS', kind: 'department' })).toBe('/setup?tab=departments&department=dep-1');
    expect(sampleHref('invalid-reference', { id: 'pos-1', code: 'AN', kind: 'position' })).toBe('/setup?tab=designations');
    expect(sampleHref('invalid-reference', { id: 77, code: 'E-0077', kind: 'employee' })).toContain('77');
    expect(sampleHref('accounts-no-rule', { id: 'a', code: '5002' })).toBeNull();
  });
});

test.describe('turning cost accounting on', () => {
  const ready = (checks: ReadinessCheck[], exposure = 0) => ({ checks, smallCellExposure: { count: exposure, threshold: 3 } });

  test('with nothing in the way only a reason is needed, and the acknowledgement is not asked for', () => {
    const g = turnOnGate(ready([check('a', 'ok', 0), check('b', 'warning', 3)]), false, '');
    expect(g).toMatchObject({ needsAck: false, canConfirm: false, hard: [] });
    expect(turnOnGate(ready([check('a', 'ok', 0)]), false, ' policy ').canConfirm).toBe(true);
  });

  test('blockers need the acknowledgement as well as the reason, and say which checks block', () => {
    const r = ready([check('employees-unresolved', 'blocker', 12), check('no-company', 'blocker', 0), check('accounts-no-rule', 'warning', 9)]);
    const g = turnOnGate(r, false, 'because');
    expect(g.needsAck).toBe(true);
    expect(g.hard.map((c) => c.key)).toEqual(['employees-unresolved']);
    expect(g.canConfirm).toBe(false);
    expect(turnOnGate(r, true, '').canConfirm).toBe(false);
    expect(turnOnGate(r, true, 'because').canConfirm).toBe(true);
  });

  test('small-cell exposure is acknowledged like a blocker (the server refuses otherwise)', () => {
    const g = turnOnGate(ready([check('a', 'ok', 0)], 2), false, 'because');
    expect(g).toMatchObject({ needsAck: true, exposure: 2, canConfirm: false });
    expect(turnOnGate(ready([check('a', 'ok', 0)], 2), true, 'because').canConfirm).toBe(true);
  });
});
