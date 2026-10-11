'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState } from 'react';
import { X } from 'lucide-react';
import { successionApi, type DrillResult, type Metric, type ReportSummary } from '../../api/succession';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import {
  METRIC_GROUPS, expectedDrillRows, personName, isRate, metricDefinition, metricLabel, metricNeedsSlateAccess, onTrackReason, planStatusLabel, planTone,
  readinessLabel, readinessTone, successionError,
} from '../../lib/succession';
import { StatusChip } from '../StatusChip';
import { ErrorBlock, GapChips, LoadingRows, PersonName, ProgressBar } from './SuccessionParts';

/**
 * The workspace's headline figures, gaps first (SUC_DESIGN §6–7). Every tile shows its definition on hover and focus,
 * and opens a panel listing exactly the rows behind it (GET reports/drill) — the figure and the row count match.
 */
export function KpiTiles({ canSeeNames, refreshKey = 0 }: { canSeeNames: boolean; refreshKey?: number }) {
  const { t } = useLocale();
  const fx = useFormat();
  const [summary, setSummary] = useState<ReportSummary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [drill, setDrill] = useState<{ metric: Metric; result: DrillResult | null; error: string | null } | null>(null);
  const drillRef = useRef<HTMLElement>(null);

  const load = useCallback(async () => {
    setError(null);
    try { setSummary(await successionApi.summary()); } catch (e) { setError(successionError(e, t)); }
  }, [t, refreshKey]); // refreshKey: re-read after a change elsewhere on the page
  useEffect(() => { void load(); }, [load]);

  const open = async (metric: Metric) => {
    setDrill({ metric, result: null, error: null });
    try {
      const result = await successionApi.drill(metric.drill);
      setDrill({ metric, result, error: null });
    } catch (e) {
      setDrill({ metric, result: null, error: successionError(e, t) });
    }
  };
  useEffect(() => { if (drill) drillRef.current?.focus(); }, [drill?.metric.key]); // eslint-disable-line react-hooks/exhaustive-deps

  if (error) return <ErrorBlock message={error} onRetry={() => void load()} />;
  if (!summary) return <LoadingRows label={t('suc.kpi.loading')} rows={2} />;
  const byKey = new Map(summary.metrics.map((m) => [m.key, m]));

  return (
    <div className="space-y-4" data-testid="suc-kpis">
      <p className="text-xs text-slate-500 dark:text-slate-400">{t('suc.kpi.asOf', { date: fx.date(summary.asOf), count: summary.activePositions })}</p>
      {METRIC_GROUPS.map((group) => {
        const metrics = group.keys.map((k) => byKey.get(k)).filter((m): m is Metric => !!m);
        if (metrics.length === 0) return null;
        return (
          <div key={group.id} className="space-y-2">
            <h2 className="text-xs font-bold uppercase tracking-wide text-slate-500 dark:text-slate-400">{t(group.label)}</h2>
            <ul className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-5" aria-label={t(group.label)}>
              {metrics.map((m) => (
                <li key={m.key}>
                  <Tile metric={m} active={drill?.metric.key === m.key} locked={metricNeedsSlateAccess(m.drill) && !canSeeNames} onOpen={() => void open(m)} />
                </li>
              ))}
            </ul>
          </div>
        );
      })}

      {drill && (
        <section ref={drillRef} tabIndex={-1} data-testid="suc-drill" data-metric={drill.metric.key}
          className="wg-card space-y-3 rounded-2xl p-4 outline-none" aria-label={t(metricLabel[drill.metric.key] ?? drill.metric.key)}>
          <div className="flex items-start justify-between gap-2">
            <div>
              <h3 className="text-sm font-bold text-slate-800 dark:text-slate-100">{t(metricLabel[drill.metric.key] ?? drill.metric.key)}</h3>
              <p className="text-xs text-slate-500 dark:text-slate-400">{t(metricDefinition[drill.metric.key] ?? drill.metric.key)}</p>
            </div>
            <button type="button" onClick={() => setDrill(null)} className="btn-secondary h-8 shrink-0 gap-1 px-2 text-xs">
              <X className="h-3.5 w-3.5" aria-hidden="true" />{t('suc.close')}
            </button>
          </div>
          {drill.error && <ErrorBlock message={drill.error} onRetry={() => void open(drill.metric)} />}
          {!drill.error && !drill.result && <LoadingRows label={t('suc.kpi.loadingRows')} rows={2} />}
          {drill.result && <DrillRows metric={drill.metric} result={drill.result} />}
        </section>
      )}
    </div>
  );
}

