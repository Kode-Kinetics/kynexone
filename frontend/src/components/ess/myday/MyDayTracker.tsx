'use client';

/**
 * "My requests": what the employee has sent and where each one stands, as named steps. One row is
 * open at a time (the one waiting for the employee first). Every row and step comes from the API's
 * own request data; a source that failed to load says so instead of looking empty.
 */

import { useId, useState } from 'react';
import Link from 'next/link';
import { AlertCircle, Banknote, CalendarOff, Check, ChevronDown, ClipboardList, type LucideIcon } from 'lucide-react';
import { StatusChip } from '../../StatusChip';
import { useT } from '../../../hooks/useT';
import { ESS_LEAVE_PATH, ESS_REQUESTS_PATH } from '../../../lib/essSelfService';
import type { TrackerItem, TrackerStep } from '../../../lib/myDay';

const ICON: Record<TrackerItem['kind'], { icon: LucideIcon; tone: string }> = {
  hr: { icon: ClipboardList, tone: 'bg-violet-500/10 text-violet-700 dark:text-violet-300' },
  leave: { icon: CalendarOff, tone: 'bg-emerald-500/10 text-emerald-700 dark:text-emerald-300' },
  advance: { icon: Banknote, tone: 'bg-amber-500/10 text-amber-700 dark:text-amber-300' },
  loan: { icon: Banknote, tone: 'bg-amber-500/10 text-amber-700 dark:text-amber-300' },
};

const MAX_ROWS = 4;

function Step({ step, last }: { step: TrackerStep; last: boolean }) {
  const t = useT();
  const done = step.state === 'done';
  const cur = step.state === 'current';
  const bad = step.state === 'problem';
  return (
    <li aria-current={cur ? 'step' : undefined} className="relative flex min-w-0 flex-1 gap-2.5 sm:block sm:pt-6 sm:pe-2">
      {!last && <span aria-hidden="true" className={`absolute start-6 end-0 top-2.5 hidden h-0.5 sm:block ${done ? 'bg-emerald-500' : 'bg-slate-200 dark:bg-white/15'}`} />}
      <span aria-hidden="true" className={`relative z-10 grid h-5 w-5 shrink-0 place-items-center rounded-full border-2 sm:absolute sm:start-0 sm:top-0 ${
        done ? 'border-emerald-600 bg-emerald-600 text-white dark:border-emerald-400 dark:bg-emerald-400 dark:text-emerald-950'
          : bad ? 'border-rose-600 bg-rose-600 text-white dark:border-rose-400 dark:bg-rose-400 dark:text-rose-950'
            : cur ? 'border-sapphire bg-white ring-4 ring-sapphire/15 dark:border-blue-400 dark:bg-slate-900 dark:ring-blue-400/20'
              : 'border-slate-400 bg-white dark:border-slate-500 dark:bg-slate-900'}`}>
        {done && <Check className="h-3 w-3" strokeWidth={3.5} />}
        {bad && <AlertCircle className="h-3 w-3" strokeWidth={3} />}
        {cur && <span className="h-1.5 w-1.5 rounded-full bg-sapphire dark:bg-blue-400" />}
      </span>
      <span className="min-w-0">
        <span aria-hidden="true" className="block text-meta font-semibold text-slate-900 dark:text-white">{step.label}</span>
        <span className="sr-only">{t(done ? '{step}, done' : cur ? '{step}, current step' : bad ? '{step}, needs attention' : '{step}, to do', { step: step.label })}</span>
        {step.detail && <span className="block text-meta text-slate-600 dark:text-slate-300"><bdi>{step.detail}</bdi></span>}
      </span>
    </li>
  );
}

