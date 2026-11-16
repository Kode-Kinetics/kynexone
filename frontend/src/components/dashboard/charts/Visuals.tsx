'use client';

/**
 * The HR Command Center's signature visuals (approved concept G).
 *
 * Every visual carries a text alternative (role="img" + aria-label, or a labelled table next
 * to it), never encodes meaning in colour alone, and draws "no data" as empty, never as zero.
 * Flat marks only (gradient fills, no extrusion); exact values always sit in text beside them.
 */

import { useEffect, useRef, useState } from 'react';
import { useT } from '../../../hooks/useT';
import { useFormat } from '../../../hooks/useFormat';

// ── Smooth trend line (hero) ─────────────────────────────────────────────────

/**
 * Monotone cubic through the points (Fritsch-Butland tangents): the line is smooth but never
 * overshoots between two months, so it cannot invent a dip or a peak the data does not have.
 */
function smoothPath(pts: Array<[number, number]>): string {
  const n = pts.length;
  if (!n) return '';
  const f = (v: number) => v.toFixed(1);
  if (n < 3) return pts.map(([x, y], i) => `${i ? 'L' : 'M'}${f(x)},${f(y)}`).join(' ');
  const m: number[] = [];
  for (let i = 0; i < n - 1; i++) m[i] = (pts[i + 1][1] - pts[i][1]) / ((pts[i + 1][0] - pts[i][0]) || 1);
  const t: number[] = [m[0]];
  for (let i = 1; i < n - 1; i++) t[i] = m[i - 1] * m[i] <= 0 ? 0 : (2 * m[i - 1] * m[i]) / (m[i - 1] + m[i]);
  t[n - 1] = m[n - 2];
  let d = `M${f(pts[0][0])},${f(pts[0][1])}`;
  for (let i = 0; i < n - 1; i++) {
    const dx = (pts[i + 1][0] - pts[i][0]) / 3;
    d += ` C${f(pts[i][0] + dx)},${f(pts[i][1] + t[i] * dx)} ${f(pts[i + 1][0] - dx)},${f(pts[i + 1][1] - t[i + 1] * dx)} ${f(pts[i + 1][0])},${f(pts[i + 1][1])}`;
  }
  return d;
}

/** Up to `count` round tick values inside [lo, hi] (1, 2, 2.5 or 5 times a power of ten). */
function niceTicks(lo: number, hi: number, count = 3): number[] {
  const range = hi - lo;
  if (!(range > 0)) return [lo];
  const raw = range / count;
  const pow = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 2.5, 5, 10].map((k) => k * pow).find((v) => v >= raw) ?? raw;
  const out: number[] = [];
  for (let v = Math.ceil(lo / step) * step; v <= hi + step * 1e-6; v += step) out.push(v);
  return out.slice(-count);
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

/**
 * White-on-brand trend for the hero band. Interactive: pointer or arrow keys move a crosshair
 * that reads out the month and amount. Motion (line draw-in, points fading in, two pulses on
 * the latest point) plays once on load and is off under reduced motion. Gaps (null) break the
 * line. The axis does not start at zero, so the ticks are round numbers and the area under the line
 * is only a faint wash: it carries the eye to the latest month without claiming a zero baseline.
 */
