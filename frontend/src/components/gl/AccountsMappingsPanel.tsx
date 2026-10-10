'use client';

import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Download, Pencil, Plus, RotateCcw, Search, Upload } from 'lucide-react';
import { notifyApiError } from '../../api/client';
import {
  financeGlApi,
  GL_ACCOUNT_TYPES,
  type GlAccount,
  type GlAccountImportResult,
  type GlMappingRow,
  type GlResolveResponse,
} from '../../api/financeGl';
import type { CostCenterDto } from '../../api/organization';
import { useT } from '../../hooks/useT';
import { useAppToast } from '../ui/AppToast';
import { Modal } from '../Modal';
import {
  accountTypeLabel,
  ActiveBadge,
  Badge,
  driverCategoryLabel,
  Field,
  FieldError,
  InheritedBadge,
  PanelState,
  problemLabel,
  problemRank,
  routeRowId,
  routeSourceLabel,
  serverMessage,
  sideLabel,
  theadClass,
  thClass,
  useConfirm,
  useScopedResource,
} from './glUi';

interface ResolveResource {
  data: GlResolveResponse | null;
  loading: boolean;
  error: string | null;
  reload: () => Promise<void> | void;
}

interface Props {
  /** null = group / tenant-default scope; a guid = a company override scope. */
  scope: string | null;
  scopeName: string;
  canManage: boolean;
  costCenters: CostCenterDto[];
  resolve: ResolveResource;
  onRoutingChanged: () => void;
}

interface MapEdit {
  accountId: string;
  segmentCostCenterId: string;
}

interface Loaded {
  own: GlAccount[];
  group: GlAccount[];
  maps: GlMappingRow[];
}

const isForce = (err: unknown): boolean =>
  Boolean((err as { response?: { data?: { requiresForce?: boolean } } })?.response?.data?.requiresForce);

/** Group rows by account type in the chart's natural order; unknown types go last. */
const TYPE_ORDER: string[] = [...GL_ACCOUNT_TYPES];
const typeIndex = (type: string) => {
  const i = TYPE_ORDER.indexOf(type === 'Revenue' ? 'Income' : type);
  return i < 0 ? TYPE_ORDER.length : i;
};

