'use client';

import { AlertCircle, Check, Info, Minus } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { PART_COPY, partsOf, stepCopy, type PartId, type PartState, type StepId, type StepState, type WizardMode } from '../../lib/companySetupWizard';

/**
 * The step rail: number, title and state for every step, with the current step's parts nested under it at lg and up
 * (Addendum B4). Blocked (must fix) is rose, review (worth a look) is amber. A step that cannot be opened yet stays
 * focusable and says which step to finish first; choosing it opens that step. A thin bar shows overall progress.
 */
export function WizardRail({ steps, current, currentPart, states, saved, issues, partStates, reachable, reachablePart, blockingTitle, onSelect, onSelectPart, disabled, mode = 'create', progress = 0 }: {
  steps: StepId[];
  current: StepId;
  currentPart?: PartId;
  states: Record<StepId, StepState>;
  /** Per step, beside its state: Saved once every section of it was saved with Save, Unsaved changes otherwise. */
  saved?: Partial<Record<StepId, 'saved' | 'unsaved' | null>>;
  /** Counts for the sub-label: fields to fix, things to check. */
  issues?: Partial<Record<StepId, { fix: number; check: number }>>;
  partStates?: Partial<Record<PartId, PartState>>;
  reachable: (step: StepId) => boolean;
  reachablePart?: (part: PartId) => boolean;
  /** The title (translated) of the step or part to finish first, for an unreachable step's sub-label. */
  blockingTitle?: string;
  /** Called for every step, reachable or not: the wizard opens the blocking one when it is not. */
  onSelect: (step: StepId) => void;
  onSelectPart?: (part: PartId) => void;
  disabled?: boolean;
  mode?: WizardMode;
  /** 0–1: the share of positions behind the current one. */
  progress?: number;
}) {
  const t = useT();
  const position = steps.indexOf(current) + 1;
  const subLabel = (id: StepId, state: StepState) => {
    const count = issues?.[id];
    if (state === 'blocked') return count?.fix ? t('{count, plural, one {# field to fix} other {# fields to fix}}', { count: count.fix }) : t('Needs a fix');
    if (state === 'review') return count?.check ? t('{count, plural, one {# thing to check} other {# things to check}}', { count: count.check }) : t('Worth a look');
    if (state === 'inProgress') return id === 7 ? t('Not checked yet') : t('In progress');
    if (state === 'done') return t('Done');
    return t('Not started');
  };
  const partLabel = (state: PartState) => state === 'done' ? t('Done') : state === 'blocked' ? t('Needs a fix') : state === 'review' ? t('Worth a look') : state === 'skipped' ? t('Skipped') : t('Not started');
  const parts = partsOf(current);
  const partIndex = currentPart ? parts.indexOf(currentPart) + 1 : 0;
  const clamped = Math.min(Math.max(progress, 0), 1);
  return (
    <nav aria-label={mode === 'existing' ? t('Company setup steps') : t('Create company steps')}>
      <div className="mb-2 h-1 overflow-hidden rounded-full bg-slate-200/80 dark:bg-white/10" aria-hidden="true">
        <div className="h-full origin-left rounded-full bg-sapphire transition-transform duration-200 ease-out motion-reduce:transition-none rtl:origin-right"
          style={{ transform: `scaleX(${clamped})` }} data-testid="wizard-progress" data-progress={clamped.toFixed(2)} />
      </div>
      <ol className={`grid gap-1 lg:grid-cols-1 ${steps.length === 6 ? 'grid-cols-6' : 'grid-cols-7'}`} data-testid="wizard-rail">
        {steps.map((id, index) => {
          const active = id === current;
          const state = states[id];
          const savedState = saved?.[id] ?? null;
          const nested = active && parts.length > 0;
          const locked = !active && !reachable(id);
          const whyId = `ccw-rail-why-${id}`;
          return (
            <li key={id} className="min-w-0">
              <button type="button" onClick={() => onSelect(id)} disabled={disabled}
                aria-disabled={locked || undefined} aria-describedby={locked ? whyId : undefined}
                aria-current={active ? (nested && currentPart ? 'true' : 'step') : undefined}
                data-state={state} data-saved={savedState ?? undefined} data-locked={locked ? 'true' : undefined}
                className={`flex w-full items-center justify-center gap-3 rounded-lg px-1 py-2 text-start transition-colors duration-150 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 disabled:cursor-wait lg:justify-start lg:px-3 ${
                  active ? 'bg-sapphire/10 text-sapphire dark:bg-sapphire/20 dark:text-blue-200'
                    : locked ? 'cursor-not-allowed text-slate-500 dark:text-slate-500'
                    : 'text-slate-700 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/5'}`}>
                <span className={`flex h-7 w-7 shrink-0 items-center justify-center rounded-full border text-xs font-semibold transition-colors duration-150 ${
                  state === 'done' ? 'border-emerald-600 bg-emerald-600 text-white'
                    : state === 'blocked' ? 'border-rose-600 bg-rose-50 text-rose-700 dark:border-rose-400 dark:bg-rose-500/15 dark:text-rose-200'
                    : state === 'review' ? 'border-amber-500 bg-amber-50 text-amber-800 dark:bg-amber-500/15 dark:text-amber-200'
                    : active ? 'border-sapphire bg-sapphire text-white'
                    : locked ? 'border-dashed border-slate-300 bg-transparent text-slate-500 dark:border-slate-600'
                    : 'border-slate-300 bg-white text-slate-600 dark:border-slate-600 dark:bg-transparent dark:text-slate-300'}`}>
                  {state === 'done' ? <Check className="h-3.5 w-3.5 motion-safe:animate-scale-in" aria-hidden="true" />
                    : state === 'blocked' ? <AlertCircle className="h-3.5 w-3.5" aria-hidden="true" />
                    : state === 'review' ? <Info className="h-3.5 w-3.5" aria-hidden="true" />
                    : index + 1}
                </span>
                <span className="sr-only lg:not-sr-only lg:min-w-0">
                  <span className="block text-sm font-semibold">{t(stepCopy(id, mode).title)}</span>
                  <span id={whyId} className="block text-xs leading-5 text-slate-600 dark:text-slate-400">
                    {locked && blockingTitle ? t('Finish {step} first', { step: blockingTitle }) : subLabel(id, state)}
                    {savedState && <>{' · '}<span className={savedState === 'saved' ? 'text-emerald-700 dark:text-emerald-300' : 'font-medium text-amber-800 dark:text-amber-300'}>{savedState === 'saved' ? t('Saved') : t('Unsaved changes')}</span></>}
                  </span>
                </span>
              </button>
              {nested && (
                <ol className="ms-[2.375rem] mt-1 hidden space-y-0.5 border-s border-slate-200 ps-3 motion-safe:animate-fade-in-fast dark:border-white/10 lg:block" data-testid="wizard-rail-parts">
                  {parts.map(part => {
                    const isCurrent = part === currentPart;
                    const partState = partStates?.[part] ?? 'notStarted';
                    const partLocked = !isCurrent && !(reachablePart?.(part) ?? false);
                    return (
                      <li key={part}>
                        <button type="button" onClick={() => onSelectPart?.(part)} disabled={disabled}
                          aria-disabled={partLocked || undefined}
                          aria-current={isCurrent ? 'step' : undefined} data-part={part} data-state={partState}
                          className={`flex h-8 w-full min-w-0 items-center gap-2 rounded-md px-2 text-start text-[13px] transition-colors duration-150 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 ${
                            isCurrent ? 'bg-white font-semibold text-sapphire shadow-sm ring-1 ring-sapphire/20 dark:bg-white/10 dark:text-blue-200'
                              : partLocked ? 'cursor-not-allowed text-slate-500'
                              : 'text-slate-600 hover:bg-slate-100 hover:text-slate-900 dark:text-slate-400 dark:hover:bg-white/5 dark:hover:text-white'}`}>
                          <span className={`flex h-2 w-2 shrink-0 rounded-full ${
                            partState === 'done' ? 'bg-emerald-600'
                              : partState === 'blocked' ? 'bg-rose-600'
                              : partState === 'review' ? 'bg-amber-500'
                              : isCurrent ? 'bg-sapphire' : 'bg-slate-300 dark:bg-slate-600'}`} aria-hidden="true" />
                          <span className="min-w-0 flex-1 truncate">{t(PART_COPY[part].title)}</span>
                          <span className="sr-only">{partLabel(partState)}</span>
                          {partState === 'skipped' && <Minus className="h-3 w-3 shrink-0 text-slate-500" aria-hidden="true" />}
                          {partState === 'skipped' && <span className="shrink-0 text-[11px] font-normal text-slate-600 dark:text-slate-400" aria-hidden="true">{t('Skipped')}</span>}
                        </button>
                      </li>
                    );
                  })}
                </ol>
              )}
            </li>
          );
        })}
      </ol>
      <p className="mt-2 text-sm font-medium text-slate-800 dark:text-slate-100 lg:hidden" data-testid="wizard-rail-position">
        {currentPart && parts.length > 0
          ? t('Step {position} of {total} · {title} · Part {part} of {parts}: {partTitle}', { position, total: steps.length, title: t(stepCopy(current, mode).title), part: partIndex, parts: parts.length, partTitle: t(PART_COPY[currentPart].title) })
          : t('Step {position} of {total}: {title}', { position, total: steps.length, title: t(stepCopy(current, mode).title) })}
      </p>
    </nav>
  );
}
