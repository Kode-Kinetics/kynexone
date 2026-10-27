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
  blue: 'text-primary-fg',
  cyan: 'text-info-fg',
  emerald: 'text-success-fg',
  amber: 'text-warning-fg',
  rose: 'text-danger-fg',
};

export function KpiCard({ metric }: { metric: KpiMetric }) {
  return (
    <article
      className={`surface border-s-[3px] p-4 ${borderTone[metric.tone]}`}
    >
      <div className="flex items-start justify-between gap-2">
        <p className="text-meta font-medium text-fg-muted">
          {metric.label}
        </p>
        {metric.trend === 'up' && (
          <TrendingUp className={`h-3.5 w-3.5 shrink-0 ${textTone[metric.tone]}`} />
        )}
        {metric.trend === 'down' && (
          <TrendingDown className="h-3.5 w-3.5 shrink-0 text-danger-fg" />
        )}
      </div>
      <p className="mt-2.5 text-kpi font-semibold tabular-nums text-fg">
        {metric.value}
      </p>
      <p className={`mt-1 text-meta font-medium ${textTone[metric.tone]}`}>{metric.delta}</p>
    </article>
  );
}
