import { test, expect } from '@playwright/test';
import type { PayrollApproval, PayrollPaymentBatch, PayrollRun } from '../src/api/payroll';
import { parseProcessingStatus, type ProcessingStatus } from '../src/api/payrollProcessing';
import {
  attentionFor, changeBetween, derivePipeline, employeesWithIssues, parseDeepLink, pollDelay, previousRunFor,
  sortByAttention, withDeepLink, type PipelineInput, type StageKey,
} from '../src/lib/payrollPipeline';

const RUN = { status: 'Draft', createdAtUtc: '2026-10-01T08:00:00Z', processedAtUtc: null, lockedAtUtc: null };
const job = (status: ProcessingStatus['status'], over: Partial<ProcessingStatus> = {}): ProcessingStatus => ({
  runId: 'r', jobId: 'j1', status, processedEmployees: null, totalEmployees: null, startedAtUtc: null, finishedAtUtc: null, errorCode: null, errorMessage: null, ...over,
});
const base = (over: Partial<PipelineInput> & { status?: string } = {}): PipelineInput => ({
  run: { ...RUN, status: over.status ?? 'Draft', processedAtUtc: over.status && over.status !== 'Draft' ? '2026-10-02T08:00:00Z' : null, lockedAtUtc: over.status === 'Locked' ? '2026-10-04T08:00:00Z' : null },
  processing: null, openErrors: 0, overrideCount: 0, approvals: [], batches: [],
  prechecks: { staleAttendanceDays: 0, employeesBlockedFromPayment: 0, companyBlockers: 0 }, reviewedAtUtc: null,
  can: { process: true, approve: true, lock: true }, currencyConfirmed: true,
  ...over,
});
const state = (m: ReturnType<typeof derivePipeline>, k: StageKey) => m.stages.find(s => s.key === k)!.state;

