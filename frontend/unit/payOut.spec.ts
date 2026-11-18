import { test, expect } from '@playwright/test';
import {
  bankResponseBuckets, confirmedResponsePurpose, derivePayOut, payOutLabel, payOutNeedsFile, payOutStageState, payOutTone, splitFromRecords,
  type PayOut, type PayOutInput, type PayOutNextKind, type PayOutStage, type PayOutStatus,
} from '../src/lib/payOut';
import { attentionFor, derivePipeline, payOutForRun, type PipelineBatch, type PipelineInput } from '../src/lib/payrollPipeline';
import { addDays, isSelectableCreditDate, isWeekend, monthGrid, nextWorkingDay, todayInRiyadh } from '../src/lib/creditDate';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { bankFileGuide } from '../src/i18n/bankFileGuide';
import { payrollUx } from '../src/i18n/payrollUx';

/**
 * Pay out is one derived object (lib/payOut.ts). For every state of the Bank / WPS flow this proves that the run bar's chip, the run
 * picker's option, the pipeline's Pay out stage, the step strip, the open panel and the primary button all read the same thing.
 */

const RUN = { id: 'r1', status: 'Locked' };
const ALL = { create: true, wps: true, settle: true, setup: true };
const batch = (over: Partial<PipelineBatch> = {}): PipelineBatch => ({
  id: 'b1', payrollRunId: 'r1', batchNumber: 'PAY-1', paymentMethod: 'WPS', totalAmount: 1000, currency: 'SAR', status: 'Created',
  wpsStatus: 'Draft', effectiveWpsStatus: 'Draft', wpsStatusChangedAtUtc: null, wpsSubmissionReference: null, wpsRejectionReason: null,
  createdAtUtc: '2026-10-05T00:00:00Z', isSaudi: true, bankFileAvailable: true, bankFileGenerated: false, allowedNextStatuses: [], ...over,
});
const facts = (over: Partial<NonNullable<PayOutInput['exportFacts']>> = {}) => ({ gate: 'ready' as const, setup: 'complete' as const, validation: 'none' as const, fileExists: false, ...over });
const records = (paid: number, returned: number, pending: number) => [
  ...Array.from({ length: paid }, () => ({ status: 'Paid', amount: 100 })),
  ...Array.from({ length: returned }, () => ({ status: 'Returned', amount: 100 })),
  ...Array.from({ length: pending }, () => ({ status: 'Pending', amount: 100 })),
  { status: 'Cancelled', amount: 999 },
];
/** What the batch list says when the bank answered every payment and none came back. */
const ALL_PAID = { unconfirmedPayments: { count: 0, total: 0 }, confirmationCoverage: 1, unresolvedReturns: { count: 0, total: 0 } };
const sub = (status: string, over: Partial<PipelineBatch> = {}) => batch({ status: 'FileGenerated', wpsStatus: status, effectiveWpsStatus: status, bankFileGenerated: true, ...over });

interface Row {
  name: string;
  input: Partial<PayOutInput>;
  status: PayOutStatus;
  stage: PayOutStage;
  next: PayOutNextKind | null;
  disabled?: boolean;
  label: string;
}

