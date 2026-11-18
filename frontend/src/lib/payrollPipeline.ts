/**
 * The payroll run pipeline: which stage a run is in, what blocks the next one, and what to do next.
 * Pure (no React, no fetch) so the rules are unit-tested and the screen only renders them.
 *
 * The stages follow the REAL lifecycle read from PayrollController.cs; only two of them are not run statuses:
 *   Draft            run.status = "Draft"                       (POST /runs creates it)
 *   Pre-checks       NOT a status. Before Process: the attendance check (stale days refuse Process) and
 *                    payment readiness (missing bank details etc. will raise validation errors)
 *   Processed        run.status = "Processed"                   (POST /runs/{id}/process, now a background job)
 *   Validated        NOT a status. Validation results are written by Process and refreshed by /validate;
 *                    "validated" means the run has no unresolved Error result (Approve and Lock both refuse otherwise)
 *   Review           NOT a status: the manpower + value reconciliation against the prior month. It is done when
 *                    the server holds an ACTIVE sign-off for this processing of the run (the server refuses
 *                    Approve without one). Off-cycle runs are not gated, so they have no review to wait for.
 *   Approved         "Processed" -> "PendingFinanceReview" (payroll review) -> "Approved" (finance decision)
 *   Locked           run.status = "Locked"                      (posts the GL journal)
 *   Pay out          NOT a status: where the money stands after the lock, read from lib/payOut.ts (derivePayOut), the one
 *                    source the run bar, the run picker and the Bank / WPS tab also read. It is Done only once the bank
 *                    confirmed every payment, or the batch / run is Paid. A generated internal payroll register is not
 *                    a bank file, and a bank file is not money that landed.
 * "Voided" is a side exit, not a stage.
 */
import type { PayrollApproval, PayrollPaymentBatch, PayrollRun } from '../api/payroll';
import { isProcessingActive, type ProcessingStatus } from '../api/payrollProcessing';
import { derivePayOut, payOutNeedsFile, payOutStageState, type PayOut, type PayOutInput } from './payOut';

export type StageKey = 'draft' | 'prechecks' | 'processed' | 'validated' | 'review' | 'approved' | 'locked' | 'paid';
export const STAGE_ORDER: readonly StageKey[] = ['draft', 'prechecks', 'processed', 'validated', 'review', 'approved', 'locked', 'paid'];

/** done = behind us; current = where the run is; blocked = where it is, but it cannot move on yet; upcoming = ahead. */
export type StageState = 'done' | 'current' | 'blocked' | 'upcoming';

export type ReasonId =
  | 'failed'                // the processing job failed ({message})
  | 'processing'            // a job is queued or running
  | 'errors_open'           // unresolved validation errors ({count})
  | 'validation_unknown'    // validation results could not be read
  | 'attendance_stale'      // attendance days that refuse Process ({count} days)
  | 'payment_gaps'          // employees who cannot be paid yet ({count}); does not stop Process
  | 'prechecks_unknown'     // the pre-checks could not be read; Process is still allowed
  | 'needs_process'         // run has not been processed
  | 'needs_validation'      // earlier stage must finish
  | 'needs_review'          // the review stop point has not been completed
  | 'needs_approval'        // not approved yet
  | 'needs_finance'         // payroll review done, finance decision outstanding
  | 'needs_lock'            // run must be locked first
  | 'no_permission_process' | 'no_permission_approve' | 'no_permission_lock'
  | 'currency_unconfirmed'  // lock summary cannot name a currency
  | 'no_batch'              // locked, no payment batch yet
  | 'batch_not_exported'    // batch exists, bank file not generated
  | 'bank_file_not_generated' // only the internal payroll register exists; the bank export has not been generated
  | 'awaiting_bank_confirmation' // the bank file exists, but the bank has not confirmed every payment ({confirmed} of {total} when known)
  | 'pay_out'               // the Pay out stage: its words are payOutLabel(model.payOut)
  | 'voided';

