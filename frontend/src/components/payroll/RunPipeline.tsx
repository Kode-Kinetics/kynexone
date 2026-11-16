'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, CheckCircle2, ChevronDown, Circle, CircleDot, Clock, Lock, Send } from 'lucide-react';
import { type PayrollRun } from '../../api/payroll';
import { isProcessingActive, startRunProcessing } from '../../api/payrollProcessing';
import { useAuth } from '../../contexts/AuthContext';
import { useFormat } from '../../hooks/useFormat';
import { useLockWithSummary } from '../../hooks/useLockWithSummary';
import { usePayrollCompanies } from '../../hooks/usePayrollCompanies';
import { useRunPipelineData } from '../../hooks/useRunPipelineData';
import { useRunRecon } from '../../hooks/useRunRecon';
import { useRunProcessing } from '../../hooks/useRunProcessing';
import { useT } from '../../hooks/useT';
import { resolvePayrollRunCurrency } from '../../lib/payrollCurrency';
import {
  attentionFor, derivePipeline, payOutForRun, sortByAttention, STAGE_ORDER,
  type AttentionKind, type NextAction, type PipelineBatch, type PipelineStage, type Reason, type StageKey,
} from '../../lib/payrollPipeline';
import { payOutLabel, payOutNeedsFile, payOutTone, type PayOut, type PayOutInput } from '../../lib/payOut';
import { paymentActionsAllowed } from '../../lib/payrollPaymentAccess';
import { isPeriodOwning, priorRunCandidates, reviewFromSignoff, reviewIsRequired } from '../../lib/payrollRecon';
import { isolateLtr } from '../../lib/bidi';
import { requestFailureReason } from '../../lib/requestFailure';
import { EnumLabel } from '../EnumLabel';
import { Button } from '../ui/Button';
import { usePeriodLabel } from './LockSummary';
import { RunReviewPanel } from './RunReviewPanel';
import { splitRunsByVoided } from '../../lib/runOptions';

const STAGES_PREF_KEY = 'kynexone-payroll-pipeline-stages';

/** The tab each stage's name opens (the review stage opens its own panel instead). */
const STAGE_TAB: Record<Exclude<StageKey, 'review'>, string> = {
  draft: 'runs', prechecks: 'validation', processed: 'runs', validated: 'validation', approved: 'approvals', locked: 'runs', paid: 'bank-wps',
};

/**
 * What the Bank / WPS tab knows about the run's pay out beyond the batch list. The run bar reads the SAME facts as the tab, so its chip,
 * its run option and the tab's steps cannot disagree (lib/payOut.ts).
 */
export interface PayOutContext extends Partial<Omit<PayOutInput, 'run' | 'batches' | 'can'>> {
  runId: string;
  /** The tab's own copy of the run's batch list (fresher than the page's after a file is made). */
  batches: PipelineBatch[] | null;
}

/** The tabs that carry a stage's own next action: on them the run bar does not repeat it (one primary per screen). */
const STAGE_TABS: Partial<Record<StageKey, string[]>> = { paid: ['bank-wps', 'payment-tracking'], approved: ['approvals'] };

const CHIP_TONE: Record<ReturnType<typeof payOutTone>, string> = {
  warning: 'badge-warning', danger: 'badge-danger', neutral: 'badge-neutral', info: 'badge-info', success: 'badge-success',
};

/**
 * The selected payroll run as the centre of the page: its stages in order, where it is, why it cannot
 * move on when it cannot, and the one thing to do next. The tabs below stay as the detailed views.
 */
