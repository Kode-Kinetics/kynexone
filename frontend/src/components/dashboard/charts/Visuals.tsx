'use client';

/**
 * The HR Command Center's signature visuals (approved concept G).
 *
 * Every visual carries a text alternative (role="img" + aria-label, or a labelled table next
 * to it), never encodes meaning in colour alone, and draws "no data" as empty, never as zero.
 * Depth (the extruded department bars) is used only for rank, with exact values in text beside it.
 */

import { useEffect, useRef, useState } from 'react';

// ── Smooth trend line (hero) ─────────────────────────────────────────────────

function smoothPath(pts: Array<[number, number]>): string {
  if (!pts.length) return '';
  let d = `M${pts[0][0].toFixed(1)},${pts[0][1].toFixed(1)}`;
  for (let i = 1; i < pts.length; i++) {
    const [x0, y0] = pts[i - 1];
    const [x1, y1] = pts[i];
    const cx = (x0 + x1) / 2;
    d += ` C${cx.toFixed(1)},${y0.toFixed(1)} ${cx.toFixed(1)},${y1.toFixed(1)} ${x1.toFixed(1)},${y1.toFixed(1)}`;
  }
  return d;
}

function useWidth(fallback: number) {
  const ref = useRef<HTMLDivElement>(null);
  const [w, setW] = useState(fallback);
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const ro = new ResizeObserver(([e]) => setW(Math.max(160, Math.round(e.contentRect.width))));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  return [ref, w] as const;
}

/** White-on-brand area trend for the hero band. Gaps (null) break the line. */
export function HeroTrend({
  points,
  format,
  label,
  height = 150,
}: {
  points: Array<{ label: string; value: number | null }>;
  format: (v: number) => string;
  label: string;
  height?: number;
}) {
  const [box, W] = useWidth(520);
  const H = height;
  const pl = 6, pr = 44, pt = 18, pb = 22;
  const vals = points.map((p) => p.value).filter((v): v is number => v != null);
  const min = Math.min(...vals);
  const max = Math.max(...vals);
  const pad = (max - min) * 0.25 || max * 0.1 || 1;
  const lo = Math.max(0, min - pad);
  const hi = max + pad;
  const x = (i: number) => pl + (i * (W - pl - pr)) / Math.max(1, points.length - 1);
  const y = (v: number) => pt + (1 - (v - lo) / (hi - lo)) * (H - pt - pb);
  const runs: Array<Array<[number, number]>> = [];
  let cur: Array<[number, number]> = [];
  points.forEach((p, i) => { if (p.value == null) { if (cur.length) runs.push(cur); cur = []; } else cur.push([x(i), y(p.value)]); });
  if (cur.length) runs.push(cur);
  let last = -1;
  points.forEach((p, i) => { if (p.value != null) last = i; });
  const ticks = [lo + (hi - lo) * 0.2, lo + (hi - lo) * 0.6, hi - (hi - lo) * 0.05];

  return (
    <div ref={box} className="w-full">
      <svg width={W} height={H} viewBox={`0 0 ${W} ${H}`} role="img" className="block max-w-full"
        aria-label={`${label}: ${points.map((p) => `${p.label} ${p.value == null ? 'no run' : format(p.value)}`).join(', ')}`}>
        <defs>
          <linearGradient id="kx-hero-area" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="#FFFFFF" stopOpacity={0.32} />
            <stop offset="100%" stopColor="#FFFFFF" stopOpacity={0} />
          </linearGradient>
        </defs>
        {ticks.map((t) => (
          <g key={t}>
            <line x1={pl} x2={W - pr} y1={y(t)} y2={y(t)} stroke="rgba(255,255,255,0.14)" />
            <text x={W - pr + 6} y={y(t)} dominantBaseline="middle" fill="rgba(255,255,255,0.88)" fontSize={11} className="tabular-nums">{format(t)}</text>
          </g>
        ))}
        {runs.map((run, i) => {
          const line = smoothPath(run);
          return (
            <g key={i}>
              <path d={line} pathLength={100} fill="none" stroke="#FFFFFF" strokeWidth={2.5} strokeLinecap="round" className="wg-arc" />
            </g>
          );
        })}
        {points.map((p, i) => (
          <text key={p.label + i} x={x(i)} y={H - 5} textAnchor={i === 0 ? 'start' : i === points.length - 1 ? 'end' : 'middle'} fontSize={11}
            fill={i === last ? '#FFFFFF' : 'rgba(255,255,255,0.85)'} fontWeight={i === last ? 600 : 400}>{p.label}</text>
        ))}
        {last >= 0 && points[last].value != null && (
          <circle cx={x(last)} cy={y(points[last].value as number)} r={5.5} fill="#2F6BFF" stroke="#FFFFFF" strokeWidth={3} />
        )}
      </svg>
    </div>
  );
}