/**
 * Two facts the payment-batch list adds per batch (absent from an older API):
 *   bankFileAvailable  the Saudi bank export applies to the batch's legal entity, so ITS file is the bank file
 *   bankFileGenerated  a bank export exists for the batch
 */
export type PipelineBatch = PayrollPaymentBatch & { bankFileAvailable?: boolean; bankFileGenerated?: boolean };

/** Which file the batch detail names: the bank file, the internal register, or (unknown / not Saudi) what the API's label says. */
export type BatchFileKind = 'bank_file' | 'register';

export interface Reason { id: ReasonId; count?: number; message?: string; confirmed?: number; total?: number }

export interface PipelineStage {
  key: StageKey;
  state: StageState;
  /** When the stage was completed, where the API records it. */
  whenUtc: string | null;
  /** Why the stage is not (yet) done, in words. Set on blocked AND upcoming stages. */
  reason: Reason | null;
  /** Extra facts: the payment batch status, the approval level, overrides. */
  detail: {
    id: 'batch'; label: string; number: string;
    /** Set when the API says which file exists; the screen then names it instead of a bare "Generated". */
    file?: BatchFileKind;
    /** A person moved the batch past its file (submitted, accepted, paid...): the status label says where it is. */
    statusAfterFile?: boolean;
  } | { id: 'payroll_review' } | { id: 'overrides'; count: number } | { id: 'signed_off'; name: string | null } | { id: 'review_not_required' } | null;
}

export type NextActionKind = 'process' | 'retry' | 'goto' | 'review' | 'lock' | 'none';
export type PayrollTabKey = string;

export interface NextAction {
  kind: NextActionKind;
  /** Which sentence labels the button; the screen owns the words. */
  label: 'process' | 'retry' | 'review_validation' | 'review_attendance' | 'review' | 'approve' | 'lock' | 'bank' | 'confirm_bank' | 'none';
  tab?: PayrollTabKey;
  /** The button is shown but cannot be used; `reason` says why. */
  disabled: boolean;
}

/** What the pre-checks found. A null field means that check could not be read. */
export interface Prechecks {
  /** Attendance days whose deductions are stale; they refuse Process. */
  staleAttendanceDays: number | null;
  /** Active employees who cannot be paid yet (no bank details, no salary...). */
  employeesBlockedFromPayment: number | null;
  companyBlockers: number | null;
}

export interface PipelineInput {
  run: Pick<PayrollRun, 'status' | 'createdAtUtc' | 'processedAtUtc' | 'lockedAtUtc'>;
  processing: ProcessingStatus | null;
  /** null = the validation results could not be read (or are not read yet). */
  openErrors: number | null;
  overrideCount: number;
  approvals: PayrollApproval[] | null;
  batches: PipelineBatch[] | null;
  prechecks: Prechecks | null;
  /** When the reconciliation was signed off (an Active server sign-off for this processing of the run); null = not yet. */
  reviewedAtUtc: string | null;
  /** Who signed it off, when the server said. */
  reviewedByName?: string | null;
  /** False when the run is not held at Review: an off-cycle run (the server does not gate it) or a company whose rule does not require the sign-off. Default true. */
  reviewApplicable?: boolean;
  can: { process: boolean; approve: boolean; lock: boolean };
  /** False when the company currency cannot be confirmed: Lock is withheld, as Approve is. */
  currencyConfirmed: boolean;
  /**
   * What the Bank / WPS tab knows beyond the batch list (the open batch, the panel's facts, the records). With it the Pay out stage
   * reads exactly what the tab reads; without it, the batch list alone.
   */
  payOut?: Partial<Omit<PayOutInput, 'run' | 'batches'>>;
}

export interface PipelineModel {
  stages: PipelineStage[];
  focus: StageKey | null;
  next: NextAction;
  nextReason: Reason | null;
  voided: boolean;
  /** True when the status is not one the backend is known to use; the screen shows no highlight. */
  unknownStatus: boolean;
  /** Where the money stands (Locked and Paid runs); null before the lock. */
  payOut: PayOut | null;
}

