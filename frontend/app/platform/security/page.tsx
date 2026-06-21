'use client';

import { useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import { RefreshCw, Shield, AlertTriangle, CheckCircle } from 'lucide-react';
import { platformApi, type TenantSecurityPosture } from '@/src/api/platform';

const RISK_CLS: Record<string, { dot: string; text: string; row: string }> = {
  Low:    { dot: 'bg-emerald-500', text: 'text-emerald-400', row: '' },
  Medium: { dot: 'bg-amber-400',   text: 'text-amber-400',   row: 'bg-amber-950/10' },
  High:   { dot: 'bg-rose-500',    text: 'text-rose-400',    row: 'bg-rose-950/15 border-l-2 border-l-rose-700' },
};

export default function SecurityCenterPage() {
  const router = useRouter();
  const [postures, setPostures] = useState<TenantSecurityPosture[]>([]);
  const [loading, setLoading]   = useState(true);
  const [err, setErr]           = useState('');

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    load();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const load = useCallback(async () => {
    setLoading(true); setErr('');
    try { setPostures(await platformApi.getSecuritySummary()); }
    catch { setErr('Could not load security summary. Check your connection and try again.'); }
    finally { setLoading(false); }
  }, []);

  const highRisk   = postures.filter(p => p.riskLevel === 'High').length;
  const noMfa      = postures.filter(p => !p.hasMfaEnabled).length;
  const noPolicy   = postures.filter(p => !p.hasSecurityPolicy).length;

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-white">Security Center</h1>
          <p className="text-xs text-slate-500 mt-0.5">Cross-tenant security posture overview</p>
        </div>
        <button type="button" onClick={load} disabled={loading} aria-label="Refresh"
          className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
          <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
        </button>
      </div>

      {err ? (
        <div className="px-5 py-4 bg-amber-500/5 border border-amber-500/20 rounded-xl">
          <p className="text-sm text-amber-400">{err}</p>
          <p className="text-xs text-slate-600 mt-1">Check your connection or try refreshing.</p>
        </div>
      ) : (
        <>
          {/* Summary chips */}
          {!loading && (
            <div className="flex flex-wrap gap-3">
              <div className={`flex items-center gap-2 px-3 py-2 rounded-lg border text-sm ${highRisk > 0 ? 'text-rose-400 bg-rose-500/5 border-rose-500/20' : 'text-emerald-400 bg-emerald-500/5 border-emerald-500/20'}`}>
                {highRisk > 0 ? <AlertTriangle className="h-4 w-4" /> : <CheckCircle className="h-4 w-4" />}
                {highRisk > 0 ? `${highRisk} high-risk tenant${highRisk !== 1 ? 's' : ''}` : 'No high-risk tenants'}
              </div>
              {noMfa > 0 && (
                <div className="flex items-center gap-2 px-3 py-2 rounded-lg border text-sm text-amber-400 bg-amber-500/5 border-amber-500/20">
                  <Shield className="h-4 w-4" />
                  {noMfa} tenant{noMfa !== 1 ? 's' : ''} without MFA
                </div>
              )}
              {noPolicy > 0 && (
                <div className="flex items-center gap-2 px-3 py-2 rounded-lg border text-sm text-amber-400 bg-amber-500/5 border-amber-500/20">
                  <AlertTriangle className="h-4 w-4" />
                  {noPolicy} without security policy
                </div>
              )}
            </div>
          )}

          <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
            {loading ? (
              <div className="flex items-center justify-center py-10">
                <div className="h-4 w-4 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
              </div>
            ) : postures.length === 0 ? (
              <p className="text-sm text-slate-600 text-center py-10">No security data available.</p>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full min-w-[650px]">
                  <thead>
                    <tr className="border-b border-white/[0.06]">
                      {['Tenant', 'MFA', 'Security Policy', 'Risk Level', 'Last Event', ''].map(h => (
                        <th key={h} className="px-4 py-2.5 text-left text-[10px] font-semibold text-slate-600 uppercase tracking-widest">{h}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {postures.sort((a, b) => {
                      const order = { High: 0, Medium: 1, Low: 2 };
                      return (order[a.riskLevel] ?? 3) - (order[b.riskLevel] ?? 3);
                    }).map(p => {
                      const risk = RISK_CLS[p.riskLevel] ?? RISK_CLS.Low;
                      return (
                        <tr key={p.tenantId} className={`border-b border-white/[0.04] last:border-0 hover:bg-white/[0.02] transition-colors ${risk.row}`}>
                          <td className="px-4 py-3">
                            <p className="text-sm text-white font-medium">{p.tenantName}</p>
                            <p className="text-[11px] text-slate-600 font-mono">/{p.tenantSlug}</p>
                          </td>
                          <td className="px-3 py-3">
                            {p.hasMfaEnabled
                              ? <span className="text-xs text-emerald-400">✓ Enabled</span>
                              : <span className="text-xs text-rose-400">✗ Disabled</span>}
                          </td>
                          <td className="px-3 py-3">
                            {p.hasSecurityPolicy
                              ? <span className="text-xs text-emerald-400">✓ Set</span>
                              : <span className="text-xs text-slate-500">—</span>}
                          </td>
                          <td className="px-3 py-3">
                            <div className="flex items-center gap-1.5">
                              <span className={`h-1.5 w-1.5 rounded-full ${risk.dot}`} />
                              <span className={`text-xs font-medium ${risk.text}`}>{p.riskLevel}</span>
                            </div>
                          </td>
                          <td className="px-3 py-3 text-[11px] text-slate-600">
                            {p.lastSecurityEvent
                              ? new Date(p.lastSecurityEvent).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })
                              : '—'}
                          </td>
                          <td className="px-3 py-3">
                            <Link href={`/platform/tenants/${p.tenantId}/security`}
                              className="text-[11px] text-sapphire hover:text-blue-300 transition-colors">
                              Policy →
                            </Link>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </>
      )}
    </div>
  );
}