function Tile({ metric, active, locked, onOpen }: { metric: Metric; active: boolean; locked: boolean; onOpen: () => void }) {
  const { t } = useLocale();
  const fx = useFormat();
  const label = t(metricLabel[metric.key] ?? metric.key);
  const definition = t(metricDefinition[metric.key] ?? metric.key);
  const value = isRate(metric) ? fx.percent(metric.value, 1) : fx.integer(metric.value);
  const tipId = `suc-kpi-def-${metric.key}`;
  const isGap = metric.key.startsWith('gap') && metric.value > 0;
  const body = (
    <>
      <span className="block text-xs font-semibold text-slate-500 dark:text-slate-400">{label}</span>
      <span className={`mt-1 block text-2xl font-bold tabular-nums ${isGap ? 'text-rose-700 dark:text-rose-300' : 'text-slate-900 dark:text-white'}`}>{value}</span>
      {isRate(metric) && (
        <span className="block text-[11px] text-slate-500 dark:text-slate-400">{t('suc.kpi.ofTotal', { count: metric.numerator ?? 0, total: metric.denominator ?? 0 })}</span>
      )}
      <span className="mt-1 block text-[11px] font-semibold text-sapphire dark:text-cyanAccent">{locked ? t('suc.kpi.namesNeedAccess') : t('suc.kpi.showRows')}</span>
      <span id={tipId} role="tooltip"
        className="pointer-events-none absolute inset-x-2 top-full z-20 mt-1 hidden rounded-lg bg-slate-900 p-2 text-[11px] font-normal text-white shadow-lg group-hover:block group-focus-visible:block dark:bg-slate-700">
        {definition}
      </span>
    </>
  );
  const cls = `group relative block h-full w-full rounded-xl border p-3 text-start transition ${active ? 'border-sapphire ring-2 ring-sapphire/30' : 'border-slate-200 dark:border-white/10'} bg-white dark:bg-white/[0.03]`;
  if (locked) {
    return <div className={cls} data-testid={`suc-kpi-${metric.key}`} tabIndex={0} aria-describedby={tipId}>{body}</div>;
  }
  return (
    <button type="button" className={`${cls} hover:border-sapphire/60`} data-testid={`suc-kpi-${metric.key}`} aria-describedby={tipId}
      aria-expanded={active} onClick={onOpen} title={definition}>
      {body}
    </button>
  );
}

function DrillRows({ metric, result }: { metric: Metric; result: DrillResult }) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const count = result.kind === 'positions' ? result.positions.length : result.kind === 'candidates' ? result.candidates.length : result.plans.length;
  const expected = expectedDrillRows(metric);
  return (
    <div className="space-y-2">
      <p className="text-xs text-slate-600 dark:text-slate-300" data-testid="suc-drill-count" data-count={count}>
        {expected === null ? t('suc.drill.countPlans', { count }) : t('suc.drill.count', { count })}
      </p>
      {count === 0 && <p className="text-xs text-slate-500">{t('suc.drill.empty')}</p>}
      {result.kind === 'positions' && result.positions.length > 0 && (
        <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
          {result.positions.map((p) => (
            <li key={p.id} data-testid="suc-drill-row" className="flex flex-wrap items-center justify-between gap-2 py-2 text-sm">
              <Link href={`/performance/succession/positions/${p.id}`} className="font-semibold text-sapphire hover:underline dark:text-cyanAccent">{p.positionTitle}</Link>
              <span className="text-xs text-slate-500">{p.companyName ?? ''}</span>
              <GapChips gaps={p.gaps} emptyLabel={t('suc.gap.none')} />
            </li>
          ))}
        </ul>
      )}
      {result.kind === 'candidates' && result.candidates.length > 0 && (
        <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
          {result.candidates.map((c) => (
            <li key={`${c.slateVersionId}-${c.nominationId}`} data-testid="suc-drill-row" className="flex flex-wrap items-center justify-between gap-2 py-2 text-sm">
              <PersonName person={c.candidate} />
              <Link href={`/performance/succession/positions/${c.criticalPositionId}`} className="text-xs text-sapphire hover:underline dark:text-cyanAccent">
                {t('suc.drill.onSlate', { position: c.positionTitle, version: c.slateVersionNo, order: c.order })}
              </Link>
              <StatusChip label={t(readinessLabel[c.readiness])} tone={readinessTone(c.readiness)} />
            </li>
          ))}
        </ul>
      )}
      {result.kind === 'plans' && result.plans.length > 0 && (
        <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
          {result.plans.map((p) => (
            <li key={p.planId} data-testid="suc-drill-row" className="grid gap-1 py-2 text-sm sm:grid-cols-[1fr_auto_10rem] sm:items-center sm:gap-3">
              <Link href={`/people?employeeId=${p.employee.employeeId}&tab=development`} className="hover:underline"><PersonName person={p.employee} /></Link>
              <span className="flex flex-wrap items-center gap-1">
                <StatusChip label={t(planStatusLabel[p.status])} tone={planTone(p.status, p.onTrack)} />
                {p.overdue > 0 && <StatusChip label={t('suc.plan.overdueCount', { count: p.overdue })} tone="rose" />}
                <span className="text-[11px] text-slate-500">{t(onTrackReason({ ...p, reviewOverdue: !p.onTrack && p.overdue === 0 }))}</span>
              </span>
              <ProgressBar percent={p.percentComplete ?? 0} label={t('suc.plan.progressFor', { name: personName(p.employee, locale) })} />
              <span className="text-[11px] text-slate-500 sm:col-span-3">{t('suc.plan.nextReview', { date: fx.date(p.nextReviewOn) })}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