const EXPORTED_WPS = new Set(['Generated', 'Downloaded', 'Submitted', 'Accepted', 'Paid', 'Reconciled']);

/** The newest decision at one approval level that approved the run. */
function latestApproval(approvals: PayrollApproval[] | null, level: string): PayrollApproval | null {
  if (!approvals) return null;
  return approvals
    .filter(a => a.approvalLevel === level && a.decision === 'Approved' && a.decidedAtUtc)
    .sort((a, b) => (b.decidedAtUtc ?? '').localeCompare(a.decidedAtUtc ?? ''))[0] ?? null;
}

/** The statuses generating and downloading a file set by themselves. Later ones (Submitted, Accepted...) a person records. */
const FILE_STEP_WPS = new Set(['Generated', 'Downloaded']);

const wpsOf = (b: PipelineBatch): string => b.effectiveWpsStatus ?? b.wpsStatus;

/**
 * Only the internal payroll register stands behind the batch's Generated/Downloaded: its company's bank file is the bank
 * export, and that has not been generated. Once a person has moved the batch on (Submitted and later) the export can no
 * longer be made for it, so the old rule applies from there and the stage is not held back for good.
 */
function onlyRegister(b: PipelineBatch): boolean {
  return b.bankFileAvailable === true && b.bankFileGenerated !== true && FILE_STEP_WPS.has(wpsOf(b));
}

/** The batch's file counts for "Paid / Exported". Without the flags (older API, other companies) a generated WPS file counts, as before. */
function hasExportedFile(b: PipelineBatch): boolean {
  return EXPORTED_WPS.has(wpsOf(b)) && !onlyRegister(b);
}

function fileKindOf(b: PipelineBatch): BatchFileKind | undefined {
  // An older API sends no flags: say only what its label said.
  if (typeof b.bankFileGenerated !== 'boolean') return undefined;
  if (b.bankFileGenerated) return 'bank_file';
  // A Saudi company's generated XML is the internal register even where the bank export is switched off.
  return b.bankFileAvailable || b.isSaudi ? 'register' : undefined;
}

/**
 * The bank file exists but the bank has not confirmed every payment: the batch lists payments still unconfirmed and it has
 * not been settled (Paid / Reconciled). Null when the API sent no confirmation facts (an older one): the file alone then
 * completes the stage, as before. The counts come from what the list carries, the unconfirmed payments and the share the
 * bank has spoken about: total = unconfirmed / (1 - coverage) and confirmed = total - unconfirmed.
 */
export function awaitingBankConfirmation(b: PipelineBatch): Reason | null {
  const unconfirmed = b.unconfirmedPayments?.count;
  if (typeof unconfirmed !== 'number' || unconfirmed <= 0) return null;
  if (wpsOf(b) === 'Paid' || wpsOf(b) === 'Reconciled') return null;
  const coverage = b.confirmationCoverage;
  if (typeof coverage !== 'number' || !Number.isFinite(coverage) || coverage < 0) return { id: 'awaiting_bank_confirmation' };
  const total = Math.round(unconfirmed / (1 - Math.min(coverage, 0.9999)));
  return { id: 'awaiting_bank_confirmation', confirmed: Math.max(0, total - unconfirmed), total };
}

const newestFirst = (a: PipelineBatch, b: PipelineBatch) =>
  (b.wpsStatusChangedAtUtc ?? b.createdAtUtc).localeCompare(a.wpsStatusChangedAtUtc ?? a.createdAtUtc);

export function exportedBatch<B extends PipelineBatch>(batches: B[] | null): B | null {
  if (!batches) return null;
  return batches.filter(hasExportedFile).sort(newestFirst)[0] ?? null;
}

