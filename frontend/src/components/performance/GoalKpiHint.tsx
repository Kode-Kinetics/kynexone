'use client';

import { useEffect, useState } from 'react';
import { useLocale } from '../../contexts/LocaleContext';
import { reviewsApi } from '../../api/performance';

type GoalKpi = { goalCount: number; totalWeight: number; kpiScore: number };

/**
 * The KPI score the employee's agreed goals of this cycle give, shown in the manager review. When there are goals the
 * server takes the KPI score from them (weights must total 100%), so the form shows that number instead of asking for
 * one. Renders nothing when the employee has no agreed goal in the cycle: the reviewer then gives the KPI score.
 */
export function GoalKpiHint({ reviewId, onKpi }: { reviewId: string; onKpi: (kpi: GoalKpi | null) => void }) {
  const { t } = useLocale();
  const [kpi, setKpi] = useState<GoalKpi | null>(null);

  useEffect(() => {
    let live = true;
    reviewsApi.get(reviewId)
      .then(d => { if (!live) return; const k = d.goalKpi ?? null; setKpi(k); onKpi(k); })
      .catch(() => { if (live) onKpi(null); });
    return () => { live = false; };
    // onKpi is a fresh closure on every render of the parent; the lookup is per review.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reviewId]);

  if (!kpi) return null;
  const balanced = kpi.totalWeight === 100;
  return (
    <p role={balanced ? undefined : 'alert'}
      className={`rounded-lg px-3 py-2 text-xs ${balanced
        ? 'bg-sapphire/10 text-sapphire dark:bg-sapphire/20 dark:text-cyanAccent'
        : 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400'}`}>
      {balanced
        ? t('KPI from {count} agreed goals: {score} (weights total {weight}%)', { count: kpi.goalCount, score: kpi.kpiScore, weight: kpi.totalWeight })
        : t('The agreed goals’ weights total {weight}%. They must total 100% before the review can be submitted.', { weight: kpi.totalWeight })}
    </p>
  );
}
