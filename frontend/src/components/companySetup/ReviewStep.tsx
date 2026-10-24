'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, CheckCircle2, ChevronDown, CircleDot, Info } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import type { CompanySetupDraftBody, CompanySetupPlan, OrgSection, PlanItem, Problem } from '../../api/companySetup';
import {
  AREA_LABEL, CHANGED_ELSEWHERE, CHANGED_FIELD_LABEL, DRAFT_NEEDS_REFRESH, buildTree, currentValueLabel, resolveChangedElsewhere, canCommit, groupPlan,
  planIsNoChange, reasonFields, setupCodeCopy, stepCopy, wizardMode, type StepId,
} from '../../lib/companySetupWizard';
import { conflictNamesRow, draftValueLabel, isOpenBucket, reviewBucket, reviewCounts, savedInStep4, type ReviewBucket as Bucket } from './reviewModel';
import { StructureTree } from './StructureTree';
import { Notice, useWizardForm } from './wizardForm';

/**
 * Step 7: exactly what Create / Update will do — the dry-run from the same planner the commit runs — grouped by the
 * step that produced it.
 *
 * Reading order (one decision at a time):
 *   1. a decision card with the one next action: fix the first blocker, or press Create / Update, or finish;
 *   2. counts that open the rows behind them (a filter, not a statistic);
 *   3. the rows, grouped by step, with what stays as it is collapsed.
 *
 * Addendum B: departments, designations and grades belong to one company (no shared group catalog), and in existing
 * mode the Step 4 sections are written live. The planner then reports those rows as `unchanged`; here they read as
 * "Saved in Step 4" so the user's work does not look like a no-op.
 */

const BADGE: Record<Bucket, string> = {
  add: 'bg-emerald-50 text-emerald-800 dark:bg-emerald-500/15 dark:text-emerald-200',
  update: 'bg-sky-50 text-sky-800 dark:bg-sky-500/15 dark:text-sky-200',
  saved: 'bg-violet-50 text-violet-800 dark:bg-violet-500/15 dark:text-violet-200',
  kept: 'bg-slate-100 text-slate-700 dark:bg-white/10 dark:text-slate-200',
  fix: 'bg-rose-100 text-rose-800 dark:bg-rose-500/20 dark:text-rose-200',
};

/** The planner's English reasons that have a plain, translated version (CompanySetupPlanner.KeptReason and reuse). */
const KEPT_REASON = 'Kept as it is: the wizard adds and edits, it never removes.';

const FOCUS_RING = 'outline-none focus-visible:ring-2 focus-visible:ring-sapphire focus-visible:ring-offset-2 focus-visible:ring-offset-white dark:focus-visible:ring-cyanAccent dark:focus-visible:ring-offset-slate-900';