/** Where a status sits: 0 draft, 1 processed (incl. payroll review), 2 approved, 3 locked, 4 paid; -1 unknown/voided. */
const phaseOf = (status: string): number => (
  status === 'Draft' ? 0 : status === 'Processed' || status === 'PendingFinanceReview' ? 1
    : status === 'Approved' ? 2 : status === 'Locked' ? 3 : status === 'Paid' ? 4 : -1);

export function derivePipeline(input: PipelineInput): PipelineModel {
  const { run, processing, openErrors, approvals, batches, can, prechecks } = input;
  const status = run.status;
  const phase = phaseOf(status);
  const voided = status === 'Voided';
  const active = isProcessingActive(processing?.status);
  const failed = processing?.status === 'Failed' && status === 'Draft';

  const stages: PipelineStage[] = STAGE_ORDER.map(key => ({ key, state: 'upcoming', whenUtc: null, reason: null, detail: null }));
  const at = (k: StageKey) => stages[STAGE_ORDER.indexOf(k)];
  let next: NextAction = { kind: 'none', label: 'none', disabled: false };
  let nextReason: Reason | null = null;

  if (phase < 0) {
    if (voided) stages.forEach(s => { s.reason = { id: 'voided' }; });
    return { stages, focus: null, next, nextReason, voided, unknownStatus: !voided, payOut: null };
  }

  // Draft: the run exists. Always behind us; what happens to a Draft run is the Pre-checks stage.
  at('draft').state = 'done';
  at('draft').whenUtc = run.createdAtUtc;

  // Pre-checks (only meaningful before Process)
  const pre = at('prechecks');
  if (phase > 0 || active) pre.state = 'done';
  else if (failed) pre.state = 'done';
  else if (prechecks && prechecks.staleAttendanceDays !== null && prechecks.staleAttendanceDays > 0) {
    pre.state = 'blocked'; pre.reason = { id: 'attendance_stale', count: prechecks.staleAttendanceDays };
  } else {
    pre.state = 'current';
    if (!prechecks || (prechecks.staleAttendanceDays === null && prechecks.employeesBlockedFromPayment === null)) pre.reason = { id: 'prechecks_unknown' };
    else if ((prechecks.employeesBlockedFromPayment ?? 0) > 0) pre.reason = { id: 'payment_gaps', count: prechecks.employeesBlockedFromPayment ?? 0 };
  }

  // Processed
  const processed = at('processed');
  if (phase > 0) { processed.state = 'done'; processed.whenUtc = run.processedAtUtc; }
  else if (active) { processed.state = 'current'; processed.reason = { id: 'processing' }; }
  else if (failed) { processed.state = 'blocked'; processed.reason = { id: 'failed', message: processing?.errorMessage ?? undefined }; }
  else processed.reason = { id: 'needs_process' };

  // Validated
  const validated = at('validated');
  if (phase >= 2) validated.state = 'done';
  else if (phase === 1) {
    if (openErrors === null) { validated.state = 'blocked'; validated.reason = { id: 'validation_unknown' }; }
    else if (openErrors > 0) { validated.state = 'blocked'; validated.reason = { id: 'errors_open', count: openErrors }; }
    else validated.state = 'done';
  } else validated.reason = { id: 'needs_process' };
  if (validated.state === 'done' && input.overrideCount > 0) validated.detail = { id: 'overrides', count: input.overrideCount };

  // Review: the stop point between "the numbers are valid" and "approve them".
  const review = at('review');
  if (phase >= 2) review.state = 'done';
  else if (phase === 1 && validated.state === 'done') {
    if (input.reviewedAtUtc) { review.state = 'done'; review.whenUtc = input.reviewedAtUtc; review.detail = { id: 'signed_off', name: input.reviewedByName ?? null }; }
    else if (input.reviewApplicable === false) { review.state = 'done'; review.detail = { id: 'review_not_required' }; }
    else { review.state = 'current'; review.reason = { id: 'needs_review' }; }
  } else review.reason = { id: phase === 0 ? 'needs_process' : 'needs_validation' };

  // Approved
  const approved = at('approved');
  if (phase >= 2) {
    approved.state = 'done';
    approved.whenUtc = latestApproval(approvals, 'FinanceReview')?.decidedAtUtc ?? null;
  } else if (phase === 1 && review.state === 'done') {
    approved.state = can.approve ? 'current' : 'blocked';
    if (!can.approve) approved.reason = { id: 'no_permission_approve' };
    else if (status === 'PendingFinanceReview') approved.reason = { id: 'needs_finance' };
    const payrollReview = latestApproval(approvals, 'PayrollReview');
    if (status === 'PendingFinanceReview' && payrollReview) { approved.whenUtc = payrollReview.decidedAtUtc; approved.detail = { id: 'payroll_review' }; }
  } else approved.reason = { id: phase === 0 ? 'needs_process' : validated.state === 'done' ? 'needs_review' : 'needs_validation' };

  // Locked
  const locked = at('locked');
  if (phase >= 3) { locked.state = 'done'; locked.whenUtc = run.lockedAtUtc; }
  else if (phase === 2) {
    if (openErrors === null) { locked.state = 'blocked'; locked.reason = { id: 'validation_unknown' }; }
    else if (openErrors > 0) { locked.state = 'blocked'; locked.reason = { id: 'errors_open', count: openErrors }; }
    else if (!can.lock) { locked.state = 'blocked'; locked.reason = { id: 'no_permission_lock' }; }
    else if (!input.currencyConfirmed) { locked.state = 'blocked'; locked.reason = { id: 'currency_unconfirmed' }; }
    else locked.state = 'current';
  } else locked.reason = { id: phase === 0 ? 'needs_process' : 'needs_approval' };

  // Pay out: one reading for the whole page (lib/payOut.ts).
  const paid = at('paid');
  const payOut = phase >= 3 ? derivePayOut({
    run, batches,
    can: { create: false, wps: false, settle: false, setup: false, ...(input.payOut?.can ?? {}) },
    openBatchId: input.payOut?.openBatchId ?? null,
    exportFacts: input.payOut?.exportFacts ?? null,
    downloaded: input.payOut?.downloaded ?? false,
    records: input.payOut?.records ?? null,
  }) : null;
  if (payOut) {
    paid.state = payOutStageState(payOut);
    if (paid.state !== 'done') paid.reason = { id: 'pay_out' };
    const batch = batches?.find(b => b.id === payOut.batchId) ?? null;
    // The batch is named once there is a file to name (or money settled); before that "generated" would be a lie.
    if (batch && payOut.status !== 'no_batch' && payOut.status !== 'setup' && payOut.status !== 'no_file' && payOut.status !== 'attention') {
      paid.whenUtc = batch.wpsStatusChangedAtUtc ?? batch.createdAtUtc;
      const file = fileKindOf(batch);
      paid.detail = {
        id: 'batch', label: batch.wpsStatusLabel ?? wpsOf(batch), number: batch.batchNumber,
        ...(file ? { file, statusAfterFile: !FILE_STEP_WPS.has(wpsOf(batch)) } : {}),
      };
    }
  } else paid.reason = { id: phase === 0 ? 'needs_process' : 'needs_lock' };

  const focusStage = stages.find(s => s.state === 'current' || s.state === 'blocked') ?? null;
  // A run whose last stage is done has nothing left to focus on; leave the end highlighted.
  const focus: StageKey | null = focusStage?.key ?? (paid.state === 'done' ? 'paid' : null);

  switch (focusStage?.key) {
    case 'prechecks':
      if (pre.state === 'blocked') {
        next = { kind: 'goto', label: 'review_attendance', tab: 'validation', disabled: false };
        nextReason = pre.reason;
      } else {
        next = { kind: 'process', label: 'process', disabled: !can.process };
        nextReason = can.process ? pre.reason : { id: 'no_permission_process' };
      }
      break;
    case 'processed':
      if (failed) { next = { kind: 'retry', label: 'retry', disabled: !can.process }; nextReason = can.process ? null : { id: 'no_permission_process' }; }
      else { next = { kind: 'none', label: 'none', disabled: true }; nextReason = { id: 'processing' }; }
      break;
    case 'validated':
      next = { kind: 'goto', label: 'review_validation', tab: 'validation', disabled: false };
      nextReason = validated.reason;
      break;
    case 'review':
      next = { kind: 'review', label: 'review', disabled: false };
      nextReason = review.reason;
      break;
    case 'approved':
      next = { kind: 'goto', label: 'approve', tab: 'approvals', disabled: !can.approve };
      nextReason = approved.reason;
      break;
    case 'locked':
      if (locked.state === 'current') next = { kind: 'lock', label: 'lock', disabled: false };
      else if (locked.reason?.id === 'errors_open') {
        // Open errors are cured on the Validation tab, not by waiting.
        next = { kind: 'goto', label: 'review_validation', tab: 'validation', disabled: false };
        nextReason = locked.reason;
      } else { next = { kind: 'lock', label: 'lock', disabled: true }; nextReason = locked.reason; }
      break;
    case 'paid':
      // Before the bank has the file the work is on Bank / WPS; after it, the bank's answers are recorded in Payment Tracking.
      next = payOut && (payOut.status === 'submitted' || payOut.status === 'confirmed')
        ? { kind: 'goto', label: 'confirm_bank', tab: 'payment-tracking', disabled: false }
        : { kind: 'goto', label: 'bank', tab: 'bank-wps', disabled: false };
      nextReason = paid.reason;
      break;
    default: break;
  }
  return { stages, focus, next, nextReason, voided, unknownStatus: false, payOut };
}

