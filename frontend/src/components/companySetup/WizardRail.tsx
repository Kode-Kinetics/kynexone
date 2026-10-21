'use client';

import { AlertTriangle, Check } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { stepCopy, type StepId, type StepState, type WizardMode } from '../../lib/companySetupWizard';

/**
 * The step rail: number, title and state for every step. A step is reachable once every step before
 * it has no blockers, so the rail can go back freely but never skip a required field.
 */
export function WizardRail({ steps, current, states, reachable, onSelect, disabled, mode = 'create' }: {
  steps: StepId[];
  current: StepId;
  states: Record<StepId, StepState>;
  reachable: (step: StepId) => boolean;
  onSelect: (step: StepId) => void;
  disabled?: boolean;
  mode?: WizardMode;
}) {
  const t = useT();
  const position = steps.indexOf(current) + 1;
  const stateLabel = (state: StepState) => state === 'done' ? t('Done') : state === 'warning' ? t('Needs attention') : t('Not started');
  return (
    <nav aria-label={mode === 'existing' ? t('Company setup steps') : t('Create company steps')}>
      <ol className={`grid gap-1 lg:grid-cols-1 ${steps.length === 6 ? 'grid-cols-6' : 'grid-cols-7'}`} data-testid="wizard-rail">
        {steps.map((id, index) => {
          const active = id === current;
          const state = states[id];
          return (
            <li key={id} className="min-w-0">
              <button type="button" onClick={() => onSelect(id)} disabled={disabled || (!active && !reachable(id))}
                aria-current={active ? 'step' : undefined}
                data-state={state}
                className={`flex w-full items-center justify-center gap-3 rounded-lg px-1 py-2 text-start transition-colors disabled:cursor-not-allowed disabled:opacity-50 lg:justify-start lg:px-3 ${active ? 'bg-sapphire/10 text-sapphire dark:bg-sapphire/20 dark:text-blue-200' : 'text-slate-700 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/5'}`}>
                <span className={`flex h-7 w-7 shrink-0 items-center justify-center rounded-full border text-xs font-semibold ${
                  state === 'done' ? 'border-emerald-600 bg-emerald-600 text-white'
                    : state === 'warning' ? 'border-amber-500 bg-amber-50 text-amber-800 dark:bg-amber-500/15 dark:text-amber-200'
                    : active ? 'border-sapphire bg-sapphire text-white' : 'border-slate-300 bg-white text-slate-600 dark:border-slate-600 dark:bg-transparent dark:text-slate-300'}`}>
                  {state === 'done' ? <Check className="h-3.5 w-3.5" aria-hidden="true" /> : state === 'warning' ? <AlertTriangle className="h-3.5 w-3.5" aria-hidden="true" /> : index + 1}
                </span>
                <span className="sr-only lg:not-sr-only lg:min-w-0">
                  <span className="block text-sm font-semibold">{t(stepCopy(id, mode).title)}</span>
                  <span className="block text-xs leading-5 text-slate-500 dark:text-slate-400">{stateLabel(state)}</span>
                </span>
              </button>
            </li>
          );
        })}
      </ol>
      <p className="mt-2 text-sm font-medium text-slate-800 dark:text-slate-100 lg:hidden" data-testid="wizard-rail-position">
        {t('Step {position} of {total}: {title}', { position, total: steps.length, title: t(stepCopy(current, mode).title) })}
      </p>
    </nav>
  );
}
