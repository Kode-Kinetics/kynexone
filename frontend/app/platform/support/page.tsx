'use client';

import { useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import { RefreshCw, X, MonitorPlay, ExternalLink, Search } from 'lucide-react';
import { platformApi, type PlatformTenantSummary, type SupportSession, type StartSupportAccessResult } from '@/src/api/platform';

function StartSessionModal({ onClose, onStarted }: { onClose: () => void; onStarted: (r: StartSupportAccessResult) => void }) {
  const [tenants, setTenants] = useState<PlatformTenantSummary[]>([]);
  const [selectedTenant, setSelectedTenant] = useState('');
  const [tenantUsers, setTenantUsers] = useState<{ id: string; email: string; fullName: string }[]>([]);
  const [selectedUser, setSelectedUser] = useState('');
  const [reason, setReason] = useState('');
  const [loadingTenants, setLoadingTenants] = useState(true);
  const [loadingUsers, setLoadingUsers] = useState(false);
  const [starting, setStarting] = useState(false);
  const [err, setErr] = useState('');

  useEffect(() => {
    platformApi.listTenants().then(ts => {
      setTenants(ts.filter(t => t.subscription?.status === 'Active' || t.subscription?.status === 'Trial'));
      setLoadingTenants(false);
    }).catch(() => setLoadingTenants(false));
  }, []);

  useEffect(() => {
    if (!selectedTenant) { setTenantUsers([]); setSelectedUser(''); return; }
    setLoadingUsers(true);
    platformApi.listAdmins(selectedTenant).then(admins => {
      setTenantUsers(admins.map(a => ({ id: a.id, email: a.email, fullName: a.fullName })));
      setLoadingUsers(false);
    }).catch(() => setLoadingUsers(false));
  }, [selectedTenant]);

  async function start(e: React.FormEvent) {
    e.preventDefault();
    if (!selectedTenant || !selectedUser || !reason.trim()) return;
    setStarting(true); setErr('');
    try {
      const r = await platformApi.startSupportAccess(selectedTenant, selectedUser, reason);
      onStarted(r);
      onClose();
    } catch (ex: unknown) {
      setErr((ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Failed to start session.');
    } finally { setStarting(false); }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-md bg-[#0d1117] border border-white/10 rounded-2xl shadow-2xl overflow-hidden">
        <div className="flex items-center justify-between px-6 py-4 border-b border-white/[0.07]">
          <h2 className="text-sm font-semibold text-white">Start Support Session</h2>
          <button type="button" onClick={onClose} aria-label="Close" className="text-slate-500 hover:text-white transition-colors">
            <X className="h-4 w-4" />
          </button>
        </div>
        <form onSubmit={start} className="px-6 py-5 space-y-4">
          <div className="px-3 py-2.5 bg-amber-500/5 border border-amber-500/20 rounded-lg text-xs text-amber-400">
            Support sessions are time-limited, audited, and cannot access sensitive data (salary, payroll, documents) without the user&apos;s data being visible in their tenant.
          </div>
          {err && <div className="text-xs text-rose-400 bg-rose-500/10 border border-rose-500/20 rounded-lg px-3 py-2">{err}</div>}
          <div>
            <label className="block text-xs text-slate-400 mb-1">Tenant</label>
            <select aria-label="Select tenant" required value={selectedTenant} onChange={e => setSelectedTenant(e.target.value)} disabled={loadingTenants}
              className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 disabled:opacity-40">
              <option value="">{loadingTenants ? 'Loading…' : 'Select tenant…'}</option>
              {tenants.map(t => <option key={t.id} value={t.id}>{t.name} (/{t.slug})</option>)}
            </select>
          </div>
          <div>
            <label className="block text-xs text-slate-400 mb-1">Target User</label>
            <select aria-label="Select user" required value={selectedUser} onChange={e => setSelectedUser(e.target.value)} disabled={!selectedTenant || loadingUsers}
              className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 disabled:opacity-40">
              <option value="">{loadingUsers ? 'Loading users…' : 'Select user…'}</option>
              {tenantUsers.map(u => <option key={u.id} value={u.id}>{u.fullName} ({u.email})</option>)}
            </select>
          </div>
          <div>
            <label className="block text-xs text-slate-400 mb-1">Reason (required) *</label>
            <textarea required rows={3} value={reason} onChange={e => setReason(e.target.value)}
              className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 resize-none placeholder-slate-600"
              placeholder="Customer reported login issue ticket #1234…" />
          </div>
          <div className="flex gap-3 pt-1">
            <button type="button" onClick={onClose} className="flex-1 border border-white/10 text-slate-400 rounded-lg py-2 text-sm transition-colors">Cancel</button>
            <button type="submit" disabled={starting || !selectedTenant || !selectedUser || !reason.trim()}
              className="flex-1 bg-amber-600 hover:bg-amber-500 text-white rounded-lg py-2 text-sm font-semibold transition-colors disabled:opacity-40">
              {starting ? 'Starting…' : 'Start Session'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

function SessionResultModal({ result, onClose }: { result: StartSupportAccessResult; onClose: () => void }) {
  function openTab() {
    window.open(`${window.location.origin}/login?impersonate=${result.token}`, '_blank');
  }
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-sm bg-[#0d1117] border border-white/10 rounded-2xl shadow-2xl p-6 space-y-4">
        <div className="text-center space-y-1">
          <div className="h-10 w-10 bg-amber-500/20 rounded-full flex items-center justify-center mx-auto mb-3">
            <MonitorPlay className="h-5 w-5 text-amber-400" />
          </div>
          <h3 className="text-sm font-semibold text-white">Support Session Active</h3>
          <p className="text-xs text-slate-400">
            Impersonating <span className="text-white font-medium">{result.targetUserEmail}</span><br />
            Expires {new Date(result.expiresAt).toLocaleTimeString()}
          </p>
        </div>
        <div className="bg-amber-500/5 border border-amber-500/20 rounded-lg px-3 py-2.5 text-xs text-amber-400">
          This session is audited. All actions performed during this session are logged against your platform account.
        </div>
        <div className="flex flex-col gap-2">
          <button type="button" onClick={openTab}
            className="flex items-center justify-center gap-2 bg-amber-600 hover:bg-amber-500 text-white rounded-lg py-2.5 text-sm font-semibold transition-colors">
            <ExternalLink className="h-3.5 w-3.5" />
            Open Tenant App
          </button>
          <button type="button" onClick={onClose}
            className="border border-white/10 text-slate-400 hover:text-white rounded-lg py-2 text-sm transition-colors">
            Close
          </button>
        </div>
      </div>
    </div>
  );
}

export default function SupportPage() {
  const router = useRouter();
  const [sessions, setSessions]   = useState<SupportSession[]>([]);
  const [loading, setLoading]     = useState(true);
  const [showStart, setShowStart] = useState(false);
  const [result, setResult]       = useState<StartSupportAccessResult | null>(null);
  const [search, setSearch]       = useState('');

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    load();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const r = await platformApi.listSupportSessions(undefined, false, 1, 20);
      setSessions(r.sessions);
    } finally { setLoading(false); }
  }, []);

  const filtered = sessions.filter(s =>
    !search || s.targetUserEmail.toLowerCase().includes(search.toLowerCase()) || s.reason.toLowerCase().includes(search.toLowerCase())
  );
  const active = sessions.filter(s => s.isActive && new Date(s.expiresAtUtc) > new Date()).length;

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-bold text-white">Support Center</h1>
          <p className="text-xs text-slate-500 mt-0.5">
            {active > 0
              ? <span className="text-amber-400 font-medium">{active} active session{active !== 1 ? 's' : ''}</span>
              : 'No active sessions'}
            {' · All access is audited and time-limited'}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <button type="button" onClick={load} disabled={loading} aria-label="Refresh"
            className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
          </button>
          <button type="button" onClick={() => setShowStart(true)}
            className="flex items-center gap-1.5 bg-amber-600 hover:bg-amber-500 text-white px-3 py-1.5 rounded-lg text-sm font-semibold transition-colors">
            <MonitorPlay className="h-3.5 w-3.5" />
            Start Session
          </button>
        </div>
      </div>

      <div className="flex items-center gap-3">
        <div className="relative flex-1 max-w-xs">
          <Search className="absolute start-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-slate-600 pointer-events-none" />
          <input type="text" value={search} onChange={e => setSearch(e.target.value)}
            placeholder="Search sessions…"
            className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg ps-8 pe-3 py-1.5 text-sm text-slate-300 placeholder-slate-600 focus:outline-none focus:border-sapphire/60 transition-colors" />
        </div>
        <Link href="/platform/support-sessions"
          className="text-xs text-sapphire hover:text-blue-300 transition-colors">
          Full sessions log →
        </Link>
      </div>

      {/* Recent sessions */}
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
            <p className="text-sm text-slate-600">No support sessions yet.</p>
            <button type="button" onClick={() => setShowStart(true)} className="text-xs text-amber-400 hover:text-amber-300">Start first session</button>
          </div>
        ) : (
          filtered.map(s => {
            const expired = new Date(s.expiresAtUtc) < new Date();
            const isActive = s.isActive && !expired && !s.endedAtUtc;
            return (
              <div key={s.id} className={`flex items-start gap-4 px-4 py-3.5 border-b border-white/[0.04] last:border-0 ${isActive ? 'bg-amber-950/10' : ''}`}>
                <div className={`h-2 w-2 rounded-full mt-1.5 shrink-0 ${isActive ? 'bg-amber-400' : 'bg-slate-700'}`} />
                <div className="flex-1 min-w-0">
                  <p className="text-sm text-white font-medium">{s.targetUserEmail}</p>
                  <p className="text-xs text-slate-500 mt-0.5 truncate">{s.reason}</p>
                  <p className="text-[11px] text-slate-600 mt-1">
                    By {s.startedByEmail} · {new Date(s.startedAtUtc).toLocaleString('en-GB')}
                  </p>
                </div>
                <span className={`text-[10px] font-semibold uppercase px-1.5 py-0.5 rounded border shrink-0 ${
                  isActive ? 'text-amber-400 bg-amber-500/10 border-amber-500/20' : 'text-slate-600 bg-transparent border-slate-800'
                }`}>
                  {isActive ? 'Active' : s.endedAtUtc ? 'Ended' : 'Expired'}
                </span>
              </div>
            );
          })
        )}
      </div>

      {showStart && <StartSessionModal onClose={() => setShowStart(false)} onStarted={r => { setResult(r); load(); }} />}
      {result && <SessionResultModal result={result} onClose={() => setResult(null)} />}
    </div>
  );
}