// ── Comparison with the previous run ───────────────────────────────────────────

type Comparable = Pick<PayrollRun, 'id' | 'companyId' | 'year' | 'month' | 'status' | 'runType'>;

/**
 * The run this one should be read against: the latest EARLIER period for the same company and the same
 * run type that actually carries totals (not Draft, not Voided). null when there is none.
 */
export function previousRunFor<T extends Comparable>(run: Comparable, runs: T[]): T | null {
  const period = (r: Comparable) => r.year * 12 + r.month;
  return runs
    .filter(r => r.id !== run.id && r.companyId === run.companyId && r.runType === run.runType
      && r.status !== 'Draft' && r.status !== 'Voided' && period(r) < period(run))
    .sort((a, b) => period(b) - period(a))[0] ?? null;
}

export interface Change { delta: number; percent: number | null }
/** percent is null when the previous figure is zero (a percentage of nothing is not a number). */
export function changeBetween(current: number, previous: number): Change {
  const delta = current - previous;
  return { delta, percent: previous === 0 ? null : (delta / Math.abs(previous)) * 100 };
}

// ── Deep link: ?tab=<tab>&run=<runId> ─────────────────────────────────────────

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export interface DeepLink { tab: string | null; runId: string | null }

/** Unknown tabs and malformed run ids come back null, never thrown and never half-applied. */
export function parseDeepLink(params: { get(name: string): string | null } | null | undefined, validTabs: readonly string[]): DeepLink {
  const tab = params?.get('tab')?.trim() ?? '';
  const run = params?.get('run')?.trim() ?? '';
  return { tab: validTabs.includes(tab) ? tab : null, runId: GUID.test(run) ? run.toLowerCase() : null };
}

