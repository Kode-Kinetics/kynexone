'use client';

import { useCallback, useEffect, useState } from 'react';
import { CartesianGrid, Legend, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { successionApi, type TrendPoint } from '../../api/succession';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { successionError } from '../../lib/succession';
import { EmptyBlock, ErrorBlock, LoadingRows, SectionCard } from './SuccessionParts';

/**
 * Coverage over time (SUC-05): at each month end, the share of active critical positions whose then-effective approved
 * slate held a Ready-now successor, and a Ready-now-or-1–2-years successor. One axis (0–100 %), two series — the second
 * dashed so the pair never relies on colour alone — with a table of the same figures underneath.
 * Palette validated with the dataviz validator: light #2F6BFF/#0E9F8E, dark #5B8CFF/#1FA899 (all checks pass).
 */
export function ReportsPanel() {
  const { t, dir } = useLocale();
  const fx = useFormat();
  const [months, setMonths] = useState(12);
  const [points, setPoints] = useState<TrendPoint[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    setPoints(null);
    try { setPoints(await successionApi.trend(months)); } catch (e) { setError(successionError(e, t)); }
  }, [months, t]);
  useEffect(() => { void load(); }, [load]);

  const label = (iso: string) => fx.date(iso, 'monthYear');
  const coverageName = t('suc.trend.coverage');
  const extendedName = t('suc.trend.extended');
  const anyData = points?.some((p) => p.activePositions > 0);

  return (
    <div className="space-y-3" data-testid="suc-reports">
      <div className="flex flex-wrap items-center gap-2">
        <label htmlFor="suc-trend-months" className="text-xs font-semibold text-slate-700 dark:text-slate-200">{t('suc.trend.range')}</label>
        <select id="suc-trend-months" className="select" value={months} onChange={(e) => setMonths(Number(e.target.value))}>
          {[6, 12, 24].map((m) => <option key={m} value={m}>{t('suc.trend.months', { count: m })}</option>)}
        </select>
      </div>
      {error && <ErrorBlock message={error} onRetry={() => void load()} />}
      {!error && !points && <LoadingRows label={t('suc.loading')} rows={2} />}
      {points && !anyData && <EmptyBlock title={t('suc.trend.emptyTitle')} body={t('suc.trend.emptyBody')} />}
      {points && anyData && (
        <SectionCard title={t('suc.trend.title')} description={t('suc.trend.definition')} testId="suc-trend">
          <div className="h-64 w-full [--suc-s1:#2F6BFF] [--suc-s2:#0E9F8E] dark:[--suc-s1:#5B8CFF] dark:[--suc-s2:#1FA899]" dir="ltr">
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={points} margin={{ top: 8, right: 16, bottom: 0, left: 0 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="currentColor" className="text-slate-200 dark:text-white/10" vertical={false} />
                <XAxis dataKey="monthEnd" tickFormatter={label} tick={{ fontSize: 11 }} stroke="currentColor" className="text-slate-500 dark:text-slate-400"
                  reversed={dir === 'rtl'} />
                <YAxis domain={[0, 100]} tickFormatter={(v: number) => fx.percent(v)} tick={{ fontSize: 11 }} stroke="currentColor" width={44}
                  className="text-slate-500 dark:text-slate-400" orientation={dir === 'rtl' ? 'right' : 'left'} />
                <Tooltip labelFormatter={(v) => label(String(v))} formatter={(v) => (v == null ? '—' : fx.percent(Number(v), 1))}
                  contentStyle={{ fontSize: 12, borderRadius: 8 }} />
                <Legend wrapperStyle={{ fontSize: 12 }} />
                <Line type="monotone" dataKey="coverage" name={coverageName} stroke="var(--suc-s1)" strokeWidth={2} dot={{ r: 4 }} connectNulls={false} />
                <Line type="monotone" dataKey="extendedCoverage" name={extendedName} stroke="var(--suc-s2)" strokeWidth={2} strokeDasharray="6 4" dot={{ r: 4 }} connectNulls={false} />
              </LineChart>
            </ResponsiveContainer>
          </div>
          <div className="overflow-x-auto">
            <table className="w-full text-xs" data-testid="suc-trend-table">
              <caption className="sr-only">{t('suc.trend.title')}</caption>
              <thead>
                <tr className="text-start text-slate-500">
                  <th scope="col" className="py-1 text-start font-semibold">{t('suc.trend.month')}</th>
                  <th scope="col" className="py-1 text-end font-semibold">{t('suc.trend.positions')}</th>
                  <th scope="col" className="py-1 text-end font-semibold">{coverageName}</th>
                  <th scope="col" className="py-1 text-end font-semibold">{extendedName}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                {points.map((p) => (
                  <tr key={p.monthEnd}>
                    <td className="py-1">{label(p.monthEnd)}</td>
                    <td className="py-1 text-end tabular-nums">{fx.integer(p.activePositions)}</td>
                    <td className="py-1 text-end tabular-nums">{p.coverage == null ? '—' : t('suc.trend.cell', { rate: fx.percent(p.coverage, 1), count: p.withReadyNow, total: p.activePositions })}</td>
                    <td className="py-1 text-end tabular-nums">{p.extendedCoverage == null ? '—' : t('suc.trend.cell', { rate: fx.percent(p.extendedCoverage, 1), count: p.withReadyNowOrOneToTwo, total: p.activePositions })}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </SectionCard>
      )}
    </div>
  );
}