/** One row per state of the spec's state machine (section c2). */
const ROWS: Row[] = [
  { name: '1a run not locked', input: { run: { status: 'Approved' }, batches: [] }, status: 'no_batch', stage: 'batch', next: 'create_batch', disabled: true, label: 'No payment batch yet' },
  { name: '1b locked, no batch', input: { batches: [] }, status: 'no_batch', stage: 'batch', next: 'create_batch', disabled: false, label: 'No payment batch yet' },
  { name: '1c two batches, none open', input: { batches: [batch(), batch({ id: 'b2' })] }, status: 'no_file', stage: 'batch', next: 'open_batch', disabled: false, label: 'No bank file yet' },
  { name: '1d no permission to create', input: { batches: [], can: { ...ALL, create: false } }, status: 'no_batch', stage: 'batch', next: 'create_batch', disabled: true, label: 'No payment batch yet' },
  { name: 'G setup incomplete', input: { exportFacts: facts({ setup: 'incomplete' }) }, status: 'setup', stage: 'setup', next: 'complete_setup', disabled: false, label: 'Bank setup needed' },
  { name: 'G setup, no setup permission', input: { exportFacts: facts({ setup: 'incomplete' }), can: { ...ALL, setup: false } }, status: 'setup', stage: 'setup', next: 'complete_setup', disabled: true, label: 'Bank setup needed' },
  { name: 'X export switched off', input: { exportFacts: facts({ gate: 'off' }) }, status: 'no_file', stage: 'check', next: null, label: 'No bank file yet' },
  { name: '2a check current', input: { exportFacts: facts() }, status: 'no_file', stage: 'check', next: 'check', disabled: false, label: 'No bank file yet' },
  { name: '2a without the panel (other tabs)', input: {}, status: 'no_file', stage: 'check', next: 'check', disabled: false, label: 'No bank file yet' },
  { name: '2b check blocked', input: { exportFacts: facts({ validation: 'blocked' }) }, status: 'attention', stage: 'check', next: 'check_again', disabled: false, label: 'Needs a fix' },
  { name: '2c check stale', input: { exportFacts: facts({ validation: 'stale' }) }, status: 'no_file', stage: 'check', next: 'check_again', disabled: false, label: 'No bank file yet' },
  { name: '3a create current', input: { exportFacts: facts({ validation: 'passed' }) }, status: 'no_file', stage: 'create', next: 'create_file', disabled: false, label: 'No bank file yet' },
  { name: '3b create refused', input: { exportFacts: facts({ validation: 'passed', createRefused: true }) }, status: 'attention', stage: 'create', next: 'create_file', disabled: false, label: 'Needs a fix' },
  // Finance rule: a file that does not tie to the approved run is never created (the check itself may pass).
  { name: '3c file does not tie to the run', input: { exportFacts: facts({ validation: 'passed', tieDifference: -100 }) }, status: 'attention', stage: 'create', next: 'create_file', disabled: true, label: 'Needs a fix' },
  { name: '2a a difference before the check: the check comes first', input: { exportFacts: facts({ tieDifference: -100 }) }, status: 'no_file', stage: 'check', next: 'check', disabled: false, label: 'No bank file yet' },
  { name: '3a a difference under a cent is no difference', input: { exportFacts: facts({ validation: 'passed', tieDifference: 0.004 }) }, status: 'no_file', stage: 'create', next: 'create_file', disabled: false, label: 'No bank file yet' },
  { name: '4a file created', input: { exportFacts: facts({ fileExists: true }), batches: [batch({ bankFileGenerated: true, wpsStatus: 'Generated', effectiveWpsStatus: 'Generated', allowedNextStatuses: ['Downloaded', 'Submitted'] })] }, status: 'created', stage: 'send', next: 'download', disabled: false, label: 'File created — not sent' },
  { name: '4b downloaded (this session)', input: { downloaded: true, batches: [batch({ bankFileGenerated: true, wpsStatus: 'Generated', effectiveWpsStatus: 'Generated', allowedNextStatuses: ['Downloaded', 'Submitted'] })] }, status: 'downloaded', stage: 'send', next: 'record_submitted', disabled: false, label: 'Downloaded — not yet submitted' },
  { name: '4b downloaded, no WPS permission', input: { downloaded: true, can: { ...ALL, wps: false }, batches: [batch({ bankFileGenerated: true, wpsStatus: 'Generated', effectiveWpsStatus: 'Generated', allowedNextStatuses: ['Submitted'] })] }, status: 'downloaded', stage: 'send', next: 'record_submitted', disabled: true, label: 'Downloaded — not yet submitted' },
  { name: '4b Downloaded status, Submitted not allowed', input: { batches: [batch({ bankFileGenerated: true, wpsStatus: 'Downloaded', effectiveWpsStatus: 'Downloaded', allowedNextStatuses: [] })] }, status: 'downloaded', stage: 'send', next: 'record_submitted', disabled: true, label: 'Downloaded — not yet submitted' },
  { name: '5a waiting (records)', input: { batches: [sub('Submitted')], records: records(115, 0, 6) }, status: 'submitted', stage: 'response', next: 'record_response', disabled: false, label: 'Submitted — waiting for the bank (6 of 121 waiting)' },
  { name: '5a waiting (batch list only)', input: { batches: [sub('Submitted', { unconfirmedPayments: { count: 3, total: 300 }, confirmationCoverage: 0.4 })] }, status: 'submitted', stage: 'response', next: 'record_response', disabled: false, label: 'Submitted — waiting for the bank (3 of 5 waiting)' },
  { name: '5a waiting, an older API', input: { batches: [sub('Submitted')] }, status: 'submitted', stage: 'response', next: 'record_response', disabled: false, label: 'Submitted — waiting for the bank' },
  { name: '5b rejected', input: { batches: [sub('Rejected', { allowedNextStatuses: ['Generated'] })] }, status: 'rejected', stage: 'response', next: 'prepare_corrected', disabled: false, label: 'Rejected by the bank' },
  { name: '5b rejected, may not redo', input: { batches: [sub('Rejected')] }, status: 'rejected', stage: 'response', next: 'prepare_corrected', disabled: true, label: 'Rejected by the bank' },
  { name: '5c all confirmed (records)', input: { batches: [sub('Accepted')], records: records(121, 0, 0) }, status: 'confirmed', stage: 'finished', next: 'settle', disabled: false, label: 'Bank confirmed 121 of 121' },
  { name: '5c all confirmed, no settle permission', input: { batches: [sub('Accepted', { ...ALL_PAID })], can: { ...ALL, settle: false } }, status: 'confirmed', stage: 'finished', next: null, label: 'Bank confirmed every payment' },
  { name: '5c all answered, some returned', input: { batches: [sub('Accepted')], records: records(118, 3, 0) }, status: 'confirmed', stage: 'response', next: 'settle', disabled: false, label: '3 returned: needs attention' },
  // Without the payment records (run bar, run picker, pipeline): the batch list's unresolved returns decide. Never "confirmed" over a return.
  { name: '5c accepted, batch list only, 2 returned', input: { batches: [sub('Accepted', { ...ALL_PAID, unresolvedReturns: { count: 2, total: 5000 } })] }, status: 'confirmed', stage: 'response', next: 'settle', disabled: false, label: '2 returned: needs attention' },
  { name: '5c submitted, batch list only, 1 returned', input: { batches: [sub('Submitted', { ...ALL_PAID, unresolvedReturns: { count: 1, total: 900 } })] }, status: 'confirmed', stage: 'response', next: 'settle', disabled: false, label: '1 returned: needs attention' },
  { name: '5c accepted, batch list only, no returns', input: { batches: [sub('Accepted', { ...ALL_PAID })] }, status: 'confirmed', stage: 'finished', next: 'settle', disabled: false, label: 'Bank confirmed every payment' },
  { name: '5c submitted, batch list only, no returns', input: { batches: [sub('Submitted', { ...ALL_PAID })] }, status: 'confirmed', stage: 'finished', next: 'settle', disabled: false, label: 'Bank confirmed every payment' },
  { name: '5c accepted, returns not reported', input: { batches: [sub('Accepted', { unconfirmedPayments: { count: 0, total: 0 }, confirmationCoverage: 1 })] }, status: 'confirmed', stage: 'response', next: 'settle', disabled: false, label: 'Bank answered every payment' },
  { name: '5c submitted, returns not reported', input: { batches: [sub('Submitted', { unconfirmedPayments: { count: 0, total: 0 }, confirmationCoverage: 1 })] }, status: 'confirmed', stage: 'response', next: 'settle', disabled: false, label: 'Bank answered every payment' },
  { name: '5a waiting with a return, batch list only', input: { batches: [sub('Submitted', { unconfirmedPayments: { count: 3, total: 300 }, confirmationCoverage: 0.4, unresolvedReturns: { count: 1, total: 100 } })] }, status: 'submitted', stage: 'response', next: 'record_response', disabled: false, label: 'Submitted — waiting for the bank (3 of 5 waiting)' },
  { name: 'F settled', input: { batches: [sub('Paid')] }, status: 'settled', stage: 'finished', next: null, label: 'Paid — settled' },
  { name: 'F run paid', input: { run: { status: 'Paid' }, batches: [] }, status: 'settled', stage: 'finished', next: null, label: 'Paid — settled' },
  { name: 'register only (WPS Generated, no bank file)', input: { batches: [batch({ wpsStatus: 'Generated', effectiveWpsStatus: 'Generated' })] }, status: 'no_file', stage: 'check', next: 'check', disabled: false, label: 'No bank file yet' },
];

