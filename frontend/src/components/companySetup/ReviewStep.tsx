'use client';

import { useLocale } from '../../contexts/LocaleContext';
import type { CompanySetupDraftBody, CompanySetupPlan, PlanAction, PlanItem, Problem } from '../../api/companySetup';
import {
  AREA_LABEL, CHANGED_ELSEWHERE, CHANGED_FIELD_LABEL, DRAFT_NEEDS_REFRESH, buildTree, currentValueLabel, resolveChangedElsewhere, canCommit, groupPlan, isPlanChange, planCounts, planIsNoChange, reasonFields, setupCodeCopy, stepCopy, wizardMode, type StepId,
} from '../../lib/companySetupWizard';
import { StructureTree } from './StructureTree';
import { Notice, useWizardForm } from './wizardForm';

const ACTION_STYLE: Record<PlanAction, string> = {
  create: 'bg-emerald-50 text-emerald-800 dark:bg-emerald-500/15 dark:text-emerald-200',
  update: 'bg-sky-50 text-sky-800 dark:bg-sky-500/15 dark:text-sky-200',
  unchanged: 'bg-slate-100 text-slate-700 dark:bg-white/10 dark:text-slate-200',
  reuse: 'bg-slate-100 text-slate-700 dark:bg-white/10 dark:text-slate-200',
  conflict: 'bg-rose-100 text-rose-800 dark:bg-rose-500/20 dark:text-rose-200',
};

/**
 * Step 7: exactly what Create will do — the dry-run from the same planner the commit runs — grouped by
 * the step that produced it. A conflict or a problem blocks Create and links to the step that fixes it.
 */
