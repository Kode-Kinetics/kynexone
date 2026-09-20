'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Package, Plus, Search, RefreshCw, ArrowRightLeft, Undo2, Wrench, Archive,
  AlertTriangle, CalendarClock, Pencil, ChevronLeft, ChevronRight, Loader2, History,
} from 'lucide-react';
import {
  assetsApi, ASSET_STATUSES, ASSET_STATUS_LABEL, ASSET_STATUS_TONE, ASSIGNMENT_STATUS_LABEL,
  type AssetDetail, type AssetListItem, type AssetLookups, type AssetSummary, type AssetUpsert, type AssetStatus,
} from '../api/assets';
import { Modal } from '../components/Modal';
import { StatusChip } from '../components/StatusChip';
import { EmployeeSearchSelect, type EmployeeSelection } from '../components/EmployeeSearchSelect';
import { useAppToast } from '../components/ui/AppToast';
import { parseApiError } from '../hooks/useApiCall';
import { useAuth } from '../contexts/AuthContext';

// ── helpers ───────────────────────────────────────────────────────────────────

function fmtDate(s: string | null | undefined) {
  if (!s) return '—';
  const d = new Date(s);
  return Number.isNaN(d.getTime()) ? s : d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
}
function fmtDateTime(s: string | null | undefined) {
  if (!s) return '—';
  const d = new Date(s);
  return Number.isNaN(d.getTime()) ? s : d.toLocaleString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
}
function fmtMoney(amount: number | null, currency: string) {
  if (amount === null || amount === undefined) return '—';
  return `${currency ? `${currency} ` : ''}${amount.toLocaleString('en', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}
function labelOf(code: string, lookups: AssetLookups | null, kind: 'categories' | 'conditions') {
  if (!code) return '—';
  return lookups?.[kind].find(v => v.code === code)?.label ?? code;
}
const ACTION_LABEL: Record<string, string> = {
  'asset.created': 'Registered',
  'asset.updated': 'Details updated',
  'asset.issued': 'Issued',
  'asset.returned': 'Returned',
  'asset.transferred': 'Transferred',
  'asset.write_off_requested': 'Write-off requested',
  'asset.write_off_rejected': 'Write-off rejected',
  'asset.written_off': 'Written off',
  'asset.retired': 'Retired',
  'asset.sent_to_repair': 'Sent to repair',
  'asset.repaired': 'Back from repair',
  'asset.return_date_changed': 'Return date changed',
};

const PAGE_SIZE = 25;

type Dialog =
  | { kind: 'create' }
  | { kind: 'edit'; asset: AssetListItem }
  | { kind: 'issue' | 'return' | 'transfer' | 'writeOff' | 'retire' | 'repair' | 'extend'; asset: AssetListItem };

// ── Page ──────────────────────────────────────────────────────────────────────

export function AssetsPage() {
  const toast = useAppToast();
  const { hasPermission } = useAuth();
  const canWrite = hasPermission('employees.write');

  const [summary, setSummary] = useState<AssetSummary | null>(null);
  const [lookups, setLookups] = useState<AssetLookups | null>(null);
  const [items, setItems] = useState<AssetListItem[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const [status, setStatus] = useState('');
  const [category, setCategory] = useState('');
  const [overdue, setOverdue] = useState(false);
  const [search, setSearch] = useState('');
  const [q, setQ] = useState('');

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [dialog, setDialog] = useState<Dialog | null>(null);
  // Deep link from Offboarding: /assets?employeeId=123 shows what that person holds.
  const [employeeId, setEmployeeId] = useState<number | null>(null);
  const [urlRead, setUrlRead] = useState(false);

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const emp = Number(params.get('employeeId'));
    if (Number.isInteger(emp) && emp > 0) setEmployeeId(emp);
    const initialQ = params.get('q');
    if (initialQ) setSearch(initialQ);
    setUrlRead(true);
  }, []);

  useEffect(() => { const t = setTimeout(() => setQ(search.trim()), 300); return () => clearTimeout(t); }, [search]);
  useEffect(() => { setPage(1); }, [status, category, overdue, q, employeeId]);

  const loadList = useCallback(async () => {
    if (!urlRead) return;
    setLoading(true); setError('');
    try {
      const [list, sum] = await Promise.all([
        assetsApi.list({ status: status || undefined, category: category || undefined, overdue: overdue || undefined, q: q || undefined, employeeId: employeeId ?? undefined, page, pageSize: PAGE_SIZE }),
        assetsApi.summary(),
      ]);
      setItems(list.items); setTotal(list.total); setSummary(sum);
    } catch (e) {
      setError(parseApiError(e));
    } finally { setLoading(false); }
  }, [status, category, overdue, q, employeeId, page, urlRead]);

  useEffect(() => { loadList(); }, [loadList]);
  useEffect(() => { assetsApi.lookups().then(setLookups).catch(() => setLookups(null)); }, []);

  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  const afterWrite = async (message: string) => {
    toast.success(message);
    setDialog(null);
    await loadList();
  };

  return (
    <div className="space-y-5 p-4 sm:p-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">Asset Register</h1>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">Company equipment and who holds it — issue, return, transfer, repair, write off and retire, with the full custody history.</p>
        </div>
        {canWrite && (
          <button type="button" className="btn-primary text-sm" onClick={() => setDialog({ kind: 'create' })}>
            <Plus className="h-3.5 w-3.5" /> Register asset
          </button>
        )}
      </div>

      {/* Summary */}
      {summary && (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <SummaryCard label="Total assets" value={summary.total} tone="bg-slate-100 text-slate-700 dark:bg-white/10 dark:text-slate-200" onClick={() => { setStatus(''); setOverdue(false); }} />
          <SummaryCard label="Assigned" value={summary.byStatus.Assigned ?? 0} tone="bg-sapphire/10 text-sapphire dark:bg-cyanAccent/10 dark:text-cyanAccent" onClick={() => { setStatus('Assigned'); setOverdue(false); }} />
          <SummaryCard label="Overdue returns" value={summary.overdue} tone="bg-rose-50 text-rose-600 dark:bg-rose-500/10 dark:text-rose-400" sub={summary.dueSoon > 0 ? `${summary.dueSoon} due within 3 days` : undefined} onClick={() => { setStatus(''); setOverdue(true); }} />
          <SummaryCard label="Write-offs pending" value={summary.pendingWriteOffs} tone="bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400" sub={`${summary.byStatus.InRepair ?? 0} in repair`} />
        </div>
      )}

      {/* Filters */}
      <div className="surface flex flex-wrap items-center gap-2 p-3">
        <div className="relative min-w-[220px] flex-1">
          <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
          <input className="input w-full pl-8" placeholder="Search tag, serial, name, make/model or holder…" value={search} onChange={e => setSearch(e.target.value)} aria-label="Search assets" />
        </div>
        <select className="select" value={status} onChange={e => setStatus(e.target.value)} aria-label="Filter by status">
          <option value="">All statuses</option>
          {ASSET_STATUSES.map(s => <option key={s} value={s}>{ASSET_STATUS_LABEL[s]}</option>)}
        </select>
        <select className="select" value={category} onChange={e => setCategory(e.target.value)} aria-label="Filter by category">
          <option value="">All categories</option>
          {(lookups?.categories ?? []).map(c => <option key={c.code} value={c.code}>{c.label}</option>)}
        </select>
        <label className="flex cursor-pointer items-center gap-2 text-sm text-slate-600 dark:text-slate-300">
          <input type="checkbox" className="h-4 w-4 rounded border-slate-300" checked={overdue} onChange={e => setOverdue(e.target.checked)} /> Overdue only
        </label>
        {employeeId !== null && (
          <button type="button" className="btn-secondary text-xs" onClick={() => setEmployeeId(null)} title="Clear employee filter">
            Held by employee #{employeeId} ×
          </button>
        )}
        <button type="button" className="btn-secondary" onClick={loadList} aria-label="Refresh"><RefreshCw className="h-4 w-4" /></button>
      </div>

      {/* List */}
      {error ? (
        <div className="rounded-xl border border-rose-200 bg-rose-50 p-4 text-sm text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-200">
          {error} <button type="button" className="ml-2 font-semibold underline" onClick={loadList}>Retry</button>
        </div>
      ) : loading ? (
        <div className="flex justify-center py-12"><div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>
      ) : items.length === 0 ? (
        <div className="surface p-10 text-center text-sm text-slate-400">
          <Package className="mx-auto mb-2 h-8 w-8 text-slate-300 dark:text-slate-600" />
          {q || status || category || overdue || employeeId !== null ? 'No assets match these filters.' : 'No assets registered yet. Click “Register asset” to add the first item.'}
        </div>
      ) : (
        <div className="surface overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="bg-slate-50 text-left text-xs uppercase tracking-wide text-slate-500 dark:bg-white/[0.04] dark:text-slate-400">
                <tr>
                  <th className="px-4 py-2.5">Tag</th>
                  <th className="px-4 py-2.5">Item</th>
                  <th className="px-4 py-2.5">Category</th>
                  <th className="px-4 py-2.5">Status</th>
                  <th className="px-4 py-2.5">Holder</th>
                  <th className="px-4 py-2.5">Due back</th>
                  <th className="px-4 py-2.5">Condition</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                {items.map(a => (
                  <tr key={a.id} className="cursor-pointer hover:bg-slate-50 dark:hover:bg-white/[0.03]" onClick={() => setSelectedId(a.id)}>
                    <td className="px-4 py-2.5 font-mono text-xs font-semibold text-slate-800 dark:text-slate-100">{a.assetTag}</td>
                    <td className="px-4 py-2.5">
                      <div className="font-medium text-slate-800 dark:text-slate-100">{a.name || `${a.make} ${a.model}`.trim() || '—'}</div>
                      <div className="text-xs text-slate-500 dark:text-slate-400">{[a.make, a.model].filter(Boolean).join(' ')}{a.serialNumber ? ` · SN ${a.serialNumber}` : ''}</div>
                    </td>
                    <td className="px-4 py-2.5 text-slate-600 dark:text-slate-300">{labelOf(a.categoryCode, lookups, 'categories')}</td>
                    <td className="px-4 py-2.5">
                      <div className="flex flex-wrap items-center gap-1">
                        <StatusChip label={ASSET_STATUS_LABEL[a.status]} tone={ASSET_STATUS_TONE[a.status]} dot />
                        {a.hasPendingWriteOff && <StatusChip label="Write-off pending" tone="amber" />}
                      </div>
                    </td>
                    <td className="px-4 py-2.5">
                      {a.currentHolder ? (
                        <div>
                          <div className="text-slate-800 dark:text-slate-100">{a.currentHolder.employeeName}</div>
                          <div className="text-xs text-slate-500 dark:text-slate-400">{a.currentHolder.employeeCode} · since {fmtDate(a.currentHolder.issuedOn)}</div>
                        </div>
                      ) : <span className="text-slate-400">—</span>}
                    </td>
                    <td className="px-4 py-2.5">
                      {a.currentHolder?.expectedReturnDate ? (
                        <span className={a.currentHolder.isOverdue ? 'font-semibold text-rose-600 dark:text-rose-400' : 'text-slate-600 dark:text-slate-300'}>
                          {fmtDate(a.currentHolder.expectedReturnDate)}{a.currentHolder.isOverdue ? ' · overdue' : ''}
                        </span>
                      ) : <span className="text-slate-400">—</span>}
                    </td>
                    <td className="px-4 py-2.5 text-slate-600 dark:text-slate-300">{labelOf(a.condition, lookups, 'conditions')}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="flex items-center justify-between border-t border-slate-100 px-4 py-2 text-xs text-slate-500 dark:border-white/[0.06] dark:text-slate-400">
            <span>{total} asset{total === 1 ? '' : 's'} · page {page} of {pages}</span>
            <div className="flex gap-1">
              <button type="button" className="btn-secondary h-7 px-2" disabled={page <= 1} onClick={() => setPage(p => p - 1)} aria-label="Previous page"><ChevronLeft className="h-3.5 w-3.5" /></button>
              <button type="button" className="btn-secondary h-7 px-2" disabled={page >= pages} onClick={() => setPage(p => p + 1)} aria-label="Next page"><ChevronRight className="h-3.5 w-3.5" /></button>
            </div>
          </div>
        </div>
      )}

      {selectedId && (
        <AssetDetailModal
          id={selectedId}
          lookups={lookups}
          canWrite={canWrite}
          onClose={() => setSelectedId(null)}
          onAction={(d) => setDialog(d)}
          refreshKey={`${dialog === null}`}
        />
      )}

      {dialog && (
        <ActionDialog
          dialog={dialog}
          lookups={lookups}
          onClose={() => setDialog(null)}
          onDone={afterWrite}
        />
      )}
    </div>
  );
}

function SummaryCard({ label, value, tone, sub, onClick }: { label: string; value: string | number; tone: string; sub?: string; onClick?: () => void }) {
  return (
    <button type="button" onClick={onClick} className="surface p-4 text-left transition hover:-translate-y-px disabled:cursor-default" disabled={!onClick}>
      <p className="text-xs text-slate-500 dark:text-slate-400">{label}</p>
      <p className={`mt-1 inline-flex rounded-lg px-2 py-0.5 text-xl font-bold ${tone}`}>{value}</p>
      {sub && <p className="mt-1 text-[11px] text-slate-400">{sub}</p>}
    </button>
  );
}

// ── Detail modal ──────────────────────────────────────────────────────────────

function AssetDetailModal({ id, lookups, canWrite, onClose, onAction, refreshKey }: {
  id: string; lookups: AssetLookups | null; canWrite: boolean; onClose: () => void; onAction: (d: Dialog) => void; refreshKey: string;
}) {
  const [detail, setDetail] = useState<AssetDetail | null>(null);
  const [error, setError] = useState('');
  const [tab, setTab] = useState<'history' | 'writeOffs' | 'audit'>('history');

  useEffect(() => {
    let cancelled = false;
    setError('');
    assetsApi.get(id).then(d => { if (!cancelled) setDetail(d); }).catch(e => { if (!cancelled) setError(parseApiError(e)); });
    return () => { cancelled = true; };
  }, [id, refreshKey]);

  const a = detail?.asset;
  const holder = a?.currentHolder ?? null;
  const frozen = a?.hasPendingWriteOff ?? false;

  const actions = useMemo(() => {
    if (!a || !canWrite) return [] as { label: string; icon: typeof Package; dialog: Dialog['kind']; primary?: boolean; disabled?: string }[];
    const pendingNote = frozen ? 'A write-off is awaiting approval' : undefined;
    const list: { label: string; icon: typeof Package; dialog: Dialog['kind']; primary?: boolean; disabled?: string }[] = [];
    if (a.status === 'InStock') {
      list.push({ label: 'Issue', icon: ArrowRightLeft, dialog: 'issue', primary: true, disabled: pendingNote });
      list.push({ label: 'Send to repair', icon: Wrench, dialog: 'repair', disabled: pendingNote });
      list.push({ label: 'Retire', icon: Archive, dialog: 'retire', disabled: pendingNote });
    }
    if (a.status === 'Assigned') {
      list.push({ label: 'Record return', icon: Undo2, dialog: 'return', primary: true, disabled: pendingNote });
      list.push({ label: 'Transfer', icon: ArrowRightLeft, dialog: 'transfer', disabled: pendingNote });
      list.push({ label: 'Change due date', icon: CalendarClock, dialog: 'extend', disabled: pendingNote });
    }
    if (a.status === 'InRepair') {
      list.push({ label: 'Repair complete', icon: Wrench, dialog: 'repair', primary: true, disabled: pendingNote });
      list.push({ label: 'Retire', icon: Archive, dialog: 'retire', disabled: pendingNote });
    }
    if (a.status !== 'Retired' && a.status !== 'Lost') {
      list.push({ label: 'Report lost / damaged', icon: AlertTriangle, dialog: 'writeOff', disabled: pendingNote });
      list.push({ label: 'Edit details', icon: Pencil, dialog: 'edit' });
    }
    return list;
  }, [a, canWrite, frozen]);

  return (
    <Modal isOpen title={a ? `${a.assetTag} — ${a.name || [a.make, a.model].filter(Boolean).join(' ') || 'Asset'}` : 'Asset'} onClose={onClose} size="xl">
      {error ? (
        <div className="rounded-xl border border-rose-200 bg-rose-50 p-4 text-sm text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-200">{error}</div>
      ) : !detail || !a ? (
        <div className="flex justify-center py-10"><Loader2 className="h-5 w-5 animate-spin text-sapphire" /></div>
      ) : (
        <div className="space-y-4">
          <div className="flex flex-wrap items-center gap-2">
            <StatusChip label={ASSET_STATUS_LABEL[a.status]} tone={ASSET_STATUS_TONE[a.status]} dot />
            {a.hasPendingWriteOff && <StatusChip label="Write-off awaiting approval" tone="amber" />}
            {holder?.isOverdue && <StatusChip label="Return overdue" tone="rose" />}
            <span className="ml-auto text-xs text-slate-400">v{a.version}</span>
          </div>

          {actions.length > 0 && (
            <div className="flex flex-wrap gap-2">
              {actions.map(x => (
                <button key={x.dialog} type="button" title={x.disabled}
                  disabled={!!x.disabled}
                  className={`${x.primary ? 'btn-primary' : 'btn-secondary'} text-xs disabled:cursor-not-allowed disabled:opacity-50`}
                  onClick={() => onAction({ kind: x.dialog, asset: a } as Dialog)}>
                  <x.icon className="h-3.5 w-3.5" /> {x.label}
                </button>
              ))}
            </div>
          )}

          <div className="grid gap-4 lg:grid-cols-2">
            <div className="surface p-4">
              <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-400">Item</p>
              <dl className="grid grid-cols-[120px_1fr] gap-y-1.5 text-sm">
                <Field k="Category" v={labelOf(a.categoryCode, lookups, 'categories')} />
                <Field k="Make / model" v={[a.make, a.model].filter(Boolean).join(' ') || '—'} />
                <Field k="Serial number" v={a.serialNumber || '—'} mono />
                <Field k="Condition" v={labelOf(a.condition, lookups, 'conditions')} />
                <Field k="Purchased" v={`${fmtDate(a.purchaseDate)}${a.purchaseCost !== null ? ` · ${fmtMoney(a.purchaseCost, a.currency)}` : ''}`} />
                <Field k="Location" v={a.locationNote || '—'} />
              </dl>
            </div>
            <div className="surface p-4">
              <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-400">Current holder</p>
              {holder ? (
                <dl className="grid grid-cols-[120px_1fr] gap-y-1.5 text-sm">
                  <Field k="Employee" v={`${holder.employeeName} (${holder.employeeCode})`} />
                  <Field k="Issued on" v={fmtDate(holder.issuedOn)} />
                  <Field k="Due back" v={holder.expectedReturnDate ? `${fmtDate(holder.expectedReturnDate)}${holder.isOverdue ? ' — overdue' : ''}` : 'No return date'} tone={holder.isOverdue ? 'text-rose-600 dark:text-rose-400 font-semibold' : undefined} />
                </dl>
              ) : (
                <p className="text-sm text-slate-400">{a.status === 'InStock' ? 'In stock — not issued to anyone.' : a.status === 'InRepair' ? 'With repair — not issued to anyone.' : 'Not held by anyone.'}</p>
              )}
            </div>
          </div>

          <div>
            <div className="mb-2 flex gap-1 border-b border-slate-100 dark:border-white/[0.07]">
              {([['history', `Custody history (${detail.assignments.length})`], ['writeOffs', `Write-offs (${detail.writeOffs.length})`], ['audit', `Audit trail (${detail.auditTrail.length})`]] as const).map(([k, label]) => (
                <button key={k} type="button" onClick={() => setTab(k)}
                  className={`-mb-px border-b-2 px-3 py-2 text-xs font-semibold ${tab === k ? 'border-sapphire text-sapphire dark:border-cyanAccent dark:text-cyanAccent' : 'border-transparent text-slate-500 hover:text-slate-800 dark:text-slate-400 dark:hover:text-slate-200'}`}>
                  {label}
                </button>
              ))}
            </div>
            {tab === 'history' && (
              detail.assignments.length === 0 ? <p className="p-3 text-sm text-slate-400">Never issued.</p> : (
                <div className="overflow-x-auto">
                  <table className="w-full text-xs">
                    <thead className="text-left uppercase tracking-wide text-slate-400"><tr>
                      <th className="px-3 py-2">Holder</th><th className="px-3 py-2">Issued</th><th className="px-3 py-2">Due back</th><th className="px-3 py-2">Closed</th><th className="px-3 py-2">Status</th><th className="px-3 py-2">Condition out → in</th><th className="px-3 py-2">Notes</th>
                    </tr></thead>
                    <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                      {detail.assignments.map(h => (
                        <tr key={h.id}>
                          <td className="px-3 py-2 text-slate-800 dark:text-slate-100">{h.employeeName} <span className="text-slate-400">{h.employeeCode}</span></td>
                          <td className="px-3 py-2 text-slate-600 dark:text-slate-300">{fmtDate(h.issuedOn)}<div className="text-[10px] text-slate-400">by {h.issuedByName || '—'}</div></td>
                          <td className={`px-3 py-2 ${h.isOverdue ? 'font-semibold text-rose-600 dark:text-rose-400' : 'text-slate-600 dark:text-slate-300'}`}>{fmtDate(h.expectedReturnDate)}</td>
                          <td className="px-3 py-2 text-slate-600 dark:text-slate-300">{fmtDate(h.returnedOn)}{h.closedByName && <div className="text-[10px] text-slate-400">by {h.closedByName}</div>}</td>
                          <td className="px-3 py-2"><StatusChip label={ASSIGNMENT_STATUS_LABEL[h.status]} tone={h.status === 'Active' ? 'blue' : h.status === 'WrittenOff' ? 'rose' : 'slate'} /></td>
                          <td className="px-3 py-2 text-slate-600 dark:text-slate-300">{labelOf(h.conditionOnIssue, lookups, 'conditions')} → {h.conditionOnReturn ? labelOf(h.conditionOnReturn, lookups, 'conditions') : '…'}</td>
                          <td className="px-3 py-2 text-slate-500 dark:text-slate-400">{[h.issueNotes, h.returnNotes].filter(Boolean).join(' · ') || '—'}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )
            )}
            {tab === 'writeOffs' && (
              detail.writeOffs.length === 0 ? <p className="p-3 text-sm text-slate-400">No write-off requests.</p> : (
                <div className="space-y-2">
                  {detail.writeOffs.map(w => (
                    <div key={w.id} className="surface flex flex-wrap items-start justify-between gap-2 p-3 text-xs">
                      <div>
                        <div className="flex items-center gap-2">
                          <StatusChip label={w.status} tone={w.status === 'Approved' ? 'rose' : w.status === 'Rejected' ? 'slate' : 'amber'} dot />
                          <span className="font-semibold text-slate-800 dark:text-slate-100">{w.kind}</span>
                          <span className="text-slate-400">requested by {w.requestedByName || '—'} · {fmtDateTime(w.requestedAtUtc)}</span>
                        </div>
                        <p className="mt-1 text-slate-600 dark:text-slate-300">{w.reason}</p>
                        {w.decidedAtUtc && <p className="mt-1 text-slate-400">Decided {fmtDateTime(w.decidedAtUtc)}{w.decisionComments ? ` — “${w.decisionComments}”` : ''}</p>}
                      </div>
                      {w.status === 'Pending' && <span className="text-slate-400">Decide it under Approvals</span>}
                    </div>
                  ))}
                </div>
              )
            )}
            {tab === 'audit' && (
              detail.auditTrail.length === 0 ? <p className="p-3 text-sm text-slate-400">No audit entries.</p> : (
                <ul className="space-y-1.5 text-xs">
                  {detail.auditTrail.map((e, i) => (
                    <li key={`${e.atUtc}-${i}`} className="flex items-start gap-2">
                      <History className="mt-0.5 h-3.5 w-3.5 shrink-0 text-slate-400" />
                      <div>
                        <span className="font-semibold text-slate-800 dark:text-slate-100">{ACTION_LABEL[e.action] ?? e.action}</span>
                        <span className="ml-2 text-slate-400">{fmtDateTime(e.atUtc)}</span>
                        {e.metadata && <div className="break-all font-mono text-[10px] text-slate-400">{e.metadata}</div>}
                      </div>
                    </li>
                  ))}
                </ul>
              )
            )}
          </div>
        </div>
      )}
    </Modal>
  );
}

function Field({ k, v, mono, tone }: { k: string; v: string; mono?: boolean; tone?: string }) {
  return (
    <>
      <dt className="text-slate-500 dark:text-slate-400">{k}</dt>
      <dd className={`${mono ? 'font-mono text-xs' : ''} ${tone ?? 'text-slate-800 dark:text-slate-100'}`}>{v}</dd>
    </>
  );
}

// ── Action dialogs ────────────────────────────────────────────────────────────

function ActionDialog({ dialog, lookups, onClose, onDone }: { dialog: Dialog; lookups: AssetLookups | null; onClose: () => void; onDone: (message: string) => Promise<void> }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const conditions = lookups?.conditions ?? [];
  const asset = dialog.kind === 'create' ? null : dialog.asset;

  // Form state (one dialog at a time, so a single bag is fine).
  const [form, setForm] = useState<AssetUpsert>(() => asset && dialog.kind === 'edit' ? {
    assetTag: asset.assetTag, name: asset.name, serialNumber: asset.serialNumber, categoryCode: asset.categoryCode, make: asset.make, model: asset.model,
    purchaseDate: asset.purchaseDate, purchaseCost: asset.purchaseCost, currency: asset.currency, condition: asset.condition,
    companyId: asset.companyId, branchId: asset.branchId, locationId: asset.locationId, locationNote: asset.locationNote, notes: '',
  } : { assetTag: '', name: '', serialNumber: '', categoryCode: lookups?.categories[0]?.code ?? '', make: '', model: '', purchaseDate: null, purchaseCost: null, currency: 'SAR', condition: 'NEW', locationNote: '', notes: '' });
  const [employee, setEmployee] = useState<EmployeeSelection | null>(null);
  const [date, setDate] = useState('');
  const [dueDate, setDueDate] = useState(dialog.kind === 'extend' ? (asset?.currentHolder?.expectedReturnDate ?? '') : '');
  const [condition, setCondition] = useState(asset?.condition ?? 'GOOD');
  const [notes, setNotes] = useState('');
  const [sendToRepair, setSendToRepair] = useState(false);
  const [kind, setKind] = useState<'Lost' | 'Damaged'>('Lost');

  const run = async (fn: () => Promise<unknown>, message: string) => {
    setBusy(true); setError('');
    try { await fn(); await onDone(message); }
    catch (e) { setError(parseApiError(e)); setBusy(false); }
  };

  const titles: Record<Dialog['kind'], string> = {
    create: 'Register asset', edit: `Edit ${asset?.assetTag ?? ''}`, issue: `Issue ${asset?.assetTag ?? ''}`, return: `Record return of ${asset?.assetTag ?? ''}`,
    transfer: `Transfer ${asset?.assetTag ?? ''}`, writeOff: `Report ${asset?.assetTag ?? ''} lost or damaged`, retire: `Retire ${asset?.assetTag ?? ''}`,
    repair: asset?.status === 'InRepair' ? `Repair complete — ${asset?.assetTag ?? ''}` : `Send ${asset?.assetTag ?? ''} to repair`, extend: `Change due date — ${asset?.assetTag ?? ''}`,
  };

  const submit = () => {
    if (!asset && dialog.kind !== 'create') return;
    switch (dialog.kind) {
      case 'create': return run(() => assetsApi.create(form), 'Asset registered.');
      case 'edit': return run(() => assetsApi.update(asset!.id, form), 'Asset updated.');
      case 'issue':
        if (!employee) { setError('Choose the employee receiving the item.'); return; }
        return run(() => assetsApi.issue(asset!.id, { employeeId: employee.intId, issuedOn: date || null, expectedReturnDate: dueDate || null, condition, notes }), `Issued to ${employee.fullName}.`);
      case 'return':
        return run(() => assetsApi.return(asset!.id, { condition, returnedOn: date || null, notes, sendToRepair }), 'Return recorded.');
      case 'transfer':
        if (!employee) { setError('Choose the employee taking over the item.'); return; }
        return run(() => assetsApi.transfer(asset!.id, { toEmployeeId: employee.intId, expectedReturnDate: dueDate || null, condition, notes }), `Transferred to ${employee.fullName}.`);
      case 'writeOff':
        return run(() => assetsApi.writeOff(asset!.id, { kind, reason: notes }), 'Write-off submitted for approval. Nothing changes on the asset until the final approver decides.');
      case 'retire':
        return run(() => assetsApi.retire(asset!.id, { reason: notes }), 'Asset retired.');
      case 'repair':
        return run(() => assetsApi.repair(asset!.id, { action: asset!.status === 'InRepair' ? 'Complete' : 'Send', condition, notes }), asset!.status === 'InRepair' ? 'Back in stock.' : 'Sent to repair.');
      case 'extend':
        return run(() => assetsApi.extendReturn(asset!.currentHolder!.assignmentId, dueDate || null), 'Due date updated; reminders re-armed.');
    }
  };

  const conditionSelect = (label = 'Condition') => (
    <div>
      <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{label}</label>
      <select className="select w-full" value={condition} onChange={e => setCondition(e.target.value)} aria-label={label}>
        {conditions.length === 0 && <option value={condition}>{condition}</option>}
        {conditions.map(c => <option key={c.code} value={c.code}>{c.label}</option>)}
      </select>
    </div>
  );

  return (
    <Modal isOpen title={titles[dialog.kind]} onClose={onClose} size={dialog.kind === 'create' || dialog.kind === 'edit' ? 'lg' : 'md'}
      footer={<>
        <button type="button" className="btn-secondary text-sm" onClick={onClose} disabled={busy}>Cancel</button>
        <button type="button" className={`${dialog.kind === 'writeOff' || dialog.kind === 'retire' ? 'btn-primary bg-rose-600 hover:bg-rose-700 shadow-rose-600/30' : 'btn-primary'} text-sm`} onClick={submit} disabled={busy}>
          {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
          {dialog.kind === 'writeOff' ? 'Submit for approval' : dialog.kind === 'create' ? 'Register' : 'Confirm'}
        </button>
      </>}>
      <div className="space-y-3">
        {(dialog.kind === 'create' || dialog.kind === 'edit') && (
          <>
            <div className="grid gap-3 sm:grid-cols-2">
              <Text label="Asset tag *" value={form.assetTag} onChange={v => setForm(f => ({ ...f, assetTag: v }))} placeholder="e.g. LAP-0042" mono />
              <Text label="Name" value={form.name ?? ''} onChange={v => setForm(f => ({ ...f, name: v }))} placeholder="e.g. Dell Latitude 7440" />
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Category</label>
                <select className="select w-full" value={form.categoryCode ?? ''} onChange={e => setForm(f => ({ ...f, categoryCode: e.target.value }))} aria-label="Category">
                  <option value="">—</option>
                  {(lookups?.categories ?? []).map(c => <option key={c.code} value={c.code}>{c.label}</option>)}
                </select>
              </div>
              <Text label="Serial number" value={form.serialNumber ?? ''} onChange={v => setForm(f => ({ ...f, serialNumber: v }))} mono />
              <Text label="Make" value={form.make ?? ''} onChange={v => setForm(f => ({ ...f, make: v }))} />
              <Text label="Model" value={form.model ?? ''} onChange={v => setForm(f => ({ ...f, model: v }))} />
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Purchase date</label>
                <input type="date" className="input w-full" value={form.purchaseDate ?? ''} onChange={e => setForm(f => ({ ...f, purchaseDate: e.target.value || null }))} aria-label="Purchase date" />
              </div>
              <div className="grid grid-cols-[1fr_80px] gap-2">
                <div>
                  <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Purchase cost</label>
                  <input type="number" min={0} step="0.01" className="input w-full" value={form.purchaseCost ?? ''} onChange={e => setForm(f => ({ ...f, purchaseCost: e.target.value === '' ? null : Number(e.target.value) }))} aria-label="Purchase cost" />
                </div>
                <Text label="Currency" value={form.currency ?? ''} onChange={v => setForm(f => ({ ...f, currency: v.toUpperCase() }))} />
              </div>
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Condition</label>
                <select className="select w-full" value={form.condition ?? ''} onChange={e => setForm(f => ({ ...f, condition: e.target.value }))} aria-label="Condition">
                  {conditions.length === 0 && <option value={form.condition ?? ''}>{form.condition}</option>}
                  {conditions.map(c => <option key={c.code} value={c.code}>{c.label}</option>)}
                </select>
              </div>
              <Text label="Location note" value={form.locationNote ?? ''} onChange={v => setForm(f => ({ ...f, locationNote: v }))} placeholder="Room, cabinet, warehouse bin…" />
            </div>
            <Text label="Notes" value={form.notes ?? ''} onChange={v => setForm(f => ({ ...f, notes: v }))} multiline />
            {dialog.kind === 'create' && <p className="text-xs text-slate-400">The owning company is taken from your current company selection; in a multi-company tenant choose it in the header first.</p>}
          </>
        )}

        {(dialog.kind === 'issue' || dialog.kind === 'transfer') && (
          <>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{dialog.kind === 'issue' ? 'Issue to *' : 'Transfer to *'}</label>
              <EmployeeSearchSelect value={employee} onChange={setEmployee} required />
              {dialog.kind === 'transfer' && asset?.currentHolder && <p className="mt-1 text-xs text-slate-400">Currently held by {asset.currentHolder.employeeName} ({asset.currentHolder.employeeCode}). Their custody period closes today.</p>}
            </div>
            <div className="grid grid-cols-2 gap-3">
              {dialog.kind === 'issue' && (
                <div>
                  <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Issued on</label>
                  <input type="date" className="input w-full" value={date} onChange={e => setDate(e.target.value)} aria-label="Issued on" />
                </div>
              )}
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Expected return</label>
                <input type="date" className="input w-full" value={dueDate} onChange={e => setDueDate(e.target.value)} aria-label="Expected return date" />
              </div>
              {conditionSelect('Condition on issue')}
            </div>
            <Text label="Notes" value={notes} onChange={setNotes} multiline />
          </>
        )}

        {dialog.kind === 'return' && (
          <>
            <div className="grid grid-cols-2 gap-3">
              {conditionSelect('Condition on return *')}
              <div>
                <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Returned on</label>
                <input type="date" className="input w-full" value={date} onChange={e => setDate(e.target.value)} aria-label="Returned on" />
              </div>
            </div>
            <label className="flex cursor-pointer items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
              <input type="checkbox" className="h-4 w-4 rounded border-slate-300" checked={sendToRepair} onChange={e => setSendToRepair(e.target.checked)} /> Send straight to repair
            </label>
            <Text label="Notes" value={notes} onChange={setNotes} multiline />
          </>
        )}

        {dialog.kind === 'writeOff' && (
          <>
            <div className="flex items-start gap-2 rounded-lg border border-amber-300/50 bg-amber-50 p-2.5 text-xs text-amber-700 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
              <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
              This creates an approval request. The item stays with its holder and cannot be returned or transferred until the request is decided. Only the workflow&apos;s final approval writes it off.
            </div>
            <div>
              <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">What happened *</label>
              <select className="select w-full" value={kind} onChange={e => setKind(e.target.value as 'Lost' | 'Damaged')} aria-label="Kind">
                <option value="Lost">Lost / stolen</option>
                <option value="Damaged">Damaged beyond repair</option>
              </select>
            </div>
            <Text label="Reason * (at least 5 characters)" value={notes} onChange={setNotes} multiline placeholder="Where, when, police report reference…" />
          </>
        )}

        {dialog.kind === 'retire' && (
          <Text label="Reason * (at least 5 characters)" value={notes} onChange={setNotes} multiline placeholder="End of life, disposed, sold…" />
        )}

        {dialog.kind === 'repair' && (
          <>
            {conditionSelect(asset?.status === 'InRepair' ? 'Condition after repair' : 'Condition')}
            <Text label="Notes" value={notes} onChange={setNotes} multiline />
          </>
        )}

        {dialog.kind === 'extend' && (
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">Expected return date (blank = no due date)</label>
            <input type="date" className="input w-full" value={dueDate} onChange={e => setDueDate(e.target.value)} aria-label="Expected return date" />
            <p className="mt-1 text-xs text-slate-400">Changing the date re-arms the due-soon and overdue reminders.</p>
          </div>
        )}

        {error && <p className="text-xs text-rose-500">{error}</p>}
      </div>
    </Modal>
  );
}

function Text({ label, value, onChange, placeholder, mono, multiline }: { label: string; value: string; onChange: (v: string) => void; placeholder?: string; mono?: boolean; multiline?: boolean }) {
  const cls = `input w-full ${mono ? 'font-mono' : ''} ${multiline ? 'h-auto resize-none' : ''}`;
  return (
    <div>
      <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{label}</label>
      {multiline
        ? <textarea className={cls} rows={2} value={value} onChange={e => onChange(e.target.value)} placeholder={placeholder} aria-label={label} />
        : <input className={cls} value={value} onChange={e => onChange(e.target.value)} placeholder={placeholder} aria-label={label} />}
    </div>
  );
}
