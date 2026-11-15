import { expect, test } from '@playwright/test';
import { bankChipFor, bankFixTarget, deriveBankStage, maskedTail } from '../src/lib/bankFileGuide';
import { deriveBankSteps, type BankFlowProgress } from '../src/lib/bankFileSteps';

/**
 * The Bank / WPS tab says where a batch is once: the header, the step list, the primary button and the chip all read the stage derived here.
 * Every row below is one place the screen can be.
 */

const ready = (over: Partial<BankFlowProgress> = {}): BankFlowProgress => ({ gate: 'ready', validation: 'none', generated: false, setup: 'complete', ...over });
const base = { hasBatch: true, runLocked: true, canCreateBatch: true, isSaudi: true };
const stageOf = (progress: BankFlowProgress | null, over: Partial<typeof base & { wpsStatus: string; hasBatch: boolean; runLocked: boolean; canCreateBatch: boolean }> = {}) => {
  const steps = deriveBankSteps({ ...base, ...over, progress });
  return deriveBankStage(steps, progress);
};

test.describe('the stage is the first step that is not done', () => {
  test('no batch on an unlocked run: step 1 is blocked, and says the run is not locked', () => {
    const s = stageOf(null, { hasBatch: false, runLocked: false });
    expect(s).toMatchObject({ key: 'batch', position: 1, state: 'blocked', reason: 'run_not_locked' });
  });

  test('no batch on a locked run: step 1 is current', () => {
    expect(stageOf(null, { hasBatch: false })).toMatchObject({ key: 'batch', position: 1, state: 'current' });
  });

  test('a batch that is open while the bank-file gate is still checking: step 2, still checking', () => {
    expect(stageOf({ gate: 'checking', validation: 'none', generated: false })).toMatchObject({ key: 'check', position: 2, state: 'current', reason: 'checking' });
  });

  test('a closed gate (switched off, no permission, failed) is step 2, blocked, with its own reason', () => {
    for (const [gate, reason] of [['off', 'off'], ['no_permission', 'no_permission'], ['failed', 'check_failed']] as const) {
      expect(stageOf({ gate, validation: 'none', generated: false }), gate).toMatchObject({ key: 'check', position: 2, state: 'blocked', reason });
    }
  });

  test('bank setup incomplete: the setup is the stage, position 0, and the check waits for it', () => {
    const progress = ready({ setup: 'incomplete' });
    const steps = deriveBankSteps({ ...base, progress });
    expect(deriveBankStage(steps, progress)).toMatchObject({ key: 'setup', position: 0, state: 'current' });
    expect(steps.map(s => `${s.key}:${s.state}`)).toEqual(['batch:done', 'check:upcoming', 'create:upcoming', 'download:upcoming']);
    expect(steps[1].reason).toBe('needs_setup');
  });

  test('bank setup incomplete does not hold a batch whose bank file already exists', () => {
    const progress = ready({ setup: 'incomplete', generated: true });
    expect(deriveBankStage(deriveBankSteps({ ...base, progress }), progress)).toMatchObject({ key: 'download', position: 4 });
  });

  test('a setup that is not reported (the gate, an older caller) is read as complete', () => {
    const progress: BankFlowProgress = { gate: 'ready', validation: 'none', generated: false };
    expect(stageOf(progress)).toMatchObject({ key: 'check', position: 2, state: 'current' });
  });

  test('setup does not come before step 1: with no batch open the stage is the batch', () => {
    expect(stageOf(ready({ setup: 'incomplete' }), { hasBatch: false })).toMatchObject({ key: 'batch', position: 1 });
  });

  test('check: current while nothing has been checked, blocked when the check found blockers, running counts as current', () => {
    expect(stageOf(ready())).toMatchObject({ key: 'check', state: 'current' });
    expect(stageOf(ready({ validation: 'running' }))).toMatchObject({ key: 'check', state: 'current' });
    expect(stageOf(ready({ validation: 'blocked' }))).toMatchObject({ key: 'check', state: 'blocked', reason: 'validation_blocked' });
  });

  test('a check that went stale (inputs changed) puts the stage back on step 2', () => {
    expect(stageOf(ready({ validation: 'stale' }))).toMatchObject({ key: 'check', position: 2 });
  });

  test('a passed check: step 3, then once the file exists step 4', () => {
    expect(stageOf(ready({ validation: 'passed' }))).toMatchObject({ key: 'create', position: 3, state: 'current' });
    expect(stageOf(ready({ validation: 'passed', generated: true }))).toMatchObject({ key: 'download', position: 4, state: 'current' });
  });

  test('a file made earlier (no check in this session) is step 4 as well', () => {
    expect(stageOf(ready({ generated: true }))).toMatchObject({ key: 'download', position: 4 });
  });

  test('recorded as submitted: nothing left, finished', () => {
    for (const wpsStatus of ['Submitted', 'Accepted', 'Paid', 'Reconciled']) {
      expect(stageOf(ready({ generated: true }), { wpsStatus }), wpsStatus).toMatchObject({ key: 'finished', state: 'finished' });
    }
  });
});