/** Tiny white trend for the hero's side panel. */
export function HeroSpark({ values, label }: { values: number[]; label: string }) {
  const W = 220, H = 56;
  const min = Math.min(...values), max = Math.max(...values);
  const span = max - min || 1;
  const pts = values.map((v, i) => [(i * W) / Math.max(1, values.length - 1), 4 + (1 - (v - min) / span) * (H - 8)] as [number, number]);
  return (
    <svg width={W} height={H} viewBox={`0 0 ${W} ${H}`} role="img" aria-label={label} className="max-w-full overflow-visible">
      <path d={smoothPath(pts)} fill="none" stroke="#A7F3D0" strokeWidth={2.25} strokeLinecap="round" />
      <circle cx={pts[pts.length - 1][0]} cy={pts[pts.length - 1][1]} r={4} fill="#A7F3D0" />
    </svg>
  );
}

// ── Run stepper ──────────────────────────────────────────────────────────────

export function Stepper({ steps, current }: { steps: string[]; current: number }) {
  // Equal slots: every step owns the same width, so the connectors are the same length.
  return (
    <ol className="grid" aria-label="Payroll run progress" ref={(n) => { if (n) n.style.gridTemplateColumns = `repeat(${steps.length}, minmax(0, 1fr))`; }}>
      {steps.map((s, i) => {
        const done = i < current;
        const cur = i === current;
        return (
          <li key={s} className="relative flex flex-col items-center gap-1.5" aria-current={cur ? 'step' : undefined}>
            {i < steps.length - 1 && (
              <span aria-hidden className={`absolute top-[10px] h-0.5 rounded ${done ? 'bg-white' : 'bg-white/25'}`}
                ref={(n) => { if (n) { n.style.insetInlineStart = 'calc(50% + 15px)'; n.style.insetInlineEnd = 'calc(-50% + 15px)'; } }} />
            )}
            <span className={`relative grid h-[22px] w-[22px] place-items-center rounded-full text-[11px] font-bold ${
              done ? 'bg-white text-blue-800' : cur ? 'bg-amber-200 text-amber-950 ring-4 ring-amber-200/30' : 'bg-white/15 text-white/85'
            }`}>{done ? '✓' : i + 1}</span>
            <span className={`text-center text-[11px] leading-tight ${done || cur ? 'text-white' : 'text-white/80'} ${cur ? 'font-semibold' : 'font-medium'}`}>
              {s === 'Review' ? <><span aria-hidden>Review</span><span className="sr-only">Finance review</span></> : s}
              <span className="sr-only">{done ? ', done' : cur ? ', current step' : ', to do'}</span>
            </span>
          </li>
        );
      })}
    </ol>
  );
}

// ── Gauge ────────────────────────────────────────────────────────────────────

