'use client';

import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, HeartPulse, RefreshCw } from 'lucide-react';
import { benefitsApi, type EssBenefits } from '@/src/api/benefits';

const money = (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** Employee self-service: the caller's own benefit enrolments. Read-only. */
export function MyBenefitsPage() {
  const [data, setData] = useState<EssBenefits | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setData(await benefitsApi.mine());
    } catch (e) {
      const res = (e as { response?: { status?: number; data?: { message?: string } } })?.response;
      setError(res?.data?.message ?? (res?.status === 403 ? 'Self-service access is not enabled for your account.' : 'Could not load your benefits.'));
      setData(null);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-lg font-bold text-slate-800 dark:text-slate-100">My Benefits</h1>
        <p className="text-xs text-slate-500 dark:text-slate-400">The benefit plans you are enrolled in and what each one costs you</p>
      </div>

      {loading ? (
        <div className="grid gap-3 sm:grid-cols-2" aria-busy="true" aria-label="Loading your benefits">
          {[0, 1].map((i) => <div key={i} className="h-36 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" />)}
        </div>
      ) : error ? (
        <div role="alert" className="flex flex-col items-center gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-8 text-center dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
          <AlertTriangle className="h-8 w-8 text-amber-500" />
          <p className="max-w-lg text-sm font-medium text-amber-800 dark:text-amber-300">{error}</p>
          <button type="button" onClick={() => void load()} className="flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700">
            <RefreshCw className="h-3.5 w-3.5" /> Retry
          </button>
        </div>
      ) : !data || data.enrollments.length === 0 ? (
        <div data-testid="my-benefits-empty" className="flex flex-col items-center gap-3 rounded-2xl border border-slate-200 bg-white p-10 text-center dark:border-white/[0.06] dark:bg-white/[0.03]">
          <HeartPulse className="h-10 w-10 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-semibold text-slate-700 dark:text-slate-200">You are not enrolled in any benefit plans</p>
          <p className="max-w-md text-xs text-slate-500 dark:text-slate-400">When HR enrols you in a plan (medical, dental, life and so on) it appears here with your share of the cost. Questions? Raise a request with HR.</p>
        </div>
      ) : (
        <div className="grid gap-3 sm:grid-cols-2" data-testid="my-benefits-list">
          {data.enrollments.map((e) => {
            const active = e.status === 'Active';
            return (
              <article key={e.id} className="rounded-2xl border border-slate-200 bg-white p-4 dark:border-white/[0.06] dark:bg-white/[0.03]">
                <div className="flex items-start justify-between gap-2">
                  <div>
                    <h2 className="text-sm font-bold text-slate-800 dark:text-slate-100">{e.planName}</h2>
                    <p className="text-[11px] text-slate-500 dark:text-slate-400">{e.planType} · {e.coverageTier}</p>
                  </div>
                  <span className={`rounded-full px-2 py-0.5 text-[10px] font-semibold ${active
                    ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/[0.12] dark:text-emerald-300'
                    : 'bg-slate-200 text-slate-600 dark:bg-white/[0.08] dark:text-slate-300'}`}>{e.status}</span>
                </div>
                <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">Covered {e.effectiveFrom} → {e.effectiveTo ?? 'ongoing'}</p>
                {e.currentEmployeeAmount !== null ? (
                  <dl className="mt-3 grid grid-cols-2 gap-2 rounded-xl bg-slate-50 p-3 text-xs dark:bg-white/[0.03]">
                    <div><dt className="text-slate-500 dark:text-slate-400">You pay ({e.contributionFrequency?.toLowerCase()})</dt><dd className="font-mono text-sm font-bold text-slate-800 dark:text-slate-100">{money(e.currentEmployeeAmount)} {e.currency}</dd></div>
                    <div><dt className="text-slate-500 dark:text-slate-400">Employer pays</dt><dd className="font-mono text-sm font-bold text-slate-800 dark:text-slate-100">{money(e.currentEmployerAmount ?? 0)} {e.currency}</dd></div>
                  </dl>
                ) : (
                  <p className="mt-3 rounded-xl bg-slate-50 p-3 text-xs text-slate-500 dark:bg-white/[0.03] dark:text-slate-400">No contribution is currently recorded for this plan.</p>
                )}
              </article>
            );
          })}
        </div>
      )}
    </div>
  );
}
