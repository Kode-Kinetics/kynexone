'use client';

import { useMemo } from 'react';
import { useLocale } from '../../contexts/LocaleContext';
import { daysInMonth, joinDate, monthNames, splitDate, yearChoices } from '../../lib/dateParts';

/**
 * Month and date pickers drawn from selects. The browser's native month and date controls print mm/dd/yyyy in the browser's own
 * language whatever the page language is, so under Arabic they showed an English format. Values are "YYYY-MM" and "YYYY-MM-DD".
 */
function useParts(value: string, kind: 'date' | 'month', onChange: (v: string) => void) {
  const { locale } = useLocale();
  const parts = splitDate(value);
  const names = useMemo(() => monthNames(locale), [locale]);
  const years = useMemo(() => {
    const center = parts.year ?? new Date().getFullYear();
    const list = yearChoices(center, 5);
    return list;
  }, [parts.year]);
  const set = (patch: Partial<typeof parts>) => {
    const next = { ...parts, ...patch };
    // A date needs all three parts; picking one part first fills the others from today so the value is never half-set.
    const now = new Date();
    if (next.year == null) next.year = now.getFullYear();
    if (next.month == null) next.month = now.getMonth() + 1;
    if (kind === 'date' && next.day == null) next.day = 1;
    onChange(joinDate(next, kind));
  };
  return { parts, names, years, set };
}

export function MonthPicker({ value, onChange, className = '', 'aria-label': ariaLabel }: { value: string; onChange: (v: string) => void; className?: string; 'aria-label'?: string }) {
  const { t } = useLocale();
  const { parts, names, years, set } = useParts(value, 'month', onChange);
  return (
    <span className={`flex gap-2 ${className}`} role="group" aria-label={ariaLabel}>
      <select className="select w-full" aria-label={t('Month')} value={parts.month ?? ''} onChange={e => set({ month: Number(e.target.value) })}>
        {parts.month == null && <option value="" />}
        {names.map((n, i) => <option key={n} value={i + 1}>{n}</option>)}
      </select>
      <select className="select w-28" aria-label={t('Year')} value={parts.year ?? ''} onChange={e => set({ year: Number(e.target.value) })}>
        {parts.year == null && <option value="" />}
        {years.map(y => <option key={y} value={y}>{y}</option>)}
      </select>
    </span>
  );
}

export function DatePicker({ value, onChange, className = '', 'aria-label': ariaLabel }: { value: string; onChange: (v: string) => void; className?: string; 'aria-label'?: string }) {
  const { t } = useLocale();
  const { parts, names, years, set } = useParts(value, 'date', onChange);
  const days = Array.from({ length: parts.year != null && parts.month != null ? daysInMonth(parts.year, parts.month) : 31 }, (_, i) => i + 1);
  return (
    <span className={`flex gap-1.5 ${className}`} role="group" aria-label={ariaLabel}>
      <select className="select w-16" aria-label={t('Day')} value={parts.day ?? ''} onChange={e => set({ day: Number(e.target.value) })}>
        {parts.day == null && <option value="" />}
        {days.map(d => <option key={d} value={d}>{d}</option>)}
      </select>
      <select className="select w-full" aria-label={t('Month')} value={parts.month ?? ''} onChange={e => set({ month: Number(e.target.value) })}>
        {parts.month == null && <option value="" />}
        {names.map((n, i) => <option key={n} value={i + 1}>{n}</option>)}
      </select>
      <select className="select w-24" aria-label={t('Year')} value={parts.year ?? ''} onChange={e => set({ year: Number(e.target.value) })}>
        {parts.year == null && <option value="" />}
        {years.map(y => <option key={y} value={y}>{y}</option>)}
      </select>
    </span>
  );
}

/** A date and a 24-hour time from selects ("YYYY-MM-DDTHH:mm"), for a deadline: the browser's own date-time control follows the browser's language and zone. */
export function DateTimePicker({ value, onChange, className = '', 'aria-label': ariaLabel }: { value: string; onChange: (v: string) => void; className?: string; 'aria-label'?: string }) {
  const { t } = useLocale();
  const [date, time = ''] = value.split('T');
  const [hh, mm] = time.split(':');
  const hour = hh === undefined || hh === '' ? null : Number(hh);
  const minute = mm === undefined || mm === '' ? null : Number(mm);
  const emit = (d: string, h: number | null, m: number | null) => onChange(d ? `${d}T${String(h ?? 0).padStart(2, '0')}:${String(m ?? 0).padStart(2, '0')}` : '');
  return (
    <span className={`flex flex-wrap items-center gap-1.5 ${className}`} role="group" aria-label={ariaLabel}>
      <DatePicker value={date ?? ''} onChange={d => emit(d, hour, minute)} />
      <select className="select w-16" aria-label={t('Hour')} value={hour ?? ''} onChange={e => emit(date ?? '', Number(e.target.value), minute)}>
        {hour == null && <option value="" />}
        {Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{String(h).padStart(2, '0')}</option>)}
      </select>
      <span aria-hidden="true">:</span>
      <select className="select w-16" aria-label={t('Minute')} value={minute ?? ''} onChange={e => emit(date ?? '', hour, Number(e.target.value))}>
        {minute == null && <option value="" />}
        {Array.from({ length: 60 }, (_, m) => <option key={m} value={m}>{String(m).padStart(2, '0')}</option>)}
      </select>
    </span>
  );
}
