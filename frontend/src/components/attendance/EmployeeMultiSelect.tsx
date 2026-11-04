'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { Search, X } from 'lucide-react';
import { employeesApi } from '../../api/employees';
import type { EmployeeListItem } from '../../api/employees';
import { departmentsApi } from '../../api/organization';
import { useLocale } from '../../contexts/LocaleContext';
import { createLatestRequestGate, runLatest } from '../../lib/latestRequest';
import {
  allShownSelected,
  clearShown,
  selectAllShown,
  shownEmployees,
  toggleEmployee,
  type Selection,
} from '../../lib/attendanceSelection';
import { StatusChip } from '../StatusChip';

/** The server pages employees at 100 at most; "select all shown" never reaches past what is on screen. */
const PAGE_SIZE = 100;

interface Props {
  /** Prefix for element ids, unique per screen. */
  id: string;
  label: string;
  selection: Selection;
  onChange: (next: Selection) => void;
  /** Most people the receiving endpoint accepts in one request. */
  max: number;
  disabled?: boolean;
}

/**
 * Pick many Active employees: server search by name or code (debounced, latest answer wins), an optional
 * department filter, "select all shown", and a running count. The selection is held by the parent and is
 * independent of the visible results, so it survives new searches and filter changes.
 */
export function EmployeeMultiSelect({ id, label, selection, onChange, max, disabled }: Props) {
  const { t, locale } = useLocale();
  const [query, setQuery] = useState('');
  const [department, setDepartment] = useState('');
  const [results, setResults] = useState<EmployeeListItem[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [searchFailed, setSearchFailed] = useState(false);
  const [atCap, setAtCap] = useState(false);
  const [departments, setDepartments] = useState<Array<{ value: string; label: string }>>([]);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Debounce spaces requests out but does not order their responses: only the latest search may write results.
  const searchGate = useMemo(() => createLatestRequestGate(), []);

  useEffect(() => {
    let cancelled = false;
    // The department list is a convenience: if it cannot be read the filter is built from the people found.
    departmentsApi.listAll().then((rows) => {
      if (cancelled) return;
      setDepartments(rows.filter((d) => d.isActive !== false).map((d) => ({ value: d.nameEn, label: locale === 'ar' && d.nameAr ? d.nameAr : d.nameEn })));
    }).catch(() => { /* fall back to departments seen in results */ });
    return () => { cancelled = true; };
  }, [locale]);

  useEffect(() => {
    searchGate.invalidate();
    setLoading(true);
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      void runLatest(searchGate, () => employeesApi.list({ search: query.trim() || undefined, department: department || undefined, pageSize: PAGE_SIZE, status: 'Active' }), {
        onResult: (page) => { setResults(page.items ?? []); setTotal(page.total ?? (page.items ?? []).length); setSearchFailed(false); },
        onError: () => { setResults([]); setTotal(0); setSearchFailed(true); },
        onSettled: () => setLoading(false),
      });
    }, 250);
    return () => { if (debounceRef.current) clearTimeout(debounceRef.current); };
  }, [query, department, searchGate]);

  const shown = useMemo(() => shownEmployees(results, department), [results, department]);
  const everyShownSelected = allShownSelected(selection, shown);
  const departmentOptions = useMemo(() => {
    const known = new Map(departments.map((d) => [d.value, d.label] as const));
    for (const r of results) if (r.department && !known.has(r.department)) known.set(r.department, r.department);
    return [...known.entries()].map(([value, text]) => ({ value, label: text })).sort((a, b) => a.label.localeCompare(b.label, locale));
  }, [departments, results, locale]);

  const nameOf = (e: { fullName: string; arabicName?: string }) => (locale === 'ar' && e.arabicName ? e.arabicName : e.fullName);
  const toggle = (e: EmployeeListItem) => {
    const next = toggleEmployee(selection, e, max);
    setAtCap(next.atCap);
    onChange(next.selection);
  };
  const toggleAllShown = () => {
    if (everyShownSelected) { setAtCap(false); onChange(clearShown(selection, shown)); return; }
    const next = selectAllShown(selection, shown, max);
    setAtCap(next.capped);
    onChange(next.selection);
  };
  const selectedList = [...selection.values()].sort((a, b) => a.employeeCode.localeCompare(b.employeeCode, undefined, { numeric: true }));

  return (
    <fieldset className="space-y-2" disabled={disabled}>
      <legend className="text-xs font-semibold text-slate-700 dark:text-slate-200">{label}</legend>
      <div className="grid gap-2 sm:grid-cols-2">
        <div>
          <label htmlFor={`${id}-search`} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">{t('Find employees')}</label>
          <div className="relative">
            <Search className="pointer-events-none absolute start-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-slate-400" aria-hidden="true" />
            <input id={`${id}-search`} type="search" className="input w-full ps-8" autoComplete="off" value={query}
              placeholder={t('Search by name or employee code')} onChange={(e) => setQuery(e.target.value)}
              onKeyDown={(e) => { if (e.key === 'Enter') e.preventDefault(); }} />
          </div>
        </div>
        <div>
          <label htmlFor={`${id}-department`} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">{t('Department')}</label>
          <select id={`${id}-department`} className="select w-full" value={department} onChange={(e) => setDepartment(e.target.value)}>
            <option value="">{t('All departments')}</option>
            {departmentOptions.map((d) => <option key={d.value} value={d.value}>{d.label}</option>)}
          </select>
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <button type="button" className="btn-secondary px-3 py-1 text-xs" onClick={toggleAllShown} disabled={shown.length === 0}>
          {everyShownSelected ? t('Deselect shown') : t('Select all shown')}
        </button>
        <span role="status" aria-live="polite">
          <StatusChip tone={selection.size > 0 ? 'blue' : 'slate'} label={t('{count, plural, one {# employee selected} other {# employees selected}}', { count: selection.size })} />
        </span>
        {selection.size > 0 && (
          <button type="button" className="text-xs text-slate-500 underline hover:text-rose-600" onClick={() => { setAtCap(false); onChange(new Map()); }}>{t('Clear selection')}</button>
        )}
      </div>
      {atCap && <p role="status" className="text-xs text-amber-700 dark:text-amber-400">{t('You can select at most {max} employees at once.', { max })}</p>}

      <ul aria-label={t('Employees matching your search')} aria-busy={loading}
        className="max-h-56 divide-y divide-slate-100 overflow-y-auto rounded-lg border border-slate-200 dark:divide-white/[0.06] dark:border-white/10">
        {shown.map((e) => (
          <li key={e.id}>
            <label className="flex cursor-pointer items-center gap-2 px-3 py-2 text-sm hover:bg-slate-50 dark:hover:bg-white/[0.03]">
              <input type="checkbox" checked={selection.has(e.id)} onChange={() => toggle(e)} />
              <span className="font-mono text-xs text-slate-500">{e.employeeCode}</span>
              <span className="min-w-0 flex-1 truncate text-slate-800 dark:text-slate-100">{nameOf(e)}</span>
              <span className="truncate text-xs text-slate-400">{e.department}</span>
            </label>
          </li>
        ))}
      </ul>
      {loading && <p role="status" className="text-xs text-slate-500">{t('Searching…')}</p>}
      {!loading && searchFailed && <p role="alert" className="text-xs text-rose-700 dark:text-rose-400">{t('Could not search employees. Try again.')}</p>}
      {!loading && !searchFailed && shown.length === 0 && <p className="text-xs text-slate-500">{t('No active employees match your search.')}</p>}
      {!loading && !searchFailed && total > shown.length && shown.length > 0 && (
        <p className="text-xs text-slate-500">{t('Showing {shown} of {total} active employees. Search or pick a department to narrow the list.', { shown: shown.length, total })}</p>
      )}

      {selection.size > 0 && (
        <details className="text-xs">
          <summary className="cursor-pointer text-slate-600 dark:text-slate-300">{t('Show selected employees')}</summary>
          <ul className="mt-2 flex flex-wrap gap-1.5">
            {selectedList.map((p) => (
              <li key={p.id} className="inline-flex items-center gap-1 rounded-full bg-slate-100 px-2.5 py-1 dark:bg-white/10">
                {p.employeeCode} · {p.fullName}
                <button type="button" aria-label={t('Remove {name}', { name: p.fullName })} onClick={() => { setAtCap(false); const next = new Map(selection); next.delete(p.id); onChange(next); }}>
                  <X className="h-3 w-3" aria-hidden="true" />
                </button>
              </li>
            ))}
          </ul>
        </details>
      )}
    </fieldset>
  );
}