const identity = (key: string, params?: Record<string, string | number>) => key.replace(/\{(\w+)\}/g, (_, k) => String(params?.[k] ?? `{${k}}`));

/** The primaries each stage may carry: a button never points at another step's work. */
const STAGE_PRIMARY: Record<PayOutStage, (PayOutNextKind | null)[]> = {
  batch: ['create_batch', 'open_batch'], setup: ['complete_setup'], check: ['check', 'check_again', null], create: ['create_file'],
  send: ['download', 'record_submitted'], response: ['record_response', 'prepare_corrected', 'settle'], finished: ['settle', null],
};

const LOCKED: PipelineInput = {
  run: { status: 'Locked', createdAtUtc: '2026-10-01T08:00:00Z', processedAtUtc: '2026-10-02T08:00:00Z', lockedAtUtc: '2026-10-04T08:00:00Z' },
  processing: null, openErrors: 0, overrideCount: 0, approvals: [], batches: [],
  prechecks: { staleAttendanceDays: 0, employeesBlockedFromPayment: 0, companyBlockers: 0 }, reviewedAtUtc: null,
  can: { process: true, approve: true, lock: true }, currencyConfirmed: true,
};

function derive(row: Row): { input: PayOutInput; p: PayOut } {
  const batches = row.input.batches ?? [batch()];
  const live = batches.filter(b => b.wpsStatus !== 'Voided');
  const input: PayOutInput = {
    run: row.input.run ?? RUN, batches, can: row.input.can ?? ALL,
    // The tab has the only batch open; with several none is open until the user picks one.
    openBatchId: row.input.openBatchId ?? (live.length === 1 ? live[0].id : null),
    exportFacts: row.input.exportFacts ?? null, downloaded: row.input.downloaded ?? false, records: row.input.records ?? null,
  };
  return { input, p: derivePayOut(input) };
}

