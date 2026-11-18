/**
 * Pay out: where a Locked run's money stands on its way to the employees, as ONE derived object.
 *
 * Every place that talks about it reads `derivePayOut` and nothing else: the run bar's status chip and the run picker's option text
 * (RunPipeline), the pipeline's "Pay out" stage and its next step (derivePipeline / attentionFor), and the Bank / WPS tab's step strip,
 * open panel and primary button (BankFileGuide). If two of them could disagree, the build would be wrong.
 *
 * Pure (no React, no fetch). The words live with the screen; this file returns keys and counts, and `payOutLabel` turns the status into
 * the one sentence every consumer shows (t is passed in, so the keys stay literal for the i18n checks).
 *
 * The facts it reads, and the precedence that removed the old contradictions:
 *  1. File exists: the bank-file panel's frozen export, or the batch list's `bankFileGenerated`. A Saudi batch's WPS status also moves for
 *     the INTERNAL payroll register, so it never says "file" on its own. Other batches (WPS / SIF): WPS status Generated or later.
 *  2. Submitted and the bank's answer: the batch's effective WPS status (Submitted, Accepted, Rejected, Paid, Reconciled).
 *  3. Answered x of y: the open batch's payment records when the tab has them (Paid, Returned, Rejected answer; Pending waits; Cancelled
 *     is out of the count), otherwise the batch list's unconfirmed payments and coverage (awaitingBankConfirmation). Returned payments:
 *     the records, otherwise the batch list's unresolvedReturns. A return, or returns not reported, never reads as "confirmed".
 *  4. Check passed: the panel's validation (this session only). It moves the stage from step 2 to step 3 and never changes the chip.
 *  5. Setup: the panel's saved employer setup; absent means complete.
 *  6. Run locked: run.status === 'Locked'.
 *  7. Tie-out: the panel's batch total against the run (run net − paid outside the file). A difference blocks step 3 (Finance rule).
 */
import { awaitingBankConfirmation, type PipelineBatch } from './payrollPipeline';

export type PayOutStatus =
  | 'no_batch' | 'setup' | 'no_file' | 'attention' | 'created' | 'downloaded'
  | 'submitted' | 'rejected' | 'confirmed' | 'settled';

export type PayOutStepKey = 'batch' | 'check' | 'create' | 'send' | 'response';
export const PAY_OUT_STEPS: readonly PayOutStepKey[] = ['batch', 'check', 'create', 'send', 'response'];
/** `setup` is the gate before step 2 (position 0); `finished` is "nothing is left to do here". */
export type PayOutStage = 'setup' | PayOutStepKey | 'finished';
export type PayOutStepState = 'done' | 'current' | 'blocked' | 'upcoming';

/** The state text under a step in the strip. */
export type PayOutStepNote =
  | 'done' | 'passed' | 'file_created' | 'recorded' | 'confirmed' | 'settled'
  | 'here' | 'waiting' | 'needs_fix' | 'rejected' | 'todo' | 'waits_setup';

/** Why a step cannot move, or why the primary is off. */
export type PayOutReason =
  | 'run_not_locked' | 'no_permission_create' | 'choose_batch'
  | 'checking' | 'gate_off' | 'gate_no_permission' | 'gate_failed'
  | 'validation_blocked' | 'stale' | 'create_refused'
  | 'no_permission_setup' | 'no_permission_wps' | 'submit_not_allowed' | 'rejected_ask_admin'
  /** The batch total is not the run's net pay minus what is paid outside the file (Finance rule: the file is never created). */
  | 'tie_mismatch'
  /** The API refuses to settle a batch with any returned or rejected payment (422 payment_records_rejected). */
  | 'payments_not_paid';

export interface PayOutStep { key: PayOutStepKey; state: PayOutStepState; note: PayOutStepNote; reason?: PayOutReason }

/** The ONE primary for the stage (the footer of the Bank / WPS panel). */
export type PayOutNextKind =
  | 'create_batch' | 'open_batch' | 'complete_setup' | 'check' | 'check_again' | 'create_file'
  | 'download' | 'record_submitted' | 'record_response' | 'prepare_corrected' | 'settle';
export interface PayOutNext { kind: PayOutNextKind; disabled: boolean; reason?: PayOutReason }

