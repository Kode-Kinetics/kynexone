'use client';

import { useCallback, useEffect, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { MonitorPlay, RefreshCw, Search, ShieldAlert } from 'lucide-react';
import { platformApi, type SupportSession } from '@/src/api/platform';

export default function SupportPage() {
  const router = useRouter();
  const [sessions, setSessions] = useState<SupportSession[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState('');

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const result = await platformApi.listSupportSessions(undefined, false, 1, 20);
      setSessions(result.sessions);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    const token = localStorage.getItem('platform_access_token');
    if (!token) {
      router.replace('/platform/login');
      return;
    }
    void load();
  }, [load, router]);

  const filtered = sessions.filter((session) =>
    !search
    || session.targetUserEmail.toLowerCase().includes(search.toLowerCase())
    || session.reason.toLowerCase().includes(search.toLowerCase()));
  const active = sessions.filter((session) =>
    session.isActive && !session.endedAtUtc && new Date(session.expiresAtUtc) > new Date()).length;

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-white">Support Center</h1>
          <p className="text-xs text-slate-500 mt-0.5">
            {active > 0
              ? <span className="text-amber-400 font-medium">{active} existing active session{active === 1 ? '' : 's'}</span>
              : 'No active sessions'}
          </p>
        </div>
        <button type="button" onClick={load} disabled={loading} aria-label="Refresh"
          className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
          <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
        </button>
      </div>

      <div className="flex items-start gap-3 rounded-xl border border-amber-500/20 bg-amber-500/5 px-4 py-3">
        <ShieldAlert className="h-4 w-4 text-amber-400 mt-0.5 shrink-0" aria-hidden />
        <div>
          <p className="text-sm font-medium text-amber-300">New privileged support sessions are temporarily disabled.</p>
          <p className="text-xs text-slate-500 mt-1">
            Existing sessions remain visible so incident owners can review and end them. Issuance will return only after server-side revocation is independently verified.
          </p>
        </div>
      </div>

      <div className="flex items-center gap-3">
        <div className="relative flex-1 max-w-xs">
          <Search className="absolute start-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-slate-600 pointer-events-none" />
          <input type="text" value={search} onChange={(event) => setSearch(event.target.value)}
            placeholder="Search sessions…"
            className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg ps-8 pe-3 py-1.5 text-sm text-slate-300 placeholder-slate-600 focus:outline-none focus:border-sapphire/60 transition-colors" />
        </div>
        <Link href="/platform/support-sessions" className="text-xs text-sapphire hover:text-blue-300 transition-colors">
          Review and end sessions →
        </Link>
      </div>

      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
        <div className="px-4 py-2.5 border-b border-white/[0.06]">
          <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">Recent Support Sessions</p>
        </div>
        {loading ? (
          <div className="flex items-center justify-center py-10">
            <div className="h-4 w-4 animate-spin rounded-full border-2 border-amber-500 border-t-transparent" />
          </div>
        ) : filtered.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-10 gap-2">
            <MonitorPlay className="h-6 w-6 text-slate-700" />
            <p className="text-sm text-slate-600">No support sessions found.</p>
          </div>
        ) : filtered.map((session) => {
          const isActive = session.isActive && !session.endedAtUtc && new Date(session.expiresAtUtc) > new Date();
          return (
            <div key={session.id} className={`flex items-start gap-4 px-4 py-3.5 border-b border-white/[0.04] last:border-0 ${isActive ? 'bg-amber-950/10' : ''}`}>
              <div className={`h-2 w-2 rounded-full mt-1.5 shrink-0 ${isActive ? 'bg-amber-400' : 'bg-slate-700'}`} />
              <div className="flex-1 min-w-0">
                <p className="text-sm text-white font-medium">{session.targetUserEmail}</p>
                <p className="text-xs text-slate-500 mt-0.5 truncate">{session.reason}</p>
                <p className="text-[11px] text-slate-600 mt-1">By {session.startedByEmail} · {new Date(session.startedAtUtc).toLocaleString('en-GB')}</p>
              </div>
              <span className={`text-[10px] font-semibold uppercase px-1.5 py-0.5 rounded border shrink-0 ${isActive ? 'text-amber-400 bg-amber-500/10 border-amber-500/20' : 'text-slate-600 border-slate-800'}`}>
                {isActive ? 'Active' : session.endedAtUtc ? 'Ended' : 'Expired'}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}