test.describe('derivePayOut: one row per state', () => {
  for (const row of ROWS) {
    test(row.name, () => {
      const { p } = derive(row);
      expect(p.status).toBe(row.status);
      expect(p.stage).toBe(row.stage);
      expect(p.next?.kind ?? null).toBe(row.next);
      if (row.disabled !== undefined) expect(p.next?.disabled).toBe(row.disabled);
      // A disabled primary always carries its reason.
      if (p.next?.disabled) expect(p.next.reason).toBeTruthy();
      expect(payOutLabel(identity, p)).toBe(row.label);
    });
  }
});

test.describe('every consumer agrees, for every state', () => {
  for (const row of ROWS) {
    test(row.name, () => {
      const { input, p } = derive(row);
      const label = payOutLabel(identity, p);

      // The strip: the stage is the first step that is not done (or the gate before step 2, or the end), and nothing else is "here".
      const open = p.steps.filter(s => s.state === 'current' || s.state === 'blocked');
      if (p.stage === 'finished') expect(open).toHaveLength(0);
      else if (p.stage === 'setup') { expect(open).toHaveLength(0); expect(p.steps[0].state).toBe('done'); expect(p.steps[1].note).toBe('waits_setup'); }
      else { expect(open).toHaveLength(1); expect(open[0].key).toBe(p.stage); expect(p.steps.findIndex(s => s.state !== 'done') + 1).toBe(p.position); }
      for (const s of p.steps.slice(0, Math.max(0, p.position - 1))) expect(s.state, `${s.key} before the stage`).toBe('done');

      // The open panel and the primary: the button belongs to the step that is open.
      expect(STAGE_PRIMARY[p.stage]).toContain(p.next?.kind ?? null);

      // The run bar and the pipeline's Pay out stage: the same object, the same words.
      const pipeline = derivePipeline({ ...LOCKED, run: { ...LOCKED.run, status: input.run!.status }, batches: input.batches ?? null, payOut: { can: input.can, openBatchId: input.openBatchId, exportFacts: input.exportFacts, downloaded: input.downloaded, records: input.records } });
      const paid = pipeline.stages.find(s => s.key === 'paid')!;
      if (input.run!.status !== 'Locked' && input.run!.status !== 'Paid') {
        // Before the lock the pipeline's focus is an earlier stage; Pay out waits for the lock.
        expect(pipeline.payOut).toBeNull();
        expect(paid.state).toBe('upcoming');
      } else {
        expect(pipeline.payOut).toEqual(p);
        expect(paid.state).toBe(payOutStageState(p));
        if (paid.state !== 'done') expect(paid.reason).toEqual({ id: 'pay_out' });
      }
      // "Paid / Exported" is gone: the stage is done only for money the bank confirmed or that is settled.
      if (paid.state === 'done') expect(['confirmed', 'settled']).toContain(p.status);
      // The pipeline's next step points where the work is.
      if (pipeline.focus === 'paid' && pipeline.next.kind === 'goto') {
        expect(pipeline.next.tab).toBe(p.status === 'submitted' || p.status === 'confirmed' ? 'payment-tracking' : 'bank-wps');
      }

      // The run picker's option for the open run ends with the chip's words.
      const option = payOutForRun(input.run as { id: string; status: string }, (input.batches ?? []) as PipelineBatch[], { can: input.can, openBatchId: input.openBatchId, exportFacts: input.exportFacts, downloaded: input.downloaded, records: input.records });
      expect(payOutLabel(identity, option)).toBe(label);
      // "Needs attention" says the same as the chip: a run whose file is still to make (or fix).
      const attention = attentionFor(input.run as { id: string; status: string }, (input.batches ?? []) as PipelineBatch[]);
      if (input.run!.status === 'Locked' && !input.exportFacts && !input.downloaded && !input.records) expect(attention === 'export').toBe(payOutNeedsFile(p));

      // Green only when the bank confirmed every payment and none came back (or the money is settled).
      const tone = payOutTone(p);
      if (tone === 'success') expect(p.status === 'settled' || (p.status === 'confirmed' && p.returned === 0)).toBe(true);
      if ((p.returned ?? 0) > 0) { expect(tone).not.toBe('success'); expect(paid.state).not.toBe('done'); expect(label).toMatch(/returned: needs attention|waiting/); }
      // No word of money moving before it did.
      if (!['confirmed', 'settled'].includes(p.status)) expect(label).not.toMatch(/Paid|Exported|confirmed/);
      if (!['created', 'downloaded', 'submitted', 'rejected', 'confirmed', 'settled'].includes(p.status)) expect(label).not.toMatch(/created|generated/i);
    });
  }
});