export interface PayOutSplit {
  /** Returned: the money came back. Rejected: the bank refused the instruction, so it reads "not paid", never "returned". */
  paid: number; returned: number; rejected: number; waiting: number; total: number;
  /** The money in each bucket; together they are the money in the file (when the records tie to it). */
  paidAmount: number; returnedAmount: number; rejectedAmount: number; waitingAmount: number; totalAmount: number;
}

export interface PayOut {
  status: PayOutStatus;
  /** The batch the facts were read from (the open one, the only live one, or the furthest along); null when none applies. */
  batchId: string | null;
  /** A batch the bank-file flow applies to (Saudi); the others keep their own layout. */
  bankFileFlow: boolean;
  stage: PayOutStage;
  /** 0 for the setup gate, 1-5 for the steps, 5 when finished. */
  position: number;
  steps: PayOutStep[];
  next: PayOutNext | null;
  /** How many payments the bank has answered, of how many (when known). */
  answered?: number;
  total?: number;
  /** The record-level split (only from the payment records). */
  split?: PayOutSplit;
  /** Payments the bank returned (from the records, else the batch list's unresolved returns); absent when not reported. */
  returned?: number;
  /** Payments the bank rejected, which are not paid (from the records, else the batch list's unresolved rejections); absent when not reported. */
  rejected?: number;
}

export interface PayOutExportFacts {
  gate: 'checking' | 'no_permission' | 'off' | 'failed' | 'ready';
  /** Absent: not reported, read as complete. */
  setup?: 'complete' | 'incomplete';
  validation: 'none' | 'running' | 'stale' | 'blocked' | 'passed';
  /** The panel holds a frozen export for the batch. */
  fileExists: boolean;
  /** The last "Create bank file" was refused (conflict, field errors). */
  createRefused?: boolean;
  /** Batch total − (run net − paid outside the file); null or absent when the API does not say what is paid outside. */
  tieDifference?: number | null;
}

export interface PayOutInput {
  run: { status: string } | null | undefined;
  /** The run's payment batches (the same list the pipeline reads); null while not known. */
  batches: PipelineBatch[] | null | undefined;
  /** The batch the Bank / WPS tab has open. */
  openBatchId?: string | null;
  can: { create: boolean; wps: boolean; settle: boolean; setup: boolean };
  /** From the bank-file panel, when it is mounted for the open batch. */
  exportFacts?: PayOutExportFacts | null;
  /** The file was downloaded in this session. */
  downloaded?: boolean;
  /** The open batch's payment records, when loaded. */
  records?: ReadonlyArray<{ status: string; amount: number }> | null;
}

const wpsOf = (b: PipelineBatch): string => b.effectiveWpsStatus || b.wpsStatus || 'Draft';
const isVoided = (b: PipelineBatch) => b.wpsStatus === 'Voided' || b.effectiveWpsStatus === 'Voided';
const RESPONSE = new Set(['Submitted', 'Accepted', 'Rejected']);
const SETTLED = new Set(['Paid', 'Reconciled']);
const FILE_WPS = new Set(['Generated', 'Downloaded', 'Submitted', 'Accepted', 'Rejected', 'Paid', 'Reconciled']);

/** The bank-file flow (the Saudi bank export) applies to the batch. */
export const bankFileFlowApplies = (b: PipelineBatch): boolean => b.isSaudi === true || b.bankFileAvailable === true;

/** Rule 1: the bank file exists. Never the WPS status alone for a bank-file batch (the internal register moves it too). */
function fileExistsFor(b: PipelineBatch, facts?: PayOutExportFacts | null): boolean {
  if (bankFileFlowApplies(b)) return !!facts?.fileExists || b.bankFileGenerated === true;
  return FILE_WPS.has(wpsOf(b));
}

/** How far a batch has gone, to pick the one the run bar speaks about when none is open. */
function rank(b: PipelineBatch): number {
  const w = wpsOf(b);
  if (SETTLED.has(w)) return 4;
  if (RESPONSE.has(w)) return 3;
  return fileExistsFor(b) ? 2 : 0;
}