test.describe('payroll pipeline stages', () => {
  test('a Draft run is at Pre-checks and the next action is Process', () => {
    const m = derivePipeline(base());
    expect(m.focus).toBe('prechecks');
    expect(m.next).toMatchObject({ kind: 'process', disabled: false });
    expect(state(m, 'draft')).toBe('done');
    expect(state(m, 'processed')).toBe('upcoming');
  });

  test('stale attendance blocks the Pre-checks stage and says how many days; the action goes to the fix, not to Process', () => {
    const m = derivePipeline(base({ prechecks: { staleAttendanceDays: 3, employeesBlockedFromPayment: 0, companyBlockers: 0 } }));
    expect(state(m, 'prechecks')).toBe('blocked');
    expect(m.nextReason).toEqual({ id: 'attendance_stale', count: 3 });
    expect(m.next).toMatchObject({ kind: 'goto', tab: 'validation' });
  });

  test('employees who cannot be paid warn but do not stop Process', () => {
    const m = derivePipeline(base({ prechecks: { staleAttendanceDays: 0, employeesBlockedFromPayment: 4, companyBlockers: 0 } }));
    expect(state(m, 'prechecks')).toBe('current');
    expect(m.nextReason).toEqual({ id: 'payment_gaps', count: 4 });
    expect(m.next).toMatchObject({ kind: 'process', disabled: false });
  });

  test('unreadable pre-checks never claim a pass', () => {
    const m = derivePipeline(base({ prechecks: null }));
    expect(m.nextReason).toEqual({ id: 'prechecks_unknown' });
  });

  test('Queued and Running put the Processed stage in focus with no action', () => {
    for (const s of ['Queued', 'Running'] as const) {
      const m = derivePipeline(base({ processing: job(s, { processedEmployees: 312, totalEmployees: 1200 }) }));
      expect(m.focus).toBe('processed');
      expect(state(m, 'processed')).toBe('current');
      expect(state(m, 'prechecks')).toBe('done');
      expect(m.next.kind).toBe('none');
    }
  });

  test('a failed job blocks Processed with the server message and offers Retry', () => {
    const m = derivePipeline(base({ processing: job('Failed', { errorMessage: 'No statutory pack for SA.' }) }));
    expect(state(m, 'processed')).toBe('blocked');
    expect(m.stages.find(s => s.key === 'processed')!.reason).toEqual({ id: 'failed', message: 'No statutory pack for SA.' });
    expect(m.next).toMatchObject({ kind: 'retry', disabled: false });
  });

  test('a Processed run with open errors is blocked at Validated, and the action is the Validation tab', () => {
    const m = derivePipeline(base({ status: 'Processed', openErrors: 2 }));
    expect(m.focus).toBe('validated');
    expect(m.nextReason).toEqual({ id: 'errors_open', count: 2 });
    expect(m.next).toMatchObject({ kind: 'goto', tab: 'validation' });
    expect(state(m, 'review')).toBe('upcoming');
    expect(state(m, 'approved')).toBe('upcoming');
  });

  test('unreadable validation results block, they are not read as zero errors', () => {
    const m = derivePipeline(base({ status: 'Processed', openErrors: null }));
    expect(state(m, 'validated')).toBe('blocked');
    expect(m.nextReason).toEqual({ id: 'validation_unknown' });
  });

  test('Review is a stop point: Approve is not the next action until the review is marked complete', () => {
    const open = derivePipeline(base({ status: 'Processed' }));
    expect(open.focus).toBe('review');
    expect(open.next.kind).toBe('review');
    expect(state(open, 'approved')).toBe('upcoming');
    expect(open.stages.find(s => s.key === 'approved')!.reason).toEqual({ id: 'needs_review' });

    const done = derivePipeline(base({ status: 'Processed', reviewedAtUtc: '2026-10-02T09:00:00Z' }));
    expect(state(done, 'review')).toBe('done');
    expect(done.focus).toBe('approved');
    expect(done.next).toMatchObject({ kind: 'goto', label: 'approve', tab: 'approvals', disabled: false });
  });

  test('without approve permission the stage is blocked and the reason is spoken', () => {
    const m = derivePipeline(base({ status: 'Processed', reviewedAtUtc: 'x', can: { process: true, approve: false, lock: true } }));
    expect(state(m, 'approved')).toBe('blocked');
    expect(m.nextReason).toEqual({ id: 'no_permission_approve' });
    expect(m.next.disabled).toBe(true);
  });

  test('PendingFinanceReview shows the payroll review time and says finance decides next', () => {
    const approvals = [{ id: 'a', payrollRunId: 'r', approvalLevel: 'PayrollReview', decision: 'Approved', notes: '', decidedByUserId: 'u', decidedAtUtc: '2026-10-03T10:00:00Z' }] as PayrollApproval[];
    const m = derivePipeline(base({ status: 'PendingFinanceReview', reviewedAtUtc: 'x', approvals }));
    const a = m.stages.find(s => s.key === 'approved')!;
    expect(a.state).toBe('current');
    expect(a.whenUtc).toBe('2026-10-03T10:00:00Z');
    expect(a.detail).toEqual({ id: 'payroll_review' });
    expect(a.reason).toEqual({ id: 'needs_finance' });
  });

  test('an Approved run offers Lock; the reason it cannot is always stated', () => {
    const ok = derivePipeline(base({ status: 'Approved' }));
    expect(ok.focus).toBe('locked');
    expect(ok.next).toMatchObject({ kind: 'lock', disabled: false });
    expect(state(ok, 'review')).toBe('done');

    const noCurrency = derivePipeline(base({ status: 'Approved', currencyConfirmed: false }));
    expect(noCurrency.next).toMatchObject({ kind: 'lock', disabled: true });
    expect(noCurrency.nextReason).toEqual({ id: 'currency_unconfirmed' });

    const noPerm = derivePipeline(base({ status: 'Approved', can: { process: true, approve: true, lock: false } }));
    expect(noPerm.nextReason).toEqual({ id: 'no_permission_lock' });

    const errors = derivePipeline(base({ status: 'Approved', openErrors: 1 }));
    expect(errors.nextReason).toEqual({ id: 'errors_open', count: 1 });
    expect(errors.next).toMatchObject({ kind: 'goto', tab: 'validation' });
  });

  test('a Locked run waits for the bank file; a generated file completes the last stage with its batch', () => {
    const locked = derivePipeline(base({ status: 'Locked' }));
    expect(locked.focus).toBe('paid');
    expect(locked.nextReason).toEqual({ id: 'no_batch' });
    expect(state(locked, 'locked')).toBe('done');
    expect(locked.stages.find(s => s.key === 'locked')!.whenUtc).toBe('2026-10-04T08:00:00Z');

    const draftBatch = [{ id: 'b', payrollRunId: 'r', batchNumber: 'B-1', wpsStatus: 'Draft', createdAtUtc: '2026-10-05T00:00:00Z' }] as PayrollPaymentBatch[];
    expect(derivePipeline(base({ status: 'Locked', batches: draftBatch })).nextReason).toEqual({ id: 'batch_not_exported' });

    const sent = [{ ...draftBatch[0], wpsStatus: 'Generated', wpsStatusLabel: 'File generated', wpsStatusChangedAtUtc: '2026-10-06T00:00:00Z' }] as PayrollPaymentBatch[];
    const done = derivePipeline(base({ status: 'Locked', batches: sent }));
    expect(state(done, 'paid')).toBe('done');
    expect(done.stages.find(s => s.key === 'paid')!.detail).toEqual({ id: 'batch', label: 'File generated', number: 'B-1' });
    expect(done.focus).toBe('paid');
  });

  test('a Voided run and an unknown status highlight nothing and invent no stage', () => {
    const voided = derivePipeline(base({ status: 'Voided' }));
    expect(voided.voided).toBe(true);
    expect(voided.focus).toBeNull();
    expect(voided.stages.every(s => s.state === 'upcoming')).toBe(true);
    const odd = derivePipeline(base({ status: 'Calculated' }));
    expect(odd.unknownStatus).toBe(true);
    expect(odd.focus).toBeNull();
  });

  test('at most one stage is the focus, and it is the first that is not done', () => {
    for (const status of ['Draft', 'Processed', 'PendingFinanceReview', 'Approved', 'Locked', 'Paid']) {
      const m = derivePipeline(base({ status }));
      const open = m.stages.filter(s => s.state === 'current' || s.state === 'blocked');
      expect(open.length).toBeLessThanOrEqual(1);
      const firstNotDone = m.stages.find(s => s.state !== 'done');
      if (firstNotDone && open.length) expect(firstNotDone.key).toBe(open[0].key);
    }
  });
});

