'use client';

import { useEffect, useRef } from 'react';

/**
 * Ambient "digital rain" — a faint Matrix-style canvas backdrop.
 *
 * Designed to be a *texture*, not a feature: it clears to full transparency
 * every frame (so whatever sits behind it stays visible), draws each column
 * as a short trail that fades upward, and is meant to be blended (screen) at
 * low opacity by the caller. Each cell's glyph is derived deterministically
 * from its (column,row) so trails stay stable instead of flickering.
 *
 * Honors prefers-reduced-motion (renders nothing animated) and cleans up its
 * RAF + ResizeObserver on unmount.
 */
export function MatrixRain({ className = '' }: { className?: string }) {
  const ref = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = ref.current;
    const parent = canvas?.parentElement;
    if (!canvas || !parent) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

    // Domain vocabulary — numbers + alphabet tokens drawn from the product itself,
    // so each falling column actually spells out system terms (HR, payroll, codes).
    const TOKENS = [
      'KYNEXONE', 'PAYROLL', 'ATTENDANCE', 'EMPLOYEE', 'LEAVE', 'OVERTIME',
      'COMPLIANCE', 'PAYSLIP', 'ONBOARDING', 'WORKFORCE', 'APPROVED', 'PENDING',
      'SHIFT', 'ROSTER', 'TENANT', 'PERFORMANCE', 'RECRUITMENT', 'EOSB', 'WPS',
      'GOSI', 'QIWA', 'KPI', 'NET', 'GROSS', 'IBAN', 'HR', 'OT',
      'EMP-1024', 'EMP-2048', 'ID#88421', 'SAR 12,500', 'AED 9,300', 'NET 9,300',
      '98.6%', '2026-06-24', 'RUN-0042', '0123456789',
    ];
    // Pad each token with spaces so columns breathe and words repeat with a gap.
    const streams = TOKENS.map((t) => `  ${t.toUpperCase()}  `);
    const dpr = Math.min(window.devicePixelRatio || 1, 2);

    let w = 0, h = 0, fontSize = 14, cols = 0;
    let drops: number[] = [];
    const trail = 16;

    const setup = () => {
      w = parent.clientWidth;
      h = parent.clientHeight;
      canvas.width = Math.floor(w * dpr);
      canvas.height = Math.floor(h * dpr);
      canvas.style.width = `${w}px`;
      canvas.style.height = `${h}px`;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      fontSize = Math.max(13, Math.round(w / 46));
      cols = Math.ceil(w / fontSize);
      drops = Array.from({ length: cols }, () => Math.random() * -(h / fontSize));
    };
    setup();

    let raf = 0;
    let last = 0;
    const stepMs = 75; // cadence of the fall

    // Each column streams one domain token; a per-column phase desyncs them so
    // the words don't line up horizontally. Reading downward spells the term.
    const glyphAt = (col: number, row: number) => {
      const s = streams[(((col * 7 + 3) % streams.length) + streams.length) % streams.length];
      const idx = (((row + col * 5) % s.length) + s.length) % s.length;
      return s[idx];
    };

    const draw = (t: number) => {
      raf = requestAnimationFrame(draw);
      if (t - last < stepMs) return;
      last = t;

      ctx.clearRect(0, 0, w, h);
      ctx.font = `${fontSize}px ui-monospace, "SFMono-Regular", monospace`;
      ctx.textBaseline = 'top';

      for (let i = 0; i < cols; i++) {
        const head = Math.floor(drops[i]);
        for (let k = 0; k < trail; k++) {
          const row = head - k;
          if (row < 0) continue;
          const y = row * fontSize;
          if (y > h) continue;
          const a = 1 - k / trail;
          if (k === 0) {
            ctx.fillStyle = 'rgba(200,255,235,0.95)';        // bright leading glyph
          } else {
            ctx.fillStyle = `rgba(45,212,191,${0.42 * a})`;  // emerald-teal trail
          }
          ctx.fillText(glyphAt(i, row), i * fontSize, y);
        }
        drops[i] += 1;
        if (head * fontSize > h && Math.random() > 0.965) {
          drops[i] = Math.random() * -20;
        }
      }
    };

    raf = requestAnimationFrame(draw);
    const ro = new ResizeObserver(setup);
    ro.observe(parent);

    return () => {
      cancelAnimationFrame(raf);
      ro.disconnect();
    };
  }, []);

  return <canvas ref={ref} aria-hidden className={className} />;
}
