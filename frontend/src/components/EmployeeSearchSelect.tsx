'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { Search, X, User } from 'lucide-react';
import { employeesApi } from '../api/employees';
import type { EmployeeListItem } from '../api/employees';

export interface EmployeeSelection {
  intId: number;
  publicId: string;
  fullName: string;
  employeeCode: string;
  department: string;
}

interface Props {
  value: EmployeeSelection | null;
  onChange: (emp: EmployeeSelection | null) => void;
  placeholder?: string;
  required?: boolean;
}

export function EmployeeSearchSelect({ value, onChange, placeholder = 'Search by name or code…', required }: Props) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<EmployeeListItem[]>([]);
  const [open, setOpen] = useState(false);
  const [searching, setSearching] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);

  // Close dropdown on outside click
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, []);

  const search = useCallback(async (q: string) => {
    if (q.trim().length < 1) { setResults([]); return; }
    setSearching(true);
    try {
      const r = await employeesApi.list({ search: q, pageSize: 8, status: 'Active' });
      setResults(r.items ?? []);
    } catch {
      setResults([]);
    } finally {
      setSearching(false);
    }
  }, []);

  // Debounce search
  useEffect(() => {
    const t = setTimeout(() => { if (open) search(query); }, 280);
    return () => clearTimeout(t);
  }, [query, open, search]);

  const select = (emp: EmployeeListItem) => {
    onChange({ intId: emp.id, publicId: emp.publicId, fullName: emp.fullName, employeeCode: emp.employeeCode, department: emp.department ?? '' });
    setQuery('');
    setResults([]);
    setOpen(false);
  };

  const clear = () => { onChange(null); setQuery(''); setResults([]); };

  if (value) {
    return (
      <div className="flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 dark:border-white/[0.1] dark:bg-white/[0.05]">
        <User className="h-4 w-4 shrink-0 text-sapphire" />
        <div className="flex-1 min-w-0">
          <p className="truncate text-sm font-semibold text-slate-900 dark:text-white">{value.fullName}</p>
          <p className="text-xs text-slate-400">{value.employeeCode}{value.department ? ` · ${value.department}` : ''}</p>
        </div>
        <button type="button" onClick={clear} className="rounded p-0.5 text-slate-400 hover:text-slate-600 dark:hover:text-slate-200" title="Clear selection">
          <X className="h-4 w-4" />
        </button>
      </div>
    );
  }

  return (
    <div ref={wrapRef} className="relative">
      <div className="flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 dark:border-white/[0.1] dark:bg-white/[0.05]">
        <Search className="h-4 w-4 shrink-0 text-slate-400" />
        <input
          type="text"
          value={query}
          onChange={(e) => { setQuery(e.target.value); setOpen(true); }}
          onFocus={() => setOpen(true)}
          placeholder={placeholder}
          required={required}
          className="flex-1 bg-transparent text-sm text-slate-900 outline-none placeholder:text-slate-400 dark:text-white"
        />
        {searching && <div className="h-4 w-4 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />}
      </div>

      {open && results.length > 0 && (
        <ul className="absolute z-50 mt-1 w-full rounded-lg border border-slate-200 bg-white py-1 shadow-lg dark:border-white/[0.1] dark:bg-slate-900">
          {results.map((emp) => (
            <li key={emp.id}>
              <button
                type="button"
                onClick={() => select(emp)}
                className="flex w-full items-center gap-3 px-3 py-2 text-left hover:bg-slate-50 dark:hover:bg-white/[0.05]"
              >
                <div className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-sapphire/10 text-xs font-bold text-sapphire">
                  {emp.fullName.charAt(0)}
                </div>
                <div className="flex-1 min-w-0">
                  <p className="truncate text-sm font-medium text-slate-900 dark:text-white">{emp.fullName}</p>
                  <p className="text-xs text-slate-400">{emp.employeeCode}{emp.department ? ` · ${emp.department}` : ''}</p>
                </div>
              </button>
            </li>
          ))}
        </ul>
      )}
      {open && query.trim().length > 0 && results.length === 0 && !searching && (
        <div className="absolute z-50 mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-3 text-sm text-slate-400 shadow-lg dark:border-white/[0.1] dark:bg-slate-900">
          No employees found for "{query}"
        </div>
      )}
    </div>
  );
}
