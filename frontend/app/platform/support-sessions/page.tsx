'use client';

import { useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { RefreshCw, X, StopCircle } from 'lucide-react';
import { platformApi, type SupportSession } from '@/src/api/platform';

export default function SupportSessionsPage() {
  const router = useRouter();
  const [sessions, setSessions] = useState<SupportSession[]>([]);
  const [total, setTotal]       = useState(0);
  const [loading, setLoading]   = useState(true);
  const [activeOnly, setActiveOnly] = useState(false);
  const [ending, setEnding]     = useState<string | null>(null);
  const [msg, setMsg]           = useState<{ text: string; ok: boolean } | null>(null);

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    load();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeOnly]);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const r = await platformApi.listSupportSessions(undefined, activeOnly, 1, 100);
      setSessions(r.sessions); setTotal(r.total);
    } finally { setLoading(false); }
  }, [activeOnly]);

  async function endSession(sessionId: string) {
    setEnding(sessionId);
    try {
      await platformApi.endSupportAccess(sessionId);
      setMsg({ text: 'Session ended.', ok: true });
      await load();
    } catch { setMsg({ text: 'Failed to end session.', ok: false }); }
    finally { setEnding(null); }
  }

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-white">Support Sessions</h1>
          <p className="text-xs text-slate-500 mt-0.5">{total} session{total !== 1 ? 's' : ''} · All support access is logged and audited</p>
        </div>
        <div className="flex items-center gap-2">
          <label className="flex items-center gap-2 text-xs text-slate-400 cursor-pointer">
            <input type="checkbox" checked={activeOnly} onChange={e => setActiveOnly(e.target.checked)}
              className="h-3.5 w-3.5 rounded accent-sapphire" />
            Active only
          </label>
          <button type="button" onClick={load} disabled={loading} title="Refresh" aria-label="Refresh"
            className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
          </button>
        </div>
      </div>

      {msg && (
        <div className={`flex items-center justify-between px-4 py-2.5 rounded-lg border text-sm ${msg.ok ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-400' : 'bg-rose-500/10 border-rose-500/20 text-rose-400'}`}>
          {msg.text}
          <button type="button" onClick={() => setMsg(null)}><X className="h-3.5 w-3.5" /></button>
        </div>
      )}

      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
        {loading ? (
          <div className="flex items-center justify-center py-10">
            <div className="h-4 w-4 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
          </div>
        ) : sessions.length === 0 ? (
          <p className="text-sm text-slate-600 text-center py-10">No support sessions found.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[800px]">
              <thead>
                <tr className="border-b border-white/[0.06]">
                  {['Target User', 'Reason', 'Started By', 'Started', 'Expires', 'Status', ''].map(h => (
                    <th key={h} className="px-4 py-2.5 text-left text-[10px] font-semibold text-slate-600 uppercase tracking-widest">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {sessions.map(s => {
                  const expired = new Date(s.expiresAtUtc) < new Date();
                  return (
                    <tr key={s.id} className="border-b border-white/[0.04] last:border-0 hover:bg-white/[0.02] transition-colors">
                      <td className="px-4 py-3">
                        <p className="text-sm text-white">{s.targetUserEmail}</p>
                        <p className="text-[11px] text-slate-600 font-mono">{s.tenantId.slice(0, 8)}</p>
                      </td>
                      <td className="px-3 py-3 text-xs text-slate-400 max-w-[180px]">
                        <p className="truncate">{s.reason}</p>
                      </td>
                      <td className="px-3 py-3 text-xs text-slate-500">{s.startedByEmail}</td>
                      <td className="px-3 py-3 text-[11px] text-slate-600 whitespace-nowrap">
                        {new Date(s.startedAtUtc).toLocaleString('en-GB')}
                      </td>
                      <td className="px-3 py-3 text-[11px] whitespace-nowrap">
                        <span className={expired ? 'text-rose-400' : 'text-slate-500'}>
                          {new Date(s.expiresAtUtc).toLocaleString('en-GB')}
                        </span>
                      </td>
                      <td className="px-3 py-3">
                        <span className={`text-[10px] font-semibold uppercase px-1.5 py-0.5 rounded border ${
                          s.isActive && !expired
                            ? 'text-emerald-400 bg-emerald-500/10 border-emerald-500/20'
                            : 'text-slate-600 bg-transparent border-slate-800'
                        }`}>
                          {s.isActive && !expired ? 'Active' : s.endedAtUtc ? 'Ended' : 'Expired'}
                        </span>
                      </td>
                      <td className="px-3 py-3">
                        {s.isActive && !expired && !s.endedAtUtc && (
                          <button type="button"
                            onClick={() => endSession(s.id)}
                            disabled={ending === s.id}
                            className="flex items-center gap-1 text-[11px] text-rose-400 border border-rose-500/20 hover:border-rose-500/40 px-2 py-1 rounded transition-colors disabled:opacity-40">
                            <StopCircle className="h-3 w-3" />
                            {ending === s.id ? '…' : 'End'}
                          </button>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}
