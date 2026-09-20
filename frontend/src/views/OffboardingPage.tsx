'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { UserMinus, X, CheckCircle2, Star, Undo2, AlertTriangle, ShieldOff, Search, Loader2 } from 'lucide-react';
import {
  offboardingApi, SEPARATION_TYPE_FALLBACK,
  type Offboarding, type OffboardingSummary, type SeparationTypeInfo,
} from '../api/offboarding';
import { employeesApi } from '../api/employees';

const EXIT_REASONS = ['Compensation', 'Career Growth', 'Management', 'Work-Life Balance', 'Relocation', 'Job Content', 'Company Culture', 'Better Offer', 'Personal', 'Other'];

/**
 * S2-F4 — who can be offboarded. Mirrors EstablishmentOccupancy.OccupyingStatuses minus Offboarded
 * (already leaving). The picker used to filter `status: 'Active'` server-side, so the
 * "suspended pending investigation, then dismissed" path — the exact case Article 80 exists for —
 * could not be started from the UI at all.
 */
const OFFBOARDABLE_STATUSES = new Set(['Active', 'Suspended']);

function daysBetween(from: Date, to: Date) { return Math.ceil((to.getTime() - from.getTime()) / 86400000); }
function fmtDate(s: string | null) { return s ? new Date(s).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' }) : '—'; }
function addDays(iso: string, days: number) {
  const d = new Date(`${iso}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}
/** KSA working week is Sun–Thu, so a computed LWD routinely lands on a Friday or Saturday. */
function weekendName(iso: string): string | null {
  if (!iso) return null;
  const day = new Date(`${iso}T00:00:00Z`).getUTCDay();
  return day === 5 ? 'Friday' : day === 6 ? 'Saturday' : null;
}
function apiMessage(e: unknown, fallback: string) {
  return (e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? fallback;
}

export function OffboardingPage() {
  const [items, setItems] = useState<Offboarding[]>([]);
  const [summary, setSummary] = useState<OffboardingSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [showInitiate, setShowInitiate] = useState(false);

  const load = () => {
    setLoading(true);
    Promise.all([offboardingApi.list(), offboardingApi.summary()])
      .then(([i, s]) => { setItems(i); setSummary(s); })
      .catch(() => {}).finally(() => setLoading(false));
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
  const [error, setError] = useState('');
  const [showSettle, setShowSettle] = useState(false);
  const [ei, setEi] = useState({ status: o.exitInterviewStatus, date: o.exitInterviewDate?.slice(0, 10) ?? '', reasonCategory: o.exitReasonCategory, rating: o.exitInterviewRating, notes: o.exitInterviewNotes });

  // "Access revoked" is handled separately below: it is an irreversible ACTION, not a note, so it must
  // not render as a togglable checkbox alongside the three that really are notes.
  const checklist: [keyof Offboarding, string][] = [
    ['assetsReturned', 'Assets returned'],
    ['knowledgeHandover', 'Knowledge handover'],
    ['finalSettlementDone', 'Final settlement'],
  ];
  const done = checklist.filter(([k]) => o[k] === true).length + (o.accessRevoked ? 1 : 0);
  const isProgress = o.status === 'InProgress';
  const daysLeft = isProgress ? daysBetween(new Date(), new Date(o.lastWorkingDay)) : null;

  const run = async (fn: () => Promise<unknown>, fallback: string) => {
    setBusy(true); setError('');
    try { await fn(); onChange(); }
    catch (e) { setError(apiMessage(e, fallback)); }
    finally { setBusy(false); }
  };

  const toggle = (k: string, v: boolean) =>
    run(() => offboardingApi.checklist(o.id, { [k]: v }), 'Could not update the checklist.');
  const revokeAccess = () => run(() => offboardingApi.revokeAccess(o.id), 'Could not revoke access.');
  const saveEi = () => run(async () => { await offboardingApi.exitInterview(o.id, ei); setEditEi(false); }, 'Could not save the exit interview.');
  const complete = () => run(() => offboardingApi.complete(o.id), 'Could not complete the offboarding.');
  const cancel = () => {
    const reason = window.prompt('Why is this separation being withdrawn? This is recorded against the rescind.');
    if (reason === null) return;
    return run(() => offboardingApi.cancel(o.id, reason), 'Could not rescind the offboarding.');
  };

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
            {!o.finalSettlementDone && (
              <button type="button" className="btn-secondary text-xs" onClick={() => setShowSettle(true)} disabled={busy}>Record settlement payment</button>
            )}
            <button type="button" className="btn-primary flex items-center gap-1 text-xs" onClick={complete} disabled={busy}><CheckCircle2 className="h-3 w-3" /> Complete &amp; Archive</button>
          </div>
        )}
      </div>

      {o.reason && <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">“{o.reason}”</p>}
      {error && <p role="alert" className="mt-2 rounded-lg bg-rose-50 px-2.5 py-1.5 text-xs text-rose-600 dark:bg-rose-500/10 dark:text-rose-400">{error}</p>}

      <div className="mt-3 grid gap-4 lg:grid-cols-2">
        {/* Checklist */}
        <div>
          <p className="mb-1.5 text-xs font-semibold text-slate-700 dark:text-slate-300">Clearance checklist <span className="text-slate-400">({done}/4)</span></p>
          <div className="grid grid-cols-2 gap-1.5">
            {checklist.map(([k, label]) => (
              <label key={k} className="flex cursor-pointer items-center gap-2 text-xs text-slate-600 dark:text-slate-300">
                <input type="checkbox" className="h-3.5 w-3.5 rounded border-slate-300 dark:border-white/20 dark:bg-white/5" checked={o[k] === true} disabled={!isProgress || busy} onChange={e => toggle(k, e.target.checked)} />
                {label}
              </label>
            ))}
            {/* S2-B2 — an ACTION, not a note. Ticking a box used to revoke nothing at all: the real
                revocation only ran inside Complete, so the ex-employee kept a working login for the
                whole settlement window. It is one-way, so it is rendered as one-way. */}
            {o.accessRevoked ? (
              <span className="flex items-center gap-2 text-xs font-medium text-emerald-600 dark:text-emerald-400">
                <ShieldOff className="h-3.5 w-3.5 shrink-0" />
                Access revoked{o.accessRevokedAtUtc ? ` · ${fmtDate(o.accessRevokedAtUtc)}` : ''}
              </span>
            ) : (
              <button
                type="button"
                className="flex items-center gap-1.5 justify-self-start rounded-lg border border-rose-300 px-2 py-0.5 text-xs font-medium text-rose-600 hover:bg-rose-50 disabled:opacity-50 dark:border-rose-500/40 dark:text-rose-400 dark:hover:bg-rose-500/10"
                disabled={!isProgress || busy}
                onClick={() => {
                  if (window.confirm(`Revoke ${o.employeeName}'s system access now?\n\nTheir account is deactivated and every active session ends immediately. This cannot be undone from here — only rescinding the offboarding restores it.`)) revokeAccess();
                }}
              >
                {busy ? <Loader2 className="h-3 w-3 animate-spin" /> : <ShieldOff className="h-3 w-3" />} Revoke access
              </button>
            )}
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

      {showSettle && (
        <RecordSettlementModal
          o={o}
          onClose={() => setShowSettle(false)}
          onDone={() => { setShowSettle(false); onChange(); }}
        />
      )}
    </div>
  );
}