export function MyDayTracker({ items, failed, onRetry }: { items: TrackerItem[]; failed: string[]; onRetry: () => void }) {
  const t = useT();
  const uid = useId();
  const shown = items.slice(0, MAX_ROWS);
  // The first one waiting for the employee opens by default; after that the employee chooses.
  const [open, setOpen] = useState<string | null | undefined>(undefined);
  const openId = open === undefined ? (shown.find((i) => i.needsYou) ?? shown[0])?.id ?? null : open;

  return (
    <section aria-labelledby={`${uid}-title`} data-testid="ess-home-hr-requests" className="wg-card flex min-w-0 flex-col gap-2 p-4">
      <header className="flex min-h-8 items-center justify-between gap-3">
        <h2 id={`${uid}-title`} className="flex items-center gap-2 text-body font-semibold text-slate-900 dark:text-white">
          {t('My requests')}
          {items.length > 0 && <span className="rounded-full bg-sapphire/10 px-2 py-0.5 text-meta font-semibold tabular-nums text-sapphire dark:bg-blue-400/15 dark:text-blue-300">{items.length}</span>}
        </h2>
        <Link href={ESS_REQUESTS_PATH} className="rounded-lg px-1 py-1 text-body-sm font-semibold text-sapphire outline-none hover:underline focus-visible:ring-2 focus-visible:ring-sapphire dark:text-blue-300">{t('Open my requests')}</Link>
      </header>

      {failed.length > 0 && (
        <p role="alert" className="flex flex-wrap items-center gap-2 rounded-xl bg-amber-50 px-3 py-2 text-meta text-amber-900 dark:bg-amber-500/10 dark:text-amber-200">
          <AlertCircle className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
          <span>{t('Could not load: {sources}. They may be missing here.', { sources: failed.join(', ') })}</span>
          <button type="button" onClick={onRetry} className="rounded font-semibold underline outline-none focus-visible:ring-2 focus-visible:ring-sapphire">{t('Retry')}</button>
        </p>
      )}

      {shown.length === 0 && failed.length === 0 && (
        <div className="flex flex-1 flex-col items-center justify-center gap-1 py-6 text-center" data-empty-state>
          <span aria-hidden="true" className="grid h-10 w-10 place-items-center rounded-full bg-emerald-500/10 text-emerald-700 dark:text-emerald-300"><Check className="h-5 w-5" /></span>
          <p className="text-body font-semibold text-slate-900 dark:text-white">{t('Nothing waiting')}</p>
          <p className="max-w-[40ch] text-body-sm text-slate-600 dark:text-slate-300">{t('Leave, letters and changes you ask for are tracked here, step by step.')}</p>
          <Link href={ESS_LEAVE_PATH} className="btn-secondary btn-sm mt-1">{t('Request leave')}</Link>
        </div>
      )}

      {shown.length > 0 && (
        <ul className="flex flex-col gap-1.5">
          {shown.map((item) => {
            const isOpen = openId === item.id;
            const { icon: Icon, tone } = ICON[item.kind];
            const bodyId = `${uid}-${item.id}`;
            const action = item.action;
            return (
              <li key={item.id} data-request={item.id} className={`rounded-xl border transition-colors duration-fast ${isOpen ? 'border-sapphire/40 bg-sapphire/[0.04] dark:border-blue-400/40 dark:bg-blue-400/[0.06]' : 'border-[color:var(--wg-line)]'}`}>
                <button type="button" aria-expanded={isOpen} aria-controls={bodyId} onClick={() => setOpen(isOpen ? null : item.id)}
                  className="grid min-h-[3rem] w-full grid-cols-[1.75rem_minmax(0,1fr)_auto_1rem] items-center gap-x-2.5 gap-y-1 rounded-xl px-2.5 py-1.5 text-start outline-none focus-visible:ring-2 focus-visible:ring-sapphire max-sm:grid-cols-[1.75rem_minmax(0,1fr)_1rem]">
                  <span aria-hidden="true" className={`grid h-7 w-7 place-items-center rounded-lg ${tone}`}><Icon className="h-4 w-4" /></span>
                  <span className="min-w-0">
                    <span className="block text-body-sm font-semibold text-slate-900 dark:text-white"><bdi>{item.title}</bdi></span>
                    <span className="block text-meta text-slate-600 dark:text-slate-300"><bdi>{item.meta}</bdi></span>
                  </span>
                  <span className="max-sm:col-start-2 max-sm:row-start-2 max-sm:justify-self-start"><StatusChip label={item.statusLabel} tone={item.tone} dot /></span>
                  <ChevronDown aria-hidden="true" className={`h-4 w-4 text-slate-500 transition-transform duration-base max-sm:col-start-3 max-sm:row-start-1 dark:text-slate-400 ${isOpen ? 'rotate-180' : ''}`} />
                </button>
                {isOpen && (
                  <div id={bodyId} className="pb-3 pe-3 ps-3 sm:ps-12">
                    {item.steps.length > 0 && (
                      <ol className="mb-2 flex flex-col gap-2 sm:flex-row sm:gap-0" aria-label={t('Steps')}>
                        {item.steps.map((s, i) => <Step key={s.key} step={s} last={i === item.steps.length - 1} />)}
                      </ol>
                    )}
                    {item.note && <p className="mb-2 text-body-sm text-slate-700 dark:text-slate-200">{item.note}</p>}
                    {action?.href && (
                      <Link href={action.href} className={item.needsYou ? 'btn-primary btn-sm' : 'btn-secondary btn-sm'}>{action.label}</Link>
                    )}
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}
      {items.length > shown.length && (
        <p className="text-meta text-slate-600 dark:text-slate-300">{t('+{count} more', { count: items.length - shown.length })}</p>
      )}
    </section>
  );
}