export function ReviewStep({ plan, state, onRetry, onGoTo, visibleSteps, onChangeAndCheck, onReloadPrefill }: {
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
}) {
  const { t, locale } = useLocale();
  const { body, context } = useWizardForm();
  const mode = wizardMode(body);
  const existing = mode === 'existing';
  const title = (step: StepId) => t(stepCopy(step, mode).title);
  // A known code reads as what to do next, in either language; anything else is the server's own words.
  const problemText = (p: Problem) => {
    const copy = setupCodeCopy(p.code);
    return copy ? t(copy) : (locale === 'ar' ? p.messageAr || p.message : p.message);
  };
  const actionLabel = (a: PlanAction) => ({
    create: existing ? t('Add') : t('Create'), update: t('Update'), unchanged: t('No change'), reuse: t('Reuse'), conflict: t('Conflict'),
  }[a]);
  /** An update names its fields in the reader's language when the server lists them; otherwise its reason. */
  const detail = (item: PlanItem) => {
    const sep = locale === 'ar' ? '، ' : ', ';
    if (item.action === 'update' && item.changedFields?.length)
      return t('Changes: {fields}', { fields: item.changedFields.map(f => (CHANGED_FIELD_LABEL[f] ? t(CHANGED_FIELD_LABEL[f]) : f)).join(sep) });
    const fromReason = item.action === 'update' ? reasonFields(item.reason) : null;
    if (fromReason) return t('Changes: {fields}', { fields: fromReason.map(k => t(k)).join(sep) });
    return item.reason || '';
  };
  const stepOf = (p: Problem): StepId => (visibleSteps.includes(p.step as StepId) ? p.step as StepId : 7);

  if (state === 'loading' && !plan) return <p role="status" className="text-sm text-slate-600 dark:text-slate-300">{t('Checking the plan against your organisation…')}</p>;
  if (state === 'error' && !plan) return (
    <Notice tone="error" role="alert">
      <p>{t('The plan could not be checked. Nothing has been created. Check your connection and try again.')}</p>
      <button type="button" className="btn-secondary mt-2" onClick={onRetry}>{t('Check again')}</button>
    </Notice>
  );
  if (!plan) return null;

  const groups = groupPlan(plan);
  const counts = planCounts(plan);
  const conflicts = counts.conflict;
  const noChange = existing && planIsNoChange(plan);
  const itemRow = (item: PlanItem, i: number) => {
    const text = detail(item);
    return (
      <li key={`${item.area}-${item.code}-${i}`} className="flex flex-wrap items-start justify-between gap-2 px-4 py-2.5" data-action={item.action}>
        <div className="min-w-0">
          <p className="text-sm text-slate-900 dark:text-white"><span className="text-slate-500 dark:text-slate-400">{t(AREA_LABEL[item.area])}</span>{' · '}<bdi>{locale === 'ar' ? item.labelAr || item.label : item.label}</bdi>{item.code && <span className="ms-1 text-xs text-slate-500"><bdi>{item.code}</bdi></span>}</p>
          {text && <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-400"><bdi>{text}</bdi></p>}
        </div>
        <span className={`shrink-0 rounded-full px-2 py-0.5 text-[11px] font-semibold ${ACTION_STYLE[item.action]}`}>{actionLabel(item.action)}</span>
      </li>
    );
  };
  return (
    <div className="space-y-6" data-testid="review-plan" data-mode={mode}>
      <div className="flex flex-wrap items-center gap-2 text-sm sm:gap-3">
        {existing ? <>
          <span className={`rounded-full px-3 py-1 font-semibold ${ACTION_STYLE.create}`} data-testid="count-create">{t('{count} to add', { count: counts.create })}</span>
          <span className={`rounded-full px-3 py-1 font-semibold ${ACTION_STYLE.update}`} data-testid="count-update">{t('{count} to update', { count: counts.update })}</span>
          <span className={`rounded-full px-3 py-1 font-semibold ${ACTION_STYLE.unchanged}`} data-testid="count-unchanged">{t('{count} stay as they are', { count: counts.unchanged + counts.reuse })}</span>
        </> : <>
          <span className={`rounded-full px-3 py-1 font-semibold ${ACTION_STYLE.create}`}>{t('{count} to create', { count: counts.create })}</span>
          <span className={`rounded-full px-3 py-1 font-semibold ${ACTION_STYLE.reuse}`}>{t('{count} to reuse', { count: counts.reuse })}</span>
        </>}
        <span className={`rounded-full px-3 py-1 font-semibold ${conflicts ? ACTION_STYLE.conflict : ACTION_STYLE.reuse}`}>{t('{count, plural, one {# conflict} other {# conflicts}}', { count: conflicts })}</span>
        <button type="button" className="btn-secondary ms-auto" onClick={onRetry} disabled={state === 'loading'}>{state === 'loading' ? t('Checking…') : t('Check again')}</button>
      </div>

      {noChange && (
        <div data-testid="review-no-change"><Notice tone="success" role="status">
          <p className="font-semibold">{t('Nothing will change.')}</p>
          <p className="mt-1">{t('Everything in this draft is already set up for {company}. Finish to close the draft; no record is changed.', { company: (locale === 'ar' && body.company.legalNameAr?.trim()) || body.company.legalNameEn })}</p>
        </Notice></div>
      )}

      {!canCommit(plan) && (
        <Notice tone="error" role="alert">
          <p className="font-semibold">{existing ? t('Resolve these before saving. Nothing has been changed yet.') : t('Resolve these before creating the company. Nothing has been created yet.')}</p>
          <ul className="mt-2 space-y-1.5">
            {plan.problems.map((p, i) => (
              <li key={`p-${i}`} className="flex flex-wrap items-center gap-2" data-code={p.code} data-field={p.field}>
                <span>{problemText(p)}</span>
                {p.code === CHANGED_ELSEWHERE && onChangeAndCheck && <>
                  <button type="button" className="btn-secondary px-2 py-1 text-xs" onClick={() => onChangeAndCheck(b => resolveChangedElsewhere(b, p, 'theirs'))}>
                    {t('Use the current value ({value})', { value: currentValueLabel(p) === p.current ? currentValueLabel(p) : t(currentValueLabel(p)) })}
                  </button>
                  <button type="button" className="btn-secondary px-2 py-1 text-xs" onClick={() => onChangeAndCheck(b => resolveChangedElsewhere(b, p, 'mine'))}>{t('Keep mine')}</button>
                </>}
                {p.code === DRAFT_NEEDS_REFRESH && onReloadPrefill
                  ? <button type="button" className="btn-secondary px-2 py-1 text-xs" onClick={onReloadPrefill}>{t('Reload the company’s current setup')}</button>
                  : <button type="button" className="text-sm font-semibold underline" onClick={() => onGoTo(stepOf(p), p.field)}>{t('Go to {step}', { step: title(stepOf(p)) })}</button>}
              </li>
            ))}
            {plan.items.filter(i => i.action === 'conflict').map(i => {
              const step = groups.find(g => g.items.includes(i))?.step ?? 7;
              return (
                <li key={`c-${i.area}-${i.code}`} className="flex flex-wrap items-center gap-2">
                  <span>{t('{area} {code}: {reason}', { area: t(AREA_LABEL[i.area]), code: i.code, reason: i.reason || t('Already exists') })}</span>
                  <button type="button" className="text-sm font-semibold underline" onClick={() => onGoTo(step)}>{t('Go to {step}', { step: title(step) })}</button>
                </li>
              );
            })}
          </ul>
        </Notice>
      )}

      {plan.warnings.length > 0 && (
        <Notice tone="warning" role="status">
          <ul className="space-y-1">{plan.warnings.map((w, i) => <li key={i}>{problemText(w)}</li>)}</ul>
        </Notice>
      )}

      <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_minmax(0,18rem)]">
        <div className="space-y-5">
          {groups.map(group => {
            const shown = group.items.filter(i => isPlanChange(i.action, mode));
            const kept = group.items.filter(i => !isPlanChange(i.action, mode));
            return (
              <section key={group.step} aria-labelledby={`ccw-plan-${group.step}`} className="rounded-xl border border-slate-200 dark:border-white/10" data-testid={`plan-group-${group.step}`}>
                <div className="flex flex-wrap items-center justify-between gap-2 border-b border-slate-100 px-4 py-2.5 dark:border-white/10">
                  <h3 id={`ccw-plan-${group.step}`} className="text-sm font-semibold text-slate-900 dark:text-white">{title(group.step)}</h3>
                  {visibleSteps.includes(group.step) && <button type="button" className="text-xs font-semibold text-sapphire hover:underline dark:text-cyanAccent" onClick={() => onGoTo(group.step)}>{t('Change')}<span className="sr-only"> {title(group.step)}</span></button>}
                </div>
                {shown.length > 0 && <ul className="divide-y divide-slate-100 dark:divide-white/10">{shown.map(itemRow)}</ul>}
                {shown.length === 0 && <p className="px-4 py-2.5 text-sm text-slate-600 dark:text-slate-400">{t('No changes in this step.')}</p>}
                {kept.length > 0 && (
                  // Collapsed by default: what stays as it is, for anyone who wants to check it.
                  <details className="border-t border-slate-100 dark:border-white/10" data-testid={`plan-kept-${group.step}`}>
                    <summary className="cursor-pointer px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-900 dark:text-slate-400 dark:hover:text-white">{t('{count} stay as they are', { count: kept.length })}</summary>
                    <ul className="divide-y divide-slate-100 dark:divide-white/10">{kept.map(itemRow)}</ul>
                  </details>
                )}
              </section>
            );
          })}
        </div>
        <aside className="rounded-xl border border-slate-200 p-4 dark:border-white/10" aria-label={t('Company structure')}>
          <p className="mb-2 text-sm font-semibold text-slate-900 dark:text-white">{t('Company structure')}</p>
          <StructureTree tree={buildTree(body, context)} />
        </aside>
      </div>
    </div>
  );
}
