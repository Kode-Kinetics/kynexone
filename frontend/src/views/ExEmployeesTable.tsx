'use client';

import { useCallback, useEffect, useState } from 'react';
import { Archive, RefreshCw, Search } from 'lucide-react';
import { employeesApi } from '../api/employees';
import type { ExEmployeeListItem } from '../api/employees';
import { Avatar } from '../components/Avatar';
import { StatusChip } from '../components/StatusChip';

const PAGE_SIZE = 25;

/**
 * Read-only Ex-Employees archive. Lists former staff (terminated / archived / offboarded /
 * soft-deleted) with directory + lifecycle metadata only — no salary, bank, or identity fields,
 * and no mutating affordances. Records are retained for statutory audit (7 years).
 */
export function ExEmployeesTable() {
  const [rows, setRows] = useState<ExEmployeeListItem[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const r = await employeesApi.listExEmployees({ search, page, pageSize: PAGE_SIZE });
      setRows(r.items);
      setTotal(r.total);
    } catch {
      setError('Could not load the ex-employees archive.');
      setRows([]);
      setTotal(0);
    } finally {
      setLoading(false);
    }
  }, [search, page]);

  useEffect(() => {
    load();
  }, [load]);

  // Reset to the first page whenever the search term changes.
  useEffect(() => {
    setPage(1);
  }, [search]);

  const fmt = (d?: string) => (d ? new Date(d).toLocaleDateString() : '-');
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  return (
    <section className="space-y-4">
      <div className="flex items-center gap-2 rounded-lg bg-slate-50 px-3 py-2 text-sm text-slate-600 dark:bg-white/[0.03] dark:text-slate-300">
        <Archive className="h-4 w-4 shrink-0" />
        Read-only archive of former staff. Records are retained for statutory audit (7 years) and cannot be edited here.
      </div>

      <div className="flex flex-col gap-2 sm:flex-row">
        <div className="relative flex-1">
          <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
          <input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            className="input w-full pl-9"
            placeholder="Search former employee code, name, email"
          />
        </div>
        <button type="button" onClick={() => load()} className="btn-secondary">
          <RefreshCw className="h-4 w-4" />
          Refresh
        </button>
      </div>

      {error && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-600 dark:bg-red-500/10 dark:text-red-300">{error}</p>}

      <div className="surface overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full min-w-[820px] text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {['Former Employee', 'Department', 'Last Status', 'Exit Date', 'Retention Until'].map((head) => (
                  <th key={head} className="px-4 py-3 text-left text-xs font-bold uppercase text-slate-400">{head}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {loading && (
                <tr><td colSpan={5} className="py-16 text-center text-sm text-slate-400">Loading archive...</td></tr>
              )}
              {!loading && rows.length === 0 && (
                <tr><td colSpan={5} className="py-16 text-center text-sm text-slate-400">No ex-employees</td></tr>
              )}
              {!loading && rows.map((r) => (
                <tr key={r.id}>
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-3">
                      <Avatar name={r.fullName} size="sm" />
                      <div>
                        <p className="font-semibold text-slate-900 dark:text-white">{r.fullName}</p>
                        <p className="text-xs text-slate-400">{r.employeeCode}</p>
                      </div>
                    </div>
                  </td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{r.department || '-'}</td>
                  <td className="px-4 py-3">
                    <StatusChip label={r.lastStatus} tone="slate" />
                    {r.isDeleted && <span className="ml-1 text-xs text-slate-400">(removed)</span>}
                  </td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{fmt(r.exitDate)}</td>
                  <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{fmt(r.retentionUntilUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className="flex items-center justify-between border-t border-slate-100 px-4 py-3 text-xs text-slate-500 dark:border-white/[0.07]">
          <span>Page {page} of {totalPages} · {total} records</span>
          <div className="flex gap-1">
            <button
              type="button"
              onClick={() => setPage((p) => Math.max(1, p - 1))}
              disabled={page === 1}
              className="btn-secondary h-7 px-2 text-xs disabled:opacity-40"
            >
              Prev
            </button>
            <button
              type="button"
              onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
              disabled={page === totalPages}
              className="btn-secondary h-7 px-2 text-xs disabled:opacity-40"
            >
              Next
            </button>
          </div>
        </div>
      </div>
    </section>
  );
}
