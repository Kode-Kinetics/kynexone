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
  const shown = items.slice(0, 4);
  return (
    <section aria-labelledby="attention-heading" className="flex flex-col gap-2.5">
      <h2 id="attention-heading" className="flex items-baseline gap-2 text-[15px] font-semibold text-slate-900 dark:text-white">
        {t('Needs attention')}
        <span className="text-xs font-medium text-slate-600 dark:text-slate-400">
          {items.filter((i) => i.severity === 'critical').length} {t('critical')}, {items.filter((i) => i.severity !== 'critical').length} {t('to review')}
          {findingsUnavailable ? `. ${t('The rules check could not be loaded.')}` : ''}
        </span>
      </h2>
      <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {shown.map((it) => {
          const crit = it.severity === 'critical';
          return (
            <li key={it.id}>
              <Link href={it.to}
                className={`wg-card wg-press group flex h-full flex-col gap-1.5 border-s-0 p-4 outline-none hover:border-[color:var(--wg-line-strong)] focus-visible:ring-2 focus-visible:ring-sapphire`}>
                <span className="flex items-start gap-2.5">
                  <span aria-hidden className={`mt-1 h-2.5 w-2.5 shrink-0 ${crit ? 'rounded-[2px] bg-rose-600' : 'rounded-full bg-amber-500'}`} />
                  <span className="sr-only">{crit ? t('Critical') : t('Warning')}: </span>
                  <span className="text-[14px] font-semibold leading-snug text-slate-900 dark:text-white">{it.title}</span>
                </span>
                <span className="line-clamp-2 ps-5 text-[13px] leading-snug text-slate-700 dark:text-slate-300">{it.detail}</span>
                <span className="mt-auto flex flex-wrap items-center justify-between gap-x-2 gap-y-1 ps-5 pt-1 text-xs">
                  <span className="text-slate-600 dark:text-slate-400">{it.source}</span>
                  <span className="inline-flex shrink-0 items-center gap-1 font-semibold text-sapphire group-hover:underline dark:text-blue-300">{t(it.cta)} <ArrowRight className="h-3 w-3" aria-hidden /></span>
                </span>
              </Link>
            </li>
          );
        })}
      </ul>
      {items.length > shown.length && (
        <p className="text-xs text-slate-600 dark:text-slate-400">{items.length - shown.length} {t('more in the modules above:')} {items.slice(4).map((i) => i.title).join('; ')}.</p>
      )}
    </section>
  );
}