test.describe('the facts it reads', () => {
  test('a generated internal register is not a bank file (Saudi batch, WPS Generated, bankFileGenerated false)', () => {
    const p = derivePayOut({ run: RUN, batches: [batch({ wpsStatus: 'Generated', effectiveWpsStatus: 'Generated' })], can: ALL });
    expect(p.status).toBe('no_file');
  });
  test('a WPS / SIF batch has its file once WPS says Generated', () => {
    const p = derivePayOut({ run: RUN, batches: [batch({ isSaudi: false, bankFileAvailable: false, wpsStatus: 'Generated', effectiveWpsStatus: 'Generated' })], can: ALL });
    expect(p.status).toBe('created');
    expect(p.bankFileFlow).toBe(false);
  });
  test('a voided batch is never the one spoken about', () => {
    const p = derivePayOut({ run: RUN, batches: [batch({ wpsStatus: 'Voided', effectiveWpsStatus: 'Voided', bankFileGenerated: true })], can: ALL });
    expect(p.status).toBe('no_batch');
  });
  test('with several batches and none open, the one furthest along speaks for the run', () => {
    const p = derivePayOut({ run: RUN, batches: [batch(), sub('Submitted', { id: 'b9' })], can: ALL });
    expect(p.batchId).toBe('b9');
    expect(p.status).toBe('submitted');
  });
  test('records: Paid, Returned and Rejected answer (Rejected is counted apart, never as returned), Pending waits, Cancelled is out', () => {
    expect(splitFromRecords([{ status: 'Paid', amount: 1 }, { status: 'Rejected', amount: 2 }, { status: 'Returned', amount: 3 }, { status: 'Pending', amount: 4.5 }, { status: 'Cancelled', amount: 9 }]))
      .toEqual({ paid: 1, returned: 1, rejected: 1, waiting: 1, total: 4, paidAmount: 1, returnedAmount: 3, rejectedAmount: 2, waitingAmount: 4.5, totalAmount: 10.5 });
  });
  test('a file that does not tie: step 3 is the blocked one, with the tie-out as its reason, and the pipeline says the same', () => {
    const input: PayOutInput = { run: RUN, batches: [batch()], openBatchId: 'b1', can: ALL, exportFacts: facts({ validation: 'passed', tieDifference: 50 }) };
    const p = derivePayOut(input);
    expect(p.steps[1]).toMatchObject({ key: 'check', state: 'done', note: 'passed' });
    expect(p.steps[2]).toMatchObject({ key: 'create', state: 'blocked', note: 'needs_fix', reason: 'tie_mismatch' });
    expect(p.next).toEqual({ kind: 'create_file', disabled: true, reason: 'tie_mismatch' });
    expect(payOutStageState(p)).toBe('blocked');
    const pipeline = derivePipeline({ ...LOCKED, batches: [batch()], payOut: { can: ALL, openBatchId: 'b1', exportFacts: input.exportFacts } });
    expect(pipeline.payOut).toEqual(p);
  });
  test('the money ties: the buckets add up to the file to the cent (the fixture of the walk-through)', () => {
    // Nine people, 129,438.90 in the file: six paid, one returned, two waiting.
    const amounts = [20838.9, 12350, 12700, 13050, 13400, 13750, 14100, 14450, 14800];
    expect(Math.round(amounts.reduce((s, a) => s + a, 0) * 100) / 100).toBe(129438.9);
    const split = splitFromRecords(amounts.map((amount, i) => ({ amount, status: i < 6 ? 'Paid' : i < 7 ? 'Returned' : 'Pending' })));
    expect(split.totalAmount).toBe(129438.9);
    expect(Math.round((split.paidAmount + split.returnedAmount + split.rejectedAmount + split.waitingAmount) * 100) / 100).toBe(split.totalAmount);
    expect(split.waitingAmount).toBe(29250);
    // Cents never drift (0.1 + 0.2).
    expect(splitFromRecords([{ status: 'Pending', amount: 0.1 }, { status: 'Pending', amount: 0.2 }]).waitingAmount).toBe(0.3);
  });
  test('the check passing moves the step, never the chip', () => {
    const before = derivePayOut({ run: RUN, batches: [batch()], openBatchId: 'b1', can: ALL, exportFacts: facts() });
    const after = derivePayOut({ run: RUN, batches: [batch()], openBatchId: 'b1', can: ALL, exportFacts: facts({ validation: 'passed' }) });
    expect(before.stage).toBe('check');
    expect(after.stage).toBe('create');
    expect(payOutLabel(identity, after)).toBe(payOutLabel(identity, before));
  });
});

