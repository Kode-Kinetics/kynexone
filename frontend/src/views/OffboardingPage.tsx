'use client';

import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { UserMinus, X, CheckCircle2, Clock, Star, Undo2, AlertTriangle } from 'lucide-react';
import { offboardingApi, type Offboarding, type OffboardingSummary } from '../api/offboarding';
import { employeesApi } from '../api/employees';
import { notifyApiError } from '../api/client';

const SEPARATION_TYPES = ['Resignation', 'Termination', 'End of Contract', 'Retirement', 'Other'];
const EXIT_REASONS = ['Compensation', 'Career Growth', 'Management', 'Work-Life Balance', 'Relocation', 'Job Content', 'Company Culture', 'Better Offer', 'Personal', 'Other'];

function daysBetween(from: Date, to: Date) { return Math.ceil((to.getTime() - from.getTime()) / 86400000); }
function fmtDate(s: string | null) { return s ? new Date(s).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' }) : '—'; }

export function OffboardingPage() {
  const [items, setItems] = useState<Offboarding[]>([]);
  const [summary, setSummary] = useState<OffboardingSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [showInitiate, setShowInitiate] = useState(false);

  const load = () => {
    setLoading(true);
    Promise.all([offboardingApi.list(), offboardingApi.summary()])
      .then(([i, s]) => { setItems(i); setSummary(s); })
      .catch(notifyApiError).finally(() => setLoading(false));
  };
  useEffect(() => { load(); }, []);

  const inProgress = items.filter(o => o.status === 'InProgress');
  const closed = items.filter(o => o.status !== 'InProgress');

  return (
    <div className="space-y-5 p-4 sm:p-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">Offboarding</h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">Manage separations end-to-end — notice period, exit interview, clearance checklist and final archive.</p>
        </div>
        <button type="button" className="btn-primary flex items-center gap-1.5 text-sm" onClick={() => setShowInitiate(true)}>
          <UserMinus className="h-3.5 w-3.5" /> Initiate Offboarding
        </button>
      </div>

      {/* Summary + attrition insight */}
      {summary && (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <SummaryCard label="Serving Notice" value={summary.inNotice} tone="bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400" />
          <SummaryCard label="Exit Interviews Pending" value={summary.exitInterviewsPending} tone="bg-rose-50 text-rose-600 dark:bg-rose-500/10 dark:text-rose-400" />
          <SummaryCard label="Avg Exit Rating" value={summary.avgExitRating ? `${summary.avgExitRating} / 5` : '—'} tone="bg-sapphire/10 text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent" />
          <SummaryCard label="Completed" value={summary.completed} tone="bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400" />
        </div>
      )}
      {summary && summary.reasons.length > 0 && (
        <div className="surface p-4">
          <p className="mb-3 text-sm font-semibold text-slate-900 dark:text-white">Why people leave</p>
          <div className="space-y-1.5">
            {summary.reasons.map(r => {
              const max = Math.max(...summary.reasons.map(x => x.count));
              return (
                <div key={r.category} className="flex items-center gap-3 text-xs">
                  <span className="w-36 shrink-0 text-slate-600 dark:text-slate-300">{r.category}</span>
                  <div className="h-2 flex-1 rounded-full bg-slate-100 dark:bg-white/10">
                    <div className="h-2 rounded-full bg-sapphire dark:bg-cyanAccent" style={{ width: `${(r.count / max) * 100}%` }} />
                  </div>
                  <span className="w-6 text-right font-medium text-slate-500">{r.count}</span>
                </div>
              );
            })}
          </div>
        </div>
      )}

      {loading ? (
        <div className="flex justify-center py-12"><div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>
      ) : items.length === 0 ? (
        <div className="surface p-10 text-center text-sm text-slate-400">No offboardings yet. Click “Initiate Offboarding” to start a separation.</div>
      ) : (
        <div className="space-y-3">
          {inProgress.map(o => <OffboardingCard key={o.id} o={o} onChange={load} />)}
          {closed.length > 0 && <p className="pt-2 text-xs font-semibold uppercase tracking-wide text-slate-400">Closed</p>}
          {closed.map(o => <OffboardingCard key={o.id} o={o} onChange={load} />)}
        </div>
      )}

      {showInitiate && <InitiateModal onClose={() => setShowInitiate(false)} onDone={() => { setShowInitiate(false); load(); }} />}
    </div>
  );
}

function SummaryCard({ label, value, tone }: { label: string; value: string | number; tone: string }) {
  return (
    <div className="surface p-4">
      <p className="text-xs text-slate-500 dark:text-slate-400">{label}</p>
      <p className={`mt-1 inline-flex rounded-lg px-2 py-0.5 text-xl font-bold ${tone}`}>{value}</p>
    </div>
  );
}

function OffboardingCard({ o, onChange }: { o: Offboarding; onChange: () => void }) {
  const [busy, setBusy] = useState(false);
  const [editEi, setEditEi] = useState(false);
  const [ei, setEi] = useState({ status: o.exitInterviewStatus, date: o.exitInterviewDate?.slice(0, 10) ?? '', reasonCategory: o.exitReasonCategory, rating: o.exitInterviewRating, notes: o.exitInterviewNotes });

  const checklist: [keyof Offboarding, string][] = [
    ['assetsReturned', 'Assets returned'], ['accessRevoked', 'Access revoked'],
    ['knowledgeHandover', 'Knowledge handover'], ['finalSettlementDone', 'Final settlement'],
  ];
  const done = checklist.filter(([k]) => o[k] === true).length;
  const isProgress = o.status === 'InProgress';
  const daysLeft = isProgress ? daysBetween(new Date(), new Date(o.lastWorkingDay)) : null;

  const toggle = async (k: string, v: boolean) => { setBusy(true); await offboardingApi.checklist(o.id, { [k]: v }).catch(notifyApiError); setBusy(false); onChange(); };
  const saveEi = async () => { setBusy(true); await offboardingApi.exitInterview(o.id, ei).catch(notifyApiError); setBusy(false); setEditEi(false); onChange(); };
  const complete = async () => { setBusy(true); await offboardingApi.complete(o.id).catch(notifyApiError); setBusy(false); onChange(); };
  const cancel = async () => { setBusy(true); await offboardingApi.cancel(o.id).catch(notifyApiError); setBusy(false); onChange(); };

  return (
    <div className="surface p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="font-semibold text-slate-800 dark:text-white">{o.employeeName}</span>
            <span className="rounded bg-slate-100 px-1.5 py-0.5 font-mono text-[10px] text-slate-500 dark:bg-white/10 dark:text-slate-400">{o.employeeCode}</span>
            <span className={`rounded-full px-2 py-0.5 text-[10px] font-semibold ${o.status === 'InProgress' ? 'bg-amber-50 text-amber-600 dark:bg-amber-500/10 dark:text-amber-400' : o.status === 'Completed' ? 'bg-emerald-50 text-emerald-600 dark:bg-emerald-500/10 dark:text-emerald-400' : 'bg-slate-100 text-slate-500'}`}>{o.status === 'InProgress' ? 'Serving notice' : o.status}</span>
          </div>
          <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
            {o.separationType} · {o.designation || '—'} · {o.department || '—'} · Last day {fmtDate(o.lastWorkingDay)}
            {daysLeft !== null && <span className={`ml-1 font-medium ${daysLeft < 0 ? 'text-rose-500' : daysLeft <= 7 ? 'text-amber-500' : 'text-slate-400'}`}>({daysLeft < 0 ? `${-daysLeft}d overdue` : `${daysLeft}d left`})</span>}
            {!o.rehireEligible && <span className="ml-1 text-rose-500">· Not rehire-eligible</span>}
          </p>
        </div>
        {isProgress && (
          <div className="flex items-center gap-2">
            <button type="button" className="btn-secondary flex items-center gap-1 text-xs" onClick={cancel} disabled={busy}><Undo2 className="h-3 w-3" /> Rescind</button>
            <button type="button" className="btn-primary flex items-center gap-1 text-xs" onClick={complete} disabled={busy}><CheckCircle2 className="h-3 w-3" /> Complete &amp; Archive</button>
          </div>
        )}
      </div>

      {o.reason && <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">“{o.reason}”</p>}

      <div className="mt-3 grid gap-4 lg:grid-cols-2">
        {/* Checklist */}
        <div>
          <p className="mb-1.5 text-xs font-semibold text-slate-700 dark:text-slate-300">Clearance checklist <span className="text-slate-400">({done}/4)</span></p>
          <div className="grid grid-cols-2 gap-1.5">
            {checklist.map(([k, label]) => (
              <label key={k} className="flex cursor-pointer items-center gap-2 text-xs text-slate-600 dark:text-slate-300">
                <input type="checkbox" className="h-3.5 w-3.5 rounded border-slate-300" checked={o[k] === true} disabled={!isProgress || busy} onChange={e => toggle(k, e.target.checked)} />
                {label}
              </label>
            ))}
          </div>
        </div>

        {/* Exit interview */}
        <div>
          <div className="mb-1.5 flex items-center justify-between">
            <p className="text-xs font-semibold text-slate-700 dark:text-slate-300">Exit interview
              <span className={`ml-1.5 rounded-full px-1.5 py-0.5 text-[10px] ${o.exitInterviewStatus === 'Completed' ? 'bg-emerald-50 text-emerald-600 dark:bg-emerald-500/10' : 'bg-slate-100 text-slate-500 dark:bg-white/10'}`}>{o.exitInterviewStatus}</span>
            </p>
            {isProgress && !editEi && <button type="button" className="text-xs text-sapphire hover:underline dark:text-cyanAccent" onClick={() => setEditEi(true)}>Record</button>}
          </div>
          {editEi ? (
            <div className="space-y-2 rounded-lg border border-slate-200 p-2 dark:border-white/10">
              <div className="grid grid-cols-2 gap-2">
                <select className="select text-xs" value={ei.status} onChange={e => setEi(v => ({ ...v, status: e.target.value }))} aria-label="Exit interview status">
                  {['Pending', 'Scheduled', 'Completed', 'Waived'].map(s => <option key={s}>{s}</option>)}
                </select>
                <input type="date" className="input text-xs" value={ei.date} onChange={e => setEi(v => ({ ...v, date: e.target.value }))} aria-label="Exit interview date" />
              </div>
              <select className="select w-full text-xs" value={ei.reasonCategory} onChange={e => setEi(v => ({ ...v, reasonCategory: e.target.value }))} aria-label="Primary reason for leaving">
                <option value="">— Primary reason for leaving —</option>
                {EXIT_REASONS.map(r => <option key={r}>{r}</option>)}
              </select>
              <div className="flex items-center gap-1">
                <span className="text-xs text-slate-500">Rating:</span>
                {[1, 2, 3, 4, 5].map(n => (
                  <button key={n} type="button" aria-label={`Rate ${n}`} onClick={() => setEi(v => ({ ...v, rating: n }))}>
                    <Star className={`h-4 w-4 ${n <= ei.rating ? 'fill-amber-400 text-amber-400' : 'text-slate-300'}`} />
                  </button>
                ))}
              </div>
              <textarea className="input w-full resize-none text-xs" rows={2} placeholder="Notes…" value={ei.notes} onChange={e => setEi(v => ({ ...v, notes: e.target.value }))} />
              <div className="flex justify-end gap-2">
                <button type="button" className="btn-secondary text-xs" onClick={() => setEditEi(false)}>Cancel</button>
                <button type="button" className="btn-primary text-xs" onClick={saveEi} disabled={busy}>Save</button>
              </div>
            </div>
          ) : (
            <div className="text-xs text-slate-500 dark:text-slate-400">
              {o.exitReasonCategory ? <p>Reason: <span className="text-slate-700 dark:text-slate-200">{o.exitReasonCategory}</span>{o.exitInterviewRating > 0 && <span> · {o.exitInterviewRating}/5</span>}</p> : <p className="text-slate-400">Not yet recorded.</p>}
              {o.exitInterviewNotes && <p className="mt-1 italic">“{o.exitInterviewNotes}”</p>}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function InitiateModal({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const [employees, setEmployees] = useState<{ id: number; fullName: string; employeeCode: string }[]>([]);
  const [form, setForm] = useState({ employeeId: 0, separationType: 'Resignation', reason: '', noticeDate: new Date().toISOString().slice(0, 10), noticePeriodDays: 30, rehireEligible: true, raiseBackfill: true });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    employeesApi.list({ status: 'Active', pageSize: 300 }).then(r => setEmployees(r.items.map(e => ({ id: e.id, fullName: e.fullName, employeeCode: e.employeeCode })))).catch(notifyApiError);
  }, []);

  const lwd = useMemo(() => {
    if (!form.noticeDate) return '';
    const d = new Date(form.noticeDate); d.setDate(d.getDate() + Number(form.noticePeriodDays || 0));
    return d.toISOString().slice(0, 10);
  }, [form.noticeDate, form.noticePeriodDays]);

  const submit = async () => {
    if (!form.employeeId) { setError('Select an employee.'); return; }
    setSaving(true); setError('');
    try {
      await offboardingApi.initiate({ ...form, lastWorkingDay: lwd });
      onDone();
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Could not initiate offboarding.');
      setSaving(false);
    }
  };

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-lg rounded-2xl border border-slate-200 bg-white shadow-2xl dark:border-white/10 dark:bg-[#0D1221]">
        <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-white/10">
          <h3 className="font-semibold text-slate-900 dark:text-white">Initiate Offboarding</h3>
          <button type="button" aria-label="Close" onClick={onClose} className="grid h-7 w-7 place-items-center rounded-lg text-slate-400 hover:bg-slate-100 dark:hover:bg-white/10"><X className="h-4 w-4" /></button>
        </div>
        <div className="space-y-3 px-6 py-4">
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Employee *</label>
            <select className="select w-full" value={form.employeeId} onChange={e => setForm(f => ({ ...f, employeeId: Number(e.target.value) }))} aria-label="Employee">
              <option value={0}>— Select —</option>
              {employees.map(e => <option key={e.id} value={e.id}>{e.fullName} ({e.employeeCode})</option>)}
            </select>
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Separation type</label>
              <select className="select w-full" value={form.separationType} onChange={e => setForm(f => ({ ...f, separationType: e.target.value }))} aria-label="Separation type">
                {SEPARATION_TYPES.map(t => <option key={t}>{t}</option>)}
              </select>
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Notice date</label>
              <input type="date" className="input w-full" value={form.noticeDate} onChange={e => setForm(f => ({ ...f, noticeDate: e.target.value }))} aria-label="Notice date" />
            </div>
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Notice period (days)</label>
              <input type="number" min={0} className="input w-full" value={form.noticePeriodDays} onChange={e => setForm(f => ({ ...f, noticePeriodDays: Number(e.target.value) }))} aria-label="Notice period days" />
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Last working day</label>
              <input type="date" className="input w-full bg-slate-50 dark:bg-white/5" value={lwd} readOnly aria-label="Last working day (computed)" />
            </div>
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Reason</label>
            <textarea className="input w-full resize-none" rows={2} placeholder="Reason for separation…" value={form.reason} onChange={e => setForm(f => ({ ...f, reason: e.target.value }))} />
          </div>
          <div className="flex flex-wrap gap-5">
            <label className="flex cursor-pointer items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
              <input type="checkbox" className="h-4 w-4 rounded border-slate-300" checked={form.rehireEligible} onChange={e => setForm(f => ({ ...f, rehireEligible: e.target.checked }))} /> Rehire eligible
            </label>
            <label className="flex cursor-pointer items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
              <input type="checkbox" className="h-4 w-4 rounded border-slate-300" checked={form.raiseBackfill} onChange={e => setForm(f => ({ ...f, raiseBackfill: e.target.checked }))} /> Raise backfill requisition
            </label>
          </div>
          <div className="flex items-start gap-2 rounded-lg border border-amber-300/50 bg-amber-50 p-2.5 text-xs text-amber-700 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
            <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
            The employee stays in headcount (serving notice) until you Complete the offboarding after their last working day. Remember to run Final Settlement / EOSB from Payroll.
          </div>
          {error && <p className="text-xs text-rose-500">{error}</p>}
        </div>
        <div className="flex justify-end gap-2 border-t border-slate-100 px-6 py-4 dark:border-white/10">
          <button type="button" className="btn-secondary text-sm" onClick={onClose}>Cancel</button>
          <button type="button" className="btn-primary flex items-center gap-1.5 text-sm" onClick={submit} disabled={saving}><UserMinus className="h-3.5 w-3.5" />{saving ? 'Initiating…' : 'Initiate'}</button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
