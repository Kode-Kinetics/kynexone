'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, CheckCircle2, Circle, CircleDot } from 'lucide-react';
import { type PayrollRun } from '../../api/payroll';
import { isProcessingActive, startRunProcessing } from '../../api/payrollProcessing';
import { useAuth } from '../../contexts/AuthContext';
import { useFormat } from '../../hooks/useFormat';
import { useLockWithSummary } from '../../hooks/useLockWithSummary';
import { usePayrollCompanies } from '../../hooks/usePayrollCompanies';
import { useRunPipelineData } from '../../hooks/useRunPipelineData';
import { useRunProcessing } from '../../hooks/useRunProcessing';
import { useT } from '../../hooks/useT';
import { resolvePayrollRunCurrency } from '../../lib/payrollCurrency';
import {
  attentionFor, derivePipeline, sortByAttention,
  type AttentionKind, type NextAction, type PipelineStage, type Reason, type StageKey,
} from '../../lib/payrollPipeline';
import { requestFailureReason } from '../../lib/requestFailure';
import { EnumLabel } from '../EnumLabel';
import { Button } from '../ui/Button';
import { usePeriodLabel } from './LockSummary';
import { RunReviewPanel } from './RunReviewPanel';

const reviewStorageKey = (run: Pick<PayrollRun, 'id' | 'processedAtUtc'>) => `kynexone.payrollReview.${run.id}.${run.processedAtUtc ?? ''}`;
const readReviewed = (run: Pick<PayrollRun, 'id' | 'processedAtUtc'>): string | null => {
  try { return window.localStorage.getItem(reviewStorageKey(run)); } catch { return null; }
};

/** The tab each stage's name opens (the review stage opens its own panel instead). */
const STAGE_TAB: Record<Exclude<StageKey, 'review'>, string> = {
  draft: 'runs', prechecks: 'validation', processed: 'runs', validated: 'validation', approved: 'approvals', locked: 'runs', paid: 'bank-wps',
};

/**
 * The selected payroll run as the centre of the page: its stages in order, where it is, why it cannot
 * move on when it cannot, and the one thing to do next. The tabs below stay as the detailed views.
 */