test.describe('comparison with the previous run', () => {
  const run = (id: string, year: number, month: number, status = 'Locked', companyId: string | null = 'c1', runType = 'Regular') => ({ id, year, month, status, companyId, runType });
  test('picks the latest earlier run of the same company and type that has totals', () => {
    const cur = run('cur', 2026, 10, 'Approved');
    const runs = [run('sep', 2026, 9), run('aug', 2026, 8), run('other-co', 2026, 9, 'Locked', 'c2'), run('void', 2026, 9, 'Voided'), run('draft', 2026, 9, 'Draft'), run('off', 2026, 9, 'Locked', 'c1', 'OffCycle'), run('later', 2026, 11), cur];
    expect(previousRunFor(cur, runs)?.id).toBe('sep');
    expect(previousRunFor(run('first', 2026, 1), [run('first', 2026, 1)])).toBeNull();
  });
  test('across a year boundary', () => {
    expect(previousRunFor(run('jan', 2026, 1), [run('dec', 2025, 12), run('nov', 2025, 11)])?.id).toBe('dec');
  });
  test('change and percent; no percent against zero', () => {
    expect(changeBetween(110, 100)).toEqual({ delta: 10, percent: 10 });
    expect(changeBetween(90, 100)).toEqual({ delta: -10, percent: -10 });
    expect(changeBetween(5, 0)).toEqual({ delta: 5, percent: null });
  });
});

