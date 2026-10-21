'use client';

import { useLocale } from '../../contexts/LocaleContext';
import type { CompanySetupPlan, PlanAction, Problem } from '../../api/companySetup';
import { AREA_LABEL, WIZARD_STEPS, buildTree, canCommit, groupPlan, type StepId } from '../../lib/companySetupWizard';
import { StructureTree } from './StructureTree';
import { Notice, useWizardForm } from './wizardForm';

const ACTION_STYLE: Record<PlanAction, string> = {
  create: 'bg-emerald-50 text-emerald-800 dark:bg-emerald-500/15 dark:text-emerald-200',
  reuse: 'bg-slate-100 text-slate-700 dark:bg-white/10 dark:text-slate-200',
  conflict: 'bg-rose-100 text-rose-800 dark:bg-rose-500/20 dark:text-rose-200',
};

/**
 * Step 7: exactly what Create will do — the dry-run from the same planner the commit runs — grouped by
 * the step that produced it. A conflict or a problem blocks Create and links to the step that fixes it.
 */
export function ReviewStep({ plan, state, onRetry, onGoTo, visibleSteps }: {
  plan: CompanySetupPlan | null;
  state: 'idle' | 'loading' | 'error';
  onRetry: () => void;
  onGoTo: (step: StepId) => void;
  visibleSteps: StepId[];
}) {
  const { t, locale } = useLocale();
  const { body, context } = useWizardForm();
  const problemText = (p: Problem) => (locale === 'ar' ? p.messageAr || p.message : p.message);
  const actionLabel = (a: PlanAction) => a === 'create' ? t('Create') : a === 'reuse' ? t('Reuse') : t('Conflict');
  const stepOf = (p: Problem): StepId => (visibleSteps.includes(p.step as StepId) ? p.step as StepId : 7);

  if (state === 'loading' && !plan) return <p role="status" className="text-sm text-slate-600 dark:text-slate-300">{t('Checking the plan against your workspace…')}</p>;
  if (state === 'error' && !plan) return (
    <Notice tone="error" role="alert">
      <p>{t('The plan could not be checked. Nothing has been created. Check your connection and try again.')}</p>
      <button type="button" className="btn-secondary mt-2" onClick={onRetry}>{t('Check again')}</button>
    </Notice>
  );
  if (!plan) return null;

  const groups = groupPlan(plan);
  const conflicts = plan.items.filter(i => i.action === 'conflict').length;
  const creates = plan.items.filter(i => i.action === 'create').length;
  const reuses = plan.items.filter(i => i.action === 'reuse').length;
  return (
    <div className="space-y-6" data-testid="review-plan">
      <div className="flex flex-wrap items-center gap-3 text-sm">
        <span className={`rounded-full px-3 py-1 font-semibold ${ACTION_STYLE.create}`}>{t('{count} to create', { count: creates })}</span>
        <span className={`rounded-full px-3 py-1 font-semibold ${ACTION_STYLE.reuse}`}>{t('{count} to reuse', { count: reuses })}</span>
        <span className={`rounded-full px-3 py-1 font-semibold ${conflicts ? ACTION_STYLE.conflict : ACTION_STYLE.reuse}`}>{t('{count, plural, one {# conflict} other {# conflicts}}', { count: conflicts })}</span>
        <button type="button" className="btn-secondary ms-auto" onClick={onRetry} disabled={state === 'loading'}>{state === 'loading' ? t('Checking…') : t('Check again')}</button>
      </div>

      {!canCommit(plan) && (
        <Notice tone="error" role="alert">
          <p className="font-semibold">{t('Resolve these before creating the company. Nothing has been created yet.')}</p>
          <ul className="mt-2 space-y-1.5">
            {plan.problems.map((p, i) => (
              <li key={`p-${i}`} className="flex flex-wrap items-center gap-2">
                <span>{problemText(p)}</span>
                <button type="button" className="text-sm font-semibold underline" onClick={() => onGoTo(stepOf(p))}>{t('Go to {step}', { step: t(WIZARD_STEPS[stepOf(p)].title) })}</button>
              </li>
            ))}
            {plan.items.filter(i => i.action === 'conflict').map(i => {
              const step = groups.find(g => g.items.includes(i))?.step ?? 7;
              return (
                <li key={`c-${i.area}-${i.code}`} className="flex flex-wrap items-center gap-2">
                  <span>{t('{area} {code}: {reason}', { area: t(AREA_LABEL[i.area]), code: i.code, reason: i.reason || t('Already exists') })}</span>
                  <button type="button" className="text-sm font-semibold underline" onClick={() => onGoTo(step)}>{t('Go to {step}', { step: t(WIZARD_STEPS[step].title) })}</button>
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
          {groups.map(group => (
            <section key={group.step} aria-labelledby={`ccw-plan-${group.step}`} className="rounded-xl border border-slate-200 dark:border-white/10" data-testid={`plan-group-${group.step}`}>
              <div className="flex flex-wrap items-center justify-between gap-2 border-b border-slate-100 px-4 py-2.5 dark:border-white/10">
                <h3 id={`ccw-plan-${group.step}`} className="text-sm font-semibold text-slate-900 dark:text-white">{t(WIZARD_STEPS[group.step].title)}</h3>
                {visibleSteps.includes(group.step) && <button type="button" className="text-xs font-semibold text-sapphire hover:underline dark:text-cyanAccent" onClick={() => onGoTo(group.step)}>{t('Change')}<span className="sr-only"> {t(WIZARD_STEPS[group.step].title)}</span></button>}
              </div>
              <ul className="divide-y divide-slate-100 dark:divide-white/10">
                {group.items.map((item, i) => (
                  <li key={`${item.area}-${item.code}-${i}`} className="flex flex-wrap items-start justify-between gap-2 px-4 py-2.5" data-action={item.action}>
                    <div className="min-w-0">
                      <p className="text-sm text-slate-900 dark:text-white"><span className="text-slate-500 dark:text-slate-400">{t(AREA_LABEL[item.area])}</span>{' · '}<bdi>{locale === 'ar' ? item.labelAr || item.label : item.label}</bdi>{item.code && <span className="ms-1 text-xs text-slate-500"><bdi>{item.code}</bdi></span>}</p>
                      {item.reason && <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-400"><bdi>{item.reason}</bdi></p>}
                    </div>
                    <span className={`shrink-0 rounded-full px-2 py-0.5 text-[11px] font-semibold ${ACTION_STYLE[item.action]}`}>{actionLabel(item.action)}</span>
                  </li>
                ))}
              </ul>
            </section>
          ))}
        </div>
        <aside className="rounded-xl border border-slate-200 p-4 dark:border-white/10" aria-label={t('Company structure')}>
          <p className="mb-2 text-sm font-semibold text-slate-900 dark:text-white">{t('Company structure')}</p>
          <StructureTree tree={buildTree(body, context)} />
        </aside>
      </div>
    </div>
  );
}
