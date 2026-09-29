'use client';

import { useEffect, useRef, useState, useCallback } from 'react';
import { Search, X, User } from 'lucide-react';
import { employeesApi } from '../api/employees';
import type { EmployeeListItem } from '../api/employees';

export interface SelectedEmployee {
  id: number;
  fullName: string;
  department: string;
  designation: string;
  employeeCode: string;
}

interface Props {
  value: SelectedEmployee | null;
  onChange: (emp: SelectedEmployee | null) => void;
  readOnly?: boolean;
  placeholder?: string;
  label?: string;
  required?: boolean;
}

export function EmployeePicker({ value, onChange, readOnly = false, placeholder = 'Search by name or ID…', label, required }: Props) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<EmployeeListItem[]>([]);
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const search = useCallback((q: string) => {
    if (q.trim().length < 1) { setResults([]); setOpen(false); return; }
    setLoading(true);
    employeesApi.list({ search: q, pageSize: 8, status: 'Active' })
      .then(r => { setResults(r.items); setOpen(true); })
      .catch(() => setResults([]))
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => search(query), 250);
    return () => { if (debounceRef.current) clearTimeout(debounceRef.current); };
  }, [query, search]);

  // Close dropdown on outside click
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, []);

  const select = (emp: EmployeeListItem) => {
    onChange({ id: emp.id, fullName: emp.fullName, department: emp.department, designation: emp.designation, employeeCode: emp.employeeCode });
    setQuery('');
    setOpen(false);
  };

  const clear = () => { onChange(null); setQuery(''); setResults([]); };

  const inputCls = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 placeholder:text-slate-400 focus:border-sapphire focus:outline-none focus:ring-2 focus:ring-sapphire/20 dark:border-white/10 dark:bg-white/[0.04] dark:text-white dark:placeholder:text-slate-500';

  return (
    <div ref={containerRef} className="relative">
      {label && (
        <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">
          {label}{required && <span className="ms-0.5 text-rose-500">*</span>}
        </label>
      )}

      {/* Selected state */}
      {value && !readOnly ? (
        <div className="flex items-center gap-2 rounded-lg border border-sapphire/30 bg-sapphire/5 px-3 py-2 dark:border-sapphire/20 dark:bg-sapphire/10">
          <div className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-sapphire/10 dark:bg-sapphire/20">
            <User className="h-3.5 w-3.5 text-sapphire dark:text-cyanAccent" />
          </div>
          <div className="min-w-0 flex-1">
            <p className="truncate text-sm font-medium text-slate-800 dark:text-white">{value.fullName}</p>
            <p className="truncate text-xs text-slate-500 dark:text-slate-400">#{value.id} · {value.department}</p>
          </div>
          <button type="button" onClick={clear} className="shrink-0 rounded p-0.5 text-slate-400 hover:text-slate-600 dark:hover:text-slate-200" aria-label="Clear employee">
            <X className="h-3.5 w-3.5" />
          </button>
        </div>
      ) : readOnly && value ? (
        <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 dark:border-white/10 dark:bg-white/[0.02]">
          <p className="text-sm text-slate-700 dark:text-slate-300">{value.fullName} <span className="text-slate-400">#{value.id}</span></p>
        </div>
      ) : (
        /* Search input */
        <div className="relative">
          <Search className="pointer-events-none absolute start-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-slate-400" />
          <input
            className={`${inputCls} ps-8`}
            placeholder={placeholder}
            value={query}
            onChange={e => setQuery(e.target.value)}
            onFocus={() => { if (results.length > 0) setOpen(true); }}
            disabled={readOnly}
            autoComplete="off"
          />
          {loading && (
            <div className="pointer-events-none absolute end-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
          )}
        </div>
      )}

      {/* Dropdown */}
      {open && results.length > 0 && (
        <div className="absolute z-50 mt-1 w-full overflow-hidden rounded-xl border border-slate-200 bg-white shadow-lg dark:border-white/10 dark:bg-slate-800">
          {results.map(emp => (
            <button
              key={emp.id}
              type="button"
              onMouseDown={e => { e.preventDefault(); select(emp); }}
              className="flex w-full items-center gap-3 px-3 py-2.5 text-start transition-colors hover:bg-slate-50 dark:hover:bg-white/[0.05]"
            >
              <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-gradient-to-br from-sapphire/20 to-sapphire/5 text-xs font-semibold text-sapphire dark:text-cyanAccent">
                {emp.fullName.charAt(0).toUpperCase()}
              </div>
              <div className="min-w-0 flex-1">
                <p className="truncate text-sm font-medium text-slate-800 dark:text-white">{emp.fullName}</p>
                <p className="truncate text-xs text-slate-500 dark:text-slate-400">#{emp.id} · {emp.department} · {emp.designation}</p>
              </div>
            </button>
          ))}
        </div>
      )}

      {open && !loading && results.length === 0 && query.length >= 1 && (
        <div className="absolute z-50 mt-1 w-full rounded-xl border border-slate-200 bg-white px-4 py-3 text-sm text-slate-500 shadow-lg dark:border-white/10 dark:bg-slate-800 dark:text-slate-400">
          No employees found for &ldquo;{query}&rdquo;
        </div>
      )}
    </div>
  );
}
