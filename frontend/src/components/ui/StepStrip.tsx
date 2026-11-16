'use client';

import { useEffect, useId, useRef, useState } from 'react';
import { AlertTriangle, ChevronDown, CheckCircle2, CircleDot, Circle } from 'lucide-react';
import { useT } from '../../hooks/useT';

/**
 * A flow's named phases in one row (Screen Standard 3d): a 4 px bar, the phase's name and its state text under it. Done phases can be
 * opened (read-only) from here; the phase being worked on is marked `aria-current="step"`. Bars fill from the inline-start, so in Arabic
 * the strip reads from the right. Below 640px it folds to segments, "Step n of N: name" and an "All steps" menu of the reachable phases.
 */
export interface StepStripItem {
  key: string;
  label: string;
  /** "1", "2"... before the name; absent for a gate (setup) between steps. */
  index?: number;
  state: 'done' | 'current' | 'blocked' | 'upcoming';
  /** The state text under the name ("Done", "You are here", "Passed"...). */
  note: string;
  /** The phase being worked on. */
  isStage: boolean;
  /** Green bar (checks passed, money confirmed); other done phases use the primary tone. */
  success?: boolean;
  /** Opens the phase read-only. */
  onView?: () => void;
}

export function StepStrip({ items, ariaLabel, compactLabel, testId, listTestId, stripId, viewingKey }: {
  items: StepStripItem[];
  ariaLabel: string;
  /** The phone line: "Step 2 of 5: Check employees". */
  compactLabel: string;
  testId?: string;
  listTestId?: string;
  stripId: string;
  /** The phase opened read-only, if any. */
  viewingKey?: string | null;
}) {
  const t = useT();
  const menuId = useId();
  const [menuOpen, setMenuOpen] = useState(false);
  const menuButton = useRef<HTMLButtonElement>(null);
  const menu = useRef<HTMLUListElement>(null);
  const reachable = items.filter(i => i.onView);

  useEffect(() => {
    if (!menuOpen) return;
    const first = menu.current?.querySelector<HTMLElement>('[role="menuitem"]');
    first?.focus();
    const onDoc = (e: MouseEvent) => { if (!menu.current?.contains(e.target as Node) && e.target !== menuButton.current) setMenuOpen(false); };
    document.addEventListener('mousedown', onDoc);
    return () => document.removeEventListener('mousedown', onDoc);
  }, [menuOpen]);

  const bar = (i: StepStripItem) => (i.state === 'done' ? (i.success ? 'bg-success' : 'bg-primary/40')
    : i.state === 'blocked' ? 'bg-danger' : i.state === 'current' ? 'bg-primary' : 'bg-line');

  return (
    <div data-testid={testId} className="min-w-0">
      <ol aria-label={ariaLabel} data-step-strip={stripId} data-testid={listTestId} className="hidden gap-3 sm:grid" style={{ gridTemplateColumns: `repeat(${items.length}, minmax(0, 1fr))` }}>
        {items.map(i => {
          const Icon = i.state === 'done' ? CheckCircle2 : i.state === 'blocked' ? AlertTriangle : i.state === 'current' ? CircleDot : Circle;
          const tone = i.state === 'done' ? (i.success ? 'text-success-fg' : 'text-primary') : i.state === 'blocked' ? 'text-danger-fg' : i.state === 'current' ? 'text-primary' : 'text-fg-subtle';
          const name = <>{i.index !== undefined && <span className="tabular-nums">{i.index} · </span>}{i.label}</>;
          return (
            <li key={i.key} data-step={i.key} data-track={i.key} data-state={i.state} aria-current={i.isStage ? 'step' : undefined} className="min-w-0">
              <div className={`h-1 rounded-full transition-colors duration-base ease-standard ${bar(i)}`} data-essential-motion="" />
              <p className={`mt-2 flex items-center gap-1.5 text-body-sm ${i.isStage || viewingKey === i.key ? 'font-semibold text-fg' : 'font-medium text-fg-muted'}`}>
                <Icon className={`h-4 w-4 shrink-0 ${tone}`} aria-hidden="true" />
                {i.onView
                  ? <button type="button" onClick={i.onView} className="min-w-0 text-start underline-offset-2 hover:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-primary" aria-pressed={viewingKey === i.key}>{name}</button>
                  : <span className="min-w-0">{name}</span>}
              </p>
              <p className={`mt-0.5 ps-[1.375rem] text-meta ${i.state === 'blocked' ? 'font-medium text-danger-fg' : i.isStage ? 'font-medium text-primary' : i.success && i.state === 'done' ? 'text-success-fg' : 'text-fg-muted'}`} data-testid={`bank-step-reason-${i.key}`}>{i.note}</p>
            </li>
          );
        })}
      </ol>

      {/* Phone: segments, where you are, and the steps you can open. */}
      <div className="sm:hidden">
        <div aria-hidden="true" className="grid gap-1.5" style={{ gridTemplateColumns: `repeat(${items.length}, minmax(0, 1fr))` }}>
          {items.map(i => <span key={i.key} className={`h-1 rounded-full ${bar(i)}`} />)}
        </div>
        <div className="relative mt-3 flex items-center justify-between gap-2">
          <p className="min-w-0 text-body font-semibold text-fg">{compactLabel}</p>
          {reachable.length > 0 && (
            <>
              <button ref={menuButton} type="button" className="btn-ghost btn-sm shrink-0" aria-haspopup="menu" aria-expanded={menuOpen} aria-controls={menuId} onClick={() => setMenuOpen(v => !v)}>
                {t('All steps')}<ChevronDown className={`h-4 w-4 transition-transform motion-reduce:transition-none ${menuOpen ? 'rotate-180' : ''}`} aria-hidden="true" />
              </button>
              {menuOpen && (
                <ul
                  ref={menu}
                  id={menuId}
                  role="menu"
                  className="surface absolute end-0 top-full z-30 mt-1 min-w-[14rem] p-1 shadow-e3"
                  onKeyDown={e => {
                    if (e.key === 'Escape') { e.preventDefault(); setMenuOpen(false); menuButton.current?.focus(); }
                    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
                      e.preventDefault();
                      const all = Array.from(menu.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? []);
                      const at = all.indexOf(document.activeElement as HTMLElement);
                      all[(at + (e.key === 'ArrowDown' ? 1 : all.length - 1)) % all.length]?.focus();
                    }
                  }}
                >
                  {reachable.map(i => (
                    <li key={i.key} role="none">
                      <button type="button" role="menuitem" className="flex min-h-11 w-full items-center gap-2 rounded-lg px-3 text-start text-body hover:bg-surface-sunken" onClick={() => { setMenuOpen(false); i.onView?.(); }}>
                        {i.index !== undefined && <span className="tabular-nums text-fg-muted">{i.index}</span>}{i.label}
                        <span className="ms-auto text-meta text-fg-muted">{i.note}</span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </>
          )}
        </div>
      </div>
    </div>
  );
}
