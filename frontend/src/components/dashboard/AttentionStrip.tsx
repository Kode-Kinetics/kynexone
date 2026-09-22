'use client';

/**
 * Needs attention: the items that need a person, at the top of the page, each with the action
 * that resolves it. Critical first. Rendered as plain cards (never only inside a chart or a
 * swipe carousel). Absent entirely when nothing needs attention: the all-clear is one line.
 */

import Link from 'next/link';
import { ArrowRight, CheckCircle2 } from 'lucide-react';
import type { AttentionItem } from './dashboardModel';
import { useT } from '../../hooks/useT';

export function AttentionStrip({ items, findingsUnavailable }: { items: AttentionItem[]; findingsUnavailable?: boolean }) {
  const t = useT();
  if (items.length === 0) {
    return (
      <p className="flex items-center gap-2 text-[13px] text-emerald-800 dark:text-emerald-300">
        <CheckCircle2 className="h-4 w-4" aria-hidden />{t('Nothing needs attention: no expired records, missing documents, pending corrections or open rule findings.')}
      </p>
    );
  }
  const critical = items.filter((i) => i.severity === 'critical').length;
  // One compact row: each issue is a pill with its action. The longer explanation is in the
  // tooltip and in the accessible name, so the row costs one line of height, not a card grid.
  return (
    <section aria-labelledby="attention-heading" className="flex min-w-0 items-center gap-2 max-sm:flex-wrap">
      <h2 id="attention-heading" className="me-1 shrink-0 whitespace-nowrap text-[13px] font-semibold text-slate-900 dark:text-white">
        {t('Needs attention')}
        <span className="ms-1.5 font-medium text-slate-600 dark:text-slate-400">{critical} {t('critical')}, {items.length - critical} {t('to review')}</span>
        {findingsUnavailable && <span className="ms-1.5 font-normal text-slate-600 dark:text-slate-400">({t('rules check unavailable')})</span>}
      </h2>
      <ul className="flex min-w-0 flex-1 items-center gap-2 overflow-hidden max-sm:basis-full max-sm:flex-col max-sm:items-stretch">
        {items.slice(0, 3).map((it) => {
          const crit = it.severity === 'critical';
          return (
            <li key={it.id} className="min-w-0 shrink">
              <Link href={it.to} title={`${it.detail} (${it.source})`}
                className={`wg-press group inline-flex h-8 max-w-full items-center gap-2 rounded-full border px-3 text-[13px] outline-none focus-visible:ring-2 focus-visible:ring-sapphire ${
                  crit ? 'border-rose-200 bg-rose-50 text-rose-950 hover:border-rose-300 dark:border-rose-400/25 dark:bg-rose-500/10 dark:text-rose-100'
                       : 'border-amber-200 bg-amber-50 text-amber-950 hover:border-amber-300 dark:border-amber-400/25 dark:bg-amber-500/10 dark:text-amber-100'}`}>
                <span aria-hidden className={`h-2 w-2 shrink-0 ${crit ? 'rounded-[2px] bg-rose-600' : 'rounded-full bg-amber-500'}`} />
                <span className="sr-only">{crit ? t('Critical') : t('Warning')}: </span>
                <span className="min-w-0 truncate font-medium">{it.title}</span>
                <span className="inline-flex shrink-0 items-center gap-0.5 whitespace-nowrap font-semibold text-sapphire group-hover:underline dark:text-blue-300">
                  {t(it.cta)} <ArrowRight className="h-3 w-3" aria-hidden />
                </span>
                <span className="sr-only">. {it.detail}</span>
              </Link>
            </li>
          );
        })}
      </ul>
      {items.length > 3 && <span className="shrink-0 whitespace-nowrap text-xs text-slate-600 dark:text-slate-400" title={items.slice(3).map((i) => i.title).join('; ')}>+{items.length - 3} {t('more')}</span>}
    </section>
  );
}
