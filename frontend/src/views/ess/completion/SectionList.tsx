'use client';

import { CheckCircle2, ChevronRight, Clock, ShieldAlert } from 'lucide-react';
import type { EssCompletion, EssSection, SectionState } from '../../../api/essCompletion';
import { useLocale } from '../../../contexts/LocaleContext';

const CHIP: Record<SectionState, string> = {
  Missing: 'bg-amber-50 text-amber-800 dark:bg-amber-500/10 dark:text-amber-300',
  Confirm: 'bg-sky-50 text-sky-800 dark:bg-sky-500/10 dark:text-sky-300',
  Pending: 'bg-slate-100 text-slate-700 dark:bg-white/10 dark:text-slate-200',
  ChangesRequested: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-300',
  Accepted: 'bg-emerald-50 text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-300',
  AwaitingFinance: 'bg-violet-50 text-violet-800 dark:bg-violet-500/10 dark:text-violet-300',
};

function StateIcon({ state }: { state: SectionState }) {
  if (state === 'Accepted') return <CheckCircle2 className="h-4 w-4" aria-hidden="true" />;
  if (state === 'Pending' || state === 'AwaitingFinance') return <Clock className="h-4 w-4" aria-hidden="true" />;
  if (state === 'ChangesRequested') return <ShieldAlert className="h-4 w-4" aria-hidden="true" />;
  return null;
}

/** Progress and one row per section HR still needs. The state is words as well as colour. */
export function SectionList({ data, selected, onSelect }: { data: EssCompletion; selected: string | null; onSelect: (s: EssSection) => void }) {
  const { t } = useLocale();
  const { done, total } = data.progress;
  const pct = total === 0 ? 100 : Math.round((done / total) * 100);
  return (
    <div className="space-y-3">
      <div>
        <div className="flex items-center justify-between text-xs font-semibold text-slate-600 dark:text-slate-300">
          <span id="ess-progress-label">{t('ess.completion.ui.progress')}</span>
          <span>{t('{done} of {total} sections done', { done, total })}</span>
        </div>
        <div role="progressbar" aria-labelledby="ess-progress-label" aria-valuemin={0} aria-valuemax={total} aria-valuenow={done} className="mt-1 h-2 overflow-hidden rounded-full bg-slate-100 dark:bg-white/10">
          <div className="h-full rounded-full bg-sapphire dark:bg-cyanAccent" style={{ width: `${pct}%` }} />
        </div>
      </div>
      {data.sections.length === 0 ? <p role="status" className="rounded-xl bg-emerald-50 p-4 text-sm font-medium text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-300">{t('Everything HR asked for is done. Thank you.')}</p> : (
        <ul className="space-y-2" aria-label={t('Complete your profile')}>
          {data.sections.map(s => (
            <li key={s.key}>
              <button type="button" data-testid={`section-${s.key}`} onClick={() => onSelect(s)} aria-current={selected === s.key ? 'true' : undefined}
                className={`flex min-h-[44px] w-full items-center justify-between gap-3 rounded-xl border p-3 text-start ${selected === s.key ? 'border-sapphire bg-sapphire/5 dark:border-cyanAccent' : 'border-slate-200 dark:border-white/10'}`}>
                <span className="min-w-0">
                  <span className="block text-sm font-semibold text-slate-800 dark:text-slate-100">{t(`ess.completion.section.${s.key}.title`)}</span>
                  <span className="block text-xs text-slate-500 dark:text-slate-400">{t(`ess.completion.section.${s.key}.hint`)}</span>
                </span>
                <span className="flex shrink-0 items-center gap-2">
                  <span className={`inline-flex items-center gap-1 rounded-full px-2 py-1 text-xs font-semibold ${CHIP[s.state]}`}><StateIcon state={s.state} />{t(`ess.completion.state.${s.state}`)}</span>
                  <ChevronRight className="h-4 w-4 text-slate-400" aria-hidden="true" />
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