export function RunPipeline({ runId, onSelectRun, onNavigate, version, onRunsChanged }: {
  runId: string | null;
  onSelectRun: (id: string | null) => void;
  onNavigate: (tab: string) => void;
  /** Bumped by the page whenever a run changed elsewhere (a tab processed or locked one). */
  version: number;
  onRunsChanged: () => void;
}) {
  const t = useT();
  const fmt = useFormat();
  const period = usePeriodLabel();
  const { hasPermission } = useAuth();
  const data = useRunPipelineData(runId, version);
  const { run } = data;
  const processing = useRunProcessing(runId, version);
  const { companies, state: companiesState } = usePayrollCompanies();
  const { requestLock, lockDialog, lockingRunId } = useLockWithSummary();

  const [actionError, setActionError] = useState('');
  const [staleAttendance, setStaleAttendance] = useState(false);
  const [starting, setStarting] = useState(false);
  const [reviewOpen, setReviewOpen] = useState(false);
  const [reviewedAt, setReviewedAt] = useState<string | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const [missingRun, setMissingRun] = useState(false);
  const [showAllAttention, setShowAllAttention] = useState(false);
  const reviewRef = useRef<HTMLDivElement>(null);

  const can = { process: hasPermission('payroll.write'), approve: hasPermission('payroll.approve'), lock: hasPermission('payroll.lock') };
  const currency = run ? resolvePayrollRunCurrency(run, companies, companiesState) : null;
  const currencyCode = currency?.status === 'resolved' ? currency.currency : null;

  // The run named in a link that does not exist (or is not visible to this user): say so, and select nothing.
  useEffect(() => {
    if (data.runsState === 'ready' && runId && !run) { setMissingRun(true); onSelectRun(null); }
    else if (runId) setMissingRun(false);
  }, [data.runsState, runId, run, onSelectRun]);

  useEffect(() => { setActionError(''); setStaleAttendance(false); setReviewOpen(false); }, [runId]);
  useEffect(() => { setReviewedAt(run ? readReviewed(run) : null); }, [run?.id, run?.processedAtUtc]); // eslint-disable-line react-hooks/exhaustive-deps

  // The job finished but the run on screen is still a Draft: read it again, once per job.
  const handledJob = useRef<string | null>(null);
  useEffect(() => {
    const s = processing.status;
    if (s?.status === 'Succeeded' && s.jobId && run?.status === 'Draft' && handledJob.current !== s.jobId) {
      handledJob.current = s.jobId;
      onRunsChanged();
    }
  }, [processing.status, run?.status, onRunsChanged]);

  // Stale attendance now refuses inside the queued job, so it arrives as the job's error code, not a refusal body.
  useEffect(() => {
    const s = processing.status;
    if (s?.status === 'Failed' && (s.errorCode === 'attendance_impact_stale' || s.errorCode === 'ERR_ATTENDANCE_IMPACT_STALE')) setStaleAttendance(true);
  }, [processing.status]);

  const batches = useMemo(() => (data.allBatches && runId ? data.allBatches.filter(b => b.payrollRunId === runId) : null), [data.allBatches, runId]);

  const model = useMemo(() => (run ? derivePipeline({
    run,
    processing: processing.status,
    openErrors: data.openErrors,
    overrideCount: data.overrideCount,
    approvals: data.approvals,
    batches,
    prechecks: data.prechecks,
    reviewedAtUtc: reviewedAt,
    can,
    currencyConfirmed: currency === null || currency.status !== 'unavailable',
  }) : null), [run, processing.status, data.openErrors, data.overrideCount, data.approvals, batches, data.prechecks, reviewedAt, can.process, can.approve, can.lock, currency]); // eslint-disable-line react-hooks/exhaustive-deps

  // Polite announcements: when the stage that needs attention changes, or the job changes state. Never per progress tick.
  const announceKey = model && run ? `${run.id}|${model.focus}|${model.stages.find(s => s.key === model.focus)?.state}|${processing.status?.status ?? ''}` : '';
  const lastKey = useRef('');
  useEffect(() => {
    if (!model || !run) { lastKey.current = ''; return; }
    const previous = lastKey.current;
    lastKey.current = announceKey;
    if (!previous || previous.split('|')[0] !== run.id || previous === announceKey) return;
    const stage = model.stages.find(s => s.key === model.focus);
    if (!stage) return;
    const failure = processing.status?.status === 'Failed' && processing.status.errorMessage ? ` ${processing.status.errorMessage}` : '';
    setAnnouncement(t('Payroll stage {stage}: {state}.', { stage: stageName(t, stage.key), state: stateName(t, stage.state) }) + failure);
  }, [announceKey]); // eslint-disable-line react-hooks/exhaustive-deps

  const markReviewed = () => {
    if (!run) return;
    const at = new Date().toISOString();
    try { window.localStorage.setItem(reviewStorageKey(run), at); } catch { /* private mode: the review still counts for this visit */ }
    setReviewedAt(at);
  };

  const process = async () => {
    if (!runId || starting) return;
    setActionError(''); setStaleAttendance(false); setStarting(true);
    try {
      await startRunProcessing(runId);
      processing.refresh();
      onRunsChanged();
    } catch (e) {
      const body = (e as { response?: { data?: { message?: string; code?: string } } })?.response?.data;
      if (body?.code === 'ERR_ATTENDANCE_IMPACT_STALE') setStaleAttendance(true);
      setActionError(body?.message ?? t('Processing could not be started. {reason}', { reason: requestFailureReason(e) }));
    } finally { setStarting(false); }
  };

  const lock = async () => {
    if (!runId) return;
    setActionError('');
    const outcome = await requestLock(runId);
    if (outcome.status === 'locked') { onRunsChanged(); setAnnouncement(t('The run is locked.')); }
    else if (outcome.status === 'failed') setActionError(outcome.message);
  };

  const perform = (next: NextAction) => {
    if (next.disabled) return;
    if (next.kind === 'process' || next.kind === 'retry') void process();
    else if (next.kind === 'lock') void lock();
    else if (next.kind === 'review') { setReviewOpen(true); requestAnimationFrame(() => reviewRef.current?.focus()); }
    else if (next.kind === 'goto' && next.tab) onNavigate(next.tab);
  };

  // ── Runs that need someone ───────────────────────────────────────────────────
  const attention = useMemo(() => sortByAttention(
    data.runs.flatMap(r => { const a = attentionFor(r, data.allBatches); return a ? [{ ...r, attention: a }] : []; }),
  ), [data.runs, data.allBatches]);
  const attentionIds = new Set(attention.map(a => a.id));
  const otherRuns = data.runs.filter(r => !attentionIds.has(r.id));
  const visibleAttention = showAllAttention ? attention : attention.slice(0, 5);

  const companyName = (r: PayrollRun) => {
    const c = companies.find(x => x.id === r.companyId);
    return c?.tradeName || c?.name || t('Whole group');
  };
  // The company is named only where two runs share a period, so a single-company tenant never sees it.
  const runLabel = (r: PayrollRun) => (data.runs.some(o => o.id !== r.id && o.year === r.year && o.month === r.month && o.companyId !== r.companyId)
    ? `${period(r)} · ${companyName(r)}` : period(r));

  const focusStage = model?.stages.find(s => s.key === model.focus) ?? null;
  const processingStatus = processing.status;
  const busy = starting || (run !== null && lockingRunId === run.id);

  return (
    <section aria-labelledby="run-pipeline-title" className="surface space-y-4 p-4" data-testid="run-pipeline">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h2 id="run-pipeline-title" className="text-h3 font-semibold text-fg">{t('Payroll run')}</h2>
          <p className="text-body-sm text-fg-muted">{t('Pick a run to see where it is and what to do next. The tabs below hold the detail.')}</p>
        </div>
        <div className="flex items-center gap-2">
          <label htmlFor="pipeline-run-picker" className="text-body-sm text-fg-muted">{t('Run')}</label>
          <select
            id="pipeline-run-picker"
            className="select min-w-[14rem]"
            value={runId ?? ''}
            onChange={e => onSelectRun(e.target.value || null)}
            disabled={data.runsState === 'loading'}
          >
            <option value="">{t('Choose a run…')}</option>
            {attention.length > 0 && (
              <optgroup label={t('Needs attention')}>
                {attention.map(r => <option key={r.id} value={r.id}>{runLabel(r)} — {attentionText(t, r.attention)}</option>)}
              </optgroup>
            )}
            {otherRuns.length > 0 && (
              <optgroup label={t('Other runs')}>
                {otherRuns.map(r => <option key={r.id} value={r.id}>{runLabel(r)} — {statusText(t, r.status)}</option>)}
              </optgroup>
            )}
          </select>
        </div>
      </div>

      {data.runsState === 'failed' && (
        <p role="alert" className="text-body-sm text-danger-fg" data-testid="pipeline-runs-failed">
          {t('The payroll runs could not be loaded.')}{' '}
          <button type="button" className="underline" onClick={onRunsChanged}>{t('Retry')}</button>
        </p>
      )}
      {missingRun && <p role="status" className="text-body-sm text-warning-fg" data-testid="pipeline-run-missing">{t('The run in this link was not found, so none is selected.')}</p>}

      {!run && data.runsState === 'ready' && attention.length > 0 && (
        <div data-testid="pipeline-attention">
          <h3 className="text-body font-semibold text-fg">{t('{count, plural, one {# run needs attention} other {# runs need attention}}', { count: attention.length })}</h3>
          <ul className="mt-2 divide-y divide-line rounded-lg border border-line">
            {visibleAttention.map(r => (
              <li key={r.id} className="flex flex-wrap items-center justify-between gap-2 px-3 py-2">
                <span className="text-body text-fg">{runLabel(r)}</span>
                <span className="flex items-center gap-2">
                  <span className="badge badge-warning">{attentionText(t, r.attention)}</span>
                  <Button size="sm" onClick={() => onSelectRun(r.id)}>{t('Open')}</Button>
                </span>
              </li>
            ))}
          </ul>
          {attention.length > 5 && (
            <Button size="sm" className="mt-2" aria-expanded={showAllAttention} onClick={() => setShowAllAttention(v => !v)}>
              {showAllAttention ? t('Show fewer') : t('Show all {count} runs', { count: fmt.integer(attention.length) })}
            </Button>
          )}
        </div>
      )}
      {!run && data.runsState === 'ready' && data.runs.length === 0 && <p className="text-body-sm text-fg-muted">{t('There are no payroll runs yet. Create one on the Payroll Runs tab.')}</p>}

      {run && model && (
        <>
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-h4 font-semibold text-fg">{runLabel(run)}</span>
            <span className="badge badge-neutral"><EnumLabel enum="PayrollRunStatus" value={run.status} /></span>
            {currencyCode && <span className="text-body-sm text-fg-muted"><bdi>{currencyCode}</bdi></span>}
          </div>

          {model.voided && <p role="status" className="rounded-lg border border-line bg-surface-sunken px-3 py-2 text-body-sm text-fg-muted">{t('This run was voided. It has no further stages.')}</p>}
          {model.unknownStatus && <p role="status" className="rounded-lg border border-line bg-surface-sunken px-3 py-2 text-body-sm text-fg-muted">{t('This run has a status the pipeline does not know, so no stage is highlighted.')}</p>}

          <ol aria-label={t('Stages of payroll run {period}', { period: period(run) })} className="grid grid-cols-2 gap-2 md:grid-cols-4 xl:grid-cols-8" data-testid="pipeline-stages">
            {model.stages.map(stage => (
              <StageItem
                key={stage.key}
                stage={stage}
                isFocus={model.focus === stage.key}
                onOpen={() => (stage.key === 'review' ? setReviewOpen(v => !v) : onNavigate(STAGE_TAB[stage.key]))}
                reviewOpen={reviewOpen}
                progress={stage.key === 'processed' && isProcessingActive(processingStatus?.status) ? processingStatus : null}
                fmt={fmt}
              />
            ))}
          </ol>

          {processingStatus?.status === 'Failed' && run.status === 'Draft' && (
            <p role="alert" className="text-body-sm text-danger-fg" data-testid="pipeline-processing-failed">
              {processingStatus.errorMessage ?? t('Processing failed. Retry, or check the pre-checks.')}
            </p>
          )}
          {processing.unavailable && isProcessingActive(processingStatus?.status) && (
            <p role="status" className="text-body-sm text-warning-fg">{t('Progress could not be refreshed just now. It will keep trying.')}</p>
          )}

          <div className="flex flex-wrap items-center gap-3 rounded-lg border border-line bg-surface-sunken p-3" data-testid="pipeline-next">
            <div className="min-w-0 flex-1">
              <p className="text-meta text-fg-muted">{t('Next step')}</p>
              <p className="text-body font-semibold text-fg" data-testid="pipeline-next-title">{nextTitle(t, model.next, focusStage)}</p>
              {model.nextReason && <p className="mt-0.5 text-body-sm text-fg-muted" id="pipeline-next-reason" data-testid="pipeline-next-reason">{reasonText(t, model.nextReason)}</p>}
            </div>
            {model.next.label !== 'none' && (
              <Button
                variant="primary"
                disabled={model.next.disabled}
                busy={busy}
                aria-describedby={model.nextReason ? 'pipeline-next-reason' : undefined}
                onClick={() => perform(model.next)}
                data-testid="pipeline-next-action"
              >
                {actionLabel(t, model.next, busy)}
              </Button>
            )}
          </div>

          {actionError && (
            <div role="alert" className="flex flex-wrap items-center gap-3 rounded-lg border border-danger/30 bg-danger-soft px-3 py-2 text-body-sm text-danger-fg" data-testid="pipeline-action-error">
              <AlertTriangle className="h-4 w-4 shrink-0" aria-hidden="true" />
              <span className="flex-1">{actionError}</span>
              {staleAttendance && <Button size="sm" onClick={() => onNavigate('validation')}>{t('Review attendance and recompute')}</Button>}
            </div>
          )}

          {(reviewOpen || model.focus === 'review') && (
            <div ref={reviewRef} tabIndex={-1} className="outline-none focus-visible:ring-2 focus-visible:ring-primary">
              <RunReviewPanel
                run={run}
                results={data.results}
                currency={currencyCode}
                reviewedAtUtc={reviewedAt}
                onComplete={markReviewed}
                onOpenValidation={() => onNavigate('validation')}
              />
            </div>
          )}
        </>
      )}

      <div role="status" aria-live="polite" className="sr-only" data-testid="pipeline-live">{announcement}</div>
      {lockDialog}
    </section>
  );
}