/** The batch the facts come from: the open one; else the only live one; else the furthest along, if any has moved. */
export function pickPayOutBatch(batches: PipelineBatch[] | null | undefined, openBatchId?: string | null): PipelineBatch | null {
  const live = (batches ?? []).filter(b => !isVoided(b));
  if (openBatchId) return live.find(b => b.id === openBatchId) ?? null;
  if (live.length === 1) return live[0];
  const moved = live.filter(b => rank(b) > 0).sort((a, b) => rank(b) - rank(a) || (b.wpsStatusChangedAtUtc ?? b.createdAtUtc).localeCompare(a.wpsStatusChangedAtUtc ?? a.createdAtUtc));
  return moved[0] ?? null;
}

/** Answered / waiting from the payment records (rule 3). Cancelled records are out of the batch. */
export function splitFromRecords(records: ReadonlyArray<{ status: string; amount: number }>): PayOutSplit {
  let paid = 0; let returned = 0; let rejected = 0; let waiting = 0; let paidAmount = 0; let returnedAmount = 0; let rejectedAmount = 0; let waitingAmount = 0;
  for (const r of records) {
    const s = (r.status ?? '').toLowerCase();
    const amount = r.amount ?? 0;
    if (s === 'cancelled') continue;
    if (s === 'paid') { paid++; paidAmount += amount; }
    else if (s === 'returned') { returned++; returnedAmount += amount; }
    else if (s === 'rejected') { rejected++; rejectedAmount += amount; }
    else { waiting++; waitingAmount += amount; }
  }
  const cents = (n: number) => Math.round(n * 100) / 100;
  return {
    paid, returned, rejected, waiting, total: paid + returned + rejected + waiting,
    paidAmount: cents(paidAmount), returnedAmount: cents(returnedAmount), rejectedAmount: cents(rejectedAmount), waitingAmount: cents(waitingAmount),
    totalAmount: cents(paidAmount + returnedAmount + rejectedAmount + waitingAmount),
  };
}

const step = (key: PayOutStepKey, state: PayOutStepState, note: PayOutStepNote, reason?: PayOutReason): PayOutStep => (reason ? { key, state, note, reason } : { key, state, note });
const NOTE_DONE: Record<PayOutStepKey, PayOutStepNote> = { batch: 'done', check: 'passed', create: 'file_created', send: 'recorded', response: 'confirmed' };
const done = (key: PayOutStepKey) => step(key, 'done', NOTE_DONE[key]);
const todo = (key: PayOutStepKey, reason?: PayOutReason) => step(key, 'upcoming', 'todo', reason);
const here = (key: PayOutStepKey, reason?: PayOutReason) => step(key, 'current', 'here', reason);
const fix = (key: PayOutStepKey, reason?: PayOutReason) => step(key, 'blocked', 'needs_fix', reason);

function result(status: PayOutStatus, batch: PipelineBatch | null, steps: PayOutStep[], next: PayOutNext | null, gate: boolean, extra: Partial<PayOut> = {}): PayOut {
  const index = steps.findIndex(s => s.state !== 'done');
  const stage: PayOutStage = gate ? 'setup' : index < 0 ? 'finished' : steps[index].key;
  const position = gate ? 0 : index < 0 ? 5 : index + 1;
  return { status, batchId: batch?.id ?? null, bankFileFlow: !!batch && bankFileFlowApplies(batch), stage, position, steps, next, ...extra };
}