export function Gauge({ pct, center, sub, label }: { pct: number | null; center: string; sub: string; label: string }) {
  const r = 56, cx = 68, cy = 68;
  const a0 = (150 * Math.PI) / 180, a1 = (390 * Math.PI) / 180;
  const at = (a: number) => [cx + r * Math.cos(a), cy + r * Math.sin(a)] as const;
  const [sx, sy] = at(a0);
  const [ex, ey] = at(a1);
  const p = pct == null ? 0 : Math.max(0, Math.min(1, pct));
  const av = a0 + (a1 - a0) * p;
  const [vx, vy] = at(av);
  return (
    <svg width={120} height={102} viewBox="0 0 136 116" role="img" aria-label={label} className="shrink-0">
      <path d={`M${sx},${sy} A${r},${r} 0 1 1 ${ex},${ey}`} fill="none" className="stroke-[color:var(--viz-track)]" strokeWidth={12} strokeLinecap="round" />
      {pct != null && p > 0 && (
        <path d={`M${sx},${sy} A${r},${r} 0 ${av - a0 > Math.PI ? 1 : 0} 1 ${vx},${vy}`} fill="none" stroke="var(--viz-1)" strokeWidth={12} strokeLinecap="round" pathLength={100} className="wg-arc" />
      )}
      <text x={cx} y={cy + 2} textAnchor="middle" className="fill-[color:var(--viz-ink)]" fontSize={center.length > 5 ? 17 : 24} fontWeight={700}>{center}</text>
      <text x={cx} y={cy + 20} textAnchor="middle" className="fill-[color:var(--viz-muted)]" fontSize={11}>{sub}</text>
    </svg>
  );
}

// ── Heatmap ──────────────────────────────────────────────────────────────────

const HEAT = [
  { min: 97, bg: '#1E3A8A', fg: '#FFFFFF', label: '97% and above' },
  { min: 94, bg: '#1D4ED8', fg: '#FFFFFF', label: '94 to 96%' },
  { min: 90, bg: '#60A5FA', fg: '#0B1220', label: '90 to 93%' },
  { min: 86, bg: '#DBEAFE', fg: '#0B1220', label: '86 to 89%' },
  { min: 0, bg: '#FEE2E2', fg: '#7F1D1D', label: 'below 86%' },
];

export function heatStyle(rate: number | null) {
  if (rate == null) return null;
  return HEAT.find((h) => rate >= h.min) ?? HEAT[HEAT.length - 1];
}

export function HeatLegend() {
  return (
    <ul className="flex flex-wrap gap-x-4 gap-y-1.5 text-[11px] text-slate-700 dark:text-slate-300" aria-label="Heatmap scale">
      {[...HEAT].reverse().map((h) => (
        <li key={h.label} className="flex items-center gap-1.5">
          <span aria-hidden className="h-3.5 w-3.5 rounded" ref={(n) => { if (n) n.style.background = h.bg; }} />{h.label}
        </li>
      ))}
      <li className="flex items-center gap-1.5"><span aria-hidden className="h-3.5 w-3.5 rounded bg-slate-100 dark:bg-white/[0.06]" />no one rostered</li>
    </ul>
  );
}

// ── 3D bars (rank by category) ───────────────────────────────────────────────

export function Bars3D({ items, label }: { items: Array<{ name: string; value: number }>; label: string }) {
  const [box, W] = useWidth(300);
  const max = Math.max(1, ...items.map((i) => i.value));
  const maxw = W - 12, bh = 12, dx = 7, dy = 5, row = 26;
  const H = 8 + items.length * row;
  return (
    <div ref={box} className="min-w-0">
      <svg width={W} height={H} viewBox={`0 0 ${W} ${H}`} role="img" aria-label={label} className="block max-w-full">
        {items.map((it, i) => {
          const y0 = 8 + i * row;
          // The depth face is part of the bar's length, so front + depth = the value.
          const w = Math.max(4, (it.value / max) * maxw - dx);
          return (
            <g key={it.name}>
              <polygon points={`2,${y0 + bh + 3} ${w + dx},${y0 + bh + 3} ${w + dx + 4},${y0 + bh + 6} 6,${y0 + bh + 6}`} fill="rgba(30,58,138,0.10)" />
              <polygon points={`0,${y0} ${w},${y0} ${w + dx},${y0 - dy} ${dx},${y0 - dy}`} className="wg-hbar fill-[color:var(--viz-bar-top)]" />
              <rect x={0} y={y0} width={w} height={bh} className="wg-hbar fill-[color:var(--viz-1)]" />
              <polygon points={`${w},${y0} ${w + dx},${y0 - dy} ${w + dx},${y0 + bh - dy} ${w},${y0 + bh}`} className="wg-hbar fill-[color:var(--viz-bar-side)]" />
            </g>
          );
        })}
      </svg>
    </div>
  );
}
