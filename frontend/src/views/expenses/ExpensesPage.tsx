'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { useRouter } from 'next/navigation';
import { ArrowLeft, CheckCircle, ClipboardCheck, Loader2, Receipt, RefreshCw, Search, SlidersHorizontal, WalletCards, XCircle } from 'lucide-react';
import {
  EXPENSE_STATUSES, expenseError, expensesApi, formatMoney, openBlob,
  type ExpenseCategory, type ExpenseClaim, type ExpenseClaimQuery, type ExpensePayoutResult,
} from '../../api/expenses';
import { payrollApi, type PayrollRun } from '../../api/payroll';
import { EmployeeSearchSelect, type EmployeeSelection } from '../../components/EmployeeSearchSelect';
import { Modal } from '../../components/Modal';
import { useAppToast } from '../../components/ui/AppToast';
import { useAuth } from '../../contexts/AuthContext';
import { ExpenseLinesTable, ExpenseProgressNote, ExpenseStatusBadge, SkeletonRows, ViolationList } from './ExpenseShared';

// W2-B — expense claims for approvers, HR, payroll and finance. Tenant/company isolation is enforced
// server-side; team scoping (a manager sees their reports) too. This page only renders what it is
// given.

type Tab = 'approvals' | 'claims' | 'payroll' | 'policy';

const TABS: { id: Tab; label: string; icon: React.ElementType }[] = [
  { id: 'approvals', label: 'To approve', icon: ClipboardCheck },
  { id: 'claims', label: 'All claims', icon: Receipt },
  { id: 'payroll', label: 'Pay via payroll', icon: WalletCards },
  { id: 'policy', label: 'Category policy', icon: SlidersHorizontal },
];

const PAYROLL_ROLES = ['Admin', 'Payroll Manager', 'Payroll Officer'];
const POLICY_ROLES = ['Admin', 'HR Manager', 'Payroll Manager', 'Finance'];