export function ReviewStep({ plan, state, onRetry, onGoTo, visibleSteps, onChangeAndCheck, onReloadPrefill, savedSections }: {
  plan: CompanySetupPlan | null;
  state: 'idle' | 'loading' | 'error';
  onRetry: () => void;
  /** `field` (a problem's path) is focused on that step. */
  onGoTo: (step: StepId, field?: string) => void;
  visibleSteps: StepId[];
  /** A9: apply a change to the draft and check the plan again (take the current value / keep mine). */
  onChangeAndCheck?: (change: (body: CompanySetupDraftBody) => CompanySetupDraftBody) => void;
  /** A9: a draft saved before baselines existed is reloaded from what the company has today. */
  onReloadPrefill?: () => void;
  /** B2: the Organisation sections this draft already saved live in Step 4 (existing mode). */
  savedSections?: readonly OrgSection[];
}) {
  const { t, locale } = useLocale();
  const { body, context, baseline } = useWizardForm();
  const mode = wizardMode(body);
  const existing = mode === 'existing';
  const company = (locale === 'ar' && body.company.legalNameAr?.trim()) || body.company.legalNameEn || '';
  const title = (step: StepId) => t(stepCopy(step, mode).title);
  const stepOf = (p: Problem): StepId => (visibleSteps.includes(p.step as StepId) ? p.step as StepId : 7);
  const [filter, setFilter] = useState<Bucket | null>(null);
  const summaryRef = useRef<HTMLHeadingElement>(null);
  const blockersRef = useRef<HTMLUListElement>(null);
  const refocus = useRef(false);

  // After "Use the current value" / "Keep my value" / reload, the clicked button is gone with the old result: land the
  // reader on the new summary instead of the top of the page.
  useEffect(() => {
    if (plan && refocus.current) { refocus.current = false; summaryRef.current?.focus(); }
  }, [plan]);
  // A filter is about one result; a new result starts unfiltered.
  useEffect(() => { setFilter(null); }, [plan]);

  const saved = useMemo(() => (existing ? savedInStep4(body, baseline, savedSections) : new Set<string>()), [existing, body, baseline, savedSections]);
  const bucketOf = (item: PlanItem): Bucket => reviewBucket(item, saved);
  const badge = (b: Bucket) => ({ add: t('New'), update: t('Changed'), saved: t('Saved in Step 4'), kept: t('No change'), fix: t('Needs a fix') }[b]);

  /** An update names its fields in the reader's language; the rest read in plain words, never the planner's English. */
  const detail = (item: PlanItem, b: Bucket) => {
    const sep = locale === 'ar' ? '، ' : ', ';
    if (item.action === 'update' && item.changedFields?.length)
      return t('Changes: {fields}', { fields: item.changedFields.map(f => (CHANGED_FIELD_LABEL[f] ? t(CHANGED_FIELD_LABEL[f]) : f)).join(sep) });
    const fromReason = item.action === 'update' ? reasonFields(item.reason) : null;
    if (fromReason) return t('Changes: {fields}', { fields: fromReason.map(k => t(k)).join(sep) });
    if (b === 'saved') return t('Already saved to the company in Step 4.');
    if (item.action === 'reuse') return t('Already set up. It is used as it is.');
    if (item.action === 'unchanged' && item.reason === KEPT_REASON) return t('Kept as it is. To remove it, use its Setup tab.');
    // Anything else is the planner's own sentence, in the reader's language when the server sends it.
    if (locale === 'ar') return item.reasonAr || (item.action !== 'conflict' && item.action !== 'update' ? '' : item.reason || '');
    return item.reason || '';
  };
  const problemText = (p: Problem) => {
    const copy = setupCodeCopy(p.code);
    return copy ? t(copy) : (locale === 'ar' ? p.messageAr || p.message : p.message);
  };

  // ── Waiting and failure, in the final layout ──────────────────────────────
  if (!plan && state === 'error') return (
    <div data-testid="review-plan" data-mode={mode}>
      <Notice tone="error" role="alert">
        <p className="font-semibold">{t('We could not check your setup.')}</p>
        <p className="mt-1">{existing ? t('Nothing has been changed. Check your connection and try again.') : t('Nothing has been created. Check your connection and try again.')}</p>
        <button type="button" className={`btn-secondary mt-3 ${FOCUS_RING}`} onClick={onRetry}>{t('Check again')}</button>
      </Notice>
    </div>
  );
  if (!plan) return <ReviewSkeleton label={t('Checking your setup against the company’s records…')} />;

  const groups = groupPlan(plan);
  const counts = reviewCounts(plan, saved);
  const conflictItems = plan.items.filter(i => i.action === 'conflict');
  const blockerCount = counts.fix;
  const blocked = !canCommit(plan);
  const noChange = existing && planIsNoChange(plan) && !blocked;
  const checking = state === 'loading';

  const firstFix = () => {
    const p = plan.problems[0];
    if (p) {
      if (p.code === DRAFT_NEEDS_REFRESH && onReloadPrefill) { refocus.current = true; onReloadPrefill(); return; }
      onGoTo(stepOf(p), p.field);
      return;
    }
    const c = conflictItems[0];
    if (c) onGoTo(groups.find(g => g.items.includes(c))?.step ?? 7);
  };
  const change = (fn: (b: CompanySetupDraftBody) => CompanySetupDraftBody) => { refocus.current = true; onChangeAndCheck?.(fn); };

  const chip = (bucket: Bucket, text: string, testId?: string) => {
    const active = filter === bucket;
    return (
      <button key={bucket} type="button" data-testid={testId} aria-pressed={bucket === 'fix' ? undefined : active}
        onClick={() => {
          if (bucket === 'fix') { blockersRef.current?.focus(); blockersRef.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' }); return; }
          setFilter(active ? null : bucket);
        }}
        className={`rounded-full px-3 py-1 text-sm font-semibold ring-inset transition-[box-shadow,transform] duration-150 active:scale-[0.98] motion-reduce:transition-none motion-reduce:active:scale-100 ${BADGE[bucket]} ${active ? 'ring-2 ring-current' : 'ring-1 ring-black/5 hover:ring-current/40 dark:ring-white/10'} ${FOCUS_RING}`}>
        {text}
      </button>
    );
  };

  const itemRow = (item: PlanItem, i: number) => {
    const b = bucketOf(item);
    const text = detail(item, b);
    return (
      <li key={`${item.area}-${item.code}-${i}`} className="flex flex-wrap items-start justify-between gap-x-3 gap-y-1 px-4 py-2.5" data-action={item.action} data-bucket={b}>
        <div className="min-w-0 flex-1">
          <p className="break-words text-sm text-slate-900 dark:text-white">
            <span className="text-slate-500 dark:text-slate-400">{t(AREA_LABEL[item.area])}</span>{' · '}
            <bdi>{locale === 'ar' ? item.labelAr || item.label : item.label}</bdi>
            {item.code && <span className="ms-1.5 text-xs text-slate-500 dark:text-slate-400"><bdi>{item.code}</bdi></span>}
          </p>
          {text && <p className="mt-0.5 text-xs leading-5 text-slate-600 dark:text-slate-400"><bdi>{text}</bdi></p>}
        </div>
        <span className={`shrink-0 rounded-full px-2 py-0.5 text-[11px] font-semibold ${BADGE[b]}`}>{badge(b)}</span>
      </li>
    );
  };

  // ── The decision card: one next action ────────────────────────────────────
  const decision = blocked ? (
    <section aria-labelledby="ccw-review-summary" data-testid="review-decision" data-state="blocked"
      className="rounded-2xl border border-rose-200 bg-rose-50/70 p-4 sm:p-5 dark:border-rose-400/30 dark:bg-rose-500/10">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex min-w-0 items-start gap-3">
          <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-rose-600 dark:text-rose-300" aria-hidden="true" />
          <div className="min-w-0">
            <h3 id="ccw-review-summary" ref={summaryRef} tabIndex={-1} className={`rounded text-base font-semibold text-rose-950 dark:text-rose-50 ${FOCUS_RING}`}>
              {existing
                ? t('{count, plural, one {# thing to fix} other {# things to fix}} before you can update {company}', { count: blockerCount, company })
                : t('{count, plural, one {# thing to fix} other {# things to fix}} before you can create {company}', { count: blockerCount, company })}
            </h3>
            <p className="mt-0.5 text-sm text-rose-900/80 dark:text-rose-100/80">{existing ? t('Nothing has been changed yet.') : t('Nothing has been created yet.')}</p>
          </div>
        </div>
        {blockerCount > 0 && <button type="button" className={`btn-primary shrink-0 ${FOCUS_RING}`} onClick={firstFix}>{blockerCount === 1 ? t('Fix this') : t('Fix the first one')}</button>}
      </div>
      <ul ref={blockersRef} tabIndex={-1} aria-label={t('Things to fix')}
        className={`mt-4 divide-y divide-rose-200/70 overflow-hidden rounded-xl border border-rose-200/70 bg-white/80 dark:divide-rose-400/20 dark:border-rose-400/20 dark:bg-slate-950/30 ${FOCUS_RING}`}>
        {plan.problems.map((p, i) => {
          const step = stepOf(p);
          const mine = p.code === CHANGED_ELSEWHERE ? draftValueLabel(body, p.field) : null;
          return (
            <li key={`p-${i}`} className="space-y-2 px-4 py-3 text-sm text-slate-900 dark:text-slate-100" data-code={p.code} data-field={p.field}>
              <p className="text-xs font-semibold uppercase tracking-wide text-rose-700 dark:text-rose-300">{title(step)}</p>
              <p className="leading-6">{problemText(p)}</p>
              <div className="flex flex-wrap items-center gap-2">
                {p.code === CHANGED_ELSEWHERE && onChangeAndCheck && <>
                  <button type="button" className={`btn-secondary px-2.5 py-1 text-xs ${FOCUS_RING}`} onClick={() => change(b => resolveChangedElsewhere(b, p, 'theirs'))}>
                    {t('Use the current value ({value})', { value: currentValueLabel(p) === p.current ? currentValueLabel(p) : t(currentValueLabel(p)) })}
                  </button>
                  <button type="button" className={`btn-secondary px-2.5 py-1 text-xs ${FOCUS_RING}`} onClick={() => change(b => resolveChangedElsewhere(b, p, 'mine'))}>
                    {mine !== null ? t('Keep my value ({value})', { value: mine === 'Yes' || mine === 'No' ? t(mine) : mine }) : t('Keep mine')}
                  </button>
                </>}
                {p.code === DRAFT_NEEDS_REFRESH && onReloadPrefill
                  ? <button type="button" className={`btn-secondary px-2.5 py-1 text-xs ${FOCUS_RING}`} onClick={() => { refocus.current = true; onReloadPrefill(); }}>{t('Reload the company’s current setup')}</button>
                  : <button type="button" className={`rounded text-sm font-semibold text-sapphire underline-offset-4 hover:underline dark:text-cyanAccent ${FOCUS_RING}`} onClick={() => onGoTo(step, p.field)}>{t('Go to {step}', { step: title(step) })}</button>}
              </div>
            </li>
          );
        })}
        {conflictItems.map(i => {
          const step = groups.find(g => g.items.includes(i))?.step ?? 7;
          return (
            <li key={`c-${i.area}-${i.code}`} className="space-y-2 px-4 py-3 text-sm text-slate-900 dark:text-slate-100">
              <p className="text-xs font-semibold uppercase tracking-wide text-rose-700 dark:text-rose-300">{title(step)}</p>
              <p className="leading-6">{(() => { const reason = (locale === 'ar' && i.reasonAr) || i.reason; return conflictNamesRow({ code: i.code, reason }) ? reason : t('{area} {code}: {reason}', { area: t(AREA_LABEL[i.area]), code: i.code, reason: reason || t('Already exists') }); })()}</p>
              <button type="button" className={`rounded text-sm font-semibold text-sapphire underline-offset-4 hover:underline dark:text-cyanAccent ${FOCUS_RING}`} onClick={() => onGoTo(step)}>{t('Go to {step}', { step: title(step) })}</button>
            </li>
          );
        })}
        {blockerCount === 0 && <li className="px-4 py-3 text-sm">{t('The check did not pass, but named nothing to fix. Check again; if it repeats, ask your group super admin.')}</li>}
      </ul>
    </section>
  ) : noChange ? (
    <section aria-labelledby="ccw-review-summary" data-testid="review-decision" data-state="no-change">
      <div data-testid="review-no-change"><Notice tone="success" role="status">
        <div className="flex items-start gap-3">
          <CheckCircle2 className="mt-0.5 h-5 w-5 shrink-0 text-emerald-600 dark:text-emerald-300" aria-hidden="true" />
          <div className="min-w-0">
            <h3 id="ccw-review-summary" ref={summaryRef} tabIndex={-1} className={`rounded font-semibold ${FOCUS_RING}`}>{t('Nothing will change.')}</h3>
            <p className="mt-1">{t('Everything in this draft is already set up for {company}. Finish to close the draft; no record is changed.', { company })}</p>
            {saved.size > 0 && <p className="mt-1">{t('Organisation rows you saved in Step 4 are already in the company.')}</p>}
          </div>
        </div>
      </Notice></div>
    </section>
  ) : (
    <section aria-labelledby="ccw-review-summary" data-testid="review-decision" data-state="ready"
      className="rounded-2xl border border-emerald-200 bg-emerald-50/70 p-4 sm:p-5 dark:border-emerald-400/30 dark:bg-emerald-500/10">
      <div className="flex items-start gap-3">
        <CheckCircle2 className="mt-0.5 h-5 w-5 shrink-0 text-emerald-600 dark:text-emerald-300" aria-hidden="true" />
        <div className="min-w-0">
          <h3 id="ccw-review-summary" ref={summaryRef} tabIndex={-1} className={`rounded text-base font-semibold text-emerald-950 dark:text-emerald-50 ${FOCUS_RING}`}>
            {existing ? t('Ready to update {company}', { company }) : t('Ready to create {company}', { company })}
          </h3>
          <p className="mt-0.5 text-sm text-emerald-900/80 dark:text-emerald-100/80">
            {existing
              ? t('Check the changes below, then press Save all changes at the bottom. Nothing changes until you do.')
              : t('Check what will be set up below, then press Create company at the bottom. Nothing is created until you do.')}
          </p>
          {saved.size > 0 && <p className="mt-1 text-sm text-emerald-900/80 dark:text-emerald-100/80">{t('Organisation rows you saved in Step 4 are already in the company.')}</p>}
        </div>
      </div>
    </section>
  );

  // ── Counts that open the rows behind them ─────────────────────────────────
  const chips = existing ? [
    chip('add', t('{count} to add', { count: counts.add }), 'count-create'),
    chip('update', t('{count} to update', { count: counts.update }), 'count-update'),
    ...(counts.saved > 0 ? [chip('saved', t('{count} saved in Step 4', { count: counts.saved }), 'count-saved')] : []),
    chip('kept', t('{count} stay as they are', { count: counts.kept }), 'count-unchanged'),
  ] : [
    chip('add', t('{count} to create', { count: counts.add }), 'count-create'),
    ...(counts.kept > 0 ? [chip('kept', t('{count} already set up', { count: counts.kept }), 'count-unchanged')] : []),
  ];
  if (blockerCount > 0) chips.push(chip('fix', t('{count} to fix', { count: blockerCount }), 'count-fix'));


  return (
    <div className="space-y-5" data-testid="review-plan" data-mode={mode} aria-busy={checking || undefined}>
      {decision}

      <div className="flex flex-wrap items-center gap-2">
        <span className="sr-only">{t('Select a count to see the rows behind it.')}</span>
        {chips}
        <div className="ms-auto flex items-center gap-2">
          {filter && <button type="button" className={`rounded text-sm font-semibold text-sapphire hover:underline dark:text-cyanAccent ${FOCUS_RING}`} onClick={() => setFilter(null)}>{t('Show everything')}</button>}
          <button type="button" className={`btn-secondary ${FOCUS_RING}`} onClick={onRetry} disabled={checking}>{checking ? t('Checking…') : t('Check again')}</button>
        </div>
      </div>

      {plan.warnings.length > 0 && (
        <Notice tone="warning" role="status">
          <p className="flex items-center gap-2 font-semibold"><Info className="h-4 w-4 shrink-0" aria-hidden="true" />{t('Worth knowing. These do not stop you.')}</p>
          <ul className="mt-1 list-disc space-y-1 ps-5">{plan.warnings.map((w, i) => <li key={i}>{problemText(w)}</li>)}</ul>
        </Notice>
      )}

      <div className={`grid gap-6 transition-opacity duration-150 motion-reduce:transition-none xl:grid-cols-[minmax(0,1fr)_minmax(0,18rem)] ${checking ? 'opacity-60' : ''}`}>
        <div className="min-w-0 space-y-4">
          {groups.map(group => {
            const groupRows = group.items.map(item => ({ item, bucket: bucketOf(item) }));
            const filtered = filter ? groupRows.filter(r => r.bucket === filter) : null;
            if (filtered && filtered.length === 0) return null;
            const shown = filtered ?? groupRows.filter(r => isOpenBucket(r.bucket));
            const kept = filtered ? [] : groupRows.filter(r => !isOpenBucket(r.bucket));
            const savedHere = groupRows.some(r => r.bucket === 'saved');
            return (
              <section key={group.step} aria-labelledby={`ccw-plan-${group.step}`} data-testid={`plan-group-${group.step}`}
                className="overflow-hidden rounded-xl border border-slate-200 bg-white dark:border-white/10 dark:bg-white/[0.02]">
                <div className="flex flex-wrap items-center justify-between gap-2 border-b border-slate-100 px-4 py-2.5 dark:border-white/10">
                  <h3 id={`ccw-plan-${group.step}`} className="text-sm font-semibold text-slate-900 dark:text-white">{title(group.step)}</h3>
                  {visibleSteps.includes(group.step) && <button type="button" className={`rounded text-xs font-semibold text-sapphire hover:underline dark:text-cyanAccent ${FOCUS_RING}`} onClick={() => onGoTo(group.step)}>{t('Change')}<span className="sr-only"> {title(group.step)}</span></button>}
                </div>
                {savedHere && !filtered && (
                  <p className="flex items-start gap-2 border-b border-slate-100 bg-violet-50/60 px-4 py-2 text-xs leading-5 text-violet-900 dark:border-white/10 dark:bg-violet-500/10 dark:text-violet-100" data-testid={`plan-saved-note-${group.step}`}>
                    <CircleDot className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
                    {existing ? t('Already saved to the company in Step 4. Save all changes does not change these again.') : t('Already saved to the company in Step 4.')}
                  </p>
                )}
                {shown.length > 0 && <ul className={`divide-y divide-slate-100 dark:divide-white/10 ${filtered ? 'motion-safe:animate-fade-in-fast' : ''}`}>{shown.map(r => itemRow(r.item, group.items.indexOf(r.item)))}</ul>}
                {shown.length === 0 && <p className="px-4 py-2.5 text-sm text-slate-600 dark:text-slate-400">{t('No changes in this step.')}</p>}
                {kept.length > 0 && (
                  <details className="group border-t border-slate-100 dark:border-white/10" data-testid={`plan-kept-${group.step}`}>
                    <summary className={`flex cursor-pointer list-none items-center gap-1.5 px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-900 dark:text-slate-400 dark:hover:text-white [&::-webkit-details-marker]:hidden ${FOCUS_RING} focus-visible:ring-inset focus-visible:ring-offset-0`}>
                      <ChevronDown className="h-3.5 w-3.5 shrink-0 transition-transform duration-200 group-open:rotate-180 motion-reduce:transition-none" aria-hidden="true" />
                      {existing ? t('{count} stay as they are', { count: kept.length }) : t('{count} already set up', { count: kept.length })}
                    </summary>
                    <ul className="divide-y divide-slate-100 motion-safe:animate-fade-in-fast dark:divide-white/10">{kept.map(r => itemRow(r.item, group.items.indexOf(r.item)))}</ul>
                  </details>
                )}
              </section>
            );
          })}
        </div>
        <aside className="h-fit rounded-xl border border-slate-200 bg-white p-4 dark:border-white/10 dark:bg-white/[0.02]" aria-label={t('Company structure')}>
          <p className="mb-2 text-sm font-semibold text-slate-900 dark:text-white">{t('Company structure')}</p>
          <StructureTree tree={buildTree(body, context)} />
        </aside>
      </div>
    </div>
  );
}

/** The review's final layout while the check runs: decision card, counts, three step cards and the structure. */
function ReviewSkeleton({ label }: { label: string }) {
  const bar = 'rounded bg-slate-200/80 dark:bg-white/10';
  return (
    <div className="space-y-5" data-testid="review-skeleton" aria-busy="true">
      <p role="status" className="sr-only">{label}</p>
      <div className="motion-safe:animate-pulse space-y-5" aria-hidden="true">
        <div className="rounded-2xl border border-slate-200 p-4 sm:p-5 dark:border-white/10">
          <div className="flex items-start gap-3">
            <div className="h-5 w-5 shrink-0 rounded-full bg-slate-200/80 dark:bg-white/10" />
            <div className="min-w-0 flex-1 space-y-2"><div className={`h-4 w-2/3 ${bar}`} /><div className={`h-3 w-5/6 ${bar}`} /></div>
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <div className="h-7 w-24 rounded-full bg-slate-200/80 dark:bg-white/10" />
          <div className="h-7 w-28 rounded-full bg-slate-200/80 dark:bg-white/10" />
          <div className="ms-auto h-9 w-28 rounded-lg bg-slate-200/80 dark:bg-white/10" />
        </div>
        <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_minmax(0,18rem)]">
          <div className="min-w-0 space-y-4">
            {[3, 2, 4].map((rows, g) => (
              <div key={g} className="overflow-hidden rounded-xl border border-slate-200 dark:border-white/10">
                <div className="border-b border-slate-100 px-4 py-3 dark:border-white/10"><div className={`h-3.5 w-32 ${bar}`} /></div>
                {Array.from({ length: rows }, (_, i) => (
                  <div key={i} className="flex items-center justify-between gap-3 border-b border-slate-100 px-4 py-3 last:border-b-0 dark:border-white/10">
                    <div className={`h-3 w-1/2 ${bar}`} /><div className="h-4 w-14 rounded-full bg-slate-200/80 dark:bg-white/10" />
                  </div>
                ))}
              </div>
            ))}
          </div>
          <div className="h-fit space-y-2 rounded-xl border border-slate-200 p-4 dark:border-white/10">
            <div className={`h-3.5 w-28 ${bar}`} />
            {[0, 1, 2, 3, 4].map(i => <div key={i} className={`h-3 ${bar}`} style={{ width: `${80 - i * 10}%`, marginInlineStart: `${(i % 3) * 12}px` }} />)}
          </div>
        </div>
      </div>
    </div>
  );
}