/** The query string for the selection, keeping every other parameter. The default tab is left out only when no run is selected. */
export function withDeepLink(current: string, link: DeepLink, defaultTab: string): string {
  const next = new URLSearchParams(current);
  if (link.tab && (link.tab !== defaultTab || link.runId)) next.set('tab', link.tab); else next.delete('tab');
  if (link.runId) next.set('run', link.runId); else next.delete('run');
  return next.toString();
}

// ── Polling ────────────────────────────────────────────────────────────────────

export const POLL_VISIBLE_MS = 2_000;
export const POLL_HIDDEN_MS = 15_000;
/** Poll fast while the tab is in front, back off while it is hidden. */
export const pollDelay = (hidden: boolean): number => (hidden ? POLL_HIDDEN_MS : POLL_VISIBLE_MS);

// ── Which runs need someone ────────────────────────────────────────────────────

export type AttentionKind = 'process' | 'approve' | 'finance' | 'lock' | 'export';

/**
 * What a run is waiting for, from its status and its payment batches alone. Per-run validation counts
 * are not part of the run list, so a run held back by open errors still reads as waiting for its stage.
 * null = nothing is waiting on a person (paid, voided, or exported).
 */
export function attentionFor(run: Pick<PayrollRun, 'id' | 'status'>, batches: PipelineBatch[] | null): AttentionKind | null {
  switch (run.status) {
    case 'Draft': return 'process';
    case 'Processed': return 'approve';
    case 'PendingFinanceReview': return 'finance';
    case 'Approved': return 'lock';
    case 'Locked':
      // Without the batch list we cannot say the file was made, and must not say it was not.
      if (!batches) return null;
      return payOutNeedsFile(payOutForRun(run, batches)) ? 'export' : null;
    default: return null;
  }
}