function fmtDate(v: string | null | undefined) {
  if (!v) return '—';
  try { return new Date(v).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' }); } catch { return v; }
}

// ── Decision modal ────────────────────────────────────────────────────────────

function DecisionModal({ claim, decision, onClose, onDone }: { claim: ExpenseClaim; decision: 'Approve' | 'Reject'; onClose: () => void; onDone: (c: ExpenseClaim) => void }) {
  const [comments, setComments] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const reject = decision === 'Reject';

  const submit = async () => {
    if (reject && !comments.trim()) { setError('Give the employee a reason for the rejection.'); return; }
    setBusy(true); setError(null);
    try { onDone(await expensesApi.decide(claim.id, decision, comments.trim() || undefined)); }
    catch (err) { setError(expenseError(err, 'The decision could not be recorded.').message); }
    finally { setBusy(false); }
  };

  return (
    <Modal isOpen title={`${decision} ${claim.claimNumber}`} onClose={onClose}
      footer={(
        <div className="flex justify-end gap-2">
          <button type="button" onClick={onClose} className="btn-secondary">Cancel</button>
          <button type="button" onClick={submit} disabled={busy} className={`${reject ? 'bg-rose-600 hover:bg-rose-700 text-white rounded-xl px-4 py-2 text-sm font-semibold' : 'btn-primary'} disabled:opacity-60`}>
            {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : null} {decision}
          </button>
        </div>
      )}>
      <div className="space-y-3">
        <p className="text-sm text-slate-700 dark:text-slate-200">
          {claim.employeeName} · {formatMoney(claim.totalAmount, claim.currency)} · {claim.lines.length} line{claim.lines.length === 1 ? '' : 's'}
        </p>
        {claim.approval && (
          <p className="text-xs text-slate-500 dark:text-slate-400">Step {claim.approval.currentStepOrder} · {claim.approval.currentApproverRole || claim.approval.currentApproverName}</p>
        )}
        <div>
          <label htmlFor="dec-comments" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{reject ? 'Reason (shown to the employee)' : 'Comment (optional)'}</label>
          <textarea id="dec-comments" value={comments} onChange={(e) => setComments(e.target.value)} maxLength={1000} rows={3} className="input w-full" />
        </div>
        <ViolationList message={error} violations={[]} />
      </div>
    </Modal>
  );
}

// ── Claim detail (drawer-style section) ───────────────────────────────────────

function ClaimDetail({ claim, onBack, onChanged, canPayroll }: { claim: ExpenseClaim; onBack: () => void; onChanged: (c: ExpenseClaim) => void; canPayroll: boolean }) {
  const toast = useAppToast();
  const [decision, setDecision] = useState<'Approve' | 'Reject' | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const download = async (lineId: string, fileName: string) => {
    try { openBlob(await expensesApi.downloadReceipt(claim.id, lineId), fileName); }
    catch (err) { setError(expenseError(err, 'The receipt could not be downloaded.').message); }
  };

  const unschedule = async () => {
    setBusy(true); setError(null);
    try { onChanged(await expensesApi.unschedule(claim.id)); toast.success('Removed from the payroll run.'); }
    catch (err) { setError(expenseError(err, 'The claim could not be removed from the run.').message); }
    finally { setBusy(false); }
  };

  return (
    <div className="space-y-4">
      <button type="button" onClick={onBack} className="inline-flex items-center gap-1 text-xs font-medium text-slate-500 hover:underline dark:text-slate-400">
        <ArrowLeft className="h-3.5 w-3.5" /> Back
      </button>
      <section className="rounded-xl border border-slate-100 bg-white p-5 dark:border-white/[0.07] dark:bg-white/[0.03]">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">{claim.claimNumber}</p>
            <h2 className="text-lg font-bold text-slate-900 dark:text-white">{claim.title}</h2>
            <p className="text-sm text-slate-600 dark:text-slate-300">{claim.employeeName} <span className="text-slate-400">· #{claim.employeeId}</span></p>
            <div className="mt-1 flex flex-wrap items-center gap-2">
              <ExpenseStatusBadge status={claim.status} />
              <span className="text-xs text-slate-500 dark:text-slate-400">{formatMoney(claim.totalAmount, claim.currency)} · submitted {fmtDate(claim.submittedAtUtc)}</span>
            </div>
            <div className="mt-2"><ExpenseProgressNote claim={claim} /></div>
          </div>
          <div className="flex flex-wrap gap-2">
            {claim.status === 'Submitted' && claim.approval?.canDecide && (
              <>
                <button type="button" onClick={() => setDecision('Reject')} className="btn-secondary inline-flex items-center gap-1 text-sm"><XCircle className="h-4 w-4" /> Reject</button>
                <button type="button" onClick={() => setDecision('Approve')} className="btn-primary inline-flex items-center gap-1 text-sm"><CheckCircle className="h-4 w-4" /> Approve</button>
              </>
            )}
            {claim.status === 'Scheduled' && canPayroll && (
              <button type="button" onClick={unschedule} disabled={busy} className="btn-secondary text-sm disabled:opacity-60">Remove from {claim.payrollPeriod ?? 'run'}</button>
            )}
          </div>
        </div>
        <div className="mt-4"><ExpenseLinesTable claim={claim} onReceipt={download} /></div>
        <div className="mt-3"><ViolationList message={error} violations={[]} /></div>
      </section>
      {decision && (
        <DecisionModal claim={claim} decision={decision} onClose={() => setDecision(null)}
          onDone={(c) => { setDecision(null); onChanged(c); toast.success(decision === 'Approve' ? 'Approved.' : 'Rejected.'); }} />
      )}
    </div>
  );
}

// ── Approvals tab ─────────────────────────────────────────────────────────────

function ApprovalsTab({ onOpen }: { onOpen: (c: ExpenseClaim) => void }) {
  const toast = useAppToast();
  const [items, setItems] = useState<ExpenseClaim[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [decision, setDecision] = useState<{ claim: ExpenseClaim; decision: 'Approve' | 'Reject' } | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try { setItems(await expensesApi.approvals()); }
    catch (err) { setItems([]); setError(expenseError(err, 'Pending approvals could not be loaded.').message); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  if (items === null) return <SkeletonRows />;
  if (error) return <div role="alert" className="rounded-xl border border-rose-200 bg-rose-50 p-4 text-sm text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/[0.08] dark:text-rose-300">{error} <button type="button" onClick={() => void load()} className="underline">Retry</button></div>;
  if (items.length === 0) {
    return (
      <div className="rounded-xl border border-dashed border-slate-200 p-10 text-center dark:border-white/[0.1]">
        <ClipboardCheck className="mx-auto h-8 w-8 text-slate-300 dark:text-slate-600" />
        <p className="mt-2 text-sm font-medium text-slate-700 dark:text-slate-200">Nothing waiting for you</p>
        <p className="text-xs text-slate-500 dark:text-slate-400">Expense claims routed to you will appear here.</p>
      </div>
    );
  }
  return (
    <>
      <ul className="space-y-2">
        {items.map((c) => (
          <li key={c.id} className="rounded-xl border border-slate-100 bg-white p-4 dark:border-white/[0.07] dark:bg-white/[0.03]">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <button type="button" onClick={() => onOpen(c)} className="min-w-0 text-left">
                <p className="text-sm font-semibold text-slate-900 hover:underline dark:text-white">{c.title}</p>
                <p className="text-[11px] text-slate-400">{c.claimNumber} · {c.employeeName} · submitted {fmtDate(c.submittedAtUtc)}</p>
                <div className="mt-1"><ExpenseProgressNote claim={c} /></div>
              </button>
              <div className="flex items-center gap-2">
                <span className="text-sm font-bold tabular-nums text-slate-900 dark:text-white">{formatMoney(c.totalAmount, c.currency)}</span>
                {c.approval?.canDecide ? (
                  <>
                    <button type="button" onClick={() => setDecision({ claim: c, decision: 'Reject' })} className="btn-secondary inline-flex items-center gap-1 text-xs"><XCircle className="h-3.5 w-3.5" /> Reject</button>
                    <button type="button" onClick={() => setDecision({ claim: c, decision: 'Approve' })} className="btn-primary inline-flex items-center gap-1 text-xs"><CheckCircle className="h-3.5 w-3.5" /> Approve</button>
                  </>
                ) : (
                  <span className="text-[11px] text-slate-400">Waiting for {c.approval?.currentApproverRole || c.approval?.currentApproverName || 'another approver'}</span>
                )}
              </div>
            </div>
          </li>
        ))}
      </ul>
      {decision && (
        <DecisionModal claim={decision.claim} decision={decision.decision} onClose={() => setDecision(null)}
          onDone={() => { setDecision(null); toast.success(decision.decision === 'Approve' ? 'Approved.' : 'Rejected.'); void load(); }} />
      )}
    </>
  );
}

// ── Claims tab ────────────────────────────────────────────────────────────────

function ClaimsTab({ categories, onOpen }: { categories: ExpenseCategory[]; onOpen: (c: ExpenseClaim) => void }) {
  const [query, setQuery] = useState<ExpenseClaimQuery>({ page: 1, pageSize: 25 });
  const [employee, setEmployee] = useState<EmployeeSelection | null>(null);
  const [items, setItems] = useState<ExpenseClaim[] | null>(null);
  const [total, setTotal] = useState(0);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const r = await expensesApi.list({ ...query, employeeId: employee?.intId });
      setItems(r.items); setTotal(r.total);
    } catch (err) { setItems([]); setError(expenseError(err, 'Claims could not be loaded.').message); }
  }, [query, employee]);
  useEffect(() => { void load(); }, [load]);

  const set = (patch: Partial<ExpenseClaimQuery>) => setQuery((q) => ({ ...q, ...patch, page: 1 }));
  const totalPages = Math.max(1, Math.ceil(total / (query.pageSize ?? 25)));
  const sum = useMemo(() => (items ?? []).reduce((s, c) => s + c.totalAmount, 0), [items]);

  return (
    <div className="space-y-3">
      <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-6">
        <div className="lg:col-span-2">
          <label htmlFor="q-search" className="sr-only">Search</label>
          <div className="relative">
            <Search className="pointer-events-none absolute left-2.5 top-2.5 h-3.5 w-3.5 text-slate-400" />
            <input id="q-search" type="text" value={query.search ?? ''} onChange={(e) => set({ search: e.target.value || undefined })} className="input w-full pl-8" placeholder="Claim number, employee, title" />
          </div>
        </div>
        <select aria-label="Status" value={query.status ?? ''} onChange={(e) => set({ status: e.target.value || undefined })} className="select">
          <option value="">Any status</option>
          {EXPENSE_STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
        </select>
        <select aria-label="Category" value={query.categoryCode ?? ''} onChange={(e) => set({ categoryCode: e.target.value || undefined })} className="select">
          <option value="">Any category</option>
          {categories.map((c) => <option key={c.code} value={c.code}>{c.nameEn}</option>)}
        </select>
        <input aria-label="From" type="date" value={query.from ?? ''} onChange={(e) => set({ from: e.target.value || undefined })} className="input" />
        <input aria-label="To" type="date" value={query.to ?? ''} onChange={(e) => set({ to: e.target.value || undefined })} className="input" />
        <div className="lg:col-span-3"><EmployeeSearchSelect value={employee} onChange={(e) => { setEmployee(e); setQuery((q) => ({ ...q, page: 1 })); }} placeholder="Filter by employee…" /></div>
        <div className="flex items-center gap-2 lg:col-span-3 lg:justify-end">
          <span className="text-xs text-slate-500 dark:text-slate-400">{total} claim{total === 1 ? '' : 's'}{items && items.length > 0 ? ` · page total ${formatMoney(sum, items[0].currency)}` : ''}</span>
          <button type="button" onClick={() => void load()} className="btn-secondary inline-flex items-center gap-1 text-xs"><RefreshCw className="h-3.5 w-3.5" /> Refresh</button>
        </div>
      </div>

      {items === null ? <SkeletonRows /> : error ? (
        <div role="alert" className="rounded-xl border border-rose-200 bg-rose-50 p-4 text-sm text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/[0.08] dark:text-rose-300">{error}</div>
      ) : items.length === 0 ? (
        <div className="rounded-xl border border-dashed border-slate-200 p-10 text-center text-sm text-slate-500 dark:border-white/[0.1] dark:text-slate-400">No claims match these filters.</div>
      ) : (
        <div className="overflow-x-auto rounded-xl border border-slate-100 dark:border-white/[0.07]">
          <table className="w-full text-sm">
            <thead className="bg-slate-50 text-left text-[11px] uppercase tracking-wide text-slate-400 dark:bg-white/[0.03]">
              <tr>
                <th className="px-3 py-2 font-semibold">Claim</th>
                <th className="px-3 py-2 font-semibold">Employee</th>
                <th className="px-3 py-2 font-semibold">Submitted</th>
                <th className="px-3 py-2 font-semibold">Status</th>
                <th className="px-3 py-2 font-semibold">Payroll</th>
                <th className="px-3 py-2 text-right font-semibold">Amount</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
              {items.map((c) => (
                <tr key={c.id} className="cursor-pointer hover:bg-slate-50 dark:hover:bg-white/[0.03]" onClick={() => onOpen(c)}>
                  <td className="px-3 py-2">
                    <p className="font-medium text-slate-900 dark:text-white">{c.title}</p>
                    <p className="text-[11px] text-slate-400">{c.claimNumber} · {c.lines.length} line{c.lines.length === 1 ? '' : 's'}</p>
                  </td>
                  <td className="px-3 py-2 text-slate-700 dark:text-slate-200">{c.employeeName}</td>
                  <td className="px-3 py-2 text-slate-500 dark:text-slate-400">{fmtDate(c.submittedAtUtc)}</td>
                  <td className="px-3 py-2"><ExpenseStatusBadge status={c.status} /></td>
                  <td className="px-3 py-2 text-slate-500 dark:text-slate-400">{c.payrollPeriod ?? '—'}</td>
                  <td className="px-3 py-2 text-right font-semibold tabular-nums text-slate-900 dark:text-white">{formatMoney(c.totalAmount, c.currency)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {totalPages > 1 && (
        <div className="flex items-center justify-end gap-2 text-xs text-slate-500 dark:text-slate-400">
          <button type="button" disabled={(query.page ?? 1) <= 1} onClick={() => setQuery((q) => ({ ...q, page: (q.page ?? 1) - 1 }))} className="btn-secondary disabled:opacity-50">Previous</button>
          <span>Page {query.page ?? 1} of {totalPages}</span>
          <button type="button" disabled={(query.page ?? 1) >= totalPages} onClick={() => setQuery((q) => ({ ...q, page: (q.page ?? 1) + 1 }))} className="btn-secondary disabled:opacity-50">Next</button>
        </div>
      )}
    </div>
  );
}

// ── Payroll tab ───────────────────────────────────────────────────────────────

function PayrollTab({ canPayroll, onOpen }: { canPayroll: boolean; onOpen: (c: ExpenseClaim) => void }) {
  const toast = useAppToast();
  const [runs, setRuns] = useState<PayrollRun[] | null>(null);
  const [runId, setRunId] = useState('');
  const [approved, setApproved] = useState<ExpenseClaim[] | null>(null);
  const [scheduled, setScheduled] = useState<ExpenseClaim[] | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<ExpensePayoutResult | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const [r, a] = await Promise.all([
        payrollApi.listRuns({ status: 'Draft', page: 1, pageSize: 50 }),
        expensesApi.list({ status: 'Approved', page: 1, pageSize: 100 }),
      ]);
      const open = r.items.filter((x) => x.status === 'Draft' && !x.lockedAtUtc);
      setRuns(open); setApproved(a.items);
      if (!runId && open.length === 1) setRunId(open[0].id);
    } catch (err) { setRuns([]); setApproved([]); setError(expenseError(err, 'Open payroll runs could not be loaded.').message); }
  }, [runId]);
  useEffect(() => { void load(); }, [load]);

  useEffect(() => {
    if (!runId) { setScheduled(null); return; }
    expensesApi.list({ status: 'Scheduled', payrollRunId: runId, page: 1, pageSize: 100 }).then((r) => setScheduled(r.items)).catch(() => setScheduled([]));
  }, [runId, result]);

  const run = runs?.find((x) => x.id === runId) ?? null;
  const candidates = useMemo(() => (approved ?? []).filter((c) => !run || !run.companyId || c.companyId === run.companyId), [approved, run]);

  const schedule = async (ids?: string[]) => {
    if (!runId) return;
    setBusy(true); setError(null); setResult(null);
    try {
      const r = await expensesApi.schedule(runId, ids);
      setResult(r);
      toast.success(`${r.scheduled} claim${r.scheduled === 1 ? '' : 's'} added to the run${r.skipped ? `, ${r.skipped} skipped` : ''}.`);
      setSelected(new Set());
      await load();
    } catch (err) { setError(expenseError(err, 'The claims could not be added to the run.').message); }
    finally { setBusy(false); }
  };

  const unschedule = async (id: string) => {
    setBusy(true); setError(null);
    try { await expensesApi.unschedule(id); setResult(null); await load(); setScheduled((s) => (s ?? []).filter((c) => c.id !== id)); toast.success('Removed from the run.'); }
    catch (err) { setError(expenseError(err, 'The claim could not be removed.').message); }
    finally { setBusy(false); }
  };

  if (!canPayroll) {
    return <div className="rounded-xl border border-dashed border-slate-200 p-10 text-center text-sm text-slate-500 dark:border-white/[0.1] dark:text-slate-400">Only payroll roles can add expense claims to a payroll run.</div>;
  }
  if (runs === null || approved === null) return <SkeletonRows />;

  return (
    <div className="space-y-4">
      <div className="rounded-xl border border-slate-100 bg-white p-4 dark:border-white/[0.07] dark:bg-white/[0.03]">
        <p className="text-xs text-slate-500 dark:text-slate-400">
          An approved claim is paid as one <span className="font-semibold">Expense Reimbursement</span> earning on the employee&apos;s payslip, in the run you add it to. It is never paid twice: a claim carries exactly one payroll adjustment.
        </p>
        <div className="mt-3 flex flex-wrap items-end gap-3">
          <div>
            <label htmlFor="run-select" className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Open payroll run</label>
            <select id="run-select" value={runId} onChange={(e) => { setRunId(e.target.value); setResult(null); setSelected(new Set()); }} className="select">
              <option value="">Choose a Draft run…</option>
              {runs.map((r) => <option key={r.id} value={r.id}>{r.year}-{String(r.month).padStart(2, '0')} · {r.runType}{r.employeeCount ? ` · ${r.employeeCount} employees` : ''}</option>)}
            </select>
          </div>
          <button type="button" disabled={!runId || busy || candidates.length === 0} onClick={() => schedule()} className="btn-primary disabled:opacity-60">
            {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : null} Add all approved ({candidates.length})
          </button>
          <button type="button" disabled={!runId || busy || selected.size === 0} onClick={() => schedule([...selected])} className="btn-secondary disabled:opacity-60">Add selected ({selected.size})</button>
          <button type="button" onClick={() => void load()} className="btn-secondary inline-flex items-center gap-1 text-xs"><RefreshCw className="h-3.5 w-3.5" /> Refresh</button>
        </div>
        {runs.length === 0 && <p className="mt-2 text-xs text-amber-700 dark:text-amber-300">There is no open (Draft) payroll run. Create one under Payroll first.</p>}
        <div className="mt-2"><ViolationList message={error} violations={[]} /></div>
      </div>

      {result && result.items.some((i) => i.outcome === 'Skipped') && (
        <div className="rounded-xl border border-amber-200 bg-amber-50 p-3 text-xs text-amber-800 dark:border-amber-500/20 dark:bg-amber-500/[0.08] dark:text-amber-200">
          <p className="font-semibold">Skipped</p>
          <ul className="mt-1 list-disc space-y-0.5 pl-4">
            {result.items.filter((i) => i.outcome === 'Skipped').map((i) => <li key={i.claimId}>{i.claimNumber || i.claimId}: {i.reason}</li>)}
          </ul>
        </div>
      )}

      <div className="grid gap-4 lg:grid-cols-2">
        <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
          <div className="border-b border-slate-100 px-4 py-3 text-sm font-semibold text-slate-900 dark:border-white/[0.07] dark:text-white">Approved, not yet in a run ({candidates.length})</div>
          {candidates.length === 0 ? (
            <p className="p-4 text-xs text-slate-500 dark:text-slate-400">Nothing approved is waiting for payroll{run ? ' for this run’s legal entity' : ''}.</p>
          ) : (
            <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
              {candidates.map((c) => (
                <li key={c.id} className="flex items-center gap-3 px-4 py-2 text-sm">
                  <input type="checkbox" aria-label={`Select ${c.claimNumber}`} checked={selected.has(c.id)} onChange={(e) => setSelected((s) => { const n = new Set(s); if (e.target.checked) n.add(c.id); else n.delete(c.id); return n; })} />
                  <button type="button" onClick={() => onOpen(c)} className="min-w-0 flex-1 text-left">
                    <p className="truncate font-medium text-slate-900 hover:underline dark:text-white">{c.employeeName}</p>
                    <p className="text-[11px] text-slate-400">{c.claimNumber} · {c.title}</p>
                  </button>
                  <span className="font-semibold tabular-nums text-slate-900 dark:text-white">{formatMoney(c.totalAmount, c.currency)}</span>
                </li>
              ))}
            </ul>
          )}
        </section>
        <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
          <div className="border-b border-slate-100 px-4 py-3 text-sm font-semibold text-slate-900 dark:border-white/[0.07] dark:text-white">In this run{scheduled ? ` (${scheduled.length})` : ''}</div>
          {!runId ? (
            <p className="p-4 text-xs text-slate-500 dark:text-slate-400">Choose a run to see what it will pay.</p>
          ) : scheduled === null ? <div className="p-4"><SkeletonRows rows={2} /></div> : scheduled.length === 0 ? (
            <p className="p-4 text-xs text-slate-500 dark:text-slate-400">No expense claims in this run yet.</p>
          ) : (
            <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
              {scheduled.map((c) => (
                <li key={c.id} className="flex items-center gap-3 px-4 py-2 text-sm">
                  <button type="button" onClick={() => onOpen(c)} className="min-w-0 flex-1 text-left">
                    <p className="truncate font-medium text-slate-900 hover:underline dark:text-white">{c.employeeName}</p>
                    <p className="text-[11px] text-slate-400">{c.claimNumber} · {c.title}</p>
                  </button>
                  <span className="font-semibold tabular-nums text-slate-900 dark:text-white">{formatMoney(c.totalAmount, c.currency)}</span>
                  <button type="button" disabled={busy} onClick={() => unschedule(c.id)} className="text-xs text-slate-500 hover:text-rose-600 disabled:opacity-50 dark:text-slate-400">Remove</button>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>
    </div>
  );
}

// ── Policy tab ────────────────────────────────────────────────────────────────

function PolicyTab({ categories, canEdit, currency, onSaved }: { categories: ExpenseCategory[]; canEdit: boolean; currency: string | null; onSaved: (c: ExpenseCategory) => void }) {
  const toast = useAppToast();
  const [drafts, setDrafts] = useState<Record<string, { max: string; receipt: string }>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const draft = (c: ExpenseCategory) => drafts[c.code] ?? { max: c.maxAmountPerClaim?.toString() ?? '', receipt: c.receiptRequiredAbove?.toString() ?? '' };

  const save = async (c: ExpenseCategory) => {
    const d = draft(c);
    setBusy(c.code); setError(null);
    try {
      const saved = await expensesApi.updatePolicy(c.code, { maxAmountPerClaim: d.max === '' ? null : Number(d.max), receiptRequiredAbove: d.receipt === '' ? null : Number(d.receipt) });
      onSaved(saved);
      setDrafts((x) => { const n = { ...x }; delete n[c.code]; return n; });
      toast.success(`${c.nameEn} policy saved.`);
    } catch (err) { setError(expenseError(err, 'The policy could not be saved.').message); }
    finally { setBusy(null); }
  };

  if (categories.length === 0) {
    return <div className="rounded-xl border border-dashed border-slate-200 p-10 text-center text-sm text-slate-500 dark:border-white/[0.1] dark:text-slate-400">No expense categories. Add values under Setup → Master Data → Expense Category.</div>;
  }

  return (
    <div className="space-y-3">
      <p className="text-xs text-slate-500 dark:text-slate-400">
        Limits apply per claim and per category (lines of the same category are summed, so a limit cannot be evaded by splitting). Leave a field empty for no limit. Amounts are in {currency ?? 'the payroll currency'}. Categories themselves are managed under Setup → Master Data.
      </p>
      <ViolationList message={error} violations={[]} />
      <div className="overflow-x-auto rounded-xl border border-slate-100 dark:border-white/[0.07]">
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-left text-[11px] uppercase tracking-wide text-slate-400 dark:bg-white/[0.03]">
            <tr>
              <th className="px-3 py-2 font-semibold">Category</th>
              <th className="px-3 py-2 font-semibold">Max per claim</th>
              <th className="px-3 py-2 font-semibold">Receipt required above</th>
              <th className="px-3 py-2" />
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
            {categories.map((c) => {
              const d = draft(c);
              const dirty = d.max !== (c.maxAmountPerClaim?.toString() ?? '') || d.receipt !== (c.receiptRequiredAbove?.toString() ?? '');
              return (
                <tr key={c.code}>
                  <td className="px-3 py-2">
                    <p className="font-medium text-slate-900 dark:text-white">{c.nameEn}</p>
                    <p className="text-[11px] text-slate-400">{c.code}{c.isActive ? '' : ' · inactive'}</p>
                  </td>
                  <td className="px-3 py-2">
                    <input aria-label={`${c.nameEn} max per claim`} type="number" min={0} step="0.01" disabled={!canEdit} value={d.max}
                      onChange={(e) => setDrafts((x) => ({ ...x, [c.code]: { ...d, max: e.target.value } }))} className="input w-36" placeholder="No limit" />
                  </td>
                  <td className="px-3 py-2">
                    <input aria-label={`${c.nameEn} receipt required above`} type="number" min={0} step="0.01" disabled={!canEdit} value={d.receipt}
                      onChange={(e) => setDrafts((x) => ({ ...x, [c.code]: { ...d, receipt: e.target.value } }))} className="input w-36" placeholder="Optional" />
                  </td>
                  <td className="px-3 py-2 text-right">
                    {canEdit && <button type="button" disabled={!dirty || busy === c.code} onClick={() => save(c)} className="btn-primary text-xs disabled:opacity-50">{busy === c.code ? 'Saving…' : 'Save'}</button>}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

// ── Page ──────────────────────────────────────────────────────────────────────

export function ExpensesPage() {
  const router = useRouter();
  const { hasRole } = useAuth();
  const toast = useAppToast();
  const [tab, setTab] = useState<Tab>('approvals');
  const [categories, setCategories] = useState<ExpenseCategory[]>([]);
  const [selected, setSelected] = useState<ExpenseClaim | null>(null);
  const canPayroll = PAYROLL_ROLES.some(hasRole);
  const canPolicy = POLICY_ROLES.some(hasRole);
  const currency = selected?.currency ?? null;

  useEffect(() => {
    expensesApi.categories(true).then(setCategories).catch(() => setCategories([]));
  }, []);

  const open = async (c: ExpenseClaim) => {
    try { setSelected(await expensesApi.get(c.id)); }
    catch (err) { toast.error(expenseError(err, 'The claim could not be opened.').message); }
  };

  return (
    <div className="mx-auto max-w-[1400px] space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="flex items-center gap-2 text-xl font-bold text-slate-900 dark:text-white"><Receipt className="h-5 w-5" /> Expense claims</h1>
          <p className="text-xs text-slate-500 dark:text-slate-400">Approve employee expenses and pay them through payroll. Single currency per legal entity.</p>
        </div>
        <button type="button" onClick={() => router.push('/reports?report=finance.expense-claims')} className="btn-secondary text-xs">Open report</button>
      </div>

      {selected ? (
        <ClaimDetail claim={selected} canPayroll={canPayroll} onBack={() => setSelected(null)} onChanged={setSelected} />
      ) : (
        <>
          <div className="flex flex-wrap gap-1 border-b border-slate-200 dark:border-white/[0.08]" role="tablist">
            {TABS.map((t) => (
              <button key={t.id} type="button" role="tab" aria-selected={tab === t.id} onClick={() => setTab(t.id)}
                className={`inline-flex items-center gap-1.5 border-b-2 px-3 py-2 text-sm font-medium transition ${tab === t.id ? 'border-sapphire text-sapphire dark:border-cyanAccent dark:text-cyanAccent' : 'border-transparent text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200'}`}>
                <t.icon className="h-4 w-4" /> {t.label}
              </button>
            ))}
          </div>
          {tab === 'approvals' && <ApprovalsTab onOpen={open} />}
          {tab === 'claims' && <ClaimsTab categories={categories} onOpen={open} />}
          {tab === 'payroll' && <PayrollTab canPayroll={canPayroll} onOpen={open} />}
          {tab === 'policy' && <PolicyTab categories={categories} canEdit={canPolicy} currency={currency} onSaved={(c) => setCategories((cs) => cs.map((x) => (x.code === c.code ? c : x)))} />}
        </>
      )}
    </div>
  );
}
