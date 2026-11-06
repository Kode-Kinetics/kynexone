'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { Search, SlidersHorizontal, X } from 'lucide-react';
import { employeesApi } from '../../api/employees';
import type { EmployeeListItem } from '../../api/employees';
import { branchesApi, departmentsApi, designationsApi } from '../../api/organization';
import { useAuth } from '../../contexts/AuthContext';
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

interface Option { value: string; label: string }

/** The org list, plus any value seen on the people found that the list lacks (an unlisted or legacy label), sorted. */
function mergeOptions(known: Option[], seen: Array<string | undefined>, locale: string): Option[] {
  const map = new Map(known.map((o) => [o.value, o.label] as const));
  for (const v of seen) if (v && !map.has(v)) map.set(v, v);
  return [...map.entries()].map(([value, label]) => ({ value, label })).sort((a, b) => a.label.localeCompare(b.label, locale));
}

function FilterSelect({ id, label, any, value, onChange, options }: { id: string; label: string; any: string; value: string; onChange: (v: string) => void; options: Option[] }) {
  return (
    <div>
      <label htmlFor={id} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">{label}</label>
      <select id={id} className="select w-full" value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">{any}</option>
        {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </div>
  );
}

interface Props {
  /** Prefix for element ids, unique per screen. */
  id: string;
  label: string;
  selection: Selection;
  onChange: (next: Selection) => void;
  /** Most people the receiving endpoint accepts in one request. */
  max: number;
  disabled?: boolean;
  /** Height cap of the results list (Tailwind max-h class); compact layouts pass a smaller one. */
  listMaxHeightClass?: string;
}

/**
 * Pick many Active employees: server "360" search (debounced, latest answer wins) over code, names, work email, job title,
 * department and branch, plus phone, personal email and the exact ID/Iqama number for employees.sensitive holders, an optional
 * department filter, "select all shown", and a running count. The selection is held by the parent and is
 * independent of the visible results, so it survives new searches and filter changes.
 */
export function EmployeeMultiSelect({ id, label, selection, onChange, max, disabled, listMaxHeightClass = 'max-h-56' }: Props) {
  const { t, locale } = useLocale();
  const { hasPermission } = useAuth();
  // The server matches phone, personal email and ID only for employees.sensitive; the hint says what this user can search.
  const searchHint = hasPermission('employees.sensitive')
    ? t('Name, code, email, phone, ID/Iqama, job title, department or branch')
    : t('Name, code, email, job title, department or branch');
  const [query, setQuery] = useState('');
  const [department, setDepartment] = useState('');
  const [branch, setBranch] = useState('');
  const [designation, setDesignation] = useState('');
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [results, setResults] = useState<EmployeeListItem[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [searchFailed, setSearchFailed] = useState(false);
  const [atCap, setAtCap] = useState(false);
  const [catalog, setCatalog] = useState<{ departments: Option[]; branches: Option[]; designations: Option[] }>({ departments: [], branches: [], designations: [] });
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Debounce spaces requests out but does not order their responses: only the latest search may write results.
  const searchGate = useMemo(() => createLatestRequestGate(), []);

  useEffect(() => {
    let cancelled = false;
    // The org lists are a convenience: any that cannot be read is built from the people found instead.
    const name = (en: string, ar?: string | null) => (locale === 'ar' && ar ? ar : en);
    void Promise.allSettled([departmentsApi.listAll(), branchesApi.listAll(), designationsApi.listAll()]).then(([d, b, j]) => {
      if (cancelled) return;
      setCatalog({
        departments: d.status === 'fulfilled' ? d.value.filter((x) => x.isActive !== false).map((x) => ({ value: x.nameEn, label: name(x.nameEn, x.nameAr) })) : [],
        branches: b.status === 'fulfilled' ? b.value.filter((x) => x.isActive !== false).map((x) => ({ value: x.nameEn, label: name(x.nameEn, x.nameAr) })) : [],
        designations: j.status === 'fulfilled' ? j.value.filter((x) => x.isActive !== false).map((x) => ({ value: x.titleEn, label: name(x.titleEn, x.titleAr) })) : [],
      });
    });
    return () => { cancelled = true; };
  }, [locale]);

  useEffect(() => {
    searchGate.invalidate();
    setLoading(true);
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      void runLatest(searchGate, () => employeesApi.list({
        search: query.trim() || undefined, department: department || undefined, branch: branch || undefined,
        designation: designation || undefined, pageSize: PAGE_SIZE, status: 'Active',
      }), {
        onResult: (page) => { setResults(page.items ?? []); setTotal(page.total ?? (page.items ?? []).length); setSearchFailed(false); },
        onError: () => { setResults([]); setTotal(0); setSearchFailed(true); },
        onSettled: () => setLoading(false),
      });
    }, 250);
    return () => { if (debounceRef.current) clearTimeout(debounceRef.current); };
  }, [query, department, branch, designation, searchGate]);

  const shown = useMemo(() => shownEmployees(results, department), [results, department]);
  const everyShownSelected = allShownSelected(selection, shown);
  const options = useMemo(() => ({
    departments: mergeOptions(catalog.departments, results.map((r) => r.department), locale),
    branches: mergeOptions(catalog.branches, results.map((r) => r.branch), locale),
    designations: mergeOptions(catalog.designations, results.map((r) => r.designation), locale),
  }), [catalog, results, locale]);
  const activeFilters = [department, branch, designation].filter(Boolean).length;
  const clearFilters = () => { setDepartment(''); setBranch(''); setDesignation(''); };

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
    <fieldset className="min-w-0 space-y-2" disabled={disabled}>
      <legend className="sr-only">{label}</legend>
      <div className="flex gap-2">
        <div className="relative min-w-0 flex-1">
          <label htmlFor={`${id}-search`} className="sr-only">{t('Find employees')}</label>
          <Search className="pointer-events-none absolute start-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-slate-400" aria-hidden="true" />
          <input id={`${id}-search`} type="search" className="input w-full ps-8" autoComplete="off" value={query}
            placeholder={searchHint} title={searchHint} onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') e.preventDefault(); }} />
        </div>
        <button type="button" className={`btn-secondary shrink-0 px-3 text-xs ${activeFilters > 0 ? 'border-sapphire text-sapphire' : ''}`}
          aria-expanded={filtersOpen} aria-controls={`${id}-filters`} onClick={() => setFiltersOpen((open) => !open)}>
          <SlidersHorizontal className="h-3.5 w-3.5" aria-hidden="true" />
          {activeFilters > 0 ? t('Filters ({count})', { count: activeFilters }) : t('Filters')}
        </button>
      </div>
      {filtersOpen && (
        <div id={`${id}-filters`} className="grid gap-2 sm:grid-cols-3">
          <FilterSelect id={`${id}-department`} label={t('Department')} any={t('All departments')} value={department} onChange={setDepartment} options={options.departments} />
          <FilterSelect id={`${id}-branch`} label={t('Branch')} any={t('All branches')} value={branch} onChange={setBranch} options={options.branches} />
          <FilterSelect id={`${id}-designation`} label={t('Job title')} any={t('All job titles')} value={designation} onChange={setDesignation} options={options.designations} />
        </div>
      )}

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
        {activeFilters > 0 && (
          <button type="button" className="text-xs text-slate-500 underline hover:text-sapphire" onClick={clearFilters}>{t('Clear filters')}</button>
        )}
      </div>
      {atCap && <p role="status" className="text-xs text-amber-700 dark:text-amber-400">{t('You can select at most {max} employees at once.', { max })}</p>}

      <ul aria-label={t('Employees matching your search')} aria-busy={loading}
        className={`${listMaxHeightClass} divide-y divide-slate-100 overflow-y-auto rounded-lg border border-slate-200 dark:divide-white/[0.06] dark:border-white/10`}>
        {shown.map((e) => (
          <li key={e.id}>
            <label className="flex cursor-pointer items-center gap-2 px-3 py-2 text-sm hover:bg-slate-50 dark:hover:bg-white/[0.03]">
              <input type="checkbox" checked={selection.has(e.id)} onChange={() => toggle(e)} />
              <span className="font-mono text-xs text-slate-500">{e.employeeCode}</span>
              <span className="min-w-0 flex-1 truncate text-slate-800 dark:text-slate-100">{nameOf(e)}</span>
              <span className="max-w-[45%] truncate text-xs text-slate-400">{[e.designation, e.department].filter(Boolean).join(' · ')}</span>
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
