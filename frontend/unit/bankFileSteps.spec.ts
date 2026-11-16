import { test, expect } from '@playwright/test';
import { deriveBankSteps, suggestedBatchReference, type BankFlowProgress } from '../src/lib/bankFileSteps';

const ready = (over: Partial<BankFlowProgress> = {}): BankFlowProgress => ({ gate: 'ready', validation: 'none', generated: false, ...over });
const base = { hasBatch: true, runLocked: true, canCreateBatch: true, isSaudi: true };
const states = (steps: ReturnType<typeof deriveBankSteps>) => steps.map(s => `${s.key}:${s.state}`);

test.describe('bank file steps', () => {
  test('no batch on an unlocked run: step 1 is blocked because the run is not Locked, the rest wait', () => {
    const steps = deriveBankSteps({ ...base, hasBatch: false, runLocked: false, progress: null });
    expect(states(steps)).toEqual(['batch:blocked', 'check:upcoming', 'create:upcoming', 'download:upcoming']);
    expect(steps[0].reason).toBe('run_not_locked');
  });

  test('no batch on a locked run: creating one is the current step; without the permission it says so', () => {
    expect(states(deriveBankSteps({ ...base, hasBatch: false, progress: null }))[0]).toBe('batch:current');
    const denied = deriveBankSteps({ ...base, hasBatch: false, canCreateBatch: false, progress: null });
    expect(denied[0]).toMatchObject({ state: 'blocked', reason: 'no_permission_create' });
  });

  test('a batch that is open and being checked for availability: step 2 is current', () => {
    const steps = deriveBankSteps({ ...base, progress: { gate: 'checking', validation: 'none', generated: false } });
    expect(states(steps)).toEqual(['batch:done', 'check:current', 'create:upcoming', 'download:upcoming']);
  });

  test.describe('a gate that is closed names its reason on step 2 and holds the later steps', () => {
    for (const [gate, reason] of [['no_permission', 'no_permission'], ['off', 'off'], ['failed', 'check_failed']] as const) {
      test(gate, () => {
        const steps = deriveBankSteps({ ...base, progress: { gate, validation: 'none', generated: false } });
        expect(steps[1]).toMatchObject({ key: 'check', state: 'blocked', reason });
        expect(steps[2].state).toBe('upcoming');
        expect(steps[3].state).toBe('upcoming');
      });
    }
  });

  test('open gate, nothing validated yet: check is current, create waits for it', () => {
    const steps = deriveBankSteps({ ...base, progress: ready() });
    expect(states(steps)).toEqual(['batch:done', 'check:current', 'create:upcoming', 'download:upcoming']);
    expect(steps[2].reason).toBe('needs_validation');
  });

  test('validation with blockers blocks step 2; a stale or running validation stays current', () => {
    expect(deriveBankSteps({ ...base, progress: ready({ validation: 'blocked' }) })[1]).toMatchObject({ state: 'blocked', reason: 'validation_blocked' });
    expect(deriveBankSteps({ ...base, progress: ready({ validation: 'stale' }) })[1].state).toBe('current');
    expect(deriveBankSteps({ ...base, progress: ready({ validation: 'running' }) })[1].state).toBe('current');
  });

  test('a passed validation completes step 2 and makes creating the file the current step', () => {
    expect(states(deriveBankSteps({ ...base, progress: ready({ validation: 'passed' }) }))).toEqual(['batch:done', 'check:done', 'create:current', 'download:upcoming']);
  });

  test('a generated file completes check and create (even after a reload with no validation) and makes download current', () => {
    expect(states(deriveBankSteps({ ...base, progress: ready({ generated: true }) }))).toEqual(['batch:done', 'check:done', 'create:done', 'download:current']);
  });

  test('a batch already submitted (or beyond) completes the last step', () => {
    for (const wpsStatus of ['Submitted', 'Accepted', 'Paid', 'Reconciled']) {
      expect(deriveBankSteps({ ...base, progress: ready({ generated: true }), wpsStatus })[3].state).toBe('done');
    }
    expect(deriveBankSteps({ ...base, progress: ready({ generated: true }), wpsStatus: 'Generated' })[3].state).toBe('current');
  });

  test('a batch that is not a Saudi batch has no bank-file flow behind steps 2 and 3', () => {
    const steps = deriveBankSteps({ ...base, isSaudi: false, progress: null });
    expect(states(steps)).toEqual(['batch:done', 'check:upcoming', 'create:upcoming', 'download:upcoming']);
  });
});

test.describe('suggested bank batch reference', () => {
  test('is the digits of the payment batch number', () => {
    expect(suggestedBatchReference('PAY-202611-220353')).toBe('202611220353');
    expect(suggestedBatchReference('PB-2026-09-0001')).toBe('2026090001');
  });
  test('keeps within the bank\'s 16 digits', () => {
    expect(suggestedBatchReference('PAY-20261122035312345678')).toHaveLength(16);
  });
  test('falls back to the run period when the number has no usable digits', () => {
    expect(suggestedBatchReference('TEST-PAYROLL-EXPORT', { year: 2026, month: 9 })).toBe('20260901');
    expect(suggestedBatchReference('PAY-000', { year: 2026, month: 11 })).toBe('20261101');
    expect(suggestedBatchReference(null)).toBe('');
  });
});
