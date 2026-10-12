'use client';

import { Bar, BarChart, CartesianGrid, Cell, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import type { Budget } from '../../api/compensation';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';

/**
 * Budget used per department, as a share of its pool (one measure, one axis). The 100 % line is the pool. Over-budget
 * bars take the reserved "serious" colour AND are named in the table beside the chart, so colour is never the only cue.
 * The table under the chart (CycleWorkspace) carries every exact figure; this is the at-a-glance view.
 */
export function BudgetChart({ budgets, currency }: { budgets: Budget[]; currency: string }) {
  const t = useT();
  const f = useFormat();
  const data = budgets.filter((b) => b.pool > 0 || b.proposed > 0).map((b) => ({
    name: b.departmentName,
    used: b.percentUsed ?? (b.proposed > 0 ? 100 : 0),
    proposed: b.proposed,
    pool: b.pool,
    over: b.overspent,
  }));
  if (data.length === 0) return null;
  const max = Math.max(110, ...data.map((d) => Math.ceil(d.used / 10) * 10));
  return (
    <figure className="h-48 w-full" aria-label={t('Budget used by department')}>
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} layout="vertical" margin={{ left: 8, right: 16, top: 4, bottom: 4 }} barCategoryGap={8}>
          <CartesianGrid horizontal={false} strokeDasharray="3 3" stroke="currentColor" className="text-slate-100 dark:text-white/[0.06]" />
          <XAxis type="number" domain={[0, max]} tickFormatter={(v) => f.percent(Number(v))} tick={{ fontSize: 10 }} tickLine={false} axisLine={false} />
          <YAxis type="category" dataKey="name" width={110} tick={{ fontSize: 11 }} tickLine={false} axisLine={false} />
          <Tooltip
            cursor={{ fillOpacity: 0.06 }}
            contentStyle={{ borderRadius: 8, fontSize: 12, border: '1px solid rgba(148,163,184,0.25)' }}
            formatter={(_v, _n, item) => {
              const p = item.payload as { proposed: number; pool: number; used: number; over: boolean };
              return [t('{proposed} of {pool} ({used})', { proposed: f.money(p.proposed, currency), pool: f.money(p.pool, currency), used: f.percent(p.used, 2) }),
                p.over ? t('Over budget') : t('Within budget')];
            }}
          />
          <ReferenceLine x={100} stroke="currentColor" className="text-slate-500" strokeDasharray="4 2" label={{ value: t('Budget'), position: 'top', fontSize: 10 }} />
          <Bar dataKey="used" radius={[0, 4, 4, 0]} barSize={14} isAnimationActive={false}>
            {data.map((d) => <Cell key={d.name} fill={d.over ? '#C2410C' : '#2F6BFF'} />)}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
    </figure>
  );
}