export function AccountsMappingsPanel({ scope, scopeName, canManage, costCenters, resolve, onRoutingChanged }: Props) {
  const t = useT();
  const toast = useAppToast();
  const { confirm, dialog } = useConfirm();
  const inCompanyView = scope !== null;

  // ── Load: this scope's accounts, the group's accounts, and the mapping rows ──
  // Group accounts are always fetched (company-scoped users included): a company inherits them and
  // may map to them, so they must be visible. A failure there degrades to "none inherited".
  const fetcher = useCallback(async (): Promise<Loaded> => {
    const [own, maps, group] = await Promise.all([
      financeGlApi.listAccounts(scope),
      financeGlApi.listMappings(scope),
      financeGlApi.listAccounts(null).catch(() => [] as GlAccount[]),
    ]);
    const groupAccounts = new Map<string, GlAccount>();
    for (const a of [...group, ...own]) if (a.companyId === null) groupAccounts.set(a.id, a);
    return {
      own: inCompanyView ? own.filter((a) => a.companyId === scope) : own.filter((a) => a.companyId === null),
      group: [...groupAccounts.values()],
      maps,
    };
  }, [scope, inCompanyView]);
  const res = useScopedResource<Loaded>(`accounts:${scope ?? 'group'}`, fetcher);
  const ownAccounts = useMemo(() => res.data?.own ?? [], [res.data]);
  const groupDefaults = useMemo(() => (inCompanyView ? res.data?.group ?? [] : []), [res.data, inCompanyView]);
  const rows = useMemo(() => res.data?.maps ?? [], [res.data]);
  const writable = canManage && res.ready;

  const [edits, setEdits] = useState<Record<string, MapEdit>>({});
  useEffect(() => {
    setEdits(
      Object.fromEntries(
        rows.map((r) => [
          r.driverKey,
          // A company view starts inherited rows unset (no override); an existing company
          // override (or the group mapping in group view) pre-fills the select.
          { accountId: r.inherited ? '' : r.mappedAccountId ?? '', segmentCostCenterId: r.inherited ? '' : r.segmentCostCenterId ?? '' },
        ]),
      ),
    );
  }, [rows]);

  const reloadAll = async () => {
    await res.reload();
    onRoutingChanged();
  };

  // Cost centers a mapping in this scope may tag: tenant-global + this company's own.
  const visibleCostCenters = useMemo(
    () => costCenters.filter((c) => c.isActive && (!c.companyId || c.companyId === scope)),
    [costCenters, scope],
  );

  // Accounts a mapping in this scope may point at: own + inherited group defaults (active only).
  const selectableAccounts = useMemo(
    () => [...ownAccounts.filter((a) => a.isActive), ...groupDefaults.filter((a) => a.isActive)],
    [ownAccounts, groupDefaults],
  );

  const accountLabel = useCallback(
    (id: string | null) => {
      if (!id) return null;
      const a = [...ownAccounts, ...groupDefaults].find((x) => x.id === id);
      return a ? `${a.code} — ${a.name}` : null;
    },
    [ownAccounts, groupDefaults],
  );

  const dirty = useMemo(
    () =>
      rows.some((r) => {
        const e = edits[r.driverKey];
        if (!e) return false;
        const base = r.inherited ? '' : r.mappedAccountId ?? '';
        const baseSeg = r.inherited ? '' : r.segmentCostCenterId ?? '';
        return e.accountId !== base || e.segmentCostCenterId !== baseSeg;
      }),
    [rows, edits],
  );

  const setEdit = (driverKey: string, patch: Partial<MapEdit>) =>
    setEdits((prev) => ({ ...prev, [driverKey]: { ...prev[driverKey], ...patch } }));

  // ── Mapping save ───────────────────────────────────────────────────────────
  const [saving, setSaving] = useState(false);
  const saveMappings = async () => {
    if (!writable) return;
    setSaving(true);
    try {
      const payload = Object.entries(edits)
        .filter(([, e]) => e.accountId)
        .map(([driverKey, e]) => ({ driverKey, accountId: e.accountId, segmentCostCenterId: e.segmentCostCenterId || null }));
      await financeGlApi.setMappings(payload, scope);
      toast.success(t('Mappings saved.'));
      await reloadAll();
    } catch (e) {
      notifyApiError(e);
    } finally {
      setSaving(false);
    }
  };

  // ── Load standard payroll chart (group scope) ─────────────────────────────
  const [seeding, setSeeding] = useState(false);
  const seed = async () => {
    const ok = await confirm({
      title: t('Load standard payroll chart?'),
      message: t('Adds only accounts and drivers that are missing; never changes existing ones.'),
      confirmLabel: t('Load standard chart'),
    });
    if (!ok) return;
    setSeeding(true);
    try {
      const r = await financeGlApi.seedDefaults();
      toast.success(
        t('Added {accounts} accounts, {drivers} drivers, {mappings} mappings and {rates} rate definitions.', {
          accounts: r.accounts ?? 0,
          drivers: r.drivers ?? 0,
          mappings: r.mappings ?? 0,
          rates: r.rateDefinitions ?? 0,
        }),
      );
      await reloadAll();
    } catch (e) {
      notifyApiError(e);
    } finally {
      setSeeding(false);
    }
  };

  // ── Export / import CSV ────────────────────────────────────────────────────
  const [exporting, setExporting] = useState(false);
  const exportCsv = async () => {
    setExporting(true);
    try {
      const blob = await financeGlApi.exportAccounts(scope);
      const url = URL.createObjectURL(blob instanceof Blob ? blob : new Blob([blob as BlobPart], { type: 'text/csv' }));
      const a = document.createElement('a');
      a.href = url;
      a.download = `chart-of-accounts-${scope ?? 'group'}.csv`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (e) {
      notifyApiError(e);
    } finally {
      setExporting(false);
    }
  };

  const fileInput = useRef<HTMLInputElement>(null);
  const [importFile, setImportFile] = useState<File | null>(null);
  const [preview, setPreview] = useState<GlAccountImportResult | null>(null);
  const [importBusy, setImportBusy] = useState(false);
  const [importError, setImportError] = useState('');

  const onFileChosen = async (file: File | undefined) => {
    if (fileInput.current) fileInput.current.value = '';
    if (!file) return;
    setImportFile(file);
    setPreview(null);
    setImportError('');
    setImportBusy(true);
    try {
      setPreview(await financeGlApi.importAccounts(file, scope, true));
    } catch (e) {
      setImportError(serverMessage(e, t));
    } finally {
      setImportBusy(false);
    }
  };

  const closeImport = () => {
    setImportFile(null);
    setPreview(null);
    setImportError('');
  };

  const applyImport = async () => {
    if (!importFile) return;
    setImportBusy(true);
    setImportError('');
    try {
      const r = await financeGlApi.importAccounts(importFile, scope, false);
      if (r.errors?.length) {
        setPreview(r);
        return;
      }
      toast.success(t('Import applied: {created} created, {updated} updated, {unchanged} unchanged.', { created: r.created, updated: r.updated, unchanged: r.unchanged }));
      closeImport();
      await reloadAll();
    } catch (e) {
      setImportError(serverMessage(e, t));
    } finally {
      setImportBusy(false);
    }
  };

  // ── Account create / edit / delete ─────────────────────────────────────────
  const [newAcct, setNewAcct] = useState({ code: '', name: '', accountType: 'Expense' });
  const [adding, setAdding] = useState(false);
  const [addError, setAddError] = useState('');
  const [editing, setEditing] = useState<GlAccount | null>(null);
  const [editForm, setEditForm] = useState({ code: '', name: '', accountType: 'Expense', isActive: true });
  const [editError, setEditError] = useState('');
  const [savingEdit, setSavingEdit] = useState(false);
  const [deletingId, setDeletingId] = useState<string | null>(null);

  const addAccount = async () => {
    if (!newAcct.code.trim() || !newAcct.name.trim()) {
      setAddError(t('Enter an account code and a name.'));
      return;
    }
    setAdding(true);
    setAddError('');
    try {
      await financeGlApi.createAccount({ code: newAcct.code.trim(), name: newAcct.name.trim(), accountType: newAcct.accountType, isActive: true }, scope);
      setNewAcct({ code: '', name: '', accountType: newAcct.accountType });
      toast.success(t('Account added.'));
      await reloadAll();
    } catch (e) {
      setAddError(serverMessage(e, t));
    } finally {
      setAdding(false);
    }
  };

  const openEdit = (a: GlAccount) => {
    setEditing(a);
    setEditForm({ code: a.code, name: a.name, accountType: a.accountType === 'Revenue' ? 'Income' : a.accountType, isActive: a.isActive });
    setEditError('');
  };

  const saveEdit = async (force = false): Promise<void> => {
    if (!editing) return;
    if (!editForm.code.trim() || !editForm.name.trim()) {
      setEditError(t('Enter an account code and a name.'));
      return;
    }
    // Deactivating a mapped account is confirmed below, when the server says it is still mapped.
    setSavingEdit(true);
    setEditError('');
    try {
      await financeGlApi.updateAccount(
        editing.id,
        { code: editForm.code.trim(), name: editForm.name.trim(), accountType: editForm.accountType, isActive: editForm.isActive },
        { force },
      );
      setEditing(null);
      toast.success(t('Account saved.'));
      await reloadAll();
    } catch (err) {
      if (isForce(err) && !force) {
        setSavingEdit(false);
        const ok = await confirm({
          title: t('This account is still mapped'),
          message: t('An active mapping in this scope points at this account. Deactivate anyway? The driver will fall back to its default account in the GL journal.'),
          confirmLabel: t('Deactivate anyway'),
          tone: 'danger',
        });
        if (ok) return saveEdit(true);
        setEditError(t('Deactivation cancelled. The account is still mapped.'));
      } else {
        // 409 once the account has been posted to: the server's sentence says why.
        setEditError(serverMessage(err, t));
      }
    } finally {
      setSavingEdit(false);
    }
  };

  const deleteAccount = async (a: GlAccount) => {
    const ok = await confirm({
      title: t('Delete account {code}?', { code: a.code }),
      message: t('{code} — {name} will be removed from the chart of accounts. An account that is mapped or has been posted to cannot be deleted; deactivate it instead.', { code: a.code, name: a.name }),
      confirmLabel: t('Delete'),
      tone: 'danger',
    });
    if (!ok) return;
    setDeletingId(a.id);
    try {
      await financeGlApi.deleteAccount(a.id);
      toast.success(t('Account deleted.'));
      await reloadAll();
    } catch (e) {
      notifyApiError(e); // surfaces "Account is in use by a payroll mapping; remap it first."
    } finally {
      setDeletingId(null);
    }
  };

  // ── Chart search / filter / grouping ───────────────────────────────────────
  const [query, setQuery] = useState('');
  const [typeFilter, setTypeFilter] = useState('');
  const chartRows = useMemo(() => {
    const q = query.trim().toLowerCase();
    const all = [
      ...ownAccounts.map((a) => ({ account: a, inherited: false })),
      ...groupDefaults.map((a) => ({ account: a, inherited: true })),
    ];
    return all
      .filter(({ account: a }) => !typeFilter || (typeFilter === 'Income' ? a.accountType === 'Income' || a.accountType === 'Revenue' : a.accountType === typeFilter))
      .filter(({ account: a }) => !q || a.code.toLowerCase().includes(q) || a.name.toLowerCase().includes(q))
      .sort((x, y) => typeIndex(x.account.accountType) - typeIndex(y.account.accountType) || x.account.code.localeCompare(y.account.code, undefined, { numeric: true }));
  }, [ownAccounts, groupDefaults, query, typeFilter]);
  const groups = useMemo(() => {
    const out: { type: string; rows: typeof chartRows }[] = [];
    for (const r of chartRows) {
      const type = r.account.accountType === 'Revenue' ? 'Income' : r.account.accountType;
      const last = out[out.length - 1];
      if (last && last.type === type) last.rows.push(r);
      else out.push({ type, rows: [r] });
    }
    return out;
  }, [chartRows]);
  const totalAccounts = ownAccounts.length + groupDefaults.length;

  const overrideCount = rows.filter((r) => !r.inherited && r.mappedAccountId).length;

  // ── "Where does it post?" — problems first ─────────────────────────────────
  const routeRows = useMemo(
    () =>
      [...(Array.isArray(resolve.data?.rows) ? resolve.data.rows : [])].sort(
        (a, b) => problemRank(a.problem) - problemRank(b.problem) || (a.label || a.driverKey).localeCompare(b.label || b.driverKey),
      ),
    [resolve.data],
  );

  return (
    <div className="space-y-6">
      {/* ── Where does it post? ── */}
      <section className="surface p-4" aria-labelledby="gl-where-heading">
        <div className="mb-3">
          <h3 id="gl-where-heading" className="text-sm font-semibold text-slate-900 dark:text-white">
            {t('Where does it post?')}
          </h3>
          <p className="text-xs text-slate-500 dark:text-slate-400">
            {t('The account each payroll posting line will actually use for {scope}, after company overrides, group mappings and fallbacks are applied.', { scope: scopeName })}
          </p>
        </div>
        <PanelState
          loading={resolve.loading && !resolve.data}
          error={resolve.data ? null : resolve.error}
          onRetry={() => void resolve.reload()}
          empty={routeRows.length === 0}
          emptyLabel={t('No posting drivers are set up yet.')}
        >
          <div className="overflow-x-auto rounded-lg border border-slate-200 dark:border-white/10">
            <table className="w-full min-w-[760px] text-sm">
              <thead className={theadClass}>
                <tr>
                  <th scope="col" className={thClass}>{t('Driver')}</th>
                  <th scope="col" className={thClass}>{t('Pay components')}</th>
                  <th scope="col" className={thClass}>{t('Side')}</th>
                  <th scope="col" className={thClass}>{t('Effective account')}</th>
                  <th scope="col" className={thClass}>{t('Source')}</th>
                  <th scope="col" className={thClass}>{t('Problem')}</th>
                </tr>
              </thead>
              <tbody>
                {routeRows.map((r) => {
                  const src = routeSourceLabel(t, r.source);
                  return (
                    <tr
                      key={r.driverKey}
                      id={routeRowId(r.driverKey)}
                      tabIndex={-1}
                      className={`border-t border-slate-100 outline-none focus:ring-2 focus:ring-inset focus:ring-sapphire dark:border-white/5 ${r.problem ? 'bg-amber-50/60 dark:bg-amber-500/[0.06]' : ''}`}
                    >
                      <td className="px-3 py-2">
                        <p className="text-slate-800 dark:text-slate-100">{r.label || r.driverKey}</p>
                        <p className="text-[11px] text-slate-500"><span className="font-mono">{r.driverKey}</span></p>
                      </td>
                      <td className="px-3 py-2">
                        {r.componentCodes && r.componentCodes.length > 0 ? (
                          <div className="flex max-w-[220px] flex-wrap gap-1">
                            {r.componentCodes.map((c) => (
                              <span key={c} className="rounded bg-slate-100 px-1.5 py-0.5 font-mono text-[11px] text-slate-600 dark:bg-white/10 dark:text-slate-300">
                                {c}
                              </span>
                            ))}
                          </div>
                        ) : (
                          <span className="text-xs text-slate-500">{t('Any matching line')}</span>
                        )}
                      </td>
                      <td className="px-3 py-2 text-xs text-slate-600 dark:text-slate-300">{sideLabel(t, r.side)}</td>
                      <td className="px-3 py-2">
                        <span className="font-mono text-xs">{r.account?.code || '—'}</span>
                        {r.account?.name && <span className="ms-1 text-xs text-slate-600 dark:text-slate-300">{r.account.name}</span>}
                      </td>
                      <td className="px-3 py-2">
                        <Badge tone={src.tone} title={src.hint}>{src.label}</Badge>
                      </td>
                      <td className="px-3 py-2 text-xs">
                        {r.problem ? (
                          <span className="font-medium text-amber-800 dark:text-amber-300">{problemLabel(t, r.problem)}</span>
                        ) : (
                          <span className="text-slate-500">{t('None')}</span>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </PanelState>
      </section>

      {/* ── Chart of accounts ── */}
      <section className="surface p-4" aria-labelledby="gl-coa-heading">
        <div className="mb-3 flex flex-wrap items-start justify-between gap-3">
          <div>
            <h3 id="gl-coa-heading" className="text-sm font-semibold text-slate-900 dark:text-white">
              {t('Chart of Accounts')}
            </h3>
            <p className="text-xs text-slate-500 dark:text-slate-400">
              {t('GL accounts payroll can post to for {scope}.', { scope: scopeName })}
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <button type="button" onClick={exportCsv} disabled={exporting || !res.ready} className="btn-secondary h-8 px-3 text-xs disabled:opacity-60">
              <Download className="h-3.5 w-3.5" aria-hidden /> {exporting ? t('Exporting…') : t('Export CSV')}
            </button>
            {canManage && (
              <>
                <input
                  ref={fileInput}
                  type="file"
                  accept=".csv,text/csv"
                  className="sr-only"
                  tabIndex={-1}
                  aria-label={t('Choose a CSV file to import')}
                  onChange={(e) => void onFileChosen(e.target.files?.[0])}
                />
                <button type="button" onClick={() => fileInput.current?.click()} disabled={!writable || importBusy} className="btn-secondary h-8 px-3 text-xs disabled:opacity-60">
                  <Upload className="h-3.5 w-3.5" aria-hidden /> {t('Import CSV')}
                </button>
              </>
            )}
          </div>
        </div>

        {!inCompanyView && canManage && (
          <div className="mb-3 flex flex-wrap items-center justify-between gap-2 rounded-lg border border-dashed border-slate-200 px-3 py-2 dark:border-white/10">
            <p className="text-xs text-slate-600 dark:text-slate-300">
              {t('Adds only accounts and drivers that are missing; never changes existing ones.')}
            </p>
            <button type="button" onClick={seed} disabled={seeding || !writable} className="btn-secondary h-8 px-3 text-xs disabled:opacity-60">
              {seeding ? t('Loading…') : t('Load standard payroll chart')}
            </button>
          </div>
        )}

        <div className="mb-3 flex flex-wrap items-end gap-2">
          <div className="relative min-w-[200px] flex-1">
            <Search className="pointer-events-none absolute start-2.5 top-1/2 z-10 h-3.5 w-3.5 -translate-y-1/2 text-slate-500" aria-hidden />
            <input
              type="search"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              aria-label={t('Search accounts by code or name')}
              placeholder={t('Search by code or name')}
              className="input h-8 w-full ps-8 text-xs"
            />
          </div>
          <select
            value={typeFilter}
            onChange={(e) => setTypeFilter(e.target.value)}
            aria-label={t('Filter by account type')}
            className="select h-8 text-xs"
          >
            <option value="">{t('All types')}</option>
            {GL_ACCOUNT_TYPES.map((type) => (
              <option key={type} value={type}>
                {accountTypeLabel(t, type)}
              </option>
            ))}
          </select>
        </div>

        <PanelState
          loading={res.loading && !res.data}
          error={res.error}
          onRetry={() => void res.reload()}
          empty={totalAccounts === 0}
          emptyLabel={
            inCompanyView
              ? t('This company has no accounts of its own, and no group accounts were found.')
              : canManage
                ? t('No accounts yet. Load the standard payroll chart or add accounts below.')
                : t('No accounts yet.')
          }
        >
          <div className="overflow-x-auto rounded-lg border border-slate-200 dark:border-white/10">
            <table className="w-full min-w-[720px] text-sm">
              <thead className={theadClass}>
                <tr>
                  <th scope="col" className={thClass}>{t('Code')}</th>
                  <th scope="col" className={thClass}>{t('Name')}</th>
                  <th scope="col" className={thClass}>{t('Type')}</th>
                  <th scope="col" className={thClass}>{t('Scope')}</th>
                  <th scope="col" className={thClass}>{t('Status')}</th>
                  <th scope="col" className={`${thClass} text-end`}>{t('Actions')}</th>
                </tr>
              </thead>
              <tbody>
                {groups.length === 0 && (
                  <tr>
                    <td colSpan={6} className="px-3 py-6 text-center text-xs text-slate-500">
                      {t('No accounts match your search.')}
                    </td>
                  </tr>
                )}
                {groups.map((g) => (
                  <Fragment key={g.type}>
                    <tr className="border-t border-slate-200 bg-slate-50/70 dark:border-white/10 dark:bg-white/[0.02]">
                      <th scope="colgroup" colSpan={6} className="px-3 py-1.5 text-start text-xs font-semibold text-slate-600 dark:text-slate-300">
                        {t('{type} ({count})', { type: accountTypeLabel(t, g.type), count: g.rows.length })}
                      </th>
                    </tr>
                    {g.rows.map(({ account: a, inherited }) => (
                      <tr key={a.id} className="border-t border-slate-100 dark:border-white/5">
                        <td className="px-3 py-2 font-mono text-xs">{a.code}</td>
                        <td className="px-3 py-2">{a.name}</td>
                        <td className="px-3 py-2 text-xs text-slate-600 dark:text-slate-300">{accountTypeLabel(t, a.accountType)}</td>
                        <td className="px-3 py-2">
                          {inherited ? <InheritedBadge /> : inCompanyView ? <Badge tone="violet">{t('Company')}</Badge> : <Badge tone="sapphire">{t('Group')}</Badge>}
                        </td>
                        <td className="px-3 py-2">
                          <ActiveBadge active={a.isActive} />
                        </td>
                        <td className="px-3 py-2">
                          <div className="flex items-center justify-end gap-1.5">
                            {canManage && !inherited ? (
                              <>
                                <button type="button" onClick={() => openEdit(a)} disabled={!writable} className="btn-secondary h-7 px-2 text-xs disabled:opacity-60">
                                  <Pencil className="h-3 w-3" aria-hidden /> {t('Edit')}
                                </button>
                                <button
                                  type="button"
                                  onClick={() => deleteAccount(a)}
                                  disabled={!writable || deletingId === a.id}
                                  aria-label={t('Delete account {code}', { code: a.code })}
                                  className="h-7 rounded-md border border-slate-200 px-2 text-xs font-medium text-rose-600 hover:border-rose-300 hover:bg-rose-50 disabled:opacity-40 dark:border-white/10 dark:text-rose-400 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10"
                                >
                                  {t('Delete')}
                                </button>
                              </>
                            ) : (
                              <span className="text-[11px] text-slate-400" aria-hidden>—</span>
                            )}
                          </div>
                        </td>
                      </tr>
                    ))}
                  </Fragment>
                ))}
              </tbody>
            </table>
          </div>
        </PanelState>

        {canManage && !res.error && (
          <div className="mt-3 rounded-lg border border-slate-200 p-3 dark:border-white/10">
            <p className="mb-2 text-xs font-semibold text-slate-700 dark:text-slate-200">
              {inCompanyView ? t('Add a company account') : t('Add a group account')}
            </p>
            <FieldError error={addError} />
            <div className="grid grid-cols-1 gap-2 sm:grid-cols-[120px_1fr_160px_auto] sm:items-end">
              <Field label={t('Code')} required>
                <input value={newAcct.code} onChange={(e) => setNewAcct((x) => ({ ...x, code: e.target.value }))} className="input h-8 w-full font-mono text-xs" placeholder="5006" />
              </Field>
              <Field label={t('Name')} required>
                <input value={newAcct.name} onChange={(e) => setNewAcct((x) => ({ ...x, name: e.target.value }))} className="input h-8 w-full text-xs" placeholder={t('Account name')} />
              </Field>
              <Field label={t('Type')}>
                <select value={newAcct.accountType} onChange={(e) => setNewAcct((x) => ({ ...x, accountType: e.target.value }))} className="select h-8 w-full text-xs">
                  {GL_ACCOUNT_TYPES.map((type) => (
                    <option key={type} value={type}>
                      {accountTypeLabel(t, type)}
                    </option>
                  ))}
                </select>
              </Field>
              <button type="button" onClick={addAccount} disabled={adding || !writable} className="btn-secondary h-8 px-3 text-xs disabled:opacity-60">
                <Plus className="h-3 w-3" aria-hidden /> {t('Add account')}
              </button>
            </div>
          </div>
        )}
      </section>

      {/* ── Payroll GL mapping ── */}
      <section className="surface p-4" aria-labelledby="gl-map-heading">
        <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
          <div>
            <h3 id="gl-map-heading" className="text-sm font-semibold text-slate-900 dark:text-white">
              {t('Payroll GL Mapping')}
            </h3>
            <p className="text-xs text-slate-500 dark:text-slate-400">
              {inCompanyView
                ? t('Point a posting line at an account to override the group mapping for this company. Leave it blank to inherit.')
                : t('Point each posting line at a group account. Leave it blank to use the driver\'s fallback account.')}
              {inCompanyView && overrideCount > 0 && (
                <span className="ms-1 font-medium text-violet-700 dark:text-violet-300">
                  {t('{count, plural, one {# company override.} other {# company overrides.}}', { count: overrideCount })}
                </span>
              )}
            </p>
          </div>
          {canManage && (
            <button type="button" onClick={saveMappings} disabled={saving || !dirty || !writable} className="btn-primary text-xs disabled:opacity-60">
              {saving ? t('Saving…') : t('Save mappings')}
            </button>
          )}
        </div>

        <PanelState
          loading={res.loading && !res.data}
          error={res.error}
          onRetry={() => void res.reload()}
          empty={rows.length === 0}
          emptyLabel={inCompanyView ? t('No posting drivers are set up yet. Ask a group administrator to load the standard payroll chart.') : t('No posting drivers are set up yet. Load the standard payroll chart above to get started.')}
        >
          <div className="overflow-x-auto rounded-lg border border-slate-200 dark:border-white/10">
            <table className="w-full min-w-[720px] text-sm">
              <thead className={theadClass}>
                <tr>
                  <th scope="col" className={thClass}>{t('Posting line')}</th>
                  <th scope="col" className={thClass}>{t('Category')}</th>
                  <th scope="col" className={thClass}>{t('Account')}</th>
                  <th scope="col" className={thClass}>{t('Cost centre tag')}</th>
                  <th scope="col" className={`${thClass} text-end`}>{t('Actions')}</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => {
                  const e = edits[r.driverKey] ?? { accountId: '', segmentCostCenterId: '' };
                  const isOverridden = inCompanyView && e.accountId !== '';
                  const inheritedAccount = inCompanyView && r.inherited ? accountLabel(r.mappedAccountId) : null;
                  return (
                    <tr key={r.driverKey} className="border-t border-slate-100 dark:border-white/5">
                      <td className="px-3 py-2">
                        <div className="flex flex-wrap items-center gap-1.5">
                          <span className="text-slate-800 dark:text-slate-100">{r.label}</span>
                          {isOverridden && <Badge tone="violet">{t('Company override')}</Badge>}
                          {inCompanyView && !isOverridden && r.mappedAccountId && <InheritedBadge />}
                        </div>
                        <p className="text-[11px] text-slate-500">
                          {inheritedAccount && !isOverridden
                            ? t('Fallback {account} · inherits {inherited}', { account: r.defaultAccount, inherited: inheritedAccount })
                            : t('Fallback {account}', { account: r.defaultAccount })}
                        </p>
                      </td>
                      <td className="px-3 py-2 text-xs text-slate-600 dark:text-slate-300">{driverCategoryLabel(t, r.category)}</td>
                      <td className="px-3 py-2">
                        <select
                          aria-label={t('Account for {line}', { line: r.label })}
                          value={e.accountId}
                          disabled={!canManage}
                          onChange={(ev) => setEdit(r.driverKey, { accountId: ev.target.value, ...(ev.target.value ? {} : { segmentCostCenterId: '' }) })}
                          className="select w-full min-w-[200px] text-xs disabled:opacity-60"
                        >
                          <option value="">{inCompanyView ? t('Inherit group mapping') : t('Use the fallback account')}</option>
                          {selectableAccounts.map((a) => (
                            <option key={a.id} value={a.id}>
                              {inCompanyView && a.companyId === null
                                ? t('{code} — {name} (group)', { code: a.code, name: a.name })
                                : `${a.code} — ${a.name}`}
                            </option>
                          ))}
                        </select>
                      </td>
                      <td className="px-3 py-2">
                        <select
                          aria-label={t('Cost centre tag for {line}', { line: r.label })}
                          title={t('Optional cost centre stamped on this posting line')}
                          value={e.segmentCostCenterId}
                          disabled={!canManage || !e.accountId}
                          onChange={(ev) => setEdit(r.driverKey, { segmentCostCenterId: ev.target.value })}
                          className="select w-full text-xs disabled:opacity-50"
                        >
                          <option value="">{t('No cost centre')}</option>
                          {visibleCostCenters.map((c) => (
                            <option key={c.id} value={c.id}>
                              {c.code}
                            </option>
                          ))}
                        </select>
                      </td>
                      <td className="px-3 py-2 text-end">
                        {canManage && isOverridden ? (
                          <button
                            type="button"
                            onClick={() => setEdit(r.driverKey, { accountId: '', segmentCostCenterId: '' })}
                            className="btn-secondary h-7 px-2 text-xs"
                          >
                            <RotateCcw className="h-3 w-3" aria-hidden /> {t('Use group mapping')}
                          </button>
                        ) : (
                          <span className="text-[11px] text-slate-400" aria-hidden>—</span>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </PanelState>
      </section>

      {/* Edit account modal */}
      <Modal
        isOpen={!!editing}
        title={t('Edit account {code}', { code: editing?.code ?? '' })}
        onClose={() => setEditing(null)}
        footer={
          <>
            <button type="button" onClick={() => setEditing(null)} className="btn-secondary">
              {t('Cancel')}
            </button>
            <button type="button" onClick={() => void saveEdit(false)} disabled={savingEdit || !writable} className="btn-primary disabled:opacity-60">
              {savingEdit ? t('Saving…') : t('Save')}
            </button>
          </>
        }
      >
        <FieldError error={editError} />
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Field label={t('Code')} required hint={t('A code that has been posted to cannot be changed.')}>
            <input value={editForm.code} onChange={(e) => setEditForm((x) => ({ ...x, code: e.target.value }))} className="input w-full font-mono" />
          </Field>
          <Field label={t('Type')}>
            <select value={editForm.accountType} onChange={(e) => setEditForm((x) => ({ ...x, accountType: e.target.value }))} className="select w-full">
              {GL_ACCOUNT_TYPES.map((type) => (
                <option key={type} value={type}>
                  {accountTypeLabel(t, type)}
                </option>
              ))}
            </select>
          </Field>
          <Field label={t('Name')} required>
            <input value={editForm.name} onChange={(e) => setEditForm((x) => ({ ...x, name: e.target.value }))} className="input w-full" />
          </Field>
          <Field label={t('Status')}>
            <select value={editForm.isActive ? 'true' : 'false'} onChange={(e) => setEditForm((x) => ({ ...x, isActive: e.target.value === 'true' }))} className="select w-full">
              <option value="true">{t('Active')}</option>
              <option value="false">{t('Inactive')}</option>
            </select>
          </Field>
        </div>
      </Modal>

      {/* Import preview (dry run) */}
      <Modal
        isOpen={!!importFile}
        title={t('Import chart of accounts')}
        onClose={closeImport}
        size="lg"
        footer={
          <>
            <button type="button" onClick={closeImport} className="btn-secondary">
              {t('Cancel')}
            </button>
            <button
              type="button"
              onClick={applyImport}
              disabled={importBusy || !preview || preview.errors.length > 0 || preview.created + preview.updated === 0}
              className="btn-primary disabled:opacity-60"
            >
              {importBusy && preview ? t('Applying…') : t('Apply import')}
            </button>
          </>
        }
      >
        <FieldError error={importError} />
        {importBusy && !preview && <p className="text-sm text-slate-500">{t('Checking the file…')}</p>}
        {preview && (
          <div className="space-y-3">
            <p className="text-xs text-slate-600 dark:text-slate-300">
              {t('Nothing has been imported yet. This is what {file} would change in {scope}.', { file: importFile?.name ?? '', scope: scopeName })}
            </p>
            <table className="w-full text-sm" aria-label={t('Import summary')}>
              <thead className={theadClass}>
                <tr>
                  <th scope="col" className={thClass}>{t('Result')}</th>
                  <th scope="col" className={`${thClass} text-end`}>{t('Accounts')}</th>
                </tr>
              </thead>
              <tbody>
                <tr className="border-t border-slate-100 dark:border-white/5">
                  <td className="px-3 py-1.5">{t('Will be created')}</td>
                  <td className="px-3 py-1.5 text-end tabular-nums">{preview.created}</td>
                </tr>
                <tr className="border-t border-slate-100 dark:border-white/5">
                  <td className="px-3 py-1.5">{t('Will be updated')}</td>
                  <td className="px-3 py-1.5 text-end tabular-nums">{preview.updated}</td>
                </tr>
                <tr className="border-t border-slate-100 dark:border-white/5">
                  <td className="px-3 py-1.5">{t('Unchanged')}</td>
                  <td className="px-3 py-1.5 text-end tabular-nums">{preview.unchanged}</td>
                </tr>
                <tr className="border-t border-slate-100 dark:border-white/5">
                  <td className="px-3 py-1.5">{t('Rows with errors')}</td>
                  <td className={`px-3 py-1.5 text-end tabular-nums ${preview.errors.length ? 'font-semibold text-rose-600' : ''}`}>{preview.errors.length}</td>
                </tr>
              </tbody>
            </table>
            {preview.errors.length > 0 && (
              <>
                <p className="text-xs font-medium text-rose-700 dark:text-rose-300">
                  {t('Fix these rows in the file and import it again. Nothing is applied while any row has an error.')}
                </p>
                <div className="max-h-60 overflow-auto rounded-lg border border-slate-200 dark:border-white/10">
                  <table className="w-full text-xs" aria-label={t('Rows with errors')}>
                    <thead className={theadClass}>
                      <tr>
                        <th scope="col" className={thClass}>{t('Row')}</th>
                        <th scope="col" className={thClass}>{t('Code')}</th>
                        <th scope="col" className={thClass}>{t('Problem')}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {preview.errors.map((er, i) => (
                        <tr key={`${er.row}-${i}`} className="border-t border-slate-100 dark:border-white/5">
                          <td className="px-3 py-1.5 tabular-nums">{er.row}</td>
                          <td className="px-3 py-1.5 font-mono">{er.code}</td>
                          <td className="px-3 py-1.5">{er.message}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </>
            )}
          </div>
        )}
      </Modal>

      {dialog}
    </div>
  );
}