export function HeroTrend({
  points,
  format,
  label,
  height = 150,
  unit,
  onScrub,
}: {
  points: Array<{ label: string; value: number | null }>;
  format: (v: number) => string;
  label: string;
  height?: number;
  /** The currency every point is in, named on the hover readout (the axis stays bare). */
  unit?: string | null;
  /** Reports the month being read (pointer or arrow keys), or null when none, so a figure beside the chart can follow it. */
  onScrub?: (index: number | null) => void;
}) {
  const [box, W] = useWidth(520);
  const [hover, setHover] = useState<number | null>(null);
  const H = height;
  const pl = 6, pt = 22, pb = 24;
  const vals = points.map((p) => p.value).filter((v): v is number => v != null);
  const min = Math.min(...vals);
  const max = Math.max(...vals);
  const pad = (max - min) * 0.25 || max * 0.1 || 1;
  const lo = Math.max(0, min - pad);
  const hi = max + pad;
  const ticks = niceTicks(lo, hi, 3);
  // The right margin follows the widest tick label, so a longer language (Arabic "1.80 مليون") never runs into the line.
  const pr = Math.max(48, Math.ceil(Math.max(...ticks.map((tk) => format(tk).length)) * 6.6) + 16);
  const x = (i: number) => pl + (i * (W - pl - pr)) / Math.max(1, points.length - 1);
  const y = (v: number) => pt + (1 - (v - lo) / (hi - lo)) * (H - pt - pb);
  const runs: Array<Array<[number, number]>> = [];
  let cur: Array<[number, number]> = [];
  points.forEach((p, i) => { if (p.value == null) { if (cur.length) runs.push(cur); cur = []; } else cur.push([x(i), y(p.value)]); });
  if (cur.length) runs.push(cur);
  let last = -1;
  points.forEach((p, i) => { if (p.value != null) last = i; });
  // Month labels thin out when they would touch: every Nth, counted back from the latest so it always shows.
  const stride = Math.max(1, Math.ceil(40 / ((W - pl - pr) / Math.max(1, points.length - 1))));
  const showLabel = (i: number) => (points.length - 1 - i) % stride === 0;
  const hv = hover != null && points[hover]?.value != null ? hover : null;
  useEffect(() => { onScrub?.(hv); }, [hv, onScrub]);

  const onKey = (e: React.KeyboardEvent) => {
    const rtl = document.documentElement.dir === 'rtl';
    const next = (d: number) => {
      let i = hover ?? last;
      do { i += d; } while (i >= 0 && i < points.length && points[i].value == null);
      if (i >= 0 && i < points.length) setHover(i);
    };
    if (e.key === (rtl ? 'ArrowLeft' : 'ArrowRight')) next(1);
    else if (e.key === (rtl ? 'ArrowRight' : 'ArrowLeft')) next(-1);
    else if (e.key === 'Escape') setHover(null);
    else return;
    e.preventDefault();
  };

  return (
    <div ref={box} className="relative w-full">
      <svg style={{ direction: 'ltr' }} width={W} height={H} viewBox={`0 0 ${W} ${H}`} role="img" tabIndex={0}
        className="block max-w-full cursor-crosshair touch-pan-y rounded-lg outline-none focus-visible:ring-2 focus-visible:ring-white/70"
        aria-label={`${label}: ${points.map((p) => `${p.label} ${p.value == null ? 'no run' : format(p.value)}`).join(', ')}. Use the arrow keys to read each month.`}
        onKeyDown={onKey}
        onBlur={() => setHover(null)}
        onPointerLeave={() => setHover(null)}
        onPointerMove={(e) => {
          const r = (e.currentTarget as SVGSVGElement).getBoundingClientRect();
          const px = ((e.clientX - r.left) / r.width) * W;
          let best = -1; let dist = Infinity;
          points.forEach((p, i) => { if (p.value != null && Math.abs(x(i) - px) < dist) { dist = Math.abs(x(i) - px); best = i; } });
          setHover(best >= 0 ? best : null);
        }}>
        <defs>
          <linearGradient id="kx-hero-area" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="#FFFFFF" stopOpacity="0.24" />
            <stop offset="100%" stopColor="#FFFFFF" stopOpacity="0" />
          </linearGradient>
          <filter id="kx-hero-glow" x="-10%" y="-40%" width="120%" height="180%">
            <feGaussianBlur stdDeviation="3" result="b" />
            <feMerge><feMergeNode in="b" /><feMergeNode in="SourceGraphic" /></feMerge>
          </filter>
        </defs>
        {ticks.map((tk) => (
          <g key={tk}>
            <line x1={pl} x2={W - pr} y1={y(tk)} y2={y(tk)} stroke="rgba(255,255,255,0.14)" />
            <text x={W - pr + 8} y={y(tk)} dominantBaseline="middle" fill="rgba(255,255,255,0.88)" fontSize={11} className="tabular-nums">{format(tk)}</text>
          </g>
        ))}
        {hv != null && (
          <line x1={x(hv)} x2={x(hv)} y1={pt - 8} y2={H - pb} stroke="rgba(255,255,255,0.55)" strokeDasharray="0" className="wg-cross" />
        )}
        {runs.filter((run) => run.length > 1).map((run, i) => (
          <path key={`area-${i}`} aria-hidden d={`${smoothPath(run)} L${run[run.length - 1][0].toFixed(1)},${H - pb} L${run[0][0].toFixed(1)},${H - pb} Z`} fill="url(#kx-hero-area)" stroke="none" />
        ))}
        {runs.map((run, i) => (
          <path key={i} d={smoothPath(run)} pathLength={100} fill="none" stroke="#FFFFFF" strokeWidth={2.75} strokeLinecap="round" filter="url(#kx-hero-glow)" className="wg-arc" />
        ))}
        {points.map((p, i) => p.value != null && (
          <circle key={`pt-${i}`} cx={x(i)} cy={y(p.value)} r={hv === i ? 5.5 : 3} fill={hv === i ? '#FFFFFF' : 'rgba(255,255,255,0.9)'}
            className="wg-dot" style={{ animationDelay: `${300 + i * 45}ms` }} />
        ))}
        {last >= 0 && points[last].value != null && (
          <g>
            <circle cx={x(last)} cy={y(points[last].value as number)} r={6} fill="none" stroke="#FFFFFF" strokeWidth={2} className="wg-ping-twice" />
            <circle cx={x(last)} cy={y(points[last].value as number)} r={6} fill="#2F6BFF" stroke="#FFFFFF" strokeWidth={3} />
          </g>
        )}
        {points.map((p, i) => showLabel(i) && (
          <text key={p.label + i} x={x(i)} y={H - 6} textAnchor={i === 0 ? 'start' : i === points.length - 1 ? 'end' : 'middle'} fontSize={11}
            fill={i === (hv ?? last) ? '#FFFFFF' : 'rgba(255,255,255,0.8)'} fontWeight={i === (hv ?? last) ? 600 : 400}>{p.label}</text>
        ))}
      </svg>
      {hv != null && (
        <div role="status" className="pointer-events-none absolute top-0 rounded-lg bg-white px-2.5 py-1 text-meta font-semibold text-blue-950 shadow-lg transition-transform duration-150"
          ref={(n) => { if (n) { const left = x(hv); const flip = left > W * 0.7; n.style.left = `${left}px`; n.style.transform = flip ? 'translate(calc(-100% - 10px), 0)' : 'translate(10px, 0)'; } }}>
          {points[hv].label}: <span className="tabular-nums">{unit ? `${unit} ` : ''}{format(points[hv].value as number)}</span>
        </div>
      )}
    </div>
  );
}

