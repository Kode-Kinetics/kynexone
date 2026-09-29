'use client';

import { TrendingDown, TrendingUp } from 'lucide-react';
import type { KpiMetric } from '../types/ui';

const borderTone: Record<KpiMetric['tone'], string> = {
  blue: 'border-s-sapphire',
  cyan: 'border-s-cyanAccent',
  emerald: 'border-s-emeraldZ',
  amber: 'border-s-amber-400',
  rose: 'border-s-rose-500',
};

const textTone: Record<KpiMetric['tone'], string> = {
  blue: 'text-sapphire',
  cyan: 'text-cyan-600 dark:text-cyanAccent',
  emerald: 'text-emerald-600 dark:text-emerald-400',
  amber: 'text-amber-600 dark:text-amber-400',
  rose: 'text-rose-600 dark:text-rose-400',
};

export function KpiCard({ metric }: { metric: KpiMetric }) {
  return (
    <article
      className={`surface rounded-xl border-s-[3px] p-4 transition-shadow hover:shadow-soft ${borderTone[metric.tone]}`}
    >
      <div className="flex items-start justify-between gap-2">
        <p className="text-xs font-semibold uppercase tracking-[0.08em] text-slate-500 dark:text-slate-400">
          {metric.label}
        </p>
        {metric.trend === 'up' && (
          <TrendingUp className={`h-3.5 w-3.5 shrink-0 ${textTone[metric.tone]}`} />
        )}
        {metric.trend === 'down' && (
          <TrendingDown className="h-3.5 w-3.5 shrink-0 text-rose-500" />
        )}
      </div>
      <p className="mt-2.5 text-2xl font-bold tracking-tight text-slate-950 dark:text-white">
        {metric.value}
      </p>
      <p className={`mt-1 text-xs font-medium ${textTone[metric.tone]}`}>{metric.delta}</p>
    </article>
  );
}