test.describe('the salary credit date', () => {
  test('today is the date in Riyadh', () => {
    expect(todayInRiyadh(new Date('2026-09-23T20:59:00Z'))).toBe('2026-09-23');
    expect(todayInRiyadh(new Date('2026-09-23T21:00:00Z'))).toBe('2026-09-24');
  });
  test('the default is the next working day after today (Friday and Saturday skipped)', () => {
    expect(nextWorkingDay('2026-09-23')).toBe('2026-09-24'); // Wed -> Thu
    expect(nextWorkingDay('2026-09-24')).toBe('2026-09-27'); // Thu -> Sun
    expect(nextWorkingDay('2026-09-25')).toBe('2026-09-27'); // Fri -> Sun
    expect(nextWorkingDay('2026-09-26')).toBe('2026-09-27'); // Sat -> Sun
    expect(nextWorkingDay('2026-12-31')).toBe('2027-01-03'); // Thu -> Sun, across the year
  });
  test('past days, Fridays and Saturdays cannot be chosen; today can', () => {
    expect(isWeekend('2026-09-25')).toBe(true);
    expect(isWeekend('2026-09-26')).toBe(true);
    expect(isWeekend('2026-09-27')).toBe(false);
    expect(isSelectableCreditDate('2026-09-22', '2026-09-23')).toBe(false);
    expect(isSelectableCreditDate('2026-09-23', '2026-09-23')).toBe(true);
    expect(isSelectableCreditDate('2026-09-25', '2026-09-23')).toBe(false);
    expect(isSelectableCreditDate('2026-02-30', '2026-01-01')).toBe(false);
    expect(addDays('2026-02-28', 1)).toBe('2026-03-01');
  });
  test('the month grid starts on Sunday', () => {
    const grid = monthGrid(2026, 8); // September 2026 starts on a Tuesday
    expect(grid[0].slice(0, 3)).toEqual([null, null, '2026-09-01']);
    expect(grid.flat().filter(Boolean)).toHaveLength(30);
  });
});

