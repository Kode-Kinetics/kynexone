'use client';

import { useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { RefreshCw, Brain } from 'lucide-react';
import { platformApi, type PlatformTenantSummary, type TenantAiUsage } from '@/src/api/platform';

function UsageBar({ pct }: { pct: number }) {
  return (
    <div className="flex items-center gap-2">
      <div className="flex-1 h-1.5 bg-white/[0.06] rounded-full overflow-hidden">
        <div className={`h-full rounded-full ${pct >= 90 ? 'bg-rose-500' : pct >= 70 ? 'bg-amber-500' : 'bg-purple-500'}`}
          style={{ width: `${Math.min(pct, 100)}%` }} />
      </div>
      <span className="text-[11px] text-slate-500 tabular-nums w-8 text-end">{pct}%</span>
    </div>
  );
}

function currentYearMonth(): string {
  const now = new Date();
  const y = now.getFullYear();
  const m = String(now.getMonth() + 1).padStart(2, '0');
  return `${y}-${m}`;
}

/** Build the last N months as { value: "YYYY-MM", label: "Month YYYY" }[] descending */
function buildMonthOptions(count = 13): { value: string; label: string }[] {
  const now = new Date();
  const opts: { value: string; label: string }[] = [];
  for (let i = 0; i < count; i++) {
    const d = new Date(now.getFullYear(), now.getMonth() - i, 1);
    const value = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
    const label = d.toLocaleDateString('en-US', { month: 'long', year: 'numeric' });
    opts.push({ value, label });
  }
  return opts;
}

const MONTH_OPTIONS = buildMonthOptions();

function formatMonthLabel(ym: string): string {
  // ym is "YYYY-MM"
  const [year, month] = ym.split('-');
  const date = new Date(Number(year), Number(month) - 1, 1);
  return date.toLocaleDateString('en-US', { month: 'long', year: 'numeric' });
}

export default function AiUsagePage() {
  const router = useRouter();
  const [tenants, setTenants] = useState<PlatformTenantSummary[]>([]);
  const [usages, setUsages]   = useState<Record<string, TenantAiUsage>>({});
  const [loading, setLoading] = useState(true);
  const [yearMonth, setYearMonth] = useState(currentYearMonth);

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    load(yearMonth);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const load = useCallback(async (ym: string) => {
    setLoading(true);
    try {
      const ts = await platformApi.listTenants();
      setTenants(ts);
      // Load AI usage per tenant (parallelize)
      const active = ts.filter(t => t.subscription?.status === 'Active' || t.subscription?.status === 'Trial');
      const results = await Promise.allSettled(
        active.map(t => platformApi.getTenantAiUsage(t.id, ym).then(u => ({ id: t.id, usage: u })))
      );
      const map: Record<string, TenantAiUsage> = {};
      for (const r of results) {
        if (r.status === 'fulfilled') map[r.value.id] = r.value.usage;
      }
      setUsages(map);
    } finally { setLoading(false); }
  }, []);

  const handleMonthChange = (ym: string) => {
    setYearMonth(ym);
    load(ym);
  };

  const tenantsWithAi = tenants.filter(t => t.subscription?.status === 'Active' || t.subscription?.status === 'Trial');

  // Totals row
  const totals = tenantsWithAi.reduce(
    (acc, t) => {
      const u = usages[t.id];
      if (u) {
        acc.tokens   += u.tokensUsed;
        acc.requests += u.requestCount;
        acc.blocked  += u.blockedCount;
      }
      return acc;
    },
    { tokens: 0, requests: 0, blocked: 0 }
  );

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between flex-wrap gap-3">
        <div>
          <h1 className="text-lg font-bold text-white">
            AI Usage &amp; Cost — {formatMonthLabel(yearMonth)}
          </h1>
          <p className="text-xs text-slate-500 mt-0.5">Token consumption per tenant for the selected month</p>
        </div>
        <div className="flex items-center gap-2">
          <select
            value={yearMonth}
            onChange={e => handleMonthChange(e.target.value)}
            aria-label="Select month"
            title="Select month"
            className="h-8 bg-[#161b22] border border-white/[0.08] rounded-lg px-2.5 text-xs text-slate-300 focus:outline-none focus:border-sapphire/60 transition-colors"
          >
            {MONTH_OPTIONS.map(o => (
              <option key={o.value} value={o.value}>{o.label}</option>
            ))}
          </select>
          <button type="button" onClick={() => load(yearMonth)} disabled={loading} title="Refresh"
            className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
          </button>
        </div>
      </div>

      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
        {loading ? (
          <div className="flex items-center justify-center py-16">
            <div className="h-5 w-5 animate-spin rounded-full border-2 border-purple-500 border-t-transparent" />
          </div>
        ) : tenantsWithAi.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-16 gap-3">
            <Brain className="h-8 w-8 text-slate-700" />
            <p className="text-sm text-slate-600">No active tenants with AI enabled.</p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[600px]">
              <thead>
                <tr className="border-b border-white/[0.06]">
                  {['Tenant', 'Plan', 'Tokens Used', 'Requests', 'Blocked', 'Usage'].map(h => (
                    <th key={h} className="px-4 py-2.5 text-start text-[10px] font-semibold text-slate-600 uppercase tracking-widest">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {tenantsWithAi.map(t => {
                  const u = usages[t.id];
                  return (
                    <tr key={t.id} className="border-b border-white/[0.04] hover:bg-white/[0.02] transition-colors">
                      <td className="px-4 py-3">
                        <p className="text-sm text-white font-medium">{t.name}</p>
                        <p className="text-[11px] text-slate-600 font-mono">/{t.slug}</p>
                      </td>
                      <td className="px-3 py-3 text-xs text-slate-400">{t.subscription?.plan}</td>
                      <td className="px-3 py-3 text-xs text-slate-300 tabular-nums">
                        {u ? u.tokensUsed.toLocaleString() : <span className="text-slate-700">—</span>}
                      </td>
                      <td className="px-3 py-3 text-xs text-slate-400 tabular-nums">
                        {u ? u.requestCount.toLocaleString() : '—'}
                      </td>
                      <td className="px-3 py-3 text-xs text-slate-500 tabular-nums">
                        {u ? u.blockedCount.toLocaleString() : '—'}
                      </td>
                      <td className="px-3 py-3 w-36">
                        {u ? (
                          u.isUnlimited
                            ? <span className="text-[11px] text-slate-600">Unlimited</span>
                            : <UsageBar pct={u.usagePct} />
                        ) : <span className="text-[11px] text-slate-700">—</span>}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
              {/* Totals row */}
              {tenantsWithAi.length > 0 && (
                <tfoot>
                  <tr className="border-t-2 border-white/[0.08] bg-white/[0.02]">
                    <td colSpan={2} className="px-4 py-3 text-xs font-semibold text-slate-400 uppercase tracking-wider">
                      Total ({tenantsWithAi.length} tenants)
                    </td>
                    <td className="px-3 py-3 text-xs font-semibold text-slate-200 tabular-nums">
                      {totals.tokens.toLocaleString()}
                    </td>
                    <td className="px-3 py-3 text-xs font-semibold text-slate-300 tabular-nums">
                      {totals.requests.toLocaleString()}
                    </td>
                    <td className="px-3 py-3 text-xs font-semibold text-slate-400 tabular-nums">
                      {totals.blocked.toLocaleString()}
                    </td>
                    <td className="px-3 py-3" />
                  </tr>
                </tfoot>
              )}
            </table>
          </div>
        )}
      </div>
    </div>
  );
}
