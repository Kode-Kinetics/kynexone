import { test, expect } from '@playwright/test';
import { attentionFor, derivePipeline, type PipelineBatch, type PipelineInput } from '../src/lib/payrollPipeline';

/**
 * "Paid / Exported" is honest about the bank: a bank file that exists is not money that landed. The payment-batch list
 * says per batch how many payments the bank has not confirmed (unconfirmedPayments) and what share it has spoken about
 * (confirmationCoverage). While any are outstanding the last stage stays Current and says so; it is Done only when the
 * batch is Paid / Reconciled or every payment is confirmed.
 */
const LOCKED: PipelineInput = {
  run: { status: 'Locked', createdAtUtc: '2026-10-01T08:00:00Z', processedAtUtc: '2026-10-02T08:00:00Z', lockedAtUtc: '2026-10-04T08:00:00Z' },
  processing: null, openErrors: 0, overrideCount: 0, approvals: [], batches: [],
  prechecks: { staleAttendanceDays: 0, employeesBlockedFromPayment: 0, companyBlockers: 0 }, reviewedAtUtc: null,
  can: { process: true, approve: true, lock: true }, currencyConfirmed: true,
};
const batch = (over: Partial<PipelineBatch> = {}): PipelineBatch => ({
  id: 'b1', payrollRunId: 'r', batchNumber: 'PB-1', paymentMethod: 'WPS', totalAmount: 1000, currency: 'SAR', status: 'Pending',
  wpsStatus: 'Generated', wpsStatusChangedAtUtc: '2026-10-06T00:00:00Z', wpsSubmissionReference: null, wpsRejectionReason: null,
  createdAtUtc: '2026-10-05T00:00:00Z', effectiveWpsStatus: 'Generated', wpsStatusLabel: 'Generated (file ready for the bank)', ...over,
});
const last = (batches: PipelineBatch[], run: Partial<PipelineInput['run']> = {}) => {
  const m = derivePipeline({ ...LOCKED, run: { ...LOCKED.run, ...run }, batches });
  return { m, stage: m.stages.find(s => s.key === 'paid')! };
};

test.describe('the last stage waits for the bank to confirm', () => {
  test('a generated bank file with payments still unconfirmed is Current, with the counts, and points at Payment Tracking', () => {
    // 5 payments, the bank has spoken about 2: 3 unconfirmed, coverage 0.4.
    const { m, stage } = last([batch({ unconfirmedPayments: { count: 3, total: 600 }, confirmationCoverage: 0.4 })]);
    expect(stage.state).toBe('current');
    expect(stage.reason).toEqual({ id: 'awaiting_bank_confirmation', confirmed: 2, total: 5 });
    expect(stage.detail).toMatchObject({ id: 'batch', number: 'PB-1' });
    expect(m.focus).toBe('paid');
    expect(m.nextReason).toEqual({ id: 'awaiting_bank_confirmation', confirmed: 2, total: 5 });
    expect(m.next).toMatchObject({ kind: 'goto', label: 'confirm_bank', tab: 'payment-tracking', disabled: false });
  });

  test('nothing confirmed yet reads 0 of the batch', () => {
    const { stage } = last([batch({ unconfirmedPayments: { count: 4, total: 800 }, confirmationCoverage: 0 })]);
    expect(stage.state).toBe('current');
    expect(stage.reason).toEqual({ id: 'awaiting_bank_confirmation', confirmed: 0, total: 4 });
  });

  test('a batch a person moved on (Submitted, Accepted) is still waiting while payments are unconfirmed', () => {
    for (const status of ['Submitted', 'Accepted']) {
      const { stage } = last([batch({ wpsStatus: status, effectiveWpsStatus: status, unconfirmedPayments: { count: 1, total: 100 }, confirmationCoverage: 0.75 })]);
      expect(stage.state, status).toBe('current');
      expect(stage.reason, status).toEqual({ id: 'awaiting_bank_confirmation', confirmed: 3, total: 4 });
    }
  });

  test('Done once every payment is confirmed, even before the batch status is moved to Paid', () => {
    const { m, stage } = last([batch({ unconfirmedPayments: { count: 0, total: 0 }, confirmationCoverage: 1 })]);
    expect(stage.state).toBe('done');
    expect(stage.reason).toBeNull();
    expect(m.next).toMatchObject({ kind: 'none', label: 'none' });
  });

  test('Done when the batch is Paid or Reconciled', () => {
    for (const status of ['Paid', 'Reconciled']) {
      const { stage } = last([batch({ wpsStatus: status, effectiveWpsStatus: status, unconfirmedPayments: { count: 2, total: 200 }, confirmationCoverage: 0.5 })]);
      expect(stage.state, status).toBe('done');
    }
  });

  test('Done when the run itself is Paid', () => {
    const { stage } = last([batch({ unconfirmedPayments: { count: 2, total: 200 }, confirmationCoverage: 0.5 })], { status: 'Paid' });
    expect(stage.state).toBe('done');
  });

  test('an older API that sends no confirmation facts keeps the old reading (a generated file completes the stage)', () => {
    const { stage } = last([batch()]);
    expect(stage.state).toBe('done');
    expect(stage.reason).toBeNull();
  });

  test('without a coverage figure the wait is still said, without inventing counts', () => {
    const { stage } = last([batch({ unconfirmedPayments: { count: 2, total: 200 } })]);
    expect(stage.state).toBe('current');
    expect(stage.reason).toEqual({ id: 'awaiting_bank_confirmation' });
  });

  test('the register-only rule still wins: no bank file, no confirmation wait', () => {
    const { stage } = last([batch({ isSaudi: true, bankFileAvailable: true, bankFileGenerated: false, unconfirmedPayments: { count: 2, total: 200 }, confirmationCoverage: 0 })]);
    expect(stage.reason).toEqual({ id: 'bank_file_not_generated' });
  });

  test('a run waiting for the bank does not read as complete in the attention list', () => {
    expect(attentionFor({ id: 'r', status: 'Locked' }, [batch({ unconfirmedPayments: { count: 3, total: 600 }, confirmationCoverage: 0.4 })])).toBeNull();
  });
});