test.describe('deep link', () => {
  const tabs = ['dashboard', 'runs', 'validation', 'approvals'];
  const id = 'AAAAAAAA-1111-4222-8333-444444444444';
  test('reads tab and run, lower-casing the id', () => {
    expect(parseDeepLink(new URLSearchParams(`tab=validation&run=${id}`), tabs)).toEqual({ tab: 'validation', runId: id.toLowerCase() });
  });
  test('unknown tab and malformed run fall back to null instead of throwing', () => {
    expect(parseDeepLink(new URLSearchParams('tab=nope&run=12'), tabs)).toEqual({ tab: null, runId: null });
    expect(parseDeepLink(null, tabs)).toEqual({ tab: null, runId: null });
  });
  test('writes the selection and keeps other parameters; the default tab is left out unless a run is named', () => {
    expect(withDeepLink('x=1', { tab: 'runs', runId: id }, 'dashboard')).toBe(`x=1&tab=runs&run=${id}`);
    expect(withDeepLink('tab=runs&run=abc', { tab: 'dashboard', runId: null }, 'dashboard')).toBe('');
    expect(withDeepLink('', { tab: 'dashboard', runId: id }, 'dashboard')).toBe(`tab=dashboard&run=${id}`);
  });
});

test.describe('processing contract', () => {
  test('parses the documented shape and rejects anything else', () => {
    const ok = parseProcessingStatus('r1', { runId: 'r1', jobId: 'j', status: 'Running', processedEmployees: 312, totalEmployees: 1200, startedAtUtc: 's', finishedAtUtc: null, errorCode: null, errorMessage: null });
    expect(ok).toMatchObject({ status: 'Running', processedEmployees: 312, totalEmployees: 1200, jobId: 'j' });
    expect(parseProcessingStatus('r1', { status: 'Idle' })).toMatchObject({ status: 'Idle', jobId: null, processedEmployees: null });
    expect(parseProcessingStatus('r1', { status: 'Exploded' })).toBeNull();
    expect(parseProcessingStatus('r1', '<html>')).toBeNull();
  });
  test('polls every 2 s in front and backs off when hidden', () => {
    expect(pollDelay(false)).toBe(2000);
    expect(pollDelay(true)).toBeGreaterThan(2000);
  });
});

test.describe('runs that need someone, and employees with issues', () => {
  const r = (id: string, status: string, year = 2026, month = 10) => ({ id, status, year, month }) as unknown as PayrollRun;
  const exported = [{ id: 'b', payrollRunId: 'L1', wpsStatus: 'Generated', createdAtUtc: 'x' }] as PayrollPaymentBatch[];
  test('attention follows the status; a locked run is fine once its file exists, unknown until batches load', () => {
    expect(attentionFor(r('a', 'Draft'), [])).toBe('process');
    expect(attentionFor(r('a', 'Processed'), [])).toBe('approve');
    expect(attentionFor(r('a', 'PendingFinanceReview'), [])).toBe('finance');
    expect(attentionFor(r('a', 'Approved'), [])).toBe('lock');
    expect(attentionFor(r('L1', 'Locked'), exported)).toBeNull();
    expect(attentionFor(r('L2', 'Locked'), exported)).toBe('export');
    expect(attentionFor(r('L2', 'Locked'), null)).toBeNull();
    expect(attentionFor(r('a', 'Paid'), [])).toBeNull();
    expect(attentionFor(r('a', 'Voided'), [])).toBeNull();
  });
  test('oldest period first, then the run furthest along', () => {
    const items = [
      { ...r('new', 'Draft', 2026, 10), attention: 'process' as const },
      { ...r('old-draft', 'Draft', 2026, 8), attention: 'process' as const },
      { ...r('old-lock', 'Approved', 2026, 8), attention: 'lock' as const },
    ];
    expect(sortByAttention(items).map(i => i.id)).toEqual(['old-lock', 'old-draft', 'new']);
  });
  test('employees with issues: errors first, resolved and run-level rows left out', () => {
    const rows = employeesWithIssues([
      { employeeId: 2, severity: 'Warning', message: 'w', isResolved: false },
      { employeeId: 1, severity: 'Error', message: 'e1', isResolved: false },
      { employeeId: 1, severity: 'Error', message: 'e2', isResolved: false },
      { employeeId: 3, severity: 'Error', message: 'done', isResolved: true },
      { employeeId: null, severity: 'Error', message: 'run level', isResolved: false },
    ]);
    expect(rows.map(x => x.employeeId)).toEqual([1, 2]);
    expect(rows[0]).toMatchObject({ errors: 2, warnings: 0 });
  });
});