/**
 * One run, no trend yet: where this run's gross went. A large split bar of net pay, deductions
 * and employer contributions, each labelled with its amount and share. Grows in once on load.
 */
export function GrossSplit({ net, deductions, employer, format }: { net: number; deductions: number; employer: number | null; format: (v: number) => string }) {
  const t = useT();
  const f = useFormat();
  const parts = [
    { key: 'net', label: t('Net pay'), value: net, cls: 'bg-white' },
    { key: 'ded', label: t('Deductions'), value: deductions, cls: 'bg-white/45' },
    ...(employer ? [{ key: 'emp', label: t('Employer contributions'), value: employer, cls: 'bg-amber-200' }] : []),
  ].filter((p) => p.value > 0);
  const total = parts.reduce((n, p) => n + p.value, 0) || 1;
  const share = (v: number) => f.percent((v / total) * 100);
  return (
    <div className="flex flex-col gap-3" role="img" aria-label={t('Where this run’s cost went: {values}', { values: parts.map((p) => `${p.label} ${format(p.value)}, ${share(p.value)}`).join('; ') })}>
      <span className="text-meta font-medium text-white/90">{t('Where this run’s cost went')}</span>
      <span className="flex h-11 w-full gap-[3px] overflow-hidden rounded-xl bg-white/10 p-[3px]">
        {parts.map((p, i) => (
          <span key={p.key} className={`wg-grow-x h-full rounded-lg ${p.cls}`} style={{ animationDelay: `${i * 120}ms` }}
            ref={(n) => { if (n) n.style.flexBasis = `${(p.value / total) * 100}%`; }} />
        ))}
      </span>
      <ul className="flex flex-wrap gap-x-6 gap-y-1.5 text-body-sm">
        {parts.map((p) => (
          <li key={p.key} className="flex items-center gap-2">
            <span aria-hidden className={`h-2.5 w-2.5 rounded ${p.cls}`} />
            <span className="text-white/90">{p.label}</span>
            <b className="font-semibold tabular-nums text-white">{format(p.value)}</b>
            <span className="text-white/80">{share(p.value)}</span>
          </li>
        ))}
      </ul>
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

/** `labels='current'` names only the active step (the rest are icons) when the band is too narrow for five labels. */
export function Stepper({ steps, current, labels = 'all' }: { steps: string[]; current: number; labels?: 'all' | 'current' }) {
  const t = useT();
  // Equal slots: every step owns the same width, so the connectors are the same length.
  return (
    <ol className="grid" aria-label={t('Payroll run progress')} ref={(n) => { if (n) n.style.gridTemplateColumns = `repeat(${steps.length}, minmax(0, 1fr))`; }}>
      {steps.map((s, i) => {
        const done = i < current;
        const cur = i === current;
        return (
          <li key={s} className="relative flex flex-col items-center gap-1.5" aria-current={cur ? 'step' : undefined}>
            {i < steps.length - 1 && (
              <span aria-hidden className={`absolute top-[10px] h-0.5 rounded ${done ? 'bg-white' : 'bg-white/25'}`}
                ref={(n) => { if (n) { n.style.insetInlineStart = 'calc(50% + 15px)'; n.style.insetInlineEnd = 'calc(-50% + 15px)'; } }} />
            )}
            <span className={`relative grid h-[22px] w-[22px] place-items-center rounded-full text-caption font-semibold ${
              done ? 'bg-white text-blue-800' : cur ? 'bg-amber-200 text-amber-950 ring-4 ring-amber-200/30' : 'bg-white/15 text-white/85'
            }`}>{done ? '✓' : i + 1}</span>
            <span className={`text-center text-caption leading-tight ${done || cur ? 'text-white' : 'text-white/80'} ${cur ? 'font-semibold' : 'font-medium'}`}>
              {/* Steps are dictionary keys. "Review" is short for finance review; say so to a screen reader. */}
              <span aria-hidden className={labels === 'current' && !cur ? 'hidden' : undefined}>{t(s)}</span>
              <span className="sr-only">{t(done ? '{step}, done' : cur ? '{step}, current step' : '{step}, to do', { step: s === 'Review' ? t('Finance review') : t(s) })}</span>
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
    <svg width={120} height={102} viewBox="0 0 136 116" role="img" aria-label={label} className="shrink-0 [.kx-dense_&]:h-[80px] [.kx-dense_&]:w-[94px] [.kx-tight_&]:h-[64px] [.kx-tight_&]:w-[76px]">
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
  { min: 97, max: null, bg: '#1E3A8A', fg: '#FFFFFF', label: '97% and above' },
  { min: 94, max: 96, bg: '#1D4ED8', fg: '#FFFFFF', label: '94 to 96%' },
  { min: 90, max: 93, bg: '#60A5FA', fg: '#0B1220', label: '90 to 93%' },
  { min: 86, max: 89, bg: '#DBEAFE', fg: '#0B1220', label: '86 to 89%' },
  { min: 0, max: 85, bg: '#FEE2E2', fg: '#7F1D1D', label: 'below 86%' },
];

export function heatStyle(rate: number | null) {
  if (rate == null) return null;
  return HEAT.find((h) => rate >= h.min) ?? HEAT[HEAT.length - 1];
}

export function HeatLegend() {
  const t = useT();
  const f = useFormat();
  const label = (h: (typeof HEAT)[number]) => h.max == null
    ? t('{rate} and above', { rate: f.percent(h.min) })
    : h.min === 0 ? t('Below {rate}', { rate: f.percent(h.max + 1) })
    : t('{from} to {to}', { from: f.integer(h.min), to: f.percent(h.max) });
  return (
    <ul className="flex flex-wrap gap-x-4 gap-y-1.5 text-caption text-slate-700 dark:text-slate-300" aria-label={t('Heatmap scale')}>
      {[...HEAT].reverse().map((h) => (
        <li key={h.label} className="flex items-center gap-1.5">
          <span aria-hidden className="h-3.5 w-3.5 rounded" ref={(n) => { if (n) n.style.background = h.bg; }} />{label(h)}
        </li>
      ))}
      <li className="flex items-center gap-1.5"><span aria-hidden className="h-3.5 w-3.5 rounded bg-slate-100 dark:bg-white/[0.06]" />{t('Nobody rostered')}</li>
    </ul>
  );
}

// ── Ring (part-to-whole) ─────────────────────────────────────────────────────

/**
 * A flat conic-gradient ring for a two-part split. The filled arc sweeps in once after mount
 * (an animated @property, so it just snaps to the value where that is unsupported or motion is
 * reduced). Exact values always sit in text beside it. `bTone="bad"` paints the remainder in the
 * warning colour: for a gap that needs action (documents missing), not a neutral rest.
 */
export function Ring({ a, b, label, center, sub, bTone = 'neutral' }: { a: number; b: number; label: string; center: string; sub: string; bTone?: 'neutral' | 'bad' }) {
  const total = a + b;
  const pct = total > 0 ? (a / total) * 100 : 0;
  const [shown, setShown] = useState(0);
  useEffect(() => {
    const id = requestAnimationFrame(() => setShown(pct));
    return () => cancelAnimationFrame(id);
  }, [pct]);
  return (
    <span className="relative inline-grid h-[112px] w-[112px] shrink-0 place-items-center" role="img" aria-label={label}>
      <span aria-hidden className={`wg-ring absolute inset-0 ${bTone === 'bad' && total > 0 ? 'wg-ring-bad' : ''}`} style={{ ['--wg-p' as string]: `${shown}%` } as React.CSSProperties} />
      <span aria-hidden className="relative flex flex-col items-center">
        <span className="text-h3 font-semibold leading-none tabular-nums text-slate-900 dark:text-white">{center}</span>
        <span className="mt-0.5 text-caption text-slate-600 dark:text-slate-400">{sub}</span>
      </span>
    </span>
  );
}

// ── Ranked bars (rank by category) ───────────────────────────────────────────

/**
 * Flat gradient bars, one row per category: name, bar, exact value. Rows share the height they are
 * given, so a tall card spreads its rows instead of leaving a blank band under them.
 */
export function RankedBars({ items, label }: { items: Array<{ name: string; value: number }>; label: string }) {
  const max = Math.max(1, ...items.map((i) => i.value));
  return (
    <div role="img" aria-label={label} className="flex min-h-0 flex-1 flex-col justify-evenly gap-2">
      {items.map((it) => (
        <div key={it.name} aria-hidden className="grid min-h-[28px] grid-cols-[minmax(0,8rem)_minmax(0,1fr)_2.25rem] items-center gap-x-3">
          <span className="min-w-0 truncate text-body-sm text-slate-800 dark:text-slate-200" title={it.name}>{it.name}</span>
          <span className="h-2.5 rounded-full bg-[color:var(--viz-track)]">
            <span className="wg-hbar wg-gradbar block h-2.5 rounded-full" ref={(n) => { if (n) n.style.width = `${Math.max(4, (it.value / max) * 100)}%`; }} />
          </span>
          <span className="text-end text-body-sm font-semibold tabular-nums text-slate-900 dark:text-white">{it.value}</span>
        </div>
      ))}
    </div>
  );
}

// ── Dot matrix (one dot per person) ──────────────────────────────────────────

/**
 * One dot per employee, coloured by status. It shows the headcount as people rather than as a
 * percentage ring, and it is only used while the count is small enough to read (the caller falls
 * back to a gauge above that). Every part is also listed in text beside it and in the label.
 */
export function DotMatrix({ parts, label }: { parts: Array<{ label: string; value: number; color: string }>; label: string }) {
  const dots = parts.flatMap((p) => Array.from({ length: Math.max(0, p.value) }, (_, i) => ({ k: `${p.label}-${i}`, color: p.color })));
  return (
    <span role="img" aria-label={label} className="grid w-full gap-[3px] [grid-template-columns:repeat(auto-fill,minmax(7px,1fr))]">
      {dots.map((d) => <span key={d.k} aria-hidden className="aspect-square max-w-[9px] rounded-full" ref={(n) => { if (n) n.style.background = d.color; }} />)}
    </span>
  );
}