export function derivePayOut(input: PayOutInput): PayOut {
  const { run, can } = input;
  const batch = pickPayOutBatch(input.batches, input.openBatchId);
  const live = (input.batches ?? []).filter(b => !isVoided(b));
  const runPaid = run?.status === 'Paid';

  // ── No batch to speak about
  if (!batch) {
    if (runPaid) return result('settled', null, PAY_OUT_STEPS.map(k => step(k, 'done', k === 'response' ? 'settled' : NOTE_DONE[k])), null, false);
    const rest = (r?: PayOutReason) => [todo('check', r), todo('create', r), todo('send', r), todo('response', r)];
    if (live.length > 1) {
      // The tab turns the button on once a batch is chosen from the list.
      return result('no_file', null, [here('batch', 'choose_batch'), ...rest('choose_batch')], { kind: 'open_batch', disabled: false }, false);
    }
    if (run?.status !== 'Locked') {
      return result('no_batch', null, [fix('batch', 'run_not_locked'), ...rest('run_not_locked')], { kind: 'create_batch', disabled: true, reason: 'run_not_locked' }, false);
    }
    if (!can.create) {
      return result('no_batch', null, [fix('batch', 'no_permission_create'), ...rest()], { kind: 'create_batch', disabled: true, reason: 'no_permission_create' }, false);
    }
    return result('no_batch', null, [here('batch'), ...rest()], { kind: 'create_batch', disabled: false }, false);
  }

  const wps = wpsOf(batch);
  const facts = input.openBatchId === batch.id || !input.openBatchId ? input.exportFacts : null;
  const fileExists = fileExistsFor(batch, facts);
  const allowed = batch.allowedNextStatuses ?? [];

  // ── Settled: paid out and posted
  if (SETTLED.has(wps) || runPaid) {
    return result('settled', batch, [done('batch'), done('check'), done('create'), done('send'), step('response', 'done', 'settled')], null, false);
  }

  // ── The bank's response
  if (RESPONSE.has(wps)) {
    const front = [done('batch'), done('check'), done('create'), done('send')];
    if (wps === 'Rejected') {
      const mayRedo = allowed.includes('Generated') && can.wps;
      return result('rejected', batch, [...front, step('response', 'blocked', 'rejected')],
        mayRedo ? { kind: 'prepare_corrected', disabled: false } : { kind: 'prepare_corrected', disabled: true, reason: 'rejected_ask_admin' }, false);
    }
    const split = input.records && input.records.length > 0 ? splitFromRecords(input.records) : undefined;
    let answered: number | undefined;
    let total: number | undefined;
    let waiting: boolean | null;
    // Money that came back: from the records, or (run bar, picker, pipeline: no records) from the batch list.
    let returned: number | undefined;
    let rejected: number | undefined;
    if (split && split.total > 0) {
      answered = split.paid + split.returned + split.rejected; total = split.total; waiting = split.waiting > 0; returned = split.returned; rejected = split.rejected;
    } else {
      const r = batch.unresolvedReturns?.count;
      if (typeof r === 'number') returned = r;
      const x = batch.unresolvedRejections?.count;
      if (typeof x === 'number') rejected = x;
      const count = batch.unconfirmedPayments?.count;
      if (typeof count === 'number') {
        waiting = count > 0;
        const w = awaitingBankConfirmation(batch);
        if (w?.total !== undefined && w.confirmed !== undefined) { answered = w.confirmed; total = w.total; }
      } else waiting = null; // an older API: the wait is said without counts
    }
    const counts = { ...(answered !== undefined && total !== undefined ? { answered, total } : {}), ...(split ? { split } : {}), ...(returned !== undefined ? { returned } : {}), ...(rejected !== undefined ? { rejected } : {}) };
    if (waiting === false) {
      // Every payment answered. Returned ones are still to be paid in a new payment batch (Payment Tracking), and returns not reported are not known to be
      // zero: only a batch with NO returns is "done".
      const clean = returned === 0 && (rejected ?? 0) === 0;
      // Finance rule: the API refuses to settle while any payment is returned or rejected, so the button is off until they are paid in a new batch.
      const notPaid = (returned ?? 0) + (rejected ?? 0) > 0;
      return result('confirmed', batch, [...front, clean ? step('response', 'done', 'confirmed') : step('response', 'current', 'here')],
        can.settle ? (notPaid ? { kind: 'settle', disabled: true, reason: 'payments_not_paid' } : { kind: 'settle', disabled: false }) : null, false, counts);
    }
    return result('submitted', batch, [...front, step('response', 'current', 'waiting')], { kind: 'record_response', disabled: false }, false, counts);
  }

  // ── The file exists: download it, then record it as submitted
  if (fileExists) {
    const downloaded = wps === 'Downloaded' || !!input.downloaded;
    const front = [done('batch'), done('check'), done('create')];
    if (!downloaded) return result('created', batch, [...front, here('send'), todo('response')], { kind: 'download', disabled: false }, false);
    const reason: PayOutReason | undefined = !can.wps ? 'no_permission_wps' : !allowed.includes('Submitted') ? 'submit_not_allowed' : undefined;
    return result('downloaded', batch, [...front, here('send'), todo('response')], { kind: 'record_submitted', disabled: !!reason, ...(reason ? { reason } : {}) }, false);
  }

  // ── No file yet
  const tail = [todo('send'), todo('response')];
  if (!bankFileFlowApplies(batch)) {
    // WPS / SIF batches keep their own layout (the batch's own file button); the strip only says where they are.
    return result('no_file', batch, [done('batch'), here('check'), todo('create'), ...tail], null, false);
  }
  const gate = facts?.gate ?? 'ready';
  if (gate === 'checking') return result('no_file', batch, [done('batch'), here('check', 'checking'), todo('create'), ...tail], { kind: 'check', disabled: true, reason: 'checking' }, false);
  if (gate !== 'ready') {
    const reason: PayOutReason = gate === 'off' ? 'gate_off' : gate === 'no_permission' ? 'gate_no_permission' : 'gate_failed';
    return result('no_file', batch, [done('batch'), fix('check', reason), todo('create'), ...tail], null, false);
  }
  if (facts?.setup === 'incomplete') {
    return result('setup', batch, [done('batch'), step('check', 'upcoming', 'waits_setup'), todo('create'), ...tail],
      can.setup ? { kind: 'complete_setup', disabled: false } : { kind: 'complete_setup', disabled: true, reason: 'no_permission_setup' }, true);
  }
  const validation = facts?.validation ?? 'none';
  if (validation === 'blocked') {
    return result('attention', batch, [done('batch'), fix('check', 'validation_blocked'), todo('create'), ...tail], { kind: 'check_again', disabled: false }, false);
  }
  if (validation === 'passed') {
    // Finance rule: a file that does not tie to the approved run is never created. The check comes first, so this blocks step 3 only.
    if (typeof facts?.tieDifference === 'number' && Math.abs(facts.tieDifference) >= 0.005) {
      return result('attention', batch, [done('batch'), done('check'), fix('create', 'tie_mismatch'), ...tail], { kind: 'create_file', disabled: true, reason: 'tie_mismatch' }, false);
    }
    if (facts?.createRefused) {
      return result('attention', batch, [done('batch'), done('check'), fix('create', 'create_refused'), ...tail], { kind: 'create_file', disabled: false }, false);
    }
    return result('no_file', batch, [done('batch'), done('check'), here('create'), ...tail], { kind: 'create_file', disabled: false }, false);
  }
  if (validation === 'stale') return result('no_file', batch, [done('batch'), here('check', 'stale'), todo('create'), ...tail], { kind: 'check_again', disabled: false }, false);
  return result('no_file', batch, [done('batch'), here('check'), todo('create'), ...tail], { kind: 'check', disabled: validation === 'running', ...(validation === 'running' ? { reason: 'checking' as const } : {}) }, false);
}