/** The run's Pay out from the batch list alone (the run picker's options for runs other than the open one). */
export function payOutForRun(run: Pick<PayrollRun, 'id' | 'status'>, batches: PipelineBatch[] | null, extra: Partial<Omit<PayOutInput, 'run' | 'batches'>> = {}): PayOut {
  return derivePayOut({
    run, batches: batches ? batches.filter(b => b.payrollRunId === run.id) : null,
    can: { create: false, wps: false, settle: false, setup: false, ...(extra.can ?? {}) },
    openBatchId: extra.openBatchId ?? null, exportFacts: extra.exportFacts ?? null, downloaded: extra.downloaded ?? false, records: extra.records ?? null,
  });
}

const ATTENTION_RANK: Record<AttentionKind, number> = { process: 0, approve: 1, finance: 2, lock: 3, export: 4 };
/** Oldest period first; within a period, the run furthest along first. */
export function sortByAttention<T extends Pick<PayrollRun, 'year' | 'month'> & { attention: AttentionKind }>(items: T[]): T[] {
  return [...items].sort((a, b) => (a.year * 12 + a.month) - (b.year * 12 + b.month) || ATTENTION_RANK[b.attention] - ATTENTION_RANK[a.attention]);
}

// ── Review: employees with issues first ────────────────────────────────────────

export interface EmployeeIssues { employeeId: number; errors: number; warnings: number; messages: string[]; /** The same findings with their codes, so a screen can show a plain sentence per code. */ findings: Array<{ code: string; message: string }> }

/** Open validation results grouped by employee; errors first, then warnings, then most results. Run-level rows (no employee) are not here. */
export function employeesWithIssues(results: Array<{ employeeId: number | null; severity: string; message: string; isResolved: boolean; code?: string }>): EmployeeIssues[] {
  const by = new Map<number, EmployeeIssues>();
  for (const r of results) {
    if (r.isResolved || r.employeeId === null) continue;
    const row = by.get(r.employeeId) ?? { employeeId: r.employeeId, errors: 0, warnings: 0, messages: [], findings: [] };
    if (r.severity === 'Error') row.errors++; else row.warnings++;
    if (row.messages.length < 3 && !row.messages.includes(r.message)) { row.messages.push(r.message); row.findings.push({ code: r.code ?? '', message: r.message }); }
    by.set(r.employeeId, row);
  }
  return [...by.values()].sort((a, b) => b.errors - a.errors || b.warnings - a.warnings || a.employeeId - b.employeeId);
}