// ── One stage ──────────────────────────────────────────────────────────────────

function StageItem({ stage, isFocus, onOpen, reviewOpen, progress, fmt }: {
  stage: PipelineStage;
  isFocus: boolean;
  onOpen: () => void;
  reviewOpen: boolean;
  progress: { processedEmployees: number | null; totalEmployees: number | null; status: string } | null;
  fmt: ReturnType<typeof useFormat>;
}) {
  const t = useT();
  const tone = stage.state === 'done' ? 'border-line bg-success-soft text-success-fg'
    : stage.state === 'blocked' ? 'border-danger bg-danger-soft text-danger-fg'
    : stage.state === 'current' ? 'border-primary bg-primary-soft text-fg'
    : 'border-line bg-surface text-fg-muted';
  const Icon = stage.state === 'done' ? CheckCircle2 : stage.state === 'blocked' ? AlertTriangle : stage.state === 'current' ? CircleDot : Circle;
  const total = progress?.totalEmployees ?? null;
  const done = progress?.processedEmployees ?? null;

  return (
    <li
      aria-current={isFocus ? 'step' : undefined}
      data-stage={stage.key}
      data-state={stage.state}
      className={`flex min-w-0 flex-col gap-1 rounded-lg border p-2 ${tone} ${isFocus ? 'ring-2 ring-primary' : ''}`}
    >
      <div className="flex items-center gap-1.5">
        <Icon className="h-4 w-4 shrink-0" aria-hidden="true" />
        <button
          type="button"
          onClick={onOpen}
          aria-expanded={stage.key === 'review' ? reviewOpen : undefined}
          className="min-w-0 text-start text-body-sm font-semibold hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
        >
          {stageName(t, stage.key)}
        </button>
      </div>
      <p className="text-meta">{stateName(t, stage.state)}</p>
      {stage.whenUtc && <p className="text-meta"><bdi>{fmt.dateTime(stage.whenUtc)}</bdi></p>}
      {stage.detail && <p className="text-meta">{detailText(t, stage.detail)}</p>}
      {progress && (
        <div data-testid="pipeline-progress">
          {total !== null && done !== null && total > 0 ? (
            <>
              <div
                role="progressbar"
                aria-label={t('Processing progress')}
                aria-valuemin={0}
                aria-valuemax={total}
                aria-valuenow={Math.min(done, total)}
                aria-valuetext={t('{processed} of {total} employees', { processed: fmt.integer(done), total: fmt.integer(total) })}
                className="h-1.5 w-full overflow-hidden rounded-full bg-line"
              >
                <div className="h-full bg-primary" style={{ width: `${Math.min(100, Math.round((done / total) * 100))}%` }} />
              </div>
              <p className="mt-1 text-meta"><bdi>{t('{processed} of {total} employees', { processed: fmt.integer(done), total: fmt.integer(total) })}</bdi></p>
            </>
          ) : (
            <p className="text-meta">{progress.status === 'Queued' ? t('Waiting in the queue') : t('Processing…')}</p>
          )}
        </div>
      )}
      {stage.reason && stage.state !== 'done' && !(progress && stage.reason.id === 'processing') && (
        <p className="text-meta" data-testid={`stage-reason-${stage.key}`}>{reasonText(t, stage.reason)}</p>
      )}
    </li>
  );
}

