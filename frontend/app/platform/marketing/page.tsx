'use client';

import { useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { Plus, RefreshCw, X, Send, Archive } from 'lucide-react';
import { platformApi, type PlatformAnnouncement } from '@/src/api/platform';

const STATUS_CLS: Record<string, string> = {
  Draft:     'text-slate-400 bg-slate-700/50 border-slate-600',
  Published: 'text-emerald-400 bg-emerald-900/30 border-emerald-700/30',
  Archived:  'text-slate-600 bg-transparent border-slate-800',
};

const TARGET_PLANS = ['All', 'Trial', 'Starter', 'Growth', 'Enterprise'];

function AnnouncementModal({ existing, onClose, onSaved }: {
  existing?: PlatformAnnouncement;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [form, setForm] = useState({
    title: existing?.title ?? '',
    body: existing?.body ?? '',
    targetPlan: existing?.targetPlan ?? 'All',
    expiresAtUtc: existing?.expiresAtUtc?.slice(0, 10) ?? '',
  });
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState('');

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true); setErr('');
    try {
      const payload = {
        ...form,
        expiresAtUtc: form.expiresAtUtc ? form.expiresAtUtc + 'T00:00:00Z' : null,
      };
      if (existing) {
        await platformApi.updateAnnouncement(existing.id, payload);
      } else {
        await platformApi.createAnnouncement(payload);
      }
      onSaved(); onClose();
    } catch (ex: unknown) {
      setErr((ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Failed.');
    } finally { setSaving(false); }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-lg bg-[#0d1117] border border-white/10 rounded-2xl shadow-e3 overflow-hidden">
        <div className="flex items-center justify-between px-6 py-4 border-b border-white/[0.07]">
          <h2 className="text-sm font-semibold text-white">{existing ? 'Edit Announcement' : 'New Announcement'}</h2>
          <button type="button" onClick={onClose} aria-label="Close" className="text-slate-500 hover:text-white transition-colors">
            <X className="h-4 w-4" />
          </button>
        </div>
        <form onSubmit={submit} className="px-6 py-5 space-y-4">
          {err && <div className="text-xs text-rose-400 bg-rose-500/10 border border-rose-500/20 rounded-lg px-3 py-2">{err}</div>}
          <div>
            <label className="block text-xs text-slate-400 mb-1">Title *</label>
            <input required value={form.title} onChange={e => setForm(f => ({ ...f, title: e.target.value }))}
              className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 placeholder-slate-600"
              placeholder="New Feature: Payroll Export" />
          </div>
          <div>
            <label className="block text-xs text-slate-400 mb-1">Message *</label>
            <textarea required rows={4} value={form.body} onChange={e => setForm(f => ({ ...f, body: e.target.value }))}
              className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60 placeholder-slate-600 resize-none"
              placeholder="We're excited to announce…" />
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="block text-xs text-slate-400 mb-1">Target Plan</label>
              <select aria-label="Target plan" value={form.targetPlan} onChange={e => setForm(f => ({ ...f, targetPlan: e.target.value }))}
                className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60">
                {TARGET_PLANS.map(p => <option key={p} value={p}>{p}</option>)}
              </select>
            </div>
            <div>
              <label className="block text-xs text-slate-400 mb-1">Expires</label>
              <input type="date" aria-label="Expires" value={form.expiresAtUtc} onChange={e => setForm(f => ({ ...f, expiresAtUtc: e.target.value }))}
                className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-sapphire/60" />
            </div>
          </div>
          <div className="flex gap-3 pt-1">
            <button type="button" onClick={onClose} className="flex-1 border border-white/10 text-slate-400 rounded-lg py-2 text-sm transition-colors">Cancel</button>
            <button type="submit" disabled={saving} className="flex-1 bg-sapphire text-white rounded-lg py-2 text-sm font-semibold transition-colors disabled:opacity-40">
              {saving ? 'Saving…' : existing ? 'Save Changes' : 'Create'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

export default function MarketingPage() {
  const router = useRouter();
  const [announcements, setAnnouncements] = useState<PlatformAnnouncement[]>([]);
  const [loading, setLoading]             = useState(true);
  const [modal, setModal]                 = useState<'new' | PlatformAnnouncement | null>(null);
  const [msg, setMsg]                     = useState<{ text: string; ok: boolean } | null>(null);
  const [filter, setFilter]               = useState<string>('');

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    load();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    try { setAnnouncements(await platformApi.listAnnouncements(filter || undefined)); }
    catch { setMsg({ text: 'Failed to load announcements.', ok: false }); }
    finally { setLoading(false); }
  }, [filter]);

  async function publish(a: PlatformAnnouncement) {
    try {
      await platformApi.updateAnnouncement(a.id, { status: 'Published' });
      setMsg({ text: `"${a.title}" published.`, ok: true });
      await load();
    } catch { setMsg({ text: 'Publish failed.', ok: false }); }
  }

  async function archive(a: PlatformAnnouncement) {
    try {
      await platformApi.deleteAnnouncement(a.id);
      setMsg({ text: `"${a.title}" archived.`, ok: true });
      await load();
    } catch { setMsg({ text: 'Archive failed.', ok: false }); }
  }

  const filtered = announcements.filter(a => !filter || a.status === filter);

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-lg font-semibold text-white">Marketing & Announcements</h1>
          <p className="text-xs text-slate-500 mt-0.5">In-app announcements targeted by plan tier</p>
        </div>
        <div className="flex items-center gap-2">
          <select aria-label="Filter by status" value={filter} onChange={e => { setFilter(e.target.value); }}
            className="bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-1.5 text-sm text-slate-300 focus:outline-none focus:border-sapphire/60">
            <option value="">All statuses</option>
            <option value="Draft">Draft</option>
            <option value="Published">Published</option>
            <option value="Archived">Archived</option>
          </select>
          <button type="button" onClick={load} disabled={loading} aria-label="Refresh"
            className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
          </button>
          <button type="button" onClick={() => setModal('new')}
            className="flex items-center gap-1.5 bg-sapphire hover:bg-blue-500 text-white px-3 py-1.5 rounded-lg text-sm font-semibold transition-colors">
            <Plus className="h-3.5 w-3.5" />
            New Announcement
          </button>
        </div>
      </div>

      {msg && (
        <div className={`flex items-center justify-between px-4 py-2.5 rounded-lg border text-sm ${msg.ok ? 'bg-emerald-500/10 border-emerald-500/20 text-emerald-400' : 'bg-rose-500/10 border-rose-500/20 text-rose-400'}`}>
          {msg.text}
          <button type="button" aria-label="Dismiss" onClick={() => setMsg(null)}><X className="h-3.5 w-3.5" /></button>
        </div>
      )}

      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
        {loading ? (
          <div className="flex items-center justify-center py-16">
            <div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" />
          </div>
        ) : filtered.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-16 gap-3">
            <p className="text-sm text-slate-600">No announcements yet.</p>
            <button type="button" onClick={() => setModal('new')} className="text-xs text-sapphire hover:text-blue-300">+ Create first announcement</button>
          </div>
        ) : (
          <div className="divide-y divide-white/[0.04]">
            {filtered.map(a => (
              <div key={a.id} className="p-5 hover:bg-white/[0.02] transition-colors">
                <div className="flex items-start justify-between gap-4">
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-2 mb-1">
                      <span className={`text-caption font-semibold uppercase tracking-wider px-1.5 py-0.5 rounded border ${STATUS_CLS[a.status]}`}>
                        {a.status}
                      </span>
                      {a.targetPlan !== 'All' && (
                        <span className="text-caption text-slate-500 bg-white/[0.04] px-1.5 py-0.5 rounded">
                          {a.targetPlan} only
                        </span>
                      )}
                      {a.expiresAtUtc && (
                        <span className="text-caption text-slate-600">
                          Expires {new Date(a.expiresAtUtc).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })}
                        </span>
                      )}
                    </div>
                    <h3 className="text-sm font-semibold text-white">{a.title}</h3>
                    <p className="text-xs text-slate-400 mt-1 line-clamp-2">{a.body}</p>
                    <p className="text-caption text-slate-700 mt-1.5">
                      Created by {a.createdByEmail} · {new Date(a.createdAtUtc).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })}
                    </p>
                  </div>
                  <div className="flex items-center gap-1.5 shrink-0">
                    <button type="button" onClick={() => setModal(a)}
                      className="text-caption text-slate-400 border border-white/10 hover:border-white/20 px-2 py-1 rounded transition-colors">
                      Edit
                    </button>
                    {a.status === 'Draft' && (
                      <button type="button" onClick={() => publish(a)}
                        className="flex items-center gap-1 text-caption text-emerald-400 border border-emerald-500/20 hover:border-emerald-500/40 px-2 py-1 rounded transition-colors">
                        <Send className="h-3 w-3" /> Publish
                      </button>
                    )}
                    {a.status !== 'Archived' && (
                      <button type="button" onClick={() => archive(a)}
                        className="flex items-center gap-1 text-caption text-slate-500 border border-white/10 hover:border-white/20 px-2 py-1 rounded transition-colors">
                        <Archive className="h-3 w-3" /> Archive
                      </button>
                    )}
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      {modal && (
        <AnnouncementModal
          existing={modal === 'new' ? undefined : modal}
          onClose={() => setModal(null)}
          onSaved={load}
        />
      )}
    </div>
  );
}
