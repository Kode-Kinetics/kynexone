import { test, expect } from '@playwright/test';
import type { PayrollRun } from '../src/api/payroll';
import { attentionFor, derivePipeline, type PipelineBatch, type PipelineInput } from '../src/lib/payrollPipeline';

/**
 * "Paid / Exported" for a company whose bank file comes from the Saudi bank export. There the generated XML is an
 * INTERNAL payroll register (not a bank or WPS file), yet generating it sets the batch to Generated. The payment-batch
 * list now says per batch whether the bank export applies (bankFileAvailable) and whether it exists (bankFileGenerated).
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

test.describe('Paid / Exported when the bank file comes from the Saudi bank export', () => {
  test('the internal register alone leaves the stage current and says the bank file is not generated', () => {
    for (const status of ['Generated', 'Downloaded']) {
      const { m, stage } = paid([batch({ wpsStatus: status, effectiveWpsStatus: status, isSaudi: true, bankFileAvailable: true, bankFileGenerated: false })]);
      expect(stage.state, status).toBe('current');
      expect(stage.reason, status).toEqual({ id: 'bank_file_not_generated' });
      expect(m.focus).toBe('paid');
      expect(m.nextReason).toEqual({ id: 'bank_file_not_generated' });
      expect(m.next).toMatchObject({ kind: 'goto', label: 'bank', tab: 'bank-wps', disabled: false });
    }
  });

  test('a generated bank file completes the stage, and the detail says it is the bank file', () => {
    for (const registerToo of [false, true]) {
      const { m, stage } = paid([batch({ isSaudi: true, bankFileAvailable: true, bankFileGenerated: true, wpsStatus: registerToo ? 'Generated' : 'Draft' })]);
      expect(stage.state).toBe('done');
      expect(stage.reason).toBeNull();
      expect(stage.detail).toMatchObject({ id: 'batch', number: 'PB-1', file: 'bank_file' });
      expect(stage.detail).not.toMatchObject({ statusAfterFile: true });
      expect(m.focus).toBe('paid');
    }
  });

  test('the newest batch with a bank file wins over a newer register-only batch', () => {
    const { stage } = paid([
      batch({ id: 'b2', batchNumber: 'PB-2', isSaudi: true, bankFileAvailable: true, bankFileGenerated: false, wpsStatusChangedAtUtc: '2026-10-09T00:00:00Z' }),
      batch({ id: 'b1', batchNumber: 'PB-1', isSaudi: true, bankFileAvailable: true, bankFileGenerated: true }),
    ]);
    expect(stage.state).toBe('done');
    expect(stage.detail).toMatchObject({ number: 'PB-1', file: 'bank_file' });
  });

  test('a register-only batch a person moved on (submitted, accepted, settled) is not held back: the export cannot be made any more', () => {
    for (const status of ['Submitted', 'Accepted', 'Paid', 'Reconciled']) {
      const { stage } = paid([batch({ wpsStatus: status, effectiveWpsStatus: status, wpsStatusLabel: status, isSaudi: true, bankFileAvailable: true, bankFileGenerated: false })]);
      expect(stage.state, status).toBe('done');
      expect(stage.detail, status).toMatchObject({ id: 'batch', number: 'PB-1', file: 'register', label: status, statusAfterFile: true });
    }
  });

  test('a voided batch never completes the stage, bank file or not', () => {
    const { stage } = paid([batch({ wpsStatus: 'Voided', effectiveWpsStatus: 'Voided', isSaudi: true, bankFileAvailable: true, bankFileGenerated: true })]);
    expect(stage.state).toBe('current');
    expect(stage.reason).toEqual({ id: 'batch_not_exported' });
  });

  test('a locked run whose only file is the register still needs someone for the bank file', () => {
    const run = { id: 'r', status: 'Locked' } as unknown as PayrollRun;
    expect(attentionFor(run, [batch({ isSaudi: true, bankFileAvailable: true, bankFileGenerated: false })])).toBe('export');
    expect(attentionFor(run, [batch({ isSaudi: true, bankFileAvailable: true, bankFileGenerated: true })])).toBeNull();
  });
});

test.describe('Paid / Exported for a company without the Saudi bank export keeps the generated WPS file rule', () => {
  test('a company the export does not apply to: the generated WPS file completes the stage, with the API label', () => {
    const { stage } = paid([batch({ isSaudi: false, bankFileAvailable: false, bankFileGenerated: false, wpsStatusLabel: 'Generated (file ready for the bank)' })]);
    expect(stage.state).toBe('done');
    expect(stage.detail).toEqual({ id: 'batch', label: 'Generated (file ready for the bank)', number: 'PB-1' });
  });

  test('an older API that sends no flags: unchanged, the generated file counts and the label is as sent', () => {
    const { stage } = paid([batch({ isSaudi: true })]);
    expect(stage.state).toBe('done');
    expect(stage.detail).toEqual({ id: 'batch', label: 'Generated (file ready for the bank)', number: 'PB-1' });
  });

  test('a Saudi company whose export is switched off keeps the old rule, but the detail still calls the file a register', () => {
    const { stage } = paid([batch({ isSaudi: true, bankFileAvailable: false, bankFileGenerated: false })]);
    expect(stage.state).toBe('done');
    expect(stage.detail).toMatchObject({ id: 'batch', number: 'PB-1', file: 'register' });
  });
});