/**
 * S2-B2 — record a final settlement paid by bank transfer / cheque / cash.
 *
 * Without this the offboarding could never be completed unless the settlement was disbursed through a
 * payroll run, which no client opens for one leaver mid-month. The API posts the real DR payable /
 * CR cash journal, so this is a payment record, not a tickbox.
 */
function RecordSettlementModal({ o, onClose, onDone }: { o: Offboarding; onClose: () => void; onDone: () => void }) {
  const [form, setForm] = useState({ method: 'BankTransfer', reference: '', amount: '', paidOn: new Date().toISOString().slice(0, 10) });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const submit = async () => {
    if (!form.reference.trim()) { setError('A bank reference or cheque number is required.'); return; }
    const amount = Number(form.amount);
    if (!Number.isFinite(amount) || amount <= 0) { setError('Enter the amount that was paid.'); return; }
    setSaving(true); setError('');
    try {
      await offboardingApi.recordExternalSettlementPayment(o.id, { ...form, reference: form.reference.trim(), amount });
      onDone();
    } catch (e) {
      setError(apiMessage(e, 'Could not record the payment.'));
      setSaving(false);
    }
  };

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-md rounded-2xl border border-slate-200 bg-white shadow-2xl dark:border-white/10 dark:bg-[#0D1221]">
        <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-white/10">
          <h3 className="font-semibold text-slate-900 dark:text-white">Settlement paid outside payroll</h3>
          <button type="button" aria-label="Close" onClick={onClose} className="grid h-7 w-7 place-items-center rounded-lg text-slate-400 hover:bg-slate-100 dark:hover:bg-white/10"><X className="h-4 w-4" /></button>
        </div>
        <div className="space-y-3 px-6 py-4">
          <p className="text-xs text-slate-500 dark:text-slate-400">
            For {o.employeeName} ({o.employeeCode}). The amount must match the approved settlement’s net payable —
            the server confirms it and posts the ledger entry that clears the payable.
          </p>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label htmlFor="settle-method" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Method</label>
              <select id="settle-method" className="select w-full" value={form.method} onChange={e => setForm(f => ({ ...f, method: e.target.value }))}>
                <option value="BankTransfer">Bank transfer</option>
                <option value="Cheque">Cheque</option>
                <option value="Cash">Cash</option>
                <option value="Other">Other</option>
              </select>
            </div>
            <div>
              <label htmlFor="settle-date" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Value date</label>
              <input id="settle-date" type="date" className="input w-full" value={form.paidOn} onChange={e => setForm(f => ({ ...f, paidOn: e.target.value }))} />
            </div>
          </div>
          <div>
            <label htmlFor="settle-ref" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Bank reference / cheque number *</label>
            <input id="settle-ref" className="input w-full" value={form.reference} onChange={e => setForm(f => ({ ...f, reference: e.target.value }))} placeholder="e.g. TRF-8842193" />
          </div>
          <div>
            <label htmlFor="settle-amount" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Amount paid *</label>
            <input id="settle-amount" type="number" step="0.01" min="0" className="input w-full" value={form.amount} onChange={e => setForm(f => ({ ...f, amount: e.target.value }))} />
          </div>
          {error && <p role="alert" className="text-xs text-rose-500 dark:text-rose-400">{error}</p>}
        </div>
        <div className="flex justify-end gap-2 border-t border-slate-100 px-6 py-4 dark:border-white/10">
          <button type="button" className="btn-secondary text-sm" onClick={onClose}>Cancel</button>
          <button type="button" className="btn-primary text-sm" onClick={submit} disabled={saving}>{saving ? 'Recording…' : 'Record payment'}</button>
        </div>
      </div>
    </div>,
    document.body,
  );
}