test.describe('the words', () => {
  const KEYS = [
    'No payment batch yet', 'Bank setup needed', 'No bank file yet', 'Needs a fix', 'File created — not sent', 'Downloaded — not yet submitted',
    'Submitted — waiting for the bank ({waiting} of {total} waiting)', 'Submitted — waiting for the bank', 'Rejected by the bank',
    '{count} returned: needs attention', 'Bank answered every payment', 'Bank confirmed {total} of {total}', 'Bank confirmed every payment', 'Paid — settled',
  ];
  test('every status has an Arabic sentence', () => {
    for (const k of KEYS) expect(LOCALE_DICTS.ar[k], k).toBeTruthy();
  });
  test('no key of the flow is given a different Arabic elsewhere', () => {
    for (const dict of [bankFileGuide.ar, payrollUx.ar]) {
      for (const [k, v] of Object.entries(dict)) expect(LOCALE_DICTS.ar[k], k).toBe(v);
    }
  });
});

/** The same sentence in Arabic: the catalogue entry with its placeholders filled. */
const arabic = (key: string, params?: Record<string, string | number>) => (LOCALE_DICTS.ar[key] ?? `MISSING AR: ${key}`).replace(/\{(\w+)\}/g, (_, k) => String(params?.[k] ?? `{${k}}`));

