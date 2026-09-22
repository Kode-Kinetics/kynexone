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

/**
 * White-on-brand trend for the hero band. Interactive: pointer or arrow keys move a crosshair
 * that reads out the month and amount. Motion (line draw-in, points fading in, two pulses on
 * the latest point) plays once on load and is off under reduced motion. Gaps (null) break the
 * line. No area fill: the axis does not start at zero, and a filled area would exaggerate it.
 */
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
  const [hover, setHover] = useState<number | null>(null);
  const H = height;
  const pl = 6, pr = 48, pt = 22, pb = 24;
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
  const hv = hover != null && points[hover]?.value != null ? hover : null;

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
      <svg width={W} height={H} viewBox={`0 0 ${W} ${H}`} role="img" tabIndex={0}
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
        {points.map((p, i) => (
          <text key={p.label + i} x={x(i)} y={H - 6} textAnchor={i === 0 ? 'start' : i === points.length - 1 ? 'end' : 'middle'} fontSize={11}
            fill={i === (hv ?? last) ? '#FFFFFF' : 'rgba(255,255,255,0.8)'} fontWeight={i === (hv ?? last) ? 600 : 400}>{p.label}</text>
        ))}
      </svg>
      {hv != null && (
        <div role="status" className="pointer-events-none absolute top-0 rounded-lg bg-white px-2.5 py-1 text-[12px] font-semibold text-blue-950 shadow-lg transition-transform duration-150"
          ref={(n) => { if (n) { const left = x(hv); const flip = left > W * 0.7; n.style.left = `${left}px`; n.style.transform = flip ? 'translate(calc(-100% - 10px), 0)' : 'translate(10px, 0)'; } }}>
          {points[hv].label}: <span className="tabular-nums">SAR {format(points[hv].value as number)}</span>
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
  const parts = [
    { key: 'net', label: 'Net pay', value: net, cls: 'bg-white' },
    { key: 'ded', label: 'Deductions', value: deductions, cls: 'bg-white/45' },
    ...(employer ? [{ key: 'emp', label: 'Employer contributions', value: employer, cls: 'bg-amber-200' }] : []),
  ].filter((p) => p.value > 0);
  const total = parts.reduce((n, p) => n + p.value, 0) || 1;
  return (
    <div className="flex flex-col gap-3" role="img" aria-label={`Where this run's cost went: ${parts.map((p) => `${p.label} ${format(p.value)}, ${Math.round((p.value / total) * 100)}%`).join('; ')}`}>
      <span className="text-[12px] font-medium text-white/90">Where this run&rsquo;s cost went</span>
      <span className="flex h-11 w-full gap-[3px] overflow-hidden rounded-xl bg-white/10 p-[3px]">
        {parts.map((p, i) => (
          <span key={p.key} className={`wg-grow-x h-full rounded-[9px] ${p.cls}`} style={{ animationDelay: `${i * 120}ms` }}
            ref={(n) => { if (n) n.style.flexBasis = `${(p.value / total) * 100}%`; }} />
        ))}
      </span>
      <ul className="flex flex-wrap gap-x-6 gap-y-1.5 text-[13px]">
        {parts.map((p) => (
          <li key={p.key} className="flex items-center gap-2">
            <span aria-hidden className={`h-2.5 w-2.5 rounded-sm ${p.cls}`} />
            <span className="text-white/90">{p.label}</span>
            <b className="font-semibold tabular-nums text-white">{format(p.value)}</b>
            <span className="text-white/80">{Math.round((p.value / total) * 100)}%</span>
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
    <svg width={120} height={102} viewBox="0 0 136 116" role="img" aria-label={label} className="shrink-0 [.kx-dense_&]:h-[80px] [.kx-dense_&]:w-[94px]">
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

// ── 3D ring (part-to-whole) ──────────────────────────────────────────────────

/**
 * A tilted, extruded ring for a two-part split. The front face sweeps in once on load; exact
 * values always sit in text beside it (the ring is never the only way to read the number).
 * Colours come from tokens so the ring works in dark mode.
 */
export function Ring3D({ a, b, label, center, sub }: { a: number; b: number; label: string; center: string; sub: string }) {
  const cx = 80, cy = 58, r = 48, sw = 17, depth = 10, tilt = 0.58;
  const total = a + b || 1;
  const c = 2 * Math.PI * r;
  const lenA = (a / total) * c;
  const layer = (dy: number, top: boolean) => (
    <g key={`${dy}-${top}`} transform={`translate(0 ${dy}) translate(${cx} ${cy}) scale(1 ${tilt}) translate(${-cx} ${-cy})`}>
      <circle cx={cx} cy={cy} r={r} fill="none" strokeWidth={sw} className={top ? 'stroke-[color:var(--viz-ring-b)]' : 'stroke-[color:var(--viz-ring-b-side)]'} />
      {a > 0 && (
        <circle cx={cx} cy={cy} r={r} fill="none" strokeWidth={sw} transform={`rotate(-90 ${cx} ${cy})`}
          strokeDasharray={`${Math.max(0, lenA - (top ? 2 : 0)).toFixed(1)} ${(c - lenA + (top ? 2 : 0)).toFixed(1)}`}
          className={top ? 'wg-ring-sweep stroke-[color:var(--viz-ring-a)]' : 'stroke-[color:var(--viz-ring-a-side)]'}
          style={top ? ({ ['--ring-len' as string]: `${lenA.toFixed(1)}`, ['--ring-c' as string]: `${c.toFixed(1)}` } as React.CSSProperties) : undefined} />
      )}
    </g>
  );
  return (
    <span className="relative inline-grid shrink-0 place-items-center">
      <svg width={160} height={130} viewBox="0 0 160 130" role="img" aria-label={label} className="wg-rise">
        <ellipse cx={cx} cy={cy + depth + 18} rx={r + 14} ry={(r + 14) * tilt * 0.4} className="fill-[color:var(--viz-shadow)]" />
        {Array.from({ length: depth }, (_, i) => layer(depth - i, false))}
        {layer(0, true)}
      </svg>
      <span className="pointer-events-none absolute inset-x-0 top-[40px] flex flex-col items-center" aria-hidden>
        <span className="text-[18px] font-bold leading-none tabular-nums text-slate-900 dark:text-white">{center}</span>
        <span className="text-[10px] text-slate-600 dark:text-slate-400">{sub}</span>
      </span>
    </span>
  );
}

// ── 3D bars (rank by category) ───────────────────────────────────────────────

export function Bars3D({ items, label }: { items: Array<{ name: string; value: number }>; label: string }) {
  const [box, W] = useWidth(300);
  const max = Math.max(1, ...items.map((i) => i.value));
  const maxw = W - 12, bh = 11, dx = 6, dy = 4, row = 22;
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