// ── Words (every one goes through t with a literal key) ───────────────────────

type T = ReturnType<typeof useT>;

function stageName(t: T, key: StageKey): string {
  switch (key) {
    case 'draft': return t('Draft');
    case 'prechecks': return t('Pre-checks');
    case 'processed': return t('Processed');
    case 'validated': return t('Validated');
    case 'review': return t('Review');
    case 'approved': return t('Approved');
    case 'locked': return t('Locked');
    case 'paid': return t('Paid / Exported');
  }
}

function stateName(t: T, state: PipelineStage['state']): string {
  switch (state) {
    case 'done': return t('Done');
    case 'current': return t('Current');
    case 'blocked': return t('Blocked');
    case 'upcoming': return t('Upcoming');
  }
}

function statusText(t: T, status: string): string {
  switch (status) {
    case 'Draft': return t('Draft');
    case 'Processed': return t('Processed');
    case 'Approved': return t('Approved');
    case 'Locked': return t('Locked');
    case 'Paid': return t('Paid');
    case 'Voided': return t('Voided');
    default: return status;
  }
}

function attentionText(t: T, kind: AttentionKind): string {
  switch (kind) {
    case 'process': return t('Ready to process');
    case 'approve': return t('Waiting for approval');
    case 'finance': return t('Waiting for finance approval');
    case 'lock': return t('Ready to lock');
    case 'export': return t('Waiting for the bank file');
  }
}

