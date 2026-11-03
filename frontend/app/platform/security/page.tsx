'use client';

import { useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import { RefreshCw, Shield, AlertTriangle, CheckCircle } from 'lucide-react';
import { platformApi, type TenantSecurityPosture } from '@/src/api/platform';

const RISK_CLS: Record<string, { dot: string; text: string; row: string }> = {
  Low:    { dot: 'bg-emerald-500', text: 'text-emerald-400', row: '' },
  Medium: { dot: 'bg-amber-400',   text: 'text-amber-400',   row: 'bg-amber-950/10' },
  High:   { dot: 'bg-rose-500',    text: 'text-rose-400',    row: 'bg-rose-950/15 border-s-2 border-s-rose-700' },
};

function PlatformMfaDatePanel() {
  const [current, setCurrent] = useState<string | null>(null);
  const [date, setDate]       = useState('');
  const [reason, setReason]   = useState('');
  const [saving, setSaving]   = useState(false);
  const [msg, setMsg]         = useState<{ text: string; ok: boolean } | null>(null);

  const load = useCallback(async () => {
    try { setCurrent((await platformApi.getPlatformPrivilegedMfaEnforcement()).platformPrivilegedMfaEnforceFromUtc); }
    catch { /* panel stays on "not set" */ }
  }, []);
  useEffect(() => { load(); }, [load]);

  async function save() {
    if (!date) { setMsg({ text: 'Pick the date mandatory two-step should start.', ok: false }); return; }
    if (reason.trim().length < 10) { setMsg({ text: 'Give a reason of at least 10 characters.', ok: false }); return; }
    setSaving(true); setMsg(null);
    try {
      await platformApi.setPlatformPrivilegedMfaEnforcement({ enforceFromUtc: `${date}T00:00:00Z`, reason: reason.trim() });
      setDate(''); setReason(''); setMsg({ text: 'Saved.', ok: true }); await load();
    } catch (e: unknown) {
      const detail = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setMsg({ text: detail ?? 'Save failed. Only a platform Owner can change this date.', ok: false });
    } finally { setSaving(false); }
  }

  return (
    <div className="bg-slate-900 border border-white/[0.07] rounded-xl overflow-hidden" data-testid="platform-wide-privileged-mfa">
      <div className="px-5 py-3 border-b border-white/[0.06] flex items-center gap-2">
        <Shield className="h-3.5 w-3.5 text-slate-500" />
        <p className="text-caption font-semibold text-slate-500 uppercase tracking-widest">Mandatory two-step: platform-wide start date</p>
      </div>
      <div className="px-5 py-4 space-y-4">
        <p className="text-xs text-slate-500">
          Platform operators and every tenant's payroll, banking and access roles must use two-step sign-in from this date.
          Moving it out postpones the requirement for the whole app, up to 90 days ahead. People who already set it up keep it. Platform Owner only; recorded in the audit log.
        </p>
        <p className="text-sm text-slate-200">Starts: <span data-testid="platform-mfa-date">{current ? new Date(current).toLocaleDateString() : 'not set'}</span></p>
        <label htmlFor="platform-mfa-date-input" className="block space-y-1.5">
          <span className="block text-xs font-medium text-slate-400">New start date</span>
          <input id="platform-mfa-date-input" type="date" value={date} onChange={e => setDate(e.target.value)}
            className="bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60" />
        </label>
        <label htmlFor="platform-mfa-reason" className="block space-y-1.5">
          <span className="block text-xs font-medium text-amber-300">Reason (required, recorded in the audit log)</span>
          <textarea id="platform-mfa-reason" rows={2} maxLength={500} value={reason} onChange={e => setReason(e.target.value)}
            className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 resize-none" />
        </label>
        {msg && <p className={`text-sm ${msg.ok ? 'text-emerald-400' : 'text-rose-400'}`} role="status">{msg.text}</p>}
        <div className="flex justify-end">
          <button type="button" onClick={save} disabled={saving || !date}
            className="bg-sapphire hover:bg-blue-500 text-white px-6 py-2 rounded-lg text-sm font-semibold transition-colors disabled:opacity-40">
            {saving ? 'Saving…' : 'Save start date'}
          </button>
        </div>
      </div>
    </div>
  );
}

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
          <h1 className="text-lg font-semibold text-white">Security Center</h1>
          <p className="text-xs text-slate-500 mt-0.5">Cross-tenant security posture overview</p>
        </div>
        <button type="button" onClick={load} disabled={loading} aria-label="Refresh"
          className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
          <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
        </button>
      </div>

      <PlatformMfaDatePanel />

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
                        <th key={h} className="px-4 py-2.5 text-start text-caption font-semibold text-slate-600 uppercase tracking-widest">{h}</th>
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
                            <p className="text-caption text-slate-600 font-mono">/{p.tenantSlug}</p>
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
                          <td className="px-3 py-3 text-caption text-slate-600">
                            {p.lastSecurityEvent
                              ? new Date(p.lastSecurityEvent).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })
                              : '—'}
                          </td>
                          <td className="px-3 py-3">
                            <Link href={`/platform/tenants/${p.tenantId}/security`}
                              className="text-caption text-sapphire hover:text-blue-300 transition-colors">
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
