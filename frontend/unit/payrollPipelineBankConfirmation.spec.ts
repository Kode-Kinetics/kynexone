import { test, expect } from '@playwright/test';
import { attentionFor, derivePipeline, type PipelineBatch, type PipelineInput } from '../src/lib/payrollPipeline';

/**
 * "Pay out" is honest about the bank: a bank file that exists is not money that landed. Once the file is recorded as submitted, the
 * payment-batch list says per batch how many payments the bank has not confirmed (unconfirmedPayments) and what share it has spoken
 * about (confirmationCoverage). While any are outstanding the last stage stays Current and says "x of y"; it is Done only when every
 * payment is confirmed or the batch / run is Paid.
 */
const LOCKED: PipelineInput = {
  run: { status: 'Locked', createdAtUtc: '2026-10-01T08:00:00Z', processedAtUtc: '2026-10-02T08:00:00Z', lockedAtUtc: '2026-10-04T08:00:00Z' },
  processing: null, openErrors: 0, overrideCount: 0, approvals: [], batches: [],
  prechecks: { staleAttendanceDays: 0, employeesBlockedFromPayment: 0, companyBlockers: 0 }, reviewedAtUtc: null,
  can: { process: true, approve: true, lock: true }, currencyConfirmed: true,
};
const batch = (over: Partial<PipelineBatch> = {}): PipelineBatch => ({
  id: 'b1', payrollRunId: 'r', batchNumber: 'PB-1', paymentMethod: 'WPS', totalAmount: 1000, currency: 'SAR', status: 'Pending',
  wpsStatus: 'Submitted', wpsStatusChangedAtUtc: '2026-10-06T00:00:00Z', wpsSubmissionReference: 'ANB-1', wpsRejectionReason: null,
  createdAtUtc: '2026-10-05T00:00:00Z', effectiveWpsStatus: 'Submitted', wpsStatusLabel: 'Submitted', isSaudi: true, bankFileAvailable: true, bankFileGenerated: true, ...over,
});
const last = (batches: PipelineBatch[], run: Partial<PipelineInput['run']> = {}) => {
  const m = derivePipeline({ ...LOCKED, run: { ...LOCKED.run, ...run }, batches });
  return { m, stage: m.stages.find(s => s.key === 'paid')! };
};

test.describe('the last stage waits for the bank to confirm', () => {
  test('a submitted file with payments still unconfirmed is Current, with the counts, and points at Payment Tracking', () => {
    // 5 payments, the bank has spoken about 2: 3 unconfirmed, coverage 0.4.
    const { m, stage } = last([batch({ unconfirmedPayments: { count: 3, total: 600 }, confirmationCoverage: 0.4 })]);
    expect(stage.state).toBe('current');
    expect(stage.reason).toEqual({ id: 'pay_out' });
    expect(m.payOut).toMatchObject({ status: 'submitted', answered: 2, total: 5 });
    expect(stage.detail).toMatchObject({ id: 'batch', number: 'PB-1' });
    expect(m.focus).toBe('paid');
    expect(m.next).toMatchObject({ kind: 'goto', label: 'confirm_bank', tab: 'payment-tracking', disabled: false });
  });

  test('nothing confirmed yet reads 0 of the batch', () => {
    const { m } = last([batch({ unconfirmedPayments: { count: 4, total: 800 }, confirmationCoverage: 0 })]);
    expect(m.payOut).toMatchObject({ status: 'submitted', answered: 0, total: 4 });
  });

  test('Submitted and Accepted both wait while payments are unconfirmed', () => {
    for (const status of ['Submitted', 'Accepted']) {
      const { m, stage } = last([batch({ wpsStatus: status, effectiveWpsStatus: status, unconfirmedPayments: { count: 1, total: 100 }, confirmationCoverage: 0.75 })]);
      expect(stage.state, status).toBe('current');
      expect(m.payOut, status).toMatchObject({ status: 'submitted', answered: 3, total: 4 });
    }
  });

  test('Done once every payment is confirmed, even before the batch status is moved to Paid', () => {
    const { m, stage } = last([batch({ unconfirmedPayments: { count: 0, total: 0 }, confirmationCoverage: 1 })]);
    expect(stage.state).toBe('done');
    expect(stage.reason).toBeNull();
    expect(m.payOut?.status).toBe('confirmed');
    expect(m.next).toMatchObject({ kind: 'none', label: 'none' });
  });

  test('Done when the batch is Paid or Reconciled, or the run itself is Paid', () => {
    for (const status of ['Paid', 'Reconciled']) {
      const { stage } = last([batch({ wpsStatus: status, effectiveWpsStatus: status, unconfirmedPayments: { count: 2, total: 200 }, confirmationCoverage: 0.5 })]);
      expect(stage.state, status).toBe('done');
    }
    const { stage } = last([batch({ unconfirmedPayments: { count: 2, total: 200 }, confirmationCoverage: 0.5 })], { status: 'Paid' });
    expect(stage.state).toBe('done');
  });

  test('a file that exists but was never recorded as submitted is not waiting for the bank: it is not sent yet', () => {
    const { m, stage } = last([batch({ wpsStatus: 'Generated', effectiveWpsStatus: 'Generated', unconfirmedPayments: { count: 3, total: 600 }, confirmationCoverage: 0.4 })]);
    expect(stage.state).toBe('current');
    expect(m.payOut?.status).toBe('created');
  });

  test('an older API that sends no confirmation facts: the wait is said without inventing counts', () => {
    const { m, stage } = last([batch()]);
    expect(stage.state).toBe('current');
    expect(m.payOut?.status).toBe('submitted');
    expect(m.payOut?.answered).toBeUndefined();
    expect(m.payOut?.total).toBeUndefined();
  });

  test('without a coverage figure the wait is still said, without inventing counts', () => {
    const { m } = last([batch({ unconfirmedPayments: { count: 2, total: 200 } })]);
    expect(m.payOut?.status).toBe('submitted');
    expect(m.payOut?.total).toBeUndefined();
  });

  test('the register-only rule still wins: no bank file, no confirmation wait', () => {
    const { m } = last([batch({ wpsStatus: 'Generated', effectiveWpsStatus: 'Generated', bankFileGenerated: false, unconfirmedPayments: { count: 2, total: 200 }, confirmationCoverage: 0 })]);
    expect(m.payOut?.status).toBe('no_file');
  });

  test('a run waiting for the bank is not in the "needs a bank file" list', () => {
    expect(attentionFor({ id: 'r', status: 'Locked' }, [batch({ unconfirmedPayments: { count: 3, total: 600 }, confirmationCoverage: 0.4 })])).toBeNull();
  });
});