function detailText(t: T, d: NonNullable<PipelineStage['detail']>): string {
  switch (d.id) {
    case 'batch': return t('Bank file {number}: {label}', { number: d.number, label: d.label });
    case 'payroll_review': return t('Payroll review is done');
    case 'overrides': return t('{count, plural, one {# error cleared by override} other {# errors cleared by override}}', { count: d.count });
    case 'this_device': return t('Marked on this device');
  }
}

function reasonText(t: T, r: Reason): string {
  switch (r.id) {
    case 'failed': return r.message ?? t('Processing failed.');
    case 'processing': return t('Processing is under way. This page updates by itself.');
    case 'errors_open': return t('{count, plural, one {# validation error is open. Resolve or override it on the Validation tab.} other {# validation errors are open. Resolve or override them on the Validation tab.}}', { count: r.count ?? 0 });
    case 'validation_unknown': return t('The validation results could not be read, so this stage cannot be confirmed.');
    case 'attendance_stale': return t('{count, plural, one {# attendance day no longer matches its punches. Process is refused until it is recomputed.} other {# attendance days no longer match their punches. Process is refused until they are recomputed.}}', { count: r.count ?? 0 });
    case 'payment_gaps': return t('{count, plural, one {# employee cannot be paid yet (for example no bank details). Processing still works, but it will raise validation errors for them.} other {# employees cannot be paid yet (for example no bank details). Processing still works, but it will raise validation errors for them.}}', { count: r.count ?? 0 });
    case 'prechecks_unknown': return t('The pre-checks could not be read. You can still process the run.');
    case 'needs_process': return t('Waits for the run to be processed.');
    case 'needs_validation': return t('Waits for the validation errors to be cleared.');
    case 'needs_review': return t('Open the review and mark it complete before approving.');
    case 'needs_approval': return t('Waits for approval.');
    case 'needs_finance': return t('Payroll review is done. A finance approver decides next.');
    case 'needs_lock': return t('Waits for the run to be locked.');
    case 'no_permission_process': return t('You do not have permission to process payroll.');
    case 'no_permission_approve': return t('You do not have permission to approve payroll.');
    case 'no_permission_lock': return t('You do not have permission to lock payroll.');
    case 'currency_unconfirmed': return t('The company currency is not confirmed, so the lock summary cannot name one. Check Setup → Companies.');
    case 'no_batch': return t('No payment batch exists yet. Create one on the Bank / WPS tab.');
    case 'batch_not_exported': return t('A payment batch exists, but its bank file has not been generated yet.');
    case 'voided': return t('Voided.');
  }
}

function nextTitle(t: T, next: NextAction, focus: PipelineStage | null): string {
  switch (next.label) {
    case 'process': return t('Process payroll');
    case 'retry': return t('Retry processing');
    case 'review_validation': return t('Review the validation results');
    case 'review_attendance': return t('Review attendance and recompute');
    case 'review': return t('Review the results');
    case 'approve': return t('Approve the run');
    case 'lock': return t('Lock the run');
    case 'bank': return t('Create the bank file');
    case 'none': return focus && focus.key === 'processed' ? t('Processing…') : t('All stages are complete.');
  }
}

function actionLabel(t: T, next: NextAction, busy: boolean): string {
  switch (next.label) {
    case 'process': return busy ? t('Starting…') : t('Process');
    case 'retry': return busy ? t('Starting…') : t('Retry');
    case 'review_validation': return t('Open Validation');
    case 'review_attendance': return t('Open attendance check');
    case 'review': return t('Open review');
    case 'approve': return t('Go to Approvals');
    case 'lock': return busy ? t('Locking…') : t('Review summary and lock');
    case 'bank': return t('Open Bank / WPS');
    case 'none': return '';
  }
}
