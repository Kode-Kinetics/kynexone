import { test, expect } from '@playwright/test';
import type { PayrollRun } from '../src/api/payroll';
import { attentionFor, derivePipeline, type PipelineBatch, type PipelineInput } from '../src/lib/payrollPipeline';

/**
 * "Pay out" (formerly "Paid / Exported") for a company whose bank file comes from the Saudi bank export. There the generated XML is an
 * INTERNAL payroll register (not a bank or WPS file), yet generating it sets the batch to Generated. The payment-batch list says per batch
 * whether the bank export applies (bankFileAvailable) and whether it exists (bankFileGenerated). The stage reads lib/payOut.ts, the same
 * object the run bar and the Bank / WPS tab read; it is Done only once the bank confirmed every payment or the money is settled.
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
const paid = (batches: PipelineBatch[]) => {
  const m = derivePipeline({ ...LOCKED, batches });
  return { m, stage: m.stages.find(s => s.key === 'paid')! };
};

test.describe('Pay out when the bank file comes from the Saudi bank export', () => {
  test('the internal register alone leaves the stage current: "No bank file yet", and the next step is Bank / WPS', () => {
    for (const status of ['Generated', 'Downloaded']) {
      const { m, stage } = paid([batch({ wpsStatus: status, effectiveWpsStatus: status, isSaudi: true, bankFileAvailable: true, bankFileGenerated: false })]);
      expect(stage.state, status).toBe('current');
      expect(stage.reason, status).toEqual({ id: 'pay_out' });
      expect(m.payOut?.status, status).toBe('no_file');
      expect(stage.detail, status).toBeNull();
      expect(m.focus).toBe('paid');
      expect(m.next).toMatchObject({ kind: 'goto', label: 'bank', tab: 'bank-wps', disabled: false });
    }
  });

  test('a generated bank file not yet with the bank is not Done: "File created", and the detail says it is the bank file', () => {
    for (const registerToo of [false, true]) {
      const { m, stage } = paid([batch({ isSaudi: true, bankFileAvailable: true, bankFileGenerated: true, wpsStatus: registerToo ? 'Generated' : 'Draft', effectiveWpsStatus: registerToo ? 'Generated' : 'Draft' })]);
      expect(stage.state).toBe('current');
      expect(m.payOut?.status).toBe('created');
      expect(stage.detail).toMatchObject({ id: 'batch', number: 'PB-1', file: 'bank_file' });
      expect(m.next).toMatchObject({ kind: 'goto', tab: 'bank-wps' });
    }
  });

  test('with several batches, the one that has gone furthest speaks for the run', () => {
    const { m, stage } = paid([
      batch({ id: 'b2', batchNumber: 'PB-2', isSaudi: true, bankFileAvailable: true, bankFileGenerated: false, wpsStatusChangedAtUtc: '2026-10-09T00:00:00Z' }),
      batch({ id: 'b1', batchNumber: 'PB-1', isSaudi: true, bankFileAvailable: true, bankFileGenerated: true }),
    ]);
    expect(m.payOut?.batchId).toBe('b1');
    expect(stage.detail).toMatchObject({ number: 'PB-1', file: 'bank_file' });
  });

  test('a batch a person recorded as submitted waits for the bank; settled money is Done', () => {
    for (const status of ['Submitted', 'Accepted']) {
      const { m, stage } = paid([batch({ wpsStatus: status, effectiveWpsStatus: status, wpsStatusLabel: status, isSaudi: true, bankFileAvailable: true, bankFileGenerated: true })]);
      expect(stage.state, status).toBe('current');
      expect(m.payOut?.status, status).toBe('submitted');
      expect(m.next, status).toMatchObject({ kind: 'goto', tab: 'payment-tracking' });
    }
    for (const status of ['Paid', 'Reconciled']) {
      const { m, stage } = paid([batch({ wpsStatus: status, effectiveWpsStatus: status, wpsStatusLabel: status, isSaudi: true, bankFileAvailable: true, bankFileGenerated: true })]);
      expect(stage.state, status).toBe('done');
      expect(m.payOut?.status, status).toBe('settled');
    }
  });

  test('a voided batch is never the one spoken about', () => {
    const { m, stage } = paid([batch({ wpsStatus: 'Voided', effectiveWpsStatus: 'Voided', isSaudi: true, bankFileAvailable: true, bankFileGenerated: true })]);
    expect(stage.state).toBe('current');
    expect(m.payOut?.status).toBe('no_batch');
  });

  test('a locked run whose only file is the register still needs someone for the bank file', () => {
    const run = { id: 'r', status: 'Locked' } as unknown as PayrollRun;
    expect(attentionFor(run, [batch({ isSaudi: true, bankFileAvailable: true, bankFileGenerated: false })])).toBe('export');
    expect(attentionFor(run, [batch({ isSaudi: true, bankFileAvailable: true, bankFileGenerated: true })])).toBeNull();
  });
});

test.describe('Pay out for a company without the Saudi bank export keeps the generated WPS file rule', () => {
  test('the generated WPS file is the file: "File created", with the API label', () => {
    const { m, stage } = paid([batch({ isSaudi: false, bankFileAvailable: false, bankFileGenerated: false })]);
    expect(stage.state).toBe('current');
    expect(m.payOut?.status).toBe('created');
    expect(stage.detail).toEqual({ id: 'batch', label: 'Generated (file ready for the bank)', number: 'PB-1' });
  });

  test('an older API that sends no flags, for a batch that is not Saudi: the generated file counts', () => {
    const { m } = paid([batch()]);
    expect(m.payOut?.status).toBe('created');
  });
});