test.describe('the chip follows the stage and nothing else', () => {
  const chip = (progress: BankFlowProgress | null, over = {}) => bankChipFor(stageOf(progress, over));
  test('waiting for the bank file through steps 1 to 3, ready at step 4, submitted at the end', () => {
    expect(chip(null, { hasBatch: false })).toBe('waiting');
    expect(chip(ready())).toBe('waiting');
    expect(chip(ready({ validation: 'passed' }))).toBe('waiting');
    expect(chip(ready({ generated: true }))).toBe('ready');
    expect(chip(ready({ generated: true }), { wpsStatus: 'Submitted' })).toBe('submitted');
  });
  test('setup needed, and a blocked step needs attention', () => {
    expect(chip(ready({ setup: 'incomplete' }))).toBe('setup');
    expect(chip(ready({ validation: 'blocked' }))).toBe('attention');
    expect(chip(null, { hasBatch: false, runLocked: false })).toBe('attention');
  });
});

test.describe('a blocker says where it is cured', () => {
  test('a field of the bank setup opens the setup on that field', () => {
    expect(bankFixTarget({ code: 'main_account_invalid', field: 'mainAccountNumber' })).toEqual({ kind: 'setup', field: 'mainAccountNumber' });
    expect(bankFixTarget({ code: 'field_required', field: 'organizationAddress2' })).toEqual({ kind: 'setup', field: 'organizationAddress2' });
  });
  test('the batch number and the date are step 2 inputs', () => {
    expect(bankFixTarget({ code: 'batch_reference_invalid', field: 'batchReference' })).toEqual({ kind: 'input', field: 'batchReference' });
    expect(bankFixTarget({ field: 'paymentDate' })).toEqual({ kind: 'input', field: 'paymentDate' });
  });
  test('a missing or invalid BIC, and a missing address, open the beneficiary editor for THAT employee', () => {
    expect(bankFixTarget({ code: 'bic_missing', employeeId: 203 })).toEqual({ kind: 'beneficiary', employeeId: 203 });
    expect(bankFixTarget({ code: 'bic_invalid', employeeId: '204' })).toEqual({ kind: 'beneficiary', employeeId: 204 });
    expect(bankFixTarget({ code: 'employee_address_missing', employeeId: 205, field: null })).toEqual({ kind: 'beneficiary', employeeId: 205 });
  });
  test('an account number or ID is changed in People only', () => {
    expect(bankFixTarget({ code: 'account_missing', employeeId: 206 })).toEqual({ kind: 'people', employeeId: 206 });
    expect(bankFixTarget({ code: 'employee_id_invalid', employeeId: 207 })).toEqual({ kind: 'people', employeeId: 207 });
    expect(bankFixTarget({ code: 'duplicate_account', employeeId: 208 })).toEqual({ kind: 'people', employeeId: 208 });
  });
  test('anything else has no Fix button rather than a button that goes nowhere', () => {
    expect(bankFixTarget({ code: 'net_not_positive', employeeId: 209 })).toEqual({ kind: 'none' });
    expect(bankFixTarget({ code: 'total_mismatch' })).toEqual({ kind: 'none' });
    expect(bankFixTarget({ code: 'bic_missing' })).toEqual({ kind: 'none' });
  });
});

test('an account is shown as its last four characters and never more', () => {
  expect(maskedTail('0108061198800026')).toBe('••0026');
  expect(maskedTail('SA03 8000 0000 6080 1016 7519')).toBe('••7519');
  expect(maskedTail('12')).toBe('');
  expect(maskedTail(null)).toBe('');
});