export function RunPipeline({ runId, onSelectRun, onNavigate, version, onRunsChanged, reviewRequestKey = 0, activeTab, payOutContext = null }: {
  runId: string | null;
  onSelectRun: (id: string | null) => void;
  onNavigate: (tab: string) => void;
  /** Bumped by the page whenever a run changed elsewhere (a tab processed or locked one). */
  version: number;
  onRunsChanged: () => void;
  /** Bumped when something elsewhere (Approve refused for want of a sign-off) sends the user back to the review. */
  reviewRequestKey?: number;
  /** The section on screen: the run bar leaves a stage's next action to the section that owns it. */
  activeTab?: string;
  /** From the Bank / WPS tab while it is open. */
  payOutContext?: PayOutContext | null;
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
  const [priorRunId, setPriorRunId] = useState<string | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const [missingRun, setMissingRun] = useState(false);
  const [showAllAttention, setShowAllAttention] = useState(false);
  const reviewRef = useRef<HTMLDivElement>(null);
  // The stage cards take half a laptop screen. With a run chosen they start folded (the run, its stage and the next step stay in view) and
  // the choice is remembered. Processing shows its progress inside a stage card, so it opens them for as long as it runs.
  const [stagesPref, setStagesPref] = useState<'open' | 'folded' | null>(null);
  useEffect(() => {
    try { const v = window.localStorage.getItem(STAGES_PREF_KEY); setStagesPref(v === 'open' || v === 'folded' ? v : null); } catch { /* storage unavailable: the default stays */ }
  }, []);
  const chooseStages = (next: 'open' | 'folded') => {
    setStagesPref(next);
    try { window.localStorage.setItem(STAGES_PREF_KEY, next); } catch { /* the choice just is not remembered */ }
  };

  const can = { process: hasPermission('payroll.write'), approve: hasPermission('payroll.approve'), lock: hasPermission('payroll.lock') };
  const payCan = {
    create: paymentActionsAllowed(hasPermission, ['create']), wps: paymentActionsAllowed(hasPermission, ['wps']),
    settle: paymentActionsAllowed(hasPermission, ['settle']), setup: hasPermission('payroll.structure_manage'),
  };
  // The Bank / WPS tab's facts apply to the run it has open; any other run is read from the batch list alone.
  const ctx = payOutContext && payOutContext.runId === runId ? payOutContext : null;
  const payOutExtra = (forRun: string | null) => (ctx && forRun === ctx.runId
    ? { can: payCan, openBatchId: ctx.openBatchId ?? null, exportFacts: ctx.exportFacts ?? null, downloaded: ctx.downloaded ?? false, records: ctx.records ?? null }
    : { can: payCan });
  const currency = run ? resolvePayrollRunCurrency(run, companies, companiesState) : null;

  // The run named in a link that does not exist (or is not visible to this user): say so, and select nothing.
  // A run created a moment ago (e.g. "Create off-cycle run" on a final settlement) can be missing from a list read just
  // before it existed, so the list is read once more for that run before it is called missing.
  const recheckedFor = useRef<string | null>(null);
  const { runs: loadedRuns, reload: reloadRuns } = data;
  useEffect(() => {
    if (data.runsState === 'ready' && runId && !run) {
      if (recheckedFor.current !== runId) { recheckedFor.current = runId; reloadRuns(); return; }
      setMissingRun(true); onSelectRun(null);
    }
    else if (runId) setMissingRun(false);
  }, [data.runsState, runId, run, loadedRuns, reloadRuns, onSelectRun]);

  useEffect(() => { setActionError(''); setStaleAttendance(false); setReviewOpen(false); setPriorRunId(null); }, [runId]);

  // The reconciliation is read while the run waits for approval (it decides whether Review is done) and
  // whenever the reviewer opens the stage. Off-cycle runs are not reconciled, so nothing is read for them.
  const periodOwning = isPeriodOwning(run?.runType);
  const awaitingApproval = run?.status === 'Processed' || run?.status === 'PendingFinanceReview';
  const recon = useRunRecon(runId, { enabled: !!run && periodOwning && (awaitingApproval || reviewOpen), priorRunId, version });
  const review = reviewFromSignoff(recon.recon?.signoff);
  // A company can switch the sign-off requirement off: the reconciliation is still shown, but the run is not held at Review.
  // Until the reconciliation has been read the sign-off is taken as required.
  const holdAtReview = reviewIsRequired(run?.runType, recon.recon);
  const signoffRequired = recon.recon?.signoffRequired !== false;

  // Approve was refused for want of a current sign-off: show the review again.
  const handledReviewRequest = useRef(reviewRequestKey);
  useEffect(() => {
    if (reviewRequestKey === handledReviewRequest.current) return;
    handledReviewRequest.current = reviewRequestKey;
    setReviewOpen(true);
    requestAnimationFrame(() => reviewRef.current?.focus());
  }, [reviewRequestKey]);

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

  const batches = useMemo(() => (ctx?.batches ?? (data.allBatches && runId ? data.allBatches.filter(b => b.payrollRunId === runId) : null)), [ctx?.batches, data.allBatches, runId]);

  const model = useMemo(() => (run ? derivePipeline({
    run,
    processing: processing.status,
    openErrors: data.openErrors,
    overrideCount: data.overrideCount,
    approvals: data.approvals,
    batches,
    prechecks: data.prechecks,
    reviewedAtUtc: review?.reviewedAtUtc ?? null,
    reviewedByName: review?.reviewedByName ?? null,
    reviewApplicable: holdAtReview,
    can,
    currencyConfirmed: currency === null || currency.status !== 'unavailable',
    payOut: payOutExtra(run.id),
  }) : null), [run, processing.status, data.openErrors, data.overrideCount, data.approvals, batches, data.prechecks, review?.reviewedAtUtc, review?.reviewedByName, holdAtReview, can.process, can.approve, can.lock, currency, ctx]); // eslint-disable-line react-hooks/exhaustive-deps

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
    data.runs.flatMap(r => {
      // The run the Bank / WPS tab has open is read with the tab's facts, so the list and the chip agree.
      const a: AttentionKind | null = ctx && r.id === ctx.runId && r.status === 'Locked'
        ? (payOutNeedsFile(payOutForRun(r, ctx.batches, payOutExtra(r.id))) ? 'export' : null)
        : attentionFor(r, data.allBatches);
      return a ? [{ ...r, attention: a }] : [];
    }),
  ), [data.runs, data.allBatches, ctx]); // eslint-disable-line react-hooks/exhaustive-deps
  const attentionIds = new Set(attention.map(a => a.id));
  const otherRuns = data.runs.filter(r => !attentionIds.has(r.id));
  // A voided run is not set beside the live ones: it has its own group.
  const { live: liveOtherRuns, voided: voidedRuns } = splitRunsByVoided(otherRuns);
  const visibleAttention = showAllAttention ? attention : attention.slice(0, 5);

  const companyName = (r: PayrollRun) => {
    const c = companies.find(x => x.id === r.companyId);
    return c?.tradeName || c?.name || t('Whole group');
  };
  // The company is named only where two runs share a period, so a single-company tenant never sees it.
  const runLabel = (r: PayrollRun) => (data.runs.some(o => o.id !== r.id && o.year === r.year && o.month === r.month && o.companyId !== r.companyId)
    ? `${period(r)} · ${companyName(r)}` : period(r));
  // A Locked (or Paid) run's option says where its money stands, in the run bar chip's words; the list never says what the page does not.
  const batchesOf = (r: PayrollRun): PipelineBatch[] | null => (ctx && r.id === ctx.runId ? ctx.batches : data.allBatches);
  const runState = (r: PayrollRun): string => (r.status === 'Locked' || r.status === 'Paid'
    ? payOutLabel(t, payOutForRun(r, batchesOf(r), payOutExtra(r.id))) : statusText(t, r.status));
  const attentionLabel = (r: PayrollRun & { attention: AttentionKind }): string => (r.attention === 'export' ? runState(r) : attentionText(t, r.attention));

  const focusStage = model?.stages.find(s => s.key === model.focus) ?? null;
  const processingStatus = processing.status;
  const processingActive = isProcessingActive(processingStatus?.status);
  const stagesOpen = !run || processingActive || (stagesPref ?? 'folded') === 'open';
  const busy = starting || (run !== null && lockingRunId === run.id);
  const payOut: PayOut | null = model?.payOut ?? null;
  // The section on screen owns this stage's next action: the run bar does not repeat it.
  const sectionOwnsNext = !!model?.focus && !!activeTab && (STAGE_TABS[model.focus] ?? []).includes(activeTab);
  const showNext = !!model && model.next.label !== 'none' && !sectionOwnsNext;
  const focusIndex = model?.focus ? STAGE_ORDER.indexOf(model.focus) : -1;
  const chipLabel = payOut && model?.focus === 'paid' ? payOutLabel(t, payOut) : null;

  const picker = (
    <select
      id="pipeline-run-picker"
      className="select h-9 w-full min-w-0 truncate md:w-auto md:min-w-[200px] md:max-w-[320px]"
      value={runId ?? ''}
      title={run ? `${runLabel(run)} — ${runState(run)}` : undefined}
      onChange={e => onSelectRun(e.target.value || null)}
      disabled={data.runsState === 'loading'}
    >
      <option value="">{t('Choose a run…')}</option>
      {attention.length > 0 && (
        <optgroup label={t('Needs attention')}>
          {attention.map(r => <option key={r.id} value={r.id}>{runLabel(r)} — {attentionLabel(r)}</option>)}
        </optgroup>
      )}
      {liveOtherRuns.length > 0 && (
        <optgroup label={t('Other runs')}>
          {liveOtherRuns.map(r => <option key={r.id} value={r.id}>{runLabel(r)} — {runState(r)}</option>)}
        </optgroup>
      )}
      {voidedRuns.length > 0 && (
        <optgroup label={t('Voided runs')}>
          {voidedRuns.map(r => <option key={r.id} value={r.id}>{runLabel(r)} — {statusText(t, r.status)}</option>)}
        </optgroup>
      )}
    </select>
  );

  // "Next: {title}" with the title in its own element (its words are tested and read on their own).
  const [nextBefore, nextAfter] = t('Next: {title}', { title: '\u0001' }).split('\u0001');

  return (
    <>
    {/* The run bar: one row, sticky under the top bar from md. Stage cards, the review and the run list open BELOW it, outside the sticky
        part, so opening them pushes the page instead of covering it. */}
    <section
      aria-labelledby="run-pipeline-title"
      className={`surface z-20 px-3 py-2 ${run ? 'md:sticky md:top-14' : ''}`}
      data-testid="run-pipeline"
      data-run-bar=""
    >
      <h2 id="run-pipeline-title" className="sr-only">{t('Payroll run')}</h2>
      {/* One row from md on every section (items shrink and cut their text; nothing wraps to a second line). */}
      <div className="flex min-h-9 flex-wrap items-center gap-x-3 gap-y-2 md:flex-nowrap">
        <label htmlFor="pipeline-run-picker" className="shrink-0 text-meta text-fg-muted max-md:sr-only">{t('Payroll run')}</label>
        <div className="w-full min-w-0 md:w-auto md:shrink">{picker}</div>
        {run && model && (
          <>
            <span className="badge badge-neutral inline-flex shrink-0 items-center gap-1">
              {run.status === 'Locked' && <Lock className="h-3 w-3" aria-hidden="true" />}
              <EnumLabel enum="PayrollRunStatus" value={run.status} />
            </span>
            {model.focus && (
              <>
                {/* The eight stages at a glance (wide screens only; the words beside it carry the meaning). */}
                <span aria-hidden="true" className="hidden items-center gap-1 min-[1440px]:flex">
                  {model.stages.map((stage, i) => (
                    <span key={stage.key} className={`h-1.5 w-4 rounded-full ${i === focusIndex ? 'bg-primary' : i < focusIndex || stage.state === 'done' ? 'bg-primary/40' : 'bg-line'}`} />
                  ))}
                </span>
                <span className="shrink-0 whitespace-nowrap text-body-sm text-fg-muted" data-testid="pipeline-folded-stage">
                  <span className="max-[1439px]:sr-only">{stagePrefix(t, focusIndex + 1)}</span>
                  <span className="font-semibold text-fg">{stageName(t, model.focus)}</span>
                </span>
              </>
            )}
            {chipLabel && payOut ? (
              <span
                className={`badge ${CHIP_TONE[payOutTone(payOut)]} inline-flex min-w-0 max-w-[320px] items-center gap-1`}
                data-testid="bank-guide-chip"
                data-chip={payOut.status}
                title={chipLabel}
              >
                {payOut.status === 'submitted' ? <Send className="h-3 w-3 shrink-0" aria-hidden="true" />
                  : payOut.status === 'confirmed' || payOut.status === 'settled' ? <CheckCircle2 className="h-3 w-3 shrink-0" aria-hidden="true" />
                  : payOut.status === 'attention' || payOut.status === 'rejected' || payOut.status === 'setup' ? <AlertTriangle className="h-3 w-3 shrink-0" aria-hidden="true" />
                  : <Clock className="h-3 w-3 shrink-0" aria-hidden="true" />}
                <span className="truncate">{chipLabel}</span>
              </span>
            ) : focusStage && focusStage.state !== 'done' ? (
              <span className={`badge shrink-0 ${focusStage.state === 'blocked' ? 'badge-danger' : 'badge-info'}`}>{stateName(t, focusStage.state)}</span>
            ) : null}
            <span className="hidden min-w-0 flex-1 md:block" />
            {showNext && (
              <div className="flex min-w-0 shrink items-center gap-2" data-testid="pipeline-next">
                <p className="min-w-0 truncate text-body-sm text-fg-muted">
                  {nextBefore}<span className="font-semibold text-fg" data-testid="pipeline-next-title">{nextTitle(t, model.next, focusStage, payOut)}</span>{nextAfter}
                </p>
                <Button
                  size="sm"
                  className="shrink-0 whitespace-nowrap"
                  disabled={model.next.disabled}
                  busy={busy}
                  aria-describedby={model.nextReason ? 'pipeline-next-reason' : undefined}
                  onClick={() => perform(model.next)}
                  data-testid="pipeline-next-action"
                >
                  {actionLabel(t, model.next, busy)}
                </Button>
              </div>
            )}
            {!processingActive && (
              <button
                type="button"
                className="btn-ghost btn-sm ms-auto shrink-0 whitespace-nowrap md:ms-0"
                aria-expanded={stagesOpen}
                aria-controls="pipeline-stages-region"
                data-testid="pipeline-stages-toggle"
                onClick={() => chooseStages(stagesOpen ? 'folded' : 'open')}
              >
                {stagesOpen ? t('Hide stages') : t('Show stages')}
                <ChevronDown className={`h-4 w-4 transition-transform duration-200 motion-reduce:transition-none ${stagesOpen ? 'rotate-180' : ''}`} aria-hidden="true" />
              </button>
            )}
          </>
        )}
      </div>
      {/* The Pay out reason is the chip's own words: not said a second time. */}
      {showNext && model?.nextReason && model.nextReason.id !== 'pay_out' && (
        <p className="mt-1 text-meta text-fg-muted" id="pipeline-next-reason" data-testid="pipeline-next-reason">{reasonText(t, model.nextReason, payOut)}</p>
      )}
    </section>

    <div className="space-y-4 empty:hidden" data-testid="pipeline-region">
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
                  <span className="badge badge-warning">{attentionLabel(r)}</span>
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
          {model.voided && <p role="status" className="rounded-lg border border-line bg-surface-sunken px-3 py-2 text-body-sm text-fg-muted">{t('This run was voided. It has no further stages.')}</p>}
          {model.unknownStatus && <p role="status" className="rounded-lg border border-line bg-surface-sunken px-3 py-2 text-body-sm text-fg-muted">{t('This run has a status the pipeline does not know, so no stage is highlighted.')}</p>}

          <ol id="pipeline-stages-region" aria-label={t('Stages of payroll run {period}', { period: period(run) })} className={`${stagesOpen ? 'grid' : 'hidden'} grid-cols-2 gap-2 md:grid-cols-4 xl:grid-cols-8`} data-testid="pipeline-stages">
            {model.stages.map(stage => (
              <StageItem
                key={stage.key}
                stage={stage}
                isFocus={model.focus === stage.key}
                onOpen={() => (stage.key === 'review' ? setReviewOpen(v => !v) : onNavigate(STAGE_TAB[stage.key]))}
                reviewOpen={reviewOpen}
                progress={stage.key === 'processed' && isProcessingActive(processingStatus?.status) ? processingStatus : null}
                fmt={fmt}
                payOut={payOut}
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
                recon={recon.recon}
                reconState={recon.state}
                reconError={recon.error}
                reloadRecon={recon.reload}
                candidates={priorRunCandidates(run, data.runs)}
                priorRunId={priorRunId}
                onPriorRunChange={setPriorRunId}
                mayApprove={can.approve}
                signoffRequired={signoffRequired}
                reviewedAtUtc={review?.reviewedAtUtc ?? null}
                reviewedByName={review?.reviewedByName ?? null}
                onOpenValidation={() => onNavigate('validation')}
                onSignedOff={() => setReviewOpen(true)}
              />
            </div>
          )}
        </>
      )}

      <div role="status" aria-live="polite" className="sr-only" data-testid="pipeline-live">{announcement}</div>
      {lockDialog}
    </div>
    </>
  );
}

