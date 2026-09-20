'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { useRouter } from 'next/navigation';
import { ArrowLeft, Loader2, Paperclip, Plus, Receipt, RefreshCw, Send, Trash2, Upload, X } from 'lucide-react';
import {
  EXPENSE_STATUSES, essExpensesApi, expenseError, formatMoney, openBlob,
  type EssExpenseConfig, type ExpenseClaim, type ExpenseClaimStatus, type ExpenseLineInput, type ExpenseViolation,
} from '../../api/expenses';
import { Modal } from '../../components/Modal';
import { useAppToast } from '../../components/ui/AppToast';
import { ExpenseLinesTable, ExpenseProgressNote, ExpenseStatusBadge, SkeletonRows, ViolationList } from './ExpenseShared';

// W2-B — "My expenses": the employee's own claims only. The server pins every call to the caller's
// employee record, so this page never sends an employee id.

type DraftLine = ExpenseLineInput & { key: string };

function today() {
  return new Date().toISOString().slice(0, 10);
}

function newLine(categoryCode = ''): DraftLine {
  return { key: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`, id: null, expenseDate: today(), categoryCode, amount: 0, description: '' };
}

function policyHint(config: EssExpenseConfig | null, code: string): string | null {
  const c = config?.categories.find((x) => x.code === code);
  if (!c) return null;
  const parts: string[] = [];
  if (c.maxAmountPerClaim != null) parts.push(`max ${formatMoney(c.maxAmountPerClaim, config?.currency)} per claim`);
  if (c.receiptRequiredAbove != null) parts.push(`receipt needed above ${formatMoney(c.receiptRequiredAbove, config?.currency)}`);
  return parts.length ? parts.join(' · ') : null;
}

// ── Draft editor ──────────────────────────────────────────────────────────────

function ClaimEditor({
  config, claim, onClose, onSaved,
}: {
  config: EssExpenseConfig;
  claim: ExpenseClaim | null;
  onClose: () => void;
  onSaved: (claim: ExpenseClaim) => void;
}) {
  const defaultCategory = config.categories[0]?.code ?? '';
  const [title, setTitle] = useState(claim?.title ?? '');
  const [lines, setLines] = useState<DraftLine[]>(() =>
    claim
      ? claim.lines.map((l) => ({ key: l.id, id: l.id, expenseDate: l.expenseDate, categoryCode: l.categoryCode, amount: l.amount, description: l.description }))
      : [newLine(defaultCategory)]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [violations, setViolations] = useState<ExpenseViolation[]>([]);

  const total = useMemo(() => lines.reduce((s, l) => s + (Number.isFinite(l.amount) ? l.amount : 0), 0), [lines]);

  const update = (key: string, patch: Partial<DraftLine>) => setLines((ls) => ls.map((l) => (l.key === key ? { ...l, ...patch } : l)));

  const save = async () => {
    setSaving(true); setError(null); setViolations([]);
    try {
      const body = { title: title.trim() || null, lines: lines.map(({ key: _k, ...l }) => ({ ...l, amount: Number(l.amount) })) };
      const saved = claim ? await essExpensesApi.update(claim.id, body) : await essExpensesApi.create(body);
      onSaved(saved);
    } catch (err) {
      const e = expenseError(err, 'The claim could not be saved.');
      setError(e.message); setViolations(e.violations);
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal isOpen title={claim ? `Edit ${claim.claimNumber}` : 'New expense claim'} onClose={onClose} size="lg"
      footer={(
        <div className="flex items-center justify-between gap-3">
          <p className="text-xs text-slate-500 dark:text-slate-400">
            Total <span className="font-semibold text-slate-800 dark:text-slate-100">{formatMoney(total, config.currency)}</span>
          </p>
          <div className="flex gap-2">
            <button type="button" onClick={onClose} className="btn-secondary">Cancel</button>
            <button type="button" onClick={save} disabled={saving || lines.length === 0} className="btn-primary disabled:opacity-60">
              {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : null} {claim ? 'Save changes' : 'Save draft'}
            </button>
          </div>
        </div>
      )}>
      <div className="space-y-4">
        <p className="rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-600 dark:bg-white/[0.04] dark:text-slate-300">
          All amounts are in <span className="font-semibold">{config.currency ?? 'your payroll currency'}</span>. Expenses in another currency must be converted before they are entered; multi-currency claims are not supported.
        </p>
        <div>
          <label htmlFor="exp-title" className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">Title (optional)</label>
          <input id="exp-title" type="text" value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} className="input w-full" placeholder="e.g. Riyadh client visit, 12–14 May" />
        </div>
        <div className="space-y-3">
          {lines.map((l, i) => {
            const hint = policyHint(config, l.categoryCode);
            return (
              <div key={l.key} className="rounded-xl border border-slate-200 p-3 dark:border-white/[0.08]">
                <div className="mb-2 flex items-center justify-between">
                  <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">Line {i + 1}</p>
                  <button type="button" onClick={() => setLines((ls) => ls.filter((x) => x.key !== l.key))} disabled={lines.length === 1}
                    className="inline-flex items-center gap-1 text-xs text-slate-500 hover:text-rose-600 disabled:opacity-40 dark:text-slate-400" aria-label={`Remove line ${i + 1}`}>
                    <Trash2 className="h-3.5 w-3.5" /> Remove
                  </button>
                </div>
                <div className="grid gap-3 sm:grid-cols-3">
                  <div>
                    <label htmlFor={`d-${l.key}`} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Date</label>
                    <input id={`d-${l.key}`} type="date" max={today()} value={l.expenseDate} onChange={(e) => update(l.key, { expenseDate: e.target.value })} className="input w-full" />
                  </div>
                  <div>
                    <label htmlFor={`c-${l.key}`} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Category</label>
                    <select id={`c-${l.key}`} value={l.categoryCode} onChange={(e) => update(l.key, { categoryCode: e.target.value })} className="select w-full">
                      <option value="">Choose…</option>
                      {config.categories.map((c) => <option key={c.code} value={c.code}>{c.nameEn}</option>)}
                    </select>
                  </div>
                  <div>
                    <label htmlFor={`a-${l.key}`} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Amount ({config.currency ?? '—'})</label>
                    <input id={`a-${l.key}`} type="number" min={0} step="0.01" value={l.amount || ''} onChange={(e) => update(l.key, { amount: Number(e.target.value) })} className="input w-full" />
                  </div>
                </div>
                <div className="mt-3">
                  <label htmlFor={`n-${l.key}`} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">What was it for?</label>
                  <input id={`n-${l.key}`} type="text" maxLength={500} value={l.description} onChange={(e) => update(l.key, { description: e.target.value })} className="input w-full" placeholder="Taxi from airport to hotel" />
                </div>
                {hint && <p className="mt-2 text-[11px] text-slate-500 dark:text-slate-400">Policy: {hint}.</p>}
              </div>
            );
          })}
          <button type="button" onClick={() => setLines((ls) => [...ls, newLine(defaultCategory)])} disabled={lines.length >= 50} className="btn-secondary inline-flex items-center gap-1 text-xs">
            <Plus className="h-3.5 w-3.5" /> Add line
          </button>
        </div>
        <ViolationList message={error} violations={violations} />
        {!claim && <p className="text-[11px] text-slate-400">Receipts are attached after the draft is saved.</p>}
      </div>
    </Modal>
  );
}

// ── Detail ────────────────────────────────────────────────────────────────────

function ClaimDetail({
  claim, config, onBack, onChanged,
}: {
  claim: ExpenseClaim;
  config: EssExpenseConfig | null;
  onBack: () => void;
  onChanged: (claim: ExpenseClaim | null) => void;
}) {
  const toast = useAppToast();
  const [editing, setEditing] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [violations, setViolations] = useState<ExpenseViolation[]>([]);
  const isDraft = claim.status === 'Draft';

  const run = async (label: string, fn: () => Promise<ExpenseClaim>, success?: string) => {
    setBusy(label); setError(null); setViolations([]);
    try {
      const next = await fn();
      onChanged(next);
      if (success) toast.success(success);
    } catch (err) {
      const e = expenseError(err, 'That did not work. Please try again.');
      setError(e.message); setViolations(e.violations);
    } finally {
      setBusy(null);
    }
  };

  const upload = (lineId: string, file: File) => {
    if (config && file.size > config.maxReceiptBytes) { setError('Receipts can be at most 10 MB.'); setViolations([]); return; }
    void run(`upload-${lineId}`, () => essExpensesApi.uploadReceipt(claim.id, lineId, file), 'Receipt attached.');
  };

  const download = async (lineId: string, fileName: string) => {
    try { openBlob(await essExpensesApi.downloadReceipt(claim.id, lineId), fileName); }
    catch (err) { setError(expenseError(err, 'The receipt could not be downloaded.').message); }
  };

  return (
    <div className="space-y-4">
      <button type="button" onClick={onBack} className="inline-flex items-center gap-1 text-xs font-medium text-slate-500 hover:underline dark:text-slate-400">
        <ArrowLeft className="h-3.5 w-3.5" /> All my expenses
      </button>
      <section className="rounded-xl border border-slate-100 bg-white p-5 dark:border-white/[0.07] dark:bg-white/[0.03]">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">{claim.claimNumber}</p>
            <h2 className="text-lg font-bold text-slate-900 dark:text-white">{claim.title}</h2>
            <div className="mt-1 flex flex-wrap items-center gap-2">
              <ExpenseStatusBadge status={claim.status} />
              <span className="text-xs text-slate-500 dark:text-slate-400">{formatMoney(claim.totalAmount, claim.currency)}</span>
            </div>
            <div className="mt-2"><ExpenseProgressNote claim={claim} /></div>
          </div>
          {isDraft && (
            <div className="flex flex-wrap gap-2">
              <button type="button" onClick={() => setEditing(true)} className="btn-secondary text-sm">Edit lines</button>
              <button type="button" onClick={() => run('cancel', () => essExpensesApi.cancel(claim.id), 'Draft cancelled.')} disabled={busy !== null} className="btn-secondary text-sm disabled:opacity-60">
                <X className="h-3.5 w-3.5" /> Cancel draft
              </button>
              <button type="button" onClick={() => run('submit', () => essExpensesApi.submit(claim.id), 'Submitted for approval.')} disabled={busy !== null} className="btn-primary text-sm disabled:opacity-60">
                {busy === 'submit' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />} Submit for approval
              </button>
            </div>
          )}
        </div>
        <div className="mt-4">
          <ExpenseLinesTable
            claim={claim}
            onReceipt={download}
            renderReceiptAction={isDraft ? (lineId) => (
              <label className="inline-flex cursor-pointer items-center gap-1 text-[11px] text-blue-600 hover:underline dark:text-blue-400">
                {busy === `upload-${lineId}` ? <Loader2 className="h-3 w-3 animate-spin" /> : <Upload className="h-3 w-3" />}
                {claim.lines.find((l) => l.id === lineId)?.hasReceipt ? 'Replace' : 'Attach receipt'}
                <input type="file" className="sr-only" accept={(config?.receiptContentTypes ?? []).join(',')}
                  onChange={(e) => { const f = e.target.files?.[0]; if (f) upload(lineId, f); e.target.value = ''; }} />
              </label>
            ) : undefined}
          />
        </div>
        <div className="mt-3"><ViolationList message={error} violations={violations} /></div>
        {isDraft && (
          <p className="mt-3 text-[11px] text-slate-400">
            Receipts: PDF, JPEG, PNG, WEBP or HEIC, up to 10 MB each. Categories with a receipt threshold block submission until a receipt is attached.
          </p>
        )}
      </section>
      {editing && config && (
        <ClaimEditor config={config} claim={claim} onClose={() => setEditing(false)} onSaved={(c) => { setEditing(false); onChanged(c); toast.success('Draft saved.'); }} />
      )}
    </div>
  );
}

// ── Page ──────────────────────────────────────────────────────────────────────

export function MyExpensesPage() {
  const router = useRouter();
  const toast = useAppToast();
  const [config, setConfig] = useState<EssExpenseConfig | null>(null);
  const [configError, setConfigError] = useState<string | null>(null);
  const [claims, setClaims] = useState<ExpenseClaim[] | null>(null);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [status, setStatus] = useState<ExpenseClaimStatus | ''>('');
  const [listError, setListError] = useState<string | null>(null);
  const [selected, setSelected] = useState<ExpenseClaim | null>(null);
  const [creating, setCreating] = useState(false);
  const pageSize = 20;

  useEffect(() => {
    essExpensesApi.config().then(setConfig).catch((err) => setConfigError(expenseError(err, 'Expense settings could not be loaded.').message));
  }, []);

  const load = useCallback(async () => {
    setListError(null);
    try {
      const r = await essExpensesApi.list({ status: status || undefined, page, pageSize });
      setClaims(r.items); setTotal(r.total);
    } catch (err) {
      setClaims([]); setListError(expenseError(err, 'Your expenses could not be loaded.').message);
    }
  }, [status, page]);

  useEffect(() => { void load(); }, [load]);

  const openClaim = async (id: string) => {
    try { setSelected(await essExpensesApi.get(id)); }
    catch (err) { toast.error(expenseError(err, 'The claim could not be opened.').message); }
  };

  const onChanged = (c: ExpenseClaim | null) => {
    setSelected(c);
    void load();
  };

  if (selected) {
    return (
      <div className="mx-auto max-w-[1100px] space-y-4">
        <ClaimDetail claim={selected} config={config} onBack={() => setSelected(null)} onChanged={onChanged} />
      </div>
    );
  }

  const totalPages = Math.max(1, Math.ceil(total / pageSize));

  return (
    <div className="mx-auto max-w-[1100px] space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <button type="button" onClick={() => router.push('/ess')} className="inline-flex items-center gap-1 text-xs font-medium text-slate-500 hover:underline dark:text-slate-400">
            <ArrowLeft className="h-3.5 w-3.5" /> Self-service
          </button>
          <h1 className="mt-1 flex items-center gap-2 text-xl font-bold text-slate-900 dark:text-white"><Receipt className="h-5 w-5" /> My expenses</h1>
          <p className="text-xs text-slate-500 dark:text-slate-400">
            Claim back business expenses you paid yourself. Approved claims are paid with your salary in the next payroll run
            {config?.currency ? <>, in <span className="font-semibold">{config.currency}</span> only</> : null}.
          </p>
        </div>
        <button type="button" onClick={() => setCreating(true)} disabled={!config} className="btn-primary inline-flex items-center gap-1.5 disabled:opacity-60">
          <Plus className="h-4 w-4" /> New claim
        </button>
      </div>

      {configError && (
        <div role="alert" className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-500/20 dark:bg-amber-500/[0.08] dark:text-amber-200">
          {configError} You can still view existing claims.
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2">
        <label htmlFor="exp-status" className="text-xs font-medium text-slate-600 dark:text-slate-400">Status</label>
        <select id="exp-status" value={status} onChange={(e) => { setPage(1); setStatus(e.target.value as ExpenseClaimStatus | ''); }} className="select">
          <option value="">All</option>
          {EXPENSE_STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
        </select>
        <button type="button" onClick={() => void load()} className="btn-secondary inline-flex items-center gap-1 text-xs" aria-label="Refresh">
          <RefreshCw className="h-3.5 w-3.5" /> Refresh
        </button>
      </div>

      {claims === null ? (
        <SkeletonRows />
      ) : listError ? (
        <div role="alert" className="rounded-xl border border-rose-200 bg-rose-50 p-4 text-sm text-rose-700 dark:border-rose-500/20 dark:bg-rose-500/[0.08] dark:text-rose-300">
          {listError} <button type="button" onClick={() => void load()} className="underline">Try again</button>
        </div>
      ) : claims.length === 0 ? (
        <div className="rounded-xl border border-dashed border-slate-200 p-10 text-center dark:border-white/[0.1]">
          <Receipt className="mx-auto h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="mt-2 text-sm font-medium text-slate-700 dark:text-slate-200">No expense claims{status ? ` with status ${status}` : ' yet'}</p>
          <p className="text-xs text-slate-500 dark:text-slate-400">Create a claim, attach your receipts, and submit it for approval.</p>
        </div>
      ) : (
        <ul className="space-y-2">
          {claims.map((c) => (
            <li key={c.id}>
              <button type="button" onClick={() => void openClaim(c.id)}
                className="w-full rounded-xl border border-slate-100 bg-white p-4 text-left transition hover:shadow-md dark:border-white/[0.07] dark:bg-white/[0.03]">
                <div className="flex flex-wrap items-start justify-between gap-2">
                  <div className="min-w-0">
                    <p className="truncate text-sm font-semibold text-slate-900 dark:text-white">{c.title}</p>
                    <p className="text-[11px] text-slate-400">
                      {c.claimNumber} · {c.lines.length} line{c.lines.length === 1 ? '' : 's'}
                      {c.lines.some((l) => l.hasReceipt) && <> · <Paperclip className="inline h-3 w-3" /> {c.lines.filter((l) => l.hasReceipt).length} receipt{c.lines.filter((l) => l.hasReceipt).length === 1 ? '' : 's'}</>}
                    </p>
                    <div className="mt-1"><ExpenseProgressNote claim={c} /></div>
                  </div>
                  <div className="text-right">
                    <p className="text-sm font-bold tabular-nums text-slate-900 dark:text-white">{formatMoney(c.totalAmount, c.currency)}</p>
                    <div className="mt-1"><ExpenseStatusBadge status={c.status} /></div>
                  </div>
                </div>
              </button>
            </li>
          ))}
        </ul>
      )}

      {totalPages > 1 && (
        <div className="flex items-center justify-end gap-2 text-xs text-slate-500 dark:text-slate-400">
          <button type="button" disabled={page <= 1} onClick={() => setPage((p) => p - 1)} className="btn-secondary disabled:opacity-50">Previous</button>
          <span>Page {page} of {totalPages}</span>
          <button type="button" disabled={page >= totalPages} onClick={() => setPage((p) => p + 1)} className="btn-secondary disabled:opacity-50">Next</button>
        </div>
      )}

      {creating && config && (
        <ClaimEditor config={config} claim={null} onClose={() => setCreating(false)}
          onSaved={(c) => { setCreating(false); toast.success('Draft saved. Attach receipts, then submit.'); setSelected(c); void load(); }} />
      )}
    </div>
  );
}