test.describe('a bank rejection is never worded as returned (W1)', () => {
  const REJECTED_EN = 'not paid: rejected by the bank';
  const REJECTED_AR = 'لم يُدفع: رفضه البنك';
  const mixed = [{ status: 'Paid', amount: 100 }, { status: 'Rejected', amount: 40 }, { status: 'Rejected', amount: 10 }, { status: 'Returned', amount: 60 }];

  test('Payroll page response buckets (the PayrollPage list): Rejected reads not paid, Returned stays returned, EN and AR', () => {
    const split = splitFromRecords(mixed);
    expect(split).toMatchObject({ returned: 1, rejected: 2, returnedAmount: 60, rejectedAmount: 50, total: 4, totalAmount: 210 });
    for (const [t, rejected, returned] of [[identity, `2 ${REJECTED_EN}`, '1 returned'], [arabic, `2 ${REJECTED_AR}`, '1 مُعادة']] as const) {
      const rows = bankResponseBuckets(t, split);
      expect(rows.find(r => r.key === 'rejected'), 'rejected row').toMatchObject({ label: rejected, amount: 50 });
      expect(rows.find(r => r.key === 'returned'), 'returned row').toMatchObject({ label: returned, amount: 60 });
      expect(rows.find(r => r.key === 'rejected')!.label).not.toMatch(/returned|مُعادة/);
    }
  });

  test('no rejected row when nothing was rejected', () => {
    expect(bankResponseBuckets(identity, splitFromRecords(records(2, 1, 1))).map(r => r.key)).toEqual(['paid', 'returned', 'waiting']);
  });

  test('pay-out chip (payOut.ts): a confirmed batch with only rejected lines says not paid, never returned, EN and AR', () => {
    const p = derivePayOut({ run: RUN, batches: [sub('Accepted')], can: ALL, records: [{ status: 'Paid', amount: 100 }, { status: 'Rejected', amount: 40 }] });
    expect(p).toMatchObject({ status: 'confirmed', returned: 0, rejected: 1 });
    expect(payOutLabel(identity, p)).toBe(`1 ${REJECTED_EN}`);
    expect(payOutLabel(arabic, p)).toBe(`1 ${REJECTED_AR}`);
    expect(payOutLabel(identity, p)).not.toMatch(/returned/);
    expect(payOutTone(p)).toBe('warning');
    expect(confirmedResponsePurpose(identity, p)).not.toMatch(/returned/);
    expect(confirmedResponsePurpose(arabic, p)).not.toMatch(/المُعادة|مُعادة/);
  });

  test('pay-out chip: returned and rejected together are both named; returned alone is unchanged', () => {
    const both = derivePayOut({ run: RUN, batches: [sub('Accepted')], can: ALL, records: mixed });
    expect(payOutLabel(identity, both)).toBe(`1 returned and 2 ${REJECTED_EN}`);
    expect(payOutLabel(arabic, both)).toBe(`1 مُعادة و2 ${REJECTED_AR}`);
    const returnedOnly = derivePayOut({ run: RUN, batches: [sub('Accepted')], can: ALL, records: records(1, 1, 0) });
    expect(payOutLabel(identity, returnedOnly)).toBe('1 returned: needs attention');
    expect(payOutLabel(arabic, returnedOnly)).toBe('1 مُعادة: تحتاج إلى معالجة');
  });

  test('pay-out chip from the batch list: unresolvedRejections is read apart from unresolvedReturns', () => {
    const p = derivePayOut({ run: RUN, batches: [sub('Accepted', { ...ALL_PAID, unresolvedRejections: { count: 2, total: 800 } })], can: ALL });
    expect(p).toMatchObject({ status: 'confirmed', returned: 0, rejected: 2 });
    expect(payOutLabel(identity, p)).toBe(`2 ${REJECTED_EN}`);
    expect(payOutStageState(p)).not.toBe('done');
  });
});