// ── One stage ──────────────────────────────────────────────────────────────────

function StageItem({ stage, isFocus, onOpen, reviewOpen, progress, fmt, payOut }: {
  stage: PipelineStage;
  isFocus: boolean;
  onOpen: () => void;
  reviewOpen: boolean;
  progress: { processedEmployees: number | null; totalEmployees: number | null; status: string } | null;
  fmt: ReturnType<typeof useFormat>;
  /** The run's pay out (the Pay out stage's words). */
  payOut: PayOut | null;
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
        <p className="text-meta" data-testid={`stage-reason-${stage.key}`}>{reasonText(t, stage.reason, payOut)}</p>
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
    case 'paid': return t('Pay out');
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
    // The label is the API's English status text; the dictionary holds its Arabic (an unknown status prints as sent).
    case 'batch': {
      // Where the API says which file exists, name it: a Saudi company's internal register is not its bank file.
      if (!d.file) return t('Bank file {number}: {label}', { number: d.number, label: t(d.label) });
      // The batch number is a left-to-right code inside what may be an Arabic sentence: isolate it so it is not reordered.
      const number = isolateLtr(d.number);
      const file = d.file === 'bank_file'
        ? t('Batch {number}: Bank file generated', { number })
        : t('Batch {number}: Register generated (internal, not a bank file)', { number });
      return d.statusAfterFile ? `${file} ${t('Status: {status}', { status: t(d.label) })}` : file;
    }
    case 'payroll_review': return t('Payroll review is done');
    case 'overrides': return t('{count, plural, one {# error cleared by override} other {# errors cleared by override}}', { count: d.count });
    case 'signed_off': return d.name ? t('Signed off by {name}', { name: d.name }) : t('Reconciliation signed off');
    case 'review_not_required': return t('No reconciliation sign-off is needed for this run');
  }
}