type PickableEmployee = { id: number; fullName: string; employeeCode: string; status: string };

function InitiateModal({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const [types, setTypes] = useState<SeparationTypeInfo[]>(SEPARATION_TYPE_FALLBACK);
  const [employees, setEmployees] = useState<PickableEmployee[]>([]);
  const [empLoading, setEmpLoading] = useState(true);
  const [empError, setEmpError] = useState('');
  const [search, setSearch] = useState('');
  const [form, setForm] = useState({ employeeId: 0, separationType: 'Resignation', reason: '', noticeDate: new Date().toISOString().slice(0, 10), noticePeriodDays: 30, rehireEligible: true, raiseBackfill: true });
  // S2-F4 — the last working day is now ENTERED, not computed and locked. It seeds from
  // notice + notice period and the operator may override it: immediate termination with pay in lieu, a
  // negotiated early release and an LWD that avoids landing on a Friday were all unenterable before.
  const [lwd, setLwd] = useState('');
  const [lwdTouched, setLwdTouched] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    offboardingApi.separationTypes()
      .then(t => { if (t.length > 0) setTypes(t); })
      .catch(() => { /* keep the local mirror — the modal must still work */ });
  }, []);

  // Server-side search rather than a 300-row dump: for any client over 300 active staff most of the
  // workforce simply was not in the list.
  const searchRef = useRef(search);
  searchRef.current = search;
  const loadEmployees = useCallback((term: string) => {
    setEmpLoading(true); setEmpError('');
    employeesApi.list({ search: term || undefined, pageSize: 50 })
      .then(r => {
        if (searchRef.current !== term) return; // a newer keystroke owns the list
        setEmployees(r.items
          .filter(e => OFFBOARDABLE_STATUSES.has(e.status))
          .map(e => ({ id: e.id, fullName: e.fullName, employeeCode: e.employeeCode, status: e.status })));
      })
      .catch(() => { if (searchRef.current === term) setEmpError('Could not load employees. Retry, or refine the search.'); })
      .finally(() => { if (searchRef.current === term) setEmpLoading(false); });
  }, []);

  useEffect(() => {
    const t = setTimeout(() => loadEmployees(search), search ? 300 : 0);
    return () => clearTimeout(t);
  }, [search, loadEmployees]);

  const computedLwd = useMemo(
    () => (form.noticeDate ? addDays(form.noticeDate, Number(form.noticePeriodDays) || 0) : ''),
    [form.noticeDate, form.noticePeriodDays],
  );
  useEffect(() => { if (!lwdTouched) setLwd(computedLwd); }, [computedLwd, lwdTouched]);

  const selectedType = types.find(t => t.code === form.separationType) ?? types[0];
  const forfeits = selectedType?.forfeitsEndOfServiceAward === true;
  const weekend = weekendName(lwd);
  const servedDays = form.noticeDate && lwd ? daysBetween(new Date(`${form.noticeDate}T00:00:00Z`), new Date(`${lwd}T00:00:00Z`)) : 0;
  const shortfall = Math.max(0, (Number(form.noticePeriodDays) || 0) - servedDays);

  const submit = async () => {
    if (!form.employeeId) { setError('Select an employee.'); return; }
    if (!lwd) { setError('Enter a last working day.'); return; }
    if (lwd < form.noticeDate) { setError('The last working day cannot be before the notice date.'); return; }
    if (selectedType?.requiresReason && !form.reason.trim()) {
      setError(`${selectedType.label} requires a recorded reason.`); return;
    }
    setSaving(true); setError('');
    try {
      await offboardingApi.initiate({ ...form, lastWorkingDay: lwd });
      onDone();
    } catch (e: unknown) {
      setError(apiMessage(e, 'Could not initiate offboarding.'));
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
            <label htmlFor="off-employee" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Employee *</label>
            <div className="relative mb-1.5">
              <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-slate-400" />
              <input
                className="input w-full pl-8"
                placeholder="Search by name or code…"
                value={search}
                onChange={e => setSearch(e.target.value)}
                aria-label="Search employees"
              />
            </div>
            <select id="off-employee" className="select w-full" value={form.employeeId} onChange={e => setForm(f => ({ ...f, employeeId: Number(e.target.value) }))}>
              <option value={0}>— Select —</option>
              {employees.map(e => (
                <option key={e.id} value={e.id}>
                  {e.fullName} ({e.employeeCode}){e.status !== 'Active' ? ` · ${e.status}` : ''}
                </option>
              ))}
            </select>
            {empLoading ? (
              <p className="mt-1 flex items-center gap-1.5 text-xs text-slate-400"><Loader2 className="h-3 w-3 animate-spin" /> Loading employees…</p>
            ) : empError ? (
              <p role="alert" className="mt-1 text-xs text-rose-500 dark:text-rose-400">{empError}</p>
            ) : employees.length === 0 ? (
              <p className="mt-1 text-xs text-slate-400">
                {search ? 'No active or suspended employee matches that search.' : 'No employees available to offboard.'}
              </p>
            ) : (
              <p className="mt-1 text-xs text-slate-400">Active and suspended staff. Search to narrow a large workforce.</p>
            )}
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label htmlFor="off-type" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Separation type</label>
              <select id="off-type" className="select w-full" value={form.separationType} onChange={e => setForm(f => ({ ...f, separationType: e.target.value }))}>
                {types.map(t => <option key={t.code} value={t.code}>{t.label}</option>)}
              </select>
            </div>
            <div>
              <label htmlFor="off-notice" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Notice date</label>
              <input id="off-notice" type="date" className="input w-full" value={form.noticeDate} onChange={e => setForm(f => ({ ...f, noticeDate: e.target.value }))} />
            </div>
          </div>
          {selectedType && (
            <p className={`text-xs ${forfeits ? 'text-rose-600 dark:text-rose-400' : 'text-slate-500 dark:text-slate-400'}`}>
              {selectedType.description}
            </p>
          )}
          {forfeits && (
            <div role="alert" className="flex items-start gap-2 rounded-lg border border-rose-300 bg-rose-50 p-2.5 text-xs text-rose-700 dark:border-rose-500/40 dark:bg-rose-500/10 dark:text-rose-300">
              <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
              <span>
                <strong>The end-of-service award will be forfeited in full.</strong> Record the specific
                Art. 80 (1)–(9) ground below. If this is not a summary dismissal for cause, choose another
                separation type — recording it as a plain termination pays the full Art. 84 award instead.
              </span>
            </div>
          )}
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label htmlFor="off-noticedays" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Contractual notice (days)</label>
              <input id="off-noticedays" type="number" min={0} className="input w-full" value={form.noticePeriodDays} onChange={e => setForm(f => ({ ...f, noticePeriodDays: Number(e.target.value) }))} />
            </div>
            <div>
              <label htmlFor="off-lwd" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Last working day *</label>
              <input id="off-lwd" type="date" className="input w-full" value={lwd} min={form.noticeDate || undefined} onChange={e => { setLwdTouched(true); setLwd(e.target.value); }} />
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
            {lwdTouched && lwd !== computedLwd && (
              <button type="button" className="text-sapphire hover:underline dark:text-cyanAccent" onClick={() => { setLwdTouched(false); setLwd(computedLwd); }}>
                Reset to notice + {Number(form.noticePeriodDays) || 0} days ({computedLwd || '—'})
              </button>
            )}
            {weekend && <span className="text-amber-600 dark:text-amber-400">Last working day falls on a {weekend}.</span>}
            {shortfall > 0 && (
              <span className="text-amber-600 dark:text-amber-400">
                {shortfall} day{shortfall === 1 ? '' : 's'} of notice unserved — the settlement will raise pay in lieu.
              </span>
            )}
          </div>
          <div>
            <label htmlFor="off-reason" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">
              Reason{selectedType?.requiresReason ? ' *' : ''}
            </label>
            <textarea
              id="off-reason"
              className="input w-full resize-none"
              rows={2}
              placeholder={forfeits ? 'Which Art. 80 ground is relied on, and the evidence…' : 'Reason for separation…'}
              value={form.reason}
              onChange={e => setForm(f => ({ ...f, reason: e.target.value }))}
            />
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
