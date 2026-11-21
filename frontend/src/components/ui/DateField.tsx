'use client';

import { forwardRef, useEffect, useId, useImperativeHandle, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react';
import { CalendarDays, ChevronLeft, ChevronRight } from 'lucide-react';
import { useT } from '../../hooks/useT';
import { useFormat } from '../../hooks/useFormat';
import { addDays, monthGrid, parseIsoDay, toIsoDay } from '../../lib/creditDate';

/**
 * One labelled date control (Screen Standard 3b): a field that shows the chosen day with its weekday ("Sun 27 Sep 2026", Latin digits)
 * and opens a month calendar. Days the caller rules out stay in the grid, announced "unavailable", and cannot be chosen by mouse or
 * keyboard. Typing an ISO date (2026-09-27) into the field is accepted.
 *
 * Keyboard in the calendar: arrows move a day / a week, PageUp / PageDown a month, Home / End the week's start / end, Enter or Space
 * chooses and closes, Escape closes and returns focus to the field. The week starts on Sunday in both languages.
 *
 * Use it inside `Field` (the label binds to the input by id). Replaces the three unlabelled selects of `DatePicker placeholders` where a
 * single day matters; other screens keep DatePicker until they move.
 */
export interface DateFieldHandle { focus: () => void }

export const DateField = forwardRef<DateFieldHandle, {
  id?: string;
  value: string;
  onChange: (iso: string) => void;
  /** A day that may be chosen. */
  isSelectable: (iso: string) => boolean;
  /** Outlined in the grid. */
  today: string;
  invalid?: boolean;
  'aria-describedby'?: string;
  'aria-invalid'?: boolean;
  'aria-required'?: boolean;
  testId?: string;
}>(function DateField({ id, value, onChange, isSelectable, today, invalid, testId, ...aria }, ref) {
  const t = useT();
  const fx = useFormat();
  const gridId = useId();
  const input = useRef<HTMLInputElement>(null);
  const popover = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState<string | null>(null);
  const [focusDay, setFocusDay] = useState(value || today);
  const [above, setAbove] = useState(false);
  const shown = parseIsoDay(focusDay) ?? parseIsoDay(today)!;
  const year = shown.getUTCFullYear();
  const month = shown.getUTCMonth();

  useImperativeHandle(ref, () => ({ focus: () => input.current?.focus() }), []);

  // Flip above the field when the window has no room below it.
  useLayoutEffect(() => {
    if (!open || !input.current) return;
    const box = input.current.getBoundingClientRect();
    setAbove(window.innerHeight - box.bottom < 340 && box.top > 340);
  }, [open]);

  // Focus the day once the calendar is drawn, and keep it on the moving day.
  useEffect(() => {
    if (!open) return;
    popover.current?.querySelector<HTMLElement>(`[data-day="${focusDay}"]`)?.focus();
  }, [open, focusDay]);

  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      if (!popover.current?.contains(e.target as Node) && !input.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDoc);
    return () => document.removeEventListener('mousedown', onDoc);
  }, [open]);

  const openCalendar = () => { setFocusDay(value || today); setOpen(true); };
  const close = (refocus = true) => { setOpen(false); if (refocus) input.current?.focus(); };
  const choose = (iso: string) => { if (!isSelectable(iso)) return; onChange(iso); close(); };

  const shownText = value ? fx.gregorian(value, 'weekdayDate') : '';
  const commitTyped = () => {
    if (editing === null) return;
    const typed = editing.trim();
    setEditing(null);
    if (parseIsoDay(typed) && isSelectable(typed)) onChange(typed);
  };

  const onGridKey = (e: KeyboardEvent<HTMLDivElement>) => {
    const move: Record<string, () => string> = {
      ArrowLeft: () => addDays(focusDay, document.dir === 'rtl' ? 1 : -1),
      ArrowRight: () => addDays(focusDay, document.dir === 'rtl' ? -1 : 1),
      ArrowUp: () => addDays(focusDay, -7),
      ArrowDown: () => addDays(focusDay, 7),
      PageUp: () => shiftMonth(focusDay, -1),
      PageDown: () => shiftMonth(focusDay, 1),
      Home: () => addDays(focusDay, -(parseIsoDay(focusDay)!.getUTCDay())),
      End: () => addDays(focusDay, 6 - parseIsoDay(focusDay)!.getUTCDay()),
    };
    if (move[e.key]) { e.preventDefault(); setFocusDay(move[e.key]()); return; }
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); choose(focusDay); return; }
    if (e.key === 'Escape') { e.preventDefault(); close(); }
  };

  const weeks = monthGrid(year, month);
  // Sunday-first weekday names (2026-09-27 is a Sunday).
  const weekdays = Array.from({ length: 7 }, (_, i) => addDays('2026-09-27', i));
  const monthTitle = fx.gregorian(toIsoDay(new Date(Date.UTC(year, month, 15))), 'monthYear');

  return (
    <div className="relative w-full max-w-[320px]" data-testid={testId}>
      <input
        ref={input}
        id={id}
        type="text"
        role="combobox"
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-controls={open ? gridId : undefined}
        aria-invalid={invalid || aria['aria-invalid'] || undefined}
        aria-describedby={aria['aria-describedby']}
        aria-required={aria['aria-required']}
        autoComplete="off"
        spellCheck={false}
        className="input w-full cursor-pointer pe-10"
        value={editing ?? shownText}
        onFocus={() => setEditing(null)}
        onClick={() => (open ? close(false) : openCalendar())}
        onChange={e => setEditing(e.target.value)}
        onBlur={commitTyped}
        onKeyDown={e => {
          if (e.key === 'Enter' && editing !== null) { e.preventDefault(); commitTyped(); return; }
          if (e.key === 'ArrowDown' || (e.key === 'Enter' && editing === null)) { e.preventDefault(); openCalendar(); }
          if (e.key === 'Escape' && open) { e.preventDefault(); close(); }
        }}
      />
      <CalendarDays className="pointer-events-none absolute end-3 top-1/2 h-4 w-4 -translate-y-1/2 text-fg-muted" aria-hidden="true" />

      {open && (
        <div
          ref={popover}
          role="dialog"
          aria-label={t('Choose a date')}
          className={`surface absolute start-0 z-40 w-[300px] p-3 shadow-e3 ${above ? 'bottom-full mb-1' : 'top-full mt-1'}`}
        >
          <div className="mb-2 flex items-center justify-between gap-2">
            <button type="button" className="btn-ghost btn-sm h-8 w-8 justify-center p-0" aria-label={t('Previous month')} onClick={() => setFocusDay(shiftMonth(focusDay, -1))}>
              <ChevronLeft className="h-4 w-4" aria-hidden="true" />
            </button>
            <p className="text-body-sm font-semibold text-fg" aria-live="polite">{monthTitle}</p>
            <button type="button" className="btn-ghost btn-sm h-8 w-8 justify-center p-0" aria-label={t('Next month')} onClick={() => setFocusDay(shiftMonth(focusDay, 1))}>
              <ChevronRight className="h-4 w-4" aria-hidden="true" />
            </button>
          </div>
          <div id={gridId} role="grid" aria-label={monthTitle} onKeyDown={onGridKey} className="text-center">
            <div role="row" className="grid grid-cols-7">
              {weekdays.map(d => <span key={d} role="columnheader" className="py-1 text-meta text-fg-muted">{fx.gregorian(d, 'weekdayShort')}</span>)}
            </div>
            {weeks.map((week, w) => (
              <div key={w} role="row" className="grid grid-cols-7 gap-y-0.5">
                {week.map((day, i) => {
                  if (!day) return <span key={i} role="gridcell" />;
                  const ok = isSelectable(day);
                  const selected = day === value;
                  const label = fx.gregorian(day, 'full');
                  return (
                    <span key={day} role="gridcell" aria-selected={selected}>
                      <button
                        type="button"
                        data-day={day}
                        tabIndex={day === focusDay ? 0 : -1}
                        aria-disabled={!ok || undefined}
                        aria-label={ok ? label : t('{date}, unavailable', { date: label })}
                        onClick={() => { setFocusDay(day); choose(day); }}
                        className={`mx-auto grid h-8 w-8 place-items-center rounded-lg text-body-sm tabular-nums focus-visible:outline focus-visible:outline-2 focus-visible:outline-primary ${
                          selected ? 'bg-primary font-semibold text-on-primary'
                            : !ok ? 'cursor-not-allowed text-fg-subtle line-through'
                            : 'text-fg hover:bg-surface-sunken'
                        } ${day === today && !selected ? 'ring-1 ring-inset ring-line-strong' : ''}`}
                      >
                        {parseIsoDay(day)!.getUTCDate()}
                      </button>
                    </span>
                  );
                })}
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
});

/** The same day a month earlier / later, clamped to the month's last day. */
function shiftMonth(iso: string, by: number): string {
  const d = parseIsoDay(iso);
  if (!d) return iso;
  const y = d.getUTCFullYear(); const m = d.getUTCMonth() + by;
  const last = new Date(Date.UTC(y, m + 1, 0)).getUTCDate();
  return toIsoDay(new Date(Date.UTC(y, m, Math.min(d.getUTCDate(), last))));
}