function reasonText(t: T, r: Reason, payOut: PayOut | null = null): string {
  switch (r.id) {
    case 'pay_out': return payOut ? payOutLabel(t, payOut) : t('Waits for the run to be locked.');
    case 'failed': return r.message ?? t('Processing failed.');
    case 'processing': return t('Processing is under way. This page updates by itself.');
    case 'errors_open': return t('{count, plural, one {# validation error is open. Resolve or override it on the Validation tab.} other {# validation errors are open. Resolve or override them on the Validation tab.}}', { count: r.count ?? 0 });
    case 'validation_unknown': return t('The validation results could not be read, so this stage cannot be confirmed.');
    case 'attendance_stale': return t('{count, plural, one {# attendance day no longer matches its punches. Process is refused until it is recomputed.} other {# attendance days no longer match their punches. Process is refused until they are recomputed.}}', { count: r.count ?? 0 });
    case 'payment_gaps': return t('{count, plural, one {# employee cannot be paid yet (for example no bank details). Processing still works, but it will raise validation errors for them.} other {# employees cannot be paid yet (for example no bank details). Processing still works, but it will raise validation errors for them.}}', { count: r.count ?? 0 });
    case 'prechecks_unknown': return t('The pre-checks could not be read. You can still process the run.');
    case 'needs_process': return t('Waits for the run to be processed.');
    case 'needs_validation': return t('Waits for the validation errors to be cleared.');
    case 'needs_review': return t('Open the review and sign off the reconciliation before approving.');
    case 'needs_approval': return t('Waits for approval.');
    case 'needs_finance': return t('Payroll review is done. A finance approver decides next.');
    case 'needs_lock': return t('Waits for the run to be locked.');
    case 'no_permission_process': return t('You do not have permission to process payroll.');
    case 'no_permission_approve': return t('You do not have permission to approve payroll.');
    case 'no_permission_lock': return t('You do not have permission to lock payroll.');
    case 'currency_unconfirmed': return t('The company currency is not confirmed, so the lock summary cannot name one. Check Setup → Companies.');
    case 'no_batch': return t('No payment batch exists yet. Create one on the Bank / WPS tab.');
    case 'batch_not_exported': return t('A payment batch exists, but its bank file has not been generated yet.');
    case 'bank_file_not_generated': return t('The internal payroll register is generated, but the bank file has not been generated yet.');
    case 'awaiting_bank_confirmation':
      return r.total === undefined || r.confirmed === undefined
        ? t('Bank file ready — waiting for bank confirmation')
        : t('Bank file ready — waiting for bank confirmation ({confirmed} of {total})', { confirmed: r.confirmed, total: r.total });
    case 'voided': return t('Voided.');
  }
}

