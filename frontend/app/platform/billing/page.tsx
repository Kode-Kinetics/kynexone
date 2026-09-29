'use client';

import { useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import { RefreshCw, TrendingUp, CreditCard, AlertTriangle, Plus } from 'lucide-react';
import { platformApi, type BillingSummary, type TenantInvoice, type PlatformTenantSummary } from '@/src/api/platform';

type AllInvoice = TenantInvoice & { tenantName: string; tenantSlug: string };

const STATUS_CLS: Record<string, string> = {
  Draft:     'text-slate-400 bg-slate-700/50 border-slate-600',
  Sent:      'text-blue-400 bg-blue-900/30 border-blue-700/30',
  Paid:      'text-emerald-400 bg-emerald-900/30 border-emerald-700/30',
  Overdue:   'text-rose-400 bg-rose-900/30 border-rose-700/30',
  Cancelled: 'text-slate-600 bg-transparent border-slate-800',
};

function MetricCard({ label, value, sub, accent = 'default', icon: Icon }: {
  label: string; value: string; sub?: string;
  accent?: 'default' | 'green' | 'rose' | 'amber';
  icon?: React.ElementType;
}) {
  const bar  = { default: 'bg-slate-700', green: 'bg-emerald-500', rose: 'bg-rose-500', amber: 'bg-amber-500' };
  const val  = { default: 'text-white', green: 'text-emerald-300', rose: 'text-rose-300', amber: 'text-amber-300' };
  const icn  = { default: 'text-slate-600', green: 'text-emerald-500', rose: 'text-rose-500', amber: 'text-amber-500' };
  return (
    <div className="relative bg-[#161b22] border border-white/[0.07] rounded-xl p-4 overflow-hidden">
      <div className={`absolute start-0 top-3 bottom-3 w-0.5 rounded-e ${bar[accent]}`} />
      <div className="ps-2">
        <div className="flex items-start justify-between gap-2 mb-2">
          <p className="text-[11px] font-semibold text-slate-500 uppercase tracking-widest">{label}</p>
          {Icon && <Icon className={`h-3.5 w-3.5 shrink-0 mt-0.5 ${icn[accent]}`} />}
        </div>
        <p className={`text-2xl font-bold leading-none ${val[accent]}`}>{value}</p>
        {sub && <p className="text-[11px] text-slate-600 mt-1.5">{sub}</p>}
      </div>
    </div>
  );
}

export default function PlatformBillingPage() {
  const router = useRouter();
  const [summary, setSummary]   = useState<BillingSummary | null>(null);
  const [invoices, setInvoices] = useState<AllInvoice[]>([]);
  const [total, setTotal]       = useState(0);
  const [page, setPage]         = useState(1);
  const [statusFilter, setStatus] = useState('');
  const [loading, setLoading]   = useState(true);
  const [summaryErr, setSummaryErr] = useState('');
  const [tenants, setTenants]   = useState<PlatformTenantSummary[]>([]);
  const [pickTenant, setPickTenant] = useState(false);
  const PAGE_SIZE = 30;

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    load();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [page, statusFilter]);

  const load = useCallback(async () => {
    setLoading(true);
    const [sum, inv, ten] = await Promise.allSettled([
      platformApi.getBillingSummary(),
      platformApi.listAllInvoices({ status: statusFilter || undefined, page, pageSize: PAGE_SIZE }),
      platformApi.listTenants(),
    ]);
    if (sum.status === 'fulfilled') setSummary(sum.value);
    else setSummaryErr('Could not load billing summary. Check your connection and try again.');
    if (inv.status === 'fulfilled') { setInvoices(inv.value.invoices); setTotal(inv.value.total); }
    if (ten.status === 'fulfilled') setTenants(ten.value);
    setLoading(false);
  }, [page, statusFilter]);

  const totalPages = Math.ceil(total / PAGE_SIZE);

  return (
    <div className="space-y-5">
      {/* Tenant picker overlay */}
      {pickTenant && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
          <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={() => setPickTenant(false)} />
          <div className="relative w-full max-w-sm bg-[#0d1117] border border-white/10 rounded-2xl shadow-2xl overflow-hidden">
            <div className="flex items-center justify-between px-5 py-4 border-b border-white/[0.07]">
              <h2 className="text-sm font-semibold text-white">Select Tenant</h2>
              <button type="button" onClick={() => setPickTenant(false)} className="text-slate-500 hover:text-white transition-colors text-xl leading-none">&times;</button>
            </div>
            <div className="max-h-80 overflow-y-auto py-2">
              {tenants.length === 0 ? (
                <p className="px-5 py-4 text-xs text-slate-500">No tenants found.</p>
              ) : tenants.map(t => (
                <Link
                  key={t.id}
                  href={`/platform/tenants/${t.id}/billing`}
                  onClick={() => setPickTenant(false)}
                  className="flex items-center gap-3 px-5 py-3 hover:bg-white/[0.04] transition-colors"
                >
                  <div className="h-8 w-8 rounded-lg bg-slate-800 border border-white/10 flex items-center justify-center shrink-0">
                    <span className="text-[10px] font-bold text-slate-400">{t.name.slice(0, 2).toUpperCase()}</span>
                  </div>
                  <div className="min-w-0">
                    <p className="text-sm text-white font-medium truncate">{t.name}</p>
                    <p className="text-[11px] text-slate-600 font-mono">/{t.slug} · {t.subscription?.plan ?? 'No plan'}</p>
                  </div>
                </Link>
              ))}
            </div>
          </div>
        </div>
      )}

      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-white">Billing & Revenue</h1>
          <p className="text-xs text-slate-500 mt-0.5">Aggregate billing across all tenants</p>
        </div>
        <div className="flex items-center gap-2">
          <button type="button" onClick={() => setPickTenant(true)}
            className="flex items-center gap-1.5 bg-blue-600 hover:bg-blue-500 text-white px-3 py-1.5 rounded-lg text-sm font-semibold transition-colors">
            <Plus className="h-3.5 w-3.5" /> New Invoice
          </button>
          <button type="button" onClick={load} disabled={loading} aria-label="Refresh"
            className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
          </button>
        </div>
      </div>

      {/* Summary cards */}
      {summaryErr ? (
        <div className="px-4 py-3 bg-amber-500/5 border border-amber-500/20 rounded-xl text-xs text-amber-400">{summaryErr}</div>
      ) : (
        <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
          <MetricCard label="Total MRR" value={summary ? `$${(summary.totalMrr ?? 0).toLocaleString()}` : '—'} sub="Monthly recurring" accent="green" icon={TrendingUp} />
          <MetricCard label="Total ARR" value={summary ? `$${(summary.totalArr ?? 0).toLocaleString()}` : '—'} sub="Annual run rate" accent="green" icon={TrendingUp} />
          <MetricCard label="Overdue" value={summary ? `$${(summary.overdueTotal ?? 0).toLocaleString()}` : '—'} sub={summary ? `${summary.overdueCount ?? 0} invoices` : undefined} accent={summary && (summary.overdueCount ?? 0) > 0 ? 'rose' : 'default'} icon={AlertTriangle} />
          <MetricCard label="Paid This Month" value={summary ? `$${(summary.paidThisMonth ?? 0).toLocaleString()}` : '—'} sub={summary ? `${summary.sentThisMonth ?? 0} sent` : undefined} accent="default" icon={CreditCard} />
        </div>
      )}

      {/* Invoice table */}
      <div>
        <div className="flex items-center justify-between mb-3">
          <p className="text-[11px] font-semibold text-slate-600 uppercase tracking-widest">All Invoices</p>
          <select aria-label="Filter by status" value={statusFilter} onChange={e => { setStatus(e.target.value); setPage(1); }}
            className="bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-1.5 text-sm text-slate-300 focus:outline-none focus:border-sapphire/60">
            <option value="">All statuses</option>
            {['Draft', 'Sent', 'Paid', 'Overdue', 'Cancelled'].map(s => <option key={s} value={s}>{s}</option>)}
          </select>
        </div>

        <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
          {loading ? (
            <div className="flex items-center justify-center py-10">
              <div className="h-4 w-4 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
            </div>
          ) : invoices.length === 0 ? (
            <p className="text-sm text-slate-600 text-center py-10">
              {total === 0 && !statusFilter ? 'No invoices across any tenants yet.' : 'No invoices match this filter.'}
            </p>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full min-w-[750px]">
                <thead>
                  <tr className="border-b border-white/[0.06]">
                    {['Tenant', 'Invoice', 'Amount', 'Status', 'Date', 'Due'].map(h => (
                      <th key={h} className="px-4 py-2.5 text-start text-[10px] font-semibold text-slate-600 uppercase tracking-widest">{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {invoices.map(inv => (
                    <tr key={inv.id} className="border-b border-white/[0.04] last:border-0 hover:bg-white/[0.02] transition-colors">
                      <td className="px-4 py-3">
                        <Link href={`/platform/tenants/${inv.tenantId}/billing`} className="text-sm text-white hover:text-blue-300 font-medium">
                          {inv.tenantName}
                        </Link>
                        <p className="text-[11px] text-slate-600 font-mono">/{inv.tenantSlug}</p>
                      </td>
                      <td className="px-3 py-3 text-xs text-slate-400 font-mono">{inv.invoiceNumber}</td>
                      <td className="px-3 py-3 text-xs text-slate-300 tabular-nums">
                        {inv.currencyCode} {(inv.amount ?? 0).toLocaleString('en-US', { minimumFractionDigits: 2 })}
                      </td>
                      <td className="px-3 py-3">
                        <span className={`text-[10px] font-semibold uppercase px-1.5 py-0.5 rounded border ${STATUS_CLS[inv.status] ?? ''}`}>
                          {inv.status}
                        </span>
                      </td>
                      <td className="px-3 py-3 text-[11px] text-slate-500">
                        {new Date(inv.invoiceDate).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })}
                      </td>
                      <td className="px-3 py-3 text-[11px] text-slate-500">
                        {new Date(inv.dueDate).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>

        {totalPages > 1 && (
          <div className="flex items-center justify-between text-xs text-slate-500 mt-3">
            <span>{total} total invoices</span>
            <div className="flex items-center gap-2">
              <button type="button" onClick={() => setPage(p => Math.max(1, p - 1))} disabled={page === 1 || loading}
                className="px-3 py-1.5 border border-white/10 rounded-lg hover:border-white/20 disabled:opacity-40 transition-colors">Prev</button>
              <span className="px-2">{page} / {totalPages}</span>
              <button type="button" onClick={() => setPage(p => Math.min(totalPages, p + 1))} disabled={page === totalPages || loading}
                className="px-3 py-1.5 border border-white/10 rounded-lg hover:border-white/20 disabled:opacity-40 transition-colors">Next</button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