// ── The pipeline's reading ────────────────────────────────────────────────────────

/** Where the run's last stage ("Pay out") stands: done only once the bank confirmed every payment, or the money is settled. */
export function payOutStageState(p: PayOut): PayOutStepState {
  if (p.status === 'settled') return 'done';
  if (p.status === 'confirmed') return p.steps[4].state === 'done' ? 'done' : 'current';
  if (p.status === 'attention' || p.status === 'rejected') return 'blocked';
  return 'current';
}

/** The run still needs someone to make or fix its bank file (the run picker's "Needs attention" group). */
export const payOutNeedsFile = (p: PayOut): boolean => p.status === 'no_batch' || p.status === 'setup' || p.status === 'no_file' || p.status === 'attention';

/** The chip's tone. Green only for money the bank confirmed (Finance rule). */
export type PayOutTone = 'warning' | 'danger' | 'neutral' | 'info' | 'success';
export function payOutTone(p: PayOut): PayOutTone {
  switch (p.status) {
    case 'no_batch': case 'no_file': case 'setup': return 'warning';
    case 'attention': case 'rejected': return 'danger';
    case 'created': case 'downloaded': return 'neutral';
    case 'submitted': return 'info';
    case 'confirmed':
      // A rejection is money the bank refused (danger); a return alone needs attention (warning).
      if ((p.rejected ?? 0) > 0) return 'danger';
      return p.returned === 0 ? 'success' : p.returned === undefined ? 'info' : 'warning';
    case 'settled': return 'success';
  }
}

type Translate = (key: string, params?: Record<string, string | number>) => string;