/** "Stage 8 of 8 · " before the stage's name (the name is drawn on its own, in bold). */
function stagePrefix(t: T, n: number): string {
  const marker = '\u0001';
  const [before] = t('Stage {n} of 8 · {stage}', { n, stage: marker }).split(marker);
  return before;
}

function nextTitle(t: T, next: NextAction, focus: PipelineStage | null, payOut: PayOut | null = null): string {
  if (focus?.key === 'paid' && payOut) {
    switch (payOut.status) {
      case 'no_batch': return t('Create the payment batch');
      case 'setup': return t('Complete the bank setup');
      case 'created': case 'downloaded': return t('Send the file to your bank');
      case 'rejected': return t('Prepare a corrected file');
      default: break;
    }
  }
  switch (next.label) {
    case 'process': return t('Process payroll');
    case 'retry': return t('Retry processing');
    case 'review_validation': return t('Review the validation results');
    case 'review_attendance': return t('Review attendance and recompute');
    case 'review': return t('Review the results');
    case 'approve': return t('Approve the run');
    case 'lock': return t('Lock the run');
    case 'bank': return t('Create the bank file');
    case 'confirm_bank': return t('Confirm payments with the bank');
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
    case 'confirm_bank': return t('Open Payment Tracking');
    case 'none': return '';
  }
}
