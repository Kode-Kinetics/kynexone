'use client';

import { useState, useEffect, useCallback, useRef } from 'react';
import { useRouter } from 'next/navigation';
import { RefreshCw, X } from 'lucide-react';
import { platformApi, type PlatformAuditLog } from '@/src/api/platform';

function FilterBar({
  filters,
  onChange,
  onClear,
}: {
  filters: { action: string; entityType: string; from: string; to: string };
  onChange: (k: keyof typeof filters, v: string) => void;
  onClear: () => void;
}) {
  const hasFilters = Object.values(filters).some(Boolean);
  const inputCls =
    'h-8 bg-white/[0.04] border border-white/[0.08] rounded-lg px-2.5 text-xs text-slate-300 placeholder-slate-600 focus:outline-none focus:border-sapphire/60 focus:bg-white/[0.06] transition-colors w-full';

  return (
    <div className="flex flex-wrap items-center gap-2">
      <input
        type="text"
        value={filters.action}
        onChange={e => onChange('action', e.target.value)}
        placeholder="Filter by action (e.g. employee.updated)"
        className={`${inputCls} min-w-[220px] flex-1`}
      />
      <input
        type="text"
        value={filters.entityType}
        onChange={e => onChange('entityType', e.target.value)}
        placeholder="Entity type (e.g. Employee)"
        className={`${inputCls} min-w-[160px] flex-1`}
      />
      <div className="flex items-center gap-1.5 flex-shrink-0">
        <span className="text-[10px] text-slate-600 uppercase tracking-wider">From</span>
        <input
          type="date"
          value={filters.from}
          onChange={e => onChange('from', e.target.value)}
          aria-label="From date"
          title="From date"
          className="h-8 bg-white/[0.04] border border-white/[0.08] rounded-lg px-2 text-xs text-slate-300 focus:outline-none focus:border-sapphire/60 transition-colors [color-scheme:dark]"
        />
      </div>
      <div className="flex items-center gap-1.5 flex-shrink-0">
        <span className="text-[10px] text-slate-600 uppercase tracking-wider">To</span>
        <input
          type="date"
          value={filters.to}
          onChange={e => onChange('to', e.target.value)}
          aria-label="To date"
          title="To date"
          className="h-8 bg-white/[0.04] border border-white/[0.08] rounded-lg px-2 text-xs text-slate-300 focus:outline-none focus:border-sapphire/60 transition-colors [color-scheme:dark]"
        />
      </div>
      {hasFilters && (
        <button
          type="button"
          onClick={onClear}
          className="flex items-center gap-1 text-xs text-slate-500 hover:text-rose-400 transition-colors flex-shrink-0"
        >
          <X className="h-3 w-3" />
          Clear filters
        </button>
      )}
    </div>
  );
}

const EMPTY_FILTERS = { action: '', entityType: '', from: '', to: '' };

export default function AuditLogsPage() {
  const router = useRouter();
  const [logs, setLogs]     = useState<PlatformAuditLog[]>([]);
  const [total, setTotal]   = useState(0);
  const [page, setPage]     = useState(1);
  const [loading, setLoading] = useState(true);
  const [filters, setFilters] = useState(EMPTY_FILTERS);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const PAGE_SIZE = 50;

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    load(1, filters);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const load = useCallback(async (p: number, f: typeof EMPTY_FILTERS) => {
    setLoading(true);
    try {
      const activeFilters = {
        ...(f.action     ? { action:     f.action }     : {}),
        ...(f.entityType ? { entityType: f.entityType } : {}),
        ...(f.from       ? { from: new Date(f.from).toISOString() } : {}),
        ...(f.to         ? { to:   new Date(f.to + 'T23:59:59').toISOString() } : {}),
      };
      const r = await platformApi.getAuditLogs(undefined, p, PAGE_SIZE, activeFilters);
      setLogs(r.logs);
      setTotal(r.total);
    } finally { setLoading(false); }
  }, []);

  // Debounced re-fetch when filters change (400 ms)
  const handleFilterChange = (k: keyof typeof EMPTY_FILTERS, v: string) => {
    const next = { ...filters, [k]: v };
    setFilters(next);
    setPage(1);
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => load(1, next), 400);
  };

  const handleClear = () => {
    setFilters(EMPTY_FILTERS);
    setPage(1);
    if (debounceRef.current) clearTimeout(debounceRef.current);
    load(1, EMPTY_FILTERS);
  };

  const handlePageChange = (p: number) => {
    setPage(p);
    load(p, filters);
  };

  const totalPages = Math.ceil(total / PAGE_SIZE);

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-white">Platform Audit Log</h1>
          <p className="text-xs text-slate-500 mt-0.5">All platform-level events across all tenants</p>
        </div>
        <button type="button" onClick={() => load(page, filters)} disabled={loading} title="Refresh" aria-label="Refresh"
          className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
          <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
        </button>
      </div>

      {/* Filter bar */}
      <FilterBar filters={filters} onChange={handleFilterChange} onClear={handleClear} />

      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
        {loading ? (
          <div className="flex items-center justify-center py-16">
            <div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
          </div>
        ) : logs.length === 0 ? (
          <p className="text-sm text-slate-600 text-center py-16">No audit events found.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[700px]">
              <thead>
                <tr className="border-b border-white/[0.06]">
                  {['Time', 'Action', 'Entity', 'Tenant', 'Performed By', 'IP'].map(h => (
                    <th key={h} className="px-4 py-2.5 text-left text-[10px] font-semibold text-slate-600 uppercase tracking-widest">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {logs.map(log => (
                  <tr key={log.id} className="border-b border-white/[0.04] last:border-0 hover:bg-white/[0.02] transition-colors">
                    <td className="px-4 py-2.5 text-[11px] text-slate-600 tabular-nums whitespace-nowrap">
                      {new Date(log.createdAtUtc).toLocaleString('en-GB')}
                    </td>
                    <td className="px-3 py-2.5">
                      <span className="text-xs font-medium text-slate-300">{log.action}</span>
                    </td>
                    <td className="px-3 py-2.5 text-[11px] text-slate-500">
                      {log.entityType}{log.entityId ? ` #${log.entityId.slice(0, 8)}` : ''}
                    </td>
                    <td className="px-3 py-2.5 text-[11px] text-slate-600 font-mono">
                      {log.tenantName ?? (log.tenantId ? log.tenantId.slice(0, 8) : 'Platform')}
                    </td>
                    <td className="px-3 py-2.5 text-xs text-slate-400">{log.performedByName}</td>
                    <td className="px-3 py-2.5 text-[11px] text-slate-600 font-mono">{log.ipAddress || '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {totalPages > 1 && (
        <div className="flex items-center justify-between text-xs text-slate-500">
          <span>{total} total events</span>
          <div className="flex items-center gap-2">
            <button type="button" onClick={() => handlePageChange(Math.max(1, page - 1))} disabled={page === 1 || loading}
              className="px-3 py-1.5 border border-white/10 rounded-lg hover:border-white/20 disabled:opacity-40 transition-colors">
              Prev
            </button>
            <span className="px-2">{page} / {totalPages}</span>
            <button type="button" onClick={() => handlePageChange(Math.min(totalPages, page + 1))} disabled={page === totalPages || loading}
              className="px-3 py-1.5 border border-white/10 rounded-lg hover:border-white/20 disabled:opacity-40 transition-colors">
              Next
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