/** The one sentence for the status: the run bar chip, the run picker option, the pipeline's Pay out stage. */
export function payOutLabel(t: Translate, p: PayOut): string {
  switch (p.status) {
    case 'no_batch': return t('No payment batch yet');
    case 'setup': return t('Bank setup needed');
    case 'no_file': return t('No bank file yet');
    case 'attention': return t('Needs a fix');
    case 'created': return t('File created — not sent');
    case 'downloaded': return t('Downloaded — not yet submitted');
    case 'submitted':
      return p.answered !== undefined && p.total !== undefined
        ? t('Submitted — waiting for the bank ({waiting} of {total} waiting)', { waiting: p.total - p.answered, total: p.total })
        : t('Submitted — waiting for the bank');
    case 'rejected': return t('Rejected by the bank');
    case 'confirmed':
      if ((p.returned ?? 0) > 0 && (p.rejected ?? 0) > 0) return t('{returned} returned and {rejected} not paid: rejected by the bank', { returned: p.returned!, rejected: p.rejected! });
      if ((p.rejected ?? 0) > 0) return t('{count} not paid: rejected by the bank', { count: p.rejected! });
      if ((p.returned ?? 0) > 0) return t('{count} returned: needs attention', { count: p.returned! });
      if (p.returned === undefined) return t('Bank answered every payment');
      return p.total ? t('Bank confirmed {total} of {total}', { total: p.total }) : t('Bank confirmed every payment');
    case 'settled': return t('Paid — settled');
  }
}

export type BankResponseBucketKey = 'paid' | 'returned' | 'rejected' | 'waiting';
export interface BankResponseBucket { key: BankResponseBucketKey; tone: 'bg-success' | 'bg-warning' | 'bg-danger' | 'bg-line-strong'; label: string; amount: number }

/** The bank-response list and bar of the Payroll page: each bucket with its words and money. A rejection reads "not paid", never "returned". */
export function bankResponseBuckets(t: Translate, split: PayOutSplit): BankResponseBucket[] {
  const rows: BankResponseBucket[] = [
    { key: 'paid', tone: 'bg-success', label: t('{count} confirmed paid', { count: split.paid }), amount: split.paidAmount },
    { key: 'returned', tone: 'bg-warning', label: t('{count} returned', { count: split.returned }), amount: split.returnedAmount },
  ];
  if (split.rejected > 0) rows.push({ key: 'rejected', tone: 'bg-danger', label: t('{count} not paid: rejected by the bank', { count: split.rejected }), amount: split.rejectedAmount });
  rows.push({ key: 'waiting', tone: 'bg-line-strong', label: t('{count} waiting', { count: split.waiting }), amount: split.waitingAmount });
  return rows;
}

/** The line under the Response step once the bank answered every payment. */
export function confirmedResponsePurpose(t: Translate, p: PayOut): string {
  if ((p.rejected ?? 0) > 0) return t('The bank answered every payment. Some were rejected by the bank and not paid: check the bank statement, then pay them in a new payment batch linked to the original batch.');
  return (p.returned ?? 0) > 0 ? t('The bank answered every payment. Pay the returned ones in a new payment batch linked to the original batch.') : t('The bank confirmed every payment.');
}

/** The line under the Response step when the bank rejected the whole file: the bank statement decides what was paid, never "no one". */
export function rejectedFilePurpose(t: Translate, reason?: string | null): string {
  return reason
    ? t('The bank rejected this file: {reason}. Nothing in it is confirmed paid; check the bank statement.', { reason })
    : t('The bank rejected this file. Nothing in it is confirmed paid; check the bank statement.');
}

/** Payments the bank returned or rejected: settle stays off until they are paid in a new payment batch. */
export const notPaidCount = (p: PayOut): number => (p.returned ?? 0) + (p.rejected ?? 0);

/** Why "Settle payments" is off: the count, and what to do first. */
export function settleBlockedText(t: Translate, p: PayOut): string {
  return t('Not settled: {count, plural, one {# payment was} other {# payments were}} returned or rejected by the bank. Pay {count, plural, one {it} other {them}} in a new payment batch linked to the original batch first.', { count: notPaidCount(p) });
}

/** The Next line of the Response step while returned or rejected payments are open (instead of "Finance settles after the bank confirms"). */
export const settleBlockedNextLine = (t: Translate): string =>
  t('Next: pay the returned or rejected payments in a new payment batch linked to the original batch. Finance settles after that.');
