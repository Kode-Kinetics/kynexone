'use client';

import { useCallback, useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
import { AlertTriangle, Building2, RefreshCw } from 'lucide-react';
import { groupApi, type GroupDashboard } from '@/src/api/group';
import { useCompany } from '@/src/contexts/CompanyContext';

/**
 * Group Overview: one card per accessible legal entity with live operational counts.
 * Data comes straight from /api/group/dashboard — scoped users see only their granted
 * companies (enforced server-side).
 */
export default function GroupDashboardPage() {
  const router = useRouter();
  const { setSelectedCompany, companyVersion } = useCompany();
  const [data, setData] = useState<GroupDashboard | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setData(await groupApi.dashboard());
    } catch (e: unknown) {
      const status = (e as { response?: { status?: number } })?.response?.status;
      setError(status === 403 ? 'You do not have access to the group overview.' : 'Could not load the group overview.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void load(); }, [load, companyVersion]);

  const drillDown = (companyId: string) => {
    setSelectedCompany(companyId);
    router.push('/dashboard');
  };

  if (loading) {
    return (
      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3" aria-busy="true">
        {[1, 2, 3, 4, 5, 6].map((i) => (
          <div key={i} className="h-52 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" />
        ))}
      </div>
    );
  }

  if (error) {
    return (
      <div className="flex flex-col items-center gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-8 text-center dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
        <AlertTriangle className="h-8 w-8 text-amber-500" />
        <p className="text-sm font-medium text-amber-800 dark:text-amber-300">{error}</p>
        <button type="button" onClick={() => void load()} className="flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700">
          <RefreshCw className="h-3.5 w-3.5" /> Retry
        </button>
      </div>
    );
  }

  const companies = data?.companies ?? [];
  if (companies.length === 0) {
    return (
      <div className="flex flex-col items-center gap-3 rounded-2xl border border-slate-200 bg-white p-10 text-center dark:border-white/[0.06] dark:bg-white/[0.03]">
        <Building2 className="h-10 w-10 text-slate-300 dark:text-slate-600" />
        <p className="text-sm font-medium text-slate-600 dark:text-slate-300">No companies to show yet.</p>
        <p className="text-xs text-slate-400">Companies appear here once they are created and you have access to them.</p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">Group Overview</h1>
          <p className="text-xs text-slate-400">{companies.length} legal entit{companies.length === 1 ? 'y' : 'ies'} in view</p>
        </div>
      </div>

      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        {companies.map((c) => (
          <div key={c.companyId} className="flex flex-col rounded-2xl border border-slate-200/80 bg-white p-4 shadow-sm dark:border-white/[0.06] dark:bg-white/[0.03]">
            <div className="mb-3 flex items-start justify-between gap-2">
              <div className="min-w-0">
                <h2 className="truncate text-sm font-bold text-slate-800 dark:text-slate-100">{c.name}</h2>
                <p className="text-xs text-slate-400">{c.code} · {c.countryCode}</p>
              </div>
              <div className="flex shrink-0 flex-col items-end gap-1">
                {!c.isActive && <span className="rounded-full bg-slate-200 px-2 py-0.5 text-[10px] font-semibold text-slate-600 dark:bg-white/[0.08] dark:text-slate-300">Suspended</span>}
                {c.approvalStatus !== 'Active' && <span className="rounded-full bg-amber-100 px-2 py-0.5 text-[10px] font-semibold text-amber-700 dark:bg-amber-500/[0.12] dark:text-amber-300">{c.approvalStatus}</span>}
                <span className={`rounded-full px-2 py-0.5 text-[10px] font-semibold ${c.complianceReady ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/[0.12] dark:text-emerald-300' : 'bg-rose-100 text-rose-700 dark:bg-rose-500/[0.12] dark:text-rose-300'}`}>
                  {c.complianceReady ? 'Compliance ready' : `${c.complianceMissingCount} compliance gap${c.complianceMissingCount === 1 ? '' : 's'}`}
                </span>
              </div>
            </div>

            <dl className="grid grid-cols-3 gap-2 text-center">
              <Metric label="Headcount" value={c.headcount} />
              <Metric label="Pending leave" value={c.pendingLeave} highlight={c.pendingLeave > 0} />
              <Metric label="Approvals" value={c.pendingApprovals} highlight={c.pendingApprovals > 0} />
              <Metric label="Absences 30d" value={c.absences30d} highlight={c.absences30d > 0} />
              <Metric label="Docs exp. 60d" value={c.expiringDocs60d} highlight={c.expiringDocs60d > 0} />
              <div className="rounded-lg bg-slate-50 p-2 dark:bg-white/[0.04]">
                <dt className="text-[10px] font-medium uppercase tracking-wide text-slate-400">Payroll</dt>
                <dd className="mt-0.5 text-xs font-bold text-slate-700 dark:text-slate-200">
                  {c.latestPayrollStatus ? `${c.latestPayrollStatus} · ${c.latestPayrollPeriod}` : '—'}
                </dd>
              </div>
            </dl>

            <button
              type="button"
              onClick={() => drillDown(c.companyId)}
              className="mt-3 rounded-lg border border-sapphire/30 px-3 py-1.5 text-xs font-semibold text-sapphire transition hover:bg-sapphire/[0.06] dark:text-cyanAccent"
            >
              Open company view
            </button>
          </div>
        ))}
      </div>
    </div>
  );
}

function Metric({ label, value, highlight = false }: { label: string; value: number; highlight?: boolean }) {
  return (
    <div className="rounded-lg bg-slate-50 p-2 dark:bg-white/[0.04]">
      <dt className="text-[10px] font-medium uppercase tracking-wide text-slate-400">{label}</dt>
      <dd className={`mt-0.5 text-sm font-bold ${highlight ? 'text-amber-600 dark:text-amber-400' : 'text-slate-700 dark:text-slate-200'}`}>{value}</dd>
    </div>
  );
}
