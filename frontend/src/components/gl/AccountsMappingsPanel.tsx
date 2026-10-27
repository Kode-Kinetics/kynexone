'use client';

import Link from 'next/link';
import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AlertCircle, Ban, Download, Info, Pencil, Plus, RotateCcw, Search, Upload } from 'lucide-react';
import { notifyApiError } from '../../api/client';
import {
  financeGlApi,
  GL_ACCOUNT_TYPES,
  isGlAccountImportResult,
  type GlAccount,
  type GlAccountImportResult,
  type GlMappingRow,
  type GlResolveResponse,
  type GlRulePresetResult,
} from '../../api/financeGl';
import type { CostCenterDto } from '../../api/organization';
import { BdiMessage } from '../BdiMessage';
import { msg } from '../../i18n/translations';
import { useT } from '../../hooks/useT';
import {
  addClassRule,
  apiErrorCode,
  cleanRule,
  COST_CENTRE_RULES,
  draftFromRow,
  duplicateClass,
  exclusiveState,
  fixedCostCentreOptions,
  FUNCTIONAL_CLASSES,
  isLockedIntoApprovedRun,
  mappingsChanged,
  matchesRuleFilter,
  removeClassRule,
  RESERVED_CLASS,
  ruleBlockedForType,
  setBaseAccount,
  setBaseClass,
  setClassRuleAccount,
  setFixedCostCentre,
  textDir,
  toMappingInputs,
  unusedClasses,
  type CostCentreRule,
  type DriverDraft,
} from '../../lib/glCostAccounting';
import { useAppToast } from '../ui/AppToast';
import { Modal } from '../Modal';
import {
  thCompact,
  accountTypeLabel,
  ActiveBadge,
  Badge,
  driverCategoryLabel,
  errorStatus,
  Field,
  FieldError,
  InheritedBadge,
  PanelState,
  Spinner,
  WarningNote,
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
  /** Opens the Cost accounting tab (readiness and the switch). Absent when this reader cannot see it. */
  onOpenCostAccounting?: () => void;
}

interface Loaded {
  own: GlAccount[];
  group: GlAccount[];
  maps: GlMappingRow[];
}

const isForce = (err: unknown): boolean =>
  Boolean((err as { response?: { data?: { requiresForce?: boolean } } })?.response?.data?.requiresForce);

const RULE_ICON: Record<CostCentreRule, typeof AlertCircle | null> = { Required: AlertCircle, Optional: null, NotAllowed: Ban };
const RULE_TONE = { Required: 'amber', Optional: 'slate', NotAllowed: 'slate' } as const;

/** Group rows by account type in the chart's natural order; unknown types go last. */
const TYPE_ORDER: string[] = [...GL_ACCOUNT_TYPES];
const typeIndex = (type: string) => {
  const i = TYPE_ORDER.indexOf(type === 'Revenue' ? 'Income' : type);
  return i < 0 ? TYPE_ORDER.length : i;
};

export function AccountsMappingsPanel({ scope, scopeName, canManage, costCenters, resolve, onRoutingChanged, onOpenCostAccounting }: Props) {
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

  // Cost accounting is a per-company switch: its state decides whether the rules below apply yet. A failed read just
  // leaves the note out; it never blocks the chart.
  const costing = useScopedResource(`costing:${scope ?? 'group'}`, () => financeGlApi.costAccountingReadiness(scope!), inCompanyView);
  const costingOff = inCompanyView && costing.data?.settings?.enabled === false;

  // M-7: a payroll run awaits finance review or lock, so the server refuses any change to what it would post.
  const [locked, setLocked] = useState<string | null>(null);
  const ruleLabel = useCallback(
    (rule: CostCentreRule) => (rule === 'Required' ? t('Required') : rule === 'Optional' ? t('Optional') : t('Not allowed')),
    [t],
  );
  const ruleHint = useCallback(
    (rule: CostCentreRule) =>
      rule === 'Required'
        ? t('Every posting to this account must carry a cost centre.')
        : rule === 'Optional'
          ? t('A cost centre is added when one is known.')
          : t('Postings to this account never carry a cost centre.'),
    [t],
  );

  // The mapping being edited: one draft per driver (base row + class rows), rebuilt whenever the rows are reloaded.
  const baseDrafts = useMemo(() => rows.map((r) => draftFromRow(r, inCompanyView)), [rows, inCompanyView]);
  const [drafts, setDrafts] = useState<Record<string, DriverDraft>>({});
  useEffect(() => {
    setDrafts(Object.fromEntries(baseDrafts.map((d) => [d.driverKey, d])));
    setMapReason('');
    setMapError('');
  }, [baseDrafts]);
  const [mapReason, setMapReason] = useState('');
  const [mapError, setMapError] = useState('');
  // M-7 on the mapping save: the server's sentence, and every control off until the user tries again.
  const [mapLocked, setMapLocked] = useState<string | null>(null);
  const [announce, setAnnounce] = useState('');

  const reloadAll = async () => {
    await res.reload();
    onRoutingChanged();
  };

  // Cost centres a fixed posting rule may use: this company's own active ones. Never a group (null-company) one, and
  // none in the group view: a fixed cost centre is set per company (AM-4).
  const fixedOptions = useMemo(() => [...fixedCostCentreOptions(costCenters, scope)].sort((a, b) => a.code.localeCompare(b.code, undefined, { numeric: true })), [costCenters, scope]);

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

  const draftList = useMemo(() => baseDrafts.map((b) => drafts[b.driverKey] ?? b), [baseDrafts, drafts]);
  // What a select currently shows, to set its direction (see textDir).
  const selectedAccountText = (id: string, placeholder: string) => accountLabel(id) ?? placeholder;
  const selectedCostCentreText = (id: string) => {
    const c = fixedOptions.find((x) => x.id === id);
    return c ? `${c.code} — ${c.name}` : t('None');
  };

  const dirty = useMemo(() => mappingsChanged(baseDrafts, draftList), [baseDrafts, draftList]);
  const hasDuplicate = draftList.some((d) => duplicateClass(d) !== null);

  const updateDraft = (driverKey: string, fn: (d: DriverDraft) => DriverDraft) =>
    setDrafts((prev) => {
      const current = prev[driverKey] ?? baseDrafts.find((b) => b.driverKey === driverKey);
      return current ? { ...prev, [driverKey]: fn(current) } : prev;
    });

  const classLabel = useCallback(
    (cls: string) => {
      switch (cls) {
        case 'COGS': return t('Cost of goods sold');
        case 'SELLING': return t('Selling');
        case 'G_AND_A': return t('General & administrative');
        case 'R_AND_D': return t('Research & development');
        case 'OPERATIONS': return t('Operations');
        case RESERVED_CLASS: return t('Capitalisable (reserved)');
        default: return cls;
      }
    },
    [t],
  );

  // ── Mapping save ───────────────────────────────────────────────────────────
  const [saving, setSaving] = useState(false);
  const saveMappings = async () => {
    if (!writable || !mapReason.trim()) return;
    setSaving(true);
    setMapError('');
    try {
      await financeGlApi.setMappings(toMappingInputs(draftList), scope, mapReason);
      toast.success(t('Mappings saved.'));
      await reloadAll();
    } catch (e) {
      if (isLockedIntoApprovedRun(e)) setMapLocked(serverMessage(e, t));
      else setMapError(serverMessage(e, t));
    } finally {
      setSaving(false);
    }
  };
  const mapEditable = canManage && !mapLocked;

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
      // Apply refuses an invalid file with 422 and the dry-run body (per-row errors, nothing
      // written) — no top-level message. Show those rows in the preview table, not a generic error.
      const body = (e as { response?: { data?: unknown } })?.response?.data;
      if (errorStatus(e) === 422 && isGlAccountImportResult(body) && body.errors.length > 0) {
        setPreview(body);
        setImportError(t('Nothing was imported. Fix the rows below and apply again.'));
        return;
      }
      setImportError(serverMessage(e, t));
    } finally {
      setImportBusy(false);
    }
  };

  // ── Account create / edit / delete ─────────────────────────────────────────
  const [newAcct, setNewAcct] = useState({ code: '', name: '', accountType: 'Expense', externalCode: '', costCenterRule: 'Optional' as CostCentreRule });
  const [adding, setAdding] = useState(false);
  const [addError, setAddError] = useState('');
  const [editing, setEditing] = useState<GlAccount | null>(null);
  const [editForm, setEditForm] = useState({ code: '', name: '', accountType: 'Expense', isActive: true, externalCode: '', costCenterRule: 'Optional' as CostCentreRule });
  const [editError, setEditError] = useState('');
  const [editRuleError, setEditRuleError] = useState('');
  const [savingEdit, setSavingEdit] = useState(false);
  const [deletingId, setDeletingId] = useState<string | null>(null);

  const addAccount = async () => {
    if (!newAcct.code.trim() || !newAcct.name.trim()) {
      setAddError(t('Enter an account code and a name.'));
      return;
    }
    if (ruleBlockedForType(newAcct.costCenterRule, newAcct.accountType)) {
      setAddError(t('Only expense and income accounts can require a cost centre.'));
      return;
    }
    setAdding(true);
    setAddError('');
    try {
      await financeGlApi.createAccount(
        { code: newAcct.code.trim(), name: newAcct.name.trim(), accountType: newAcct.accountType, isActive: true, externalCode: newAcct.externalCode.trim() || null, ...(newAcct.costCenterRule !== 'Optional' ? { costCenterRule: newAcct.costCenterRule } : {}) },
        scope,
      );
      setNewAcct({ code: '', name: '', accountType: newAcct.accountType, externalCode: '', costCenterRule: 'Optional' });
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
    setEditForm({ code: a.code, name: a.name, accountType: a.accountType === 'Revenue' ? 'Income' : a.accountType, isActive: a.isActive, externalCode: a.externalCode ?? '', costCenterRule: cleanRule(a.costCenterRule) });
    setEditError('');
    setEditRuleError('');
  };

  const saveEdit = async (force = false): Promise<void> => {
    if (!editing) return;
    if (!editForm.code.trim() || !editForm.name.trim()) {
      setEditError(t('Enter an account code and a name.'));
      return;
    }
    // The rule is never rewritten for the user: a type that cannot carry a Required rule stops the save.
    if (ruleBlockedForType(editForm.costCenterRule, editForm.accountType)) {
      setEditRuleError(t('Only expense and income accounts can require a cost centre.'));
      return;
    }
    // Deactivating a mapped account is confirmed below, when the server says it is still mapped.
    setSavingEdit(true);
    setEditError('');
    setEditRuleError('');
    try {
      await financeGlApi.updateAccount(
        editing.id,
        // Always sent: '' clears the ERP code on the server, a value replaces it. A server that predates cost accounting
        // sends no rule, and then none is sent back.
        {
          code: editForm.code.trim(), name: editForm.name.trim(), accountType: editForm.accountType, isActive: editForm.isActive, externalCode: editForm.externalCode.trim(),
          ...(editing.costCenterRule !== undefined ? { costCenterRule: editForm.costCenterRule } : {}),
        },
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
      } else if (apiErrorCode(err) === 'cost_centre_rule_invalid_for_type') {
        setEditRuleError(serverMessage(err, t));
      } else {
        // 409 once the account has been posted to: the server's sentence says why.
        if (isLockedIntoApprovedRun(err)) setLocked(serverMessage(err, t));
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
  const [ruleFilter, setRuleFilter] = useState('');
  const chartRows = useMemo(() => {
    const q = query.trim().toLowerCase();
    const all = [
      ...ownAccounts.map((a) => ({ account: a, inherited: false })),
      ...groupDefaults.map((a) => ({ account: a, inherited: true })),
    ];
    return all
      .filter(({ account: a }) => !typeFilter || (typeFilter === 'Income' ? a.accountType === 'Income' || a.accountType === 'Revenue' : a.accountType === typeFilter))
      .filter(({ account: a }) => matchesRuleFilter(a, ruleFilter))
      .filter(({ account: a }) => !q || a.code.toLowerCase().includes(q) || a.name.toLowerCase().includes(q) || (a.externalCode ?? '').toLowerCase().includes(q))
      .sort((x, y) => typeIndex(x.account.accountType) - typeIndex(y.account.accountType) || x.account.code.localeCompare(y.account.code, undefined, { numeric: true }));
  }, [ownAccounts, groupDefaults, query, typeFilter, ruleFilter]);
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

  // ── Apply the recommended cost centre rules (preview first, then a reason) ──
  const [presetOpen, setPresetOpen] = useState(false);
  const [preset, setPreset] = useState<GlRulePresetResult | null>(null);
  const [presetLoading, setPresetLoading] = useState(false);
  const [presetLoadError, setPresetLoadError] = useState('');
  const [presetReason, setPresetReason] = useState('');
  const [presetBusy, setPresetBusy] = useState(false);
  const [presetError, setPresetError] = useState('');

  const loadPreset = async () => {
    setPresetLoading(true);
    setPresetLoadError('');
    try {
      setPreset(await financeGlApi.previewRulePreset(scope));
    } catch (e) {
      setPreset(null);
      setPresetLoadError(serverMessage(e, t));
    } finally {
      setPresetLoading(false);
    }
  };
  const openPreset = () => {
    setPresetOpen(true);
    setPreset(null);
    setPresetReason('');
    setPresetError('');
    void loadPreset();
  };
  const applyPreset = async () => {
    if (!presetReason.trim()) {
      setPresetError(t('Enter a reason for this change.'));
      return;
    }
    setPresetBusy(true);
    setPresetError('');
    try {
      const r = await financeGlApi.applyRulePreset(presetReason.trim(), scope);
      setPresetOpen(false);
      toast.success(t('{count, plural, one {Rules applied to # account.} other {Rules applied to # accounts.}}', { count: r.changed }));
      await reloadAll();
    } catch (e) {
      if (isLockedIntoApprovedRun(e)) setLocked(serverMessage(e, t));
      setPresetError(serverMessage(e, t));
    } finally {
      setPresetBusy(false);
    }
  };

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
          <div className="overflow-x-auto rounded-lg border border-slate-200 dark:border-white/10 outline-none focus-visible:ring-2 focus-visible:ring-sapphire dark:focus-visible:ring-cyanAccent" tabIndex={0} role="group" aria-labelledby="gl-where-heading">
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
                        <p className="text-caption text-slate-500"><span className="font-mono">{r.driverKey}</span></p>
                      </td>
                      <td className="px-3 py-2">
                        {r.componentCodes && r.componentCodes.length > 0 ? (
                          <div className="flex max-w-[220px] flex-wrap gap-1">
                            {r.componentCodes.map((c) => (
                              <span key={c} className="rounded bg-slate-100 px-1.5 py-0.5 font-mono text-caption text-slate-600 dark:bg-white/10 dark:text-slate-300">
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
                <button type="button" onClick={openPreset} disabled={!writable || !!locked} data-testid="cc-acct-preset-open" className="btn-secondary h-8 px-3 text-xs disabled:opacity-60">
                  {t('Apply recommended rules')}
                </button>
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

        <div role="note" data-testid="cc-acct-explainer" className="mb-3 flex items-start gap-2 rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-700 dark:bg-white/[0.04] dark:text-slate-300">
          <Info className="mt-0.5 h-3.5 w-3.5 shrink-0 text-sapphire dark:text-cyanAccent" aria-hidden />
          <p>{t('Account type says what kind of money an account holds (asset, liability, equity, income or expense); a cost centre says which team or department the cost belongs to, and only income and expense accounts can carry one.')}</p>
        </div>
        {costingOff && (
          <div data-testid="cc-acct-costing-off-note">
            <WarningNote>
              {t('Cost accounting is off for this company. These rules apply once you turn it on.')}
              {onOpenCostAccounting && (
                <button type="button" onClick={onOpenCostAccounting} className="ms-2 font-semibold underline underline-offset-2 hover:no-underline">
                  {t('Open Cost accounting')}
                </button>
              )}
            </WarningNote>
          </div>
        )}
        {locked && (
          <div role="alert" data-testid="cc-acct-locked-banner" className="mb-3 flex flex-wrap items-center justify-between gap-2 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-xs text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">
            <p>{locked}</p>
            <button type="button" onClick={() => setLocked(null)} className="btn-secondary h-7 px-2 text-xs">{t('Retry')}</button>
          </div>
        )}

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
              data-setup-filter
              aria-label={t('Search accounts by code or name')}
              placeholder={t('Search by code or name')}
              className="input h-8 w-full ps-8 text-xs"
            />
          </div>
          <select
            value={typeFilter}
            onChange={(e) => setTypeFilter(e.target.value)}
            data-setup-filter
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
          <select
            value={ruleFilter}
            onChange={(e) => setRuleFilter(e.target.value)}
            data-setup-filter
            data-testid="cc-acct-rule-filter"
            aria-label={t('Filter by cost centre rule')}
            className="select h-8 text-xs"
          >
            <option value="">{t('All rules')}</option>
            {COST_CENTRE_RULES.map((rule) => (
              <option key={rule} value={rule}>
                {ruleLabel(rule)}
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
          <div className="overflow-x-auto rounded-lg border border-slate-200 dark:border-white/10 outline-none focus-visible:ring-2 focus-visible:ring-sapphire dark:focus-visible:ring-cyanAccent" tabIndex={0} role="group" aria-labelledby="gl-coa-heading">
            {/* Fixed layout: every column shares the width the page gives it (about 640px beside Setup's menu), long text wraps or is cut with a title, and nothing needs a horizontal scrollbar. */}
            <table className="w-full table-fixed text-sm">
              <colgroup>
                <col className="w-[8%]" />
                <col className="w-[13%]" />
                <col className="w-[27%]" />
                <col className="w-[9%]" />
                <col className="w-[14%]" />
                <col className="w-[8%]" />
                <col className="w-[8%]" />
                <col className="w-[13%]" />
              </colgroup>
              <thead className={theadClass}>
                <tr>
                  <th scope="col" className={thCompact}>{t('Code')}</th>
                  <th scope="col" className={thCompact}>{t('ERP account code')}</th>
                  <th scope="col" className={thCompact}>{t('Name')}</th>
                  <th scope="col" className={thCompact}>{t('Type')}</th>
                  <th scope="col" className={thCompact}>{t('Cost centre rule')}</th>
                  <th scope="col" className={thCompact}>{t('Scope')}</th>
                  <th scope="col" className={thCompact}>{t('Status')}</th>
                  <th scope="col" className={`${thCompact} text-end`}>{t('Actions')}</th>
                </tr>
              </thead>
              <tbody>
                {groups.length === 0 && (
                  <tr>
                    <td colSpan={8} className="px-3 py-6 text-center text-xs text-slate-500">
                      {t('No accounts match your search.')}
                    </td>
                  </tr>
                )}
                {groups.map((g) => (
                  <Fragment key={g.type}>
                    <tr className="border-t border-slate-200 bg-slate-50/70 dark:border-white/10 dark:bg-white/[0.02]">
                      <th scope="colgroup" colSpan={8} className="px-3 py-1.5 text-start text-xs font-semibold text-slate-600 dark:text-slate-300">
                        {t('{type} ({count})', { type: accountTypeLabel(t, g.type), count: g.rows.length })}
                      </th>
                    </tr>
                    {g.rows.map(({ account: a, inherited }) => (
                      <tr key={a.id} className="border-t border-slate-100 dark:border-white/5">
                        <td className="px-2 py-2 font-mono text-xs"><bdi dir="ltr" className="break-all">{a.code}</bdi></td>
                        <td className="px-2 py-2 font-mono text-xs text-slate-600 dark:text-slate-300">
                          {a.externalCode ? <bdi dir="ltr" className="break-all">{a.externalCode}</bdi> : <><span className="text-slate-400" aria-hidden>—</span><span className="sr-only">{t('No ERP account code')}</span></>}
                        </td>
                        <td className="px-2 py-2"><span data-testid={`cc-acct-name-${a.code}`} dir="auto" title={a.name} className="block overflow-hidden text-ellipsis whitespace-nowrap">{a.name}</span></td>
                        <td className="px-2 py-2 text-xs text-slate-600 dark:text-slate-300">{accountTypeLabel(t, a.accountType)}</td>
                        <td className="px-2 py-2">
                          {(() => {
                            const rule = cleanRule(a.costCenterRule);
                            const Icon = RULE_ICON[rule];
                            return (
                              <Badge
                                tone={RULE_TONE[rule]}
                                testId={`cc-acct-rule-${a.code}`}
                                label={inherited ? t('Rule: {rule} (inherited)', { rule: ruleLabel(rule) }) : t('Cost centre rule: {rule}', { rule: ruleLabel(rule) })}
                              >
                                {Icon && <Icon className="h-3 w-3" aria-hidden />}
                                {ruleLabel(rule)}
                              </Badge>
                            );
                          })()}
                        </td>
                        <td className="px-2 py-2">
                          {inherited ? <InheritedBadge short /> : inCompanyView ? <Badge tone="violet">{t('Company')}</Badge> : <Badge tone="sapphire">{t('Group')}</Badge>}
                        </td>
                        <td className="px-2 py-2">
                          <ActiveBadge active={a.isActive} />
                        </td>
                        <td className="px-2 py-2">
                          <div className="flex flex-col items-end gap-1.5">
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
                                  className="h-7 rounded-lg border border-slate-200 px-2 text-xs font-medium text-rose-600 hover:border-rose-300 hover:bg-rose-50 disabled:opacity-40 dark:border-white/10 dark:text-rose-400 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10"
                                >
                                  {t('Delete')}
                                </button>
                              </>
                            ) : (
                              <span className="text-caption text-slate-400" aria-hidden>—</span>
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
            <div className="flex flex-wrap items-end gap-2">
              <div className="w-28"><Field label={t('Code')} required>
                <input value={newAcct.code} onChange={(e) => setNewAcct((x) => ({ ...x, code: e.target.value }))} className="input h-8 w-full font-mono text-xs" placeholder="5006" />
              </Field></div>
              <div className="min-w-[11rem] flex-1"><Field label={t('Name')} required>
                <input value={newAcct.name} onChange={(e) => setNewAcct((x) => ({ ...x, name: e.target.value }))} className="input h-8 w-full text-xs" placeholder={t('Account name')} />
              </Field></div>
              <div className="w-36"><Field label={t('Type')}>
                <select value={newAcct.accountType} onChange={(e) => setNewAcct((x) => ({ ...x, accountType: e.target.value }))} className="select h-8 w-full text-xs" aria-describedby="gl-new-type-hint">
                  {GL_ACCOUNT_TYPES.map((type) => (
                    <option key={type} value={type}>
                      {accountTypeLabel(t, type)}
                    </option>
                  ))}
                </select>
              </Field></div>
              <div className="w-40"><Field label={t('Cost centre rule')}>
                <select
                  value={newAcct.costCenterRule}
                  onChange={(e) => setNewAcct((x) => ({ ...x, costCenterRule: e.target.value as CostCentreRule }))}
                  data-testid="cc-acct-form-rule"
                  className="select h-8 w-full text-xs"
                >
                  {COST_CENTRE_RULES.map((rule) => (
                    <option key={rule} value={rule} disabled={ruleBlockedForType(rule, newAcct.accountType)}>
                      {ruleLabel(rule)}
                    </option>
                  ))}
                </select>
              </Field></div>
              <div className="w-40"><Field label={t('ERP account code')}>
                <input
                  value={newAcct.externalCode}
                  onChange={(e) => setNewAcct((x) => ({ ...x, externalCode: e.target.value }))}
                  maxLength={60}
                  className="input h-8 w-full font-mono text-xs"
                  placeholder={t('Optional')}
                  aria-describedby="gl-new-erp-code-hint"
                />
              </Field></div>
              <button type="button" onClick={addAccount} disabled={adding || !writable} className="btn-secondary h-8 px-3 text-xs disabled:opacity-60">
                <Plus className="h-3 w-3" aria-hidden /> {t('Add account')}
              </button>
            </div>
            <p id="gl-new-type-hint" data-testid="cc-acct-type-hint" className="mt-1.5 text-caption leading-snug text-slate-500 dark:text-slate-400">
              {t('What kind of account this is. Cost centres are set separately, in Cost centre rule.')}
            </p>
            <p id="gl-new-erp-code-hint" className="mt-1.5 text-caption leading-snug text-slate-500 dark:text-slate-400">
              {t('ERP account code')}: {t('The account code in your ERP if it differs from this one. Journal exports use it.')}
            </p>
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
            <button
              type="button"
              onClick={saveMappings}
              disabled={saving || !dirty || !writable || !mapReason.trim() || hasDuplicate || !!mapLocked}
              data-testid="cc-map-save"
              className="btn-primary text-xs disabled:opacity-60"
            >
              {saving ? t('Saving…') : t('Save mappings')}
            </button>
          )}
        </div>

        <div className="mb-3 space-y-1 text-caption leading-snug text-slate-500 dark:text-slate-400">
          <p>{t('Functional class: use a different account for cost centres of this class, for example Selling or Operations.')}</p>
          <p>{t('Fixed cost centre: always post this line to one cost centre, whatever the employee\'s own cost centre.')}</p>
          <p id="cc-map-account-first-hint">{t('Choose an account for a line before setting its functional class or fixed cost centre.')}</p>
          {!inCompanyView && (
            <p id="cc-map-group-hint" data-testid="cc-map-group-scope-hint" className="font-medium text-slate-600 dark:text-slate-300">
              {t('Fixed cost centres are set per company. Switch to a company to set one.')}
            </p>
          )}
        </div>

        {mapLocked && (
          <div role="alert" data-testid="cc-map-locked-banner" className="mb-3 flex flex-wrap items-center justify-between gap-2 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-xs text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">
            <p>{mapLocked}</p>
            <div className="flex items-center gap-2">
              <Link href="/payroll" className="font-semibold underline underline-offset-2 hover:no-underline">{t('Open the approved run')}</Link>
              <button type="button" onClick={() => setMapLocked(null)} className="btn-secondary h-7 px-2 text-xs">{t('Retry')}</button>
            </div>
          </div>
        )}
        <FieldError error={mapError} testId="cc-map-error" />

        {canManage && dirty && (
          <div className="mb-3 max-w-xl">
            <Field label={t('Reason for this change')} required hint={t('Recorded in the audit log.')}>
              <input value={mapReason} onChange={(e) => setMapReason(e.target.value)} disabled={!!mapLocked} data-testid="cc-map-reason" className="input w-full" maxLength={500} />
            </Field>
          </div>
        )}

        <PanelState
          loading={res.loading && !res.data}
          error={res.error}
          onRetry={() => void res.reload()}
          empty={rows.length === 0}
          emptyLabel={inCompanyView ? t('No posting drivers are set up yet. Ask a group administrator to load the standard payroll chart.') : t('No posting drivers are set up yet. Load the standard payroll chart above to get started.')}
        >
          <div className="overflow-x-auto rounded-lg border border-slate-200 dark:border-white/10 outline-none focus-visible:ring-2 focus-visible:ring-sapphire dark:focus-visible:ring-cyanAccent" tabIndex={0} role="group" aria-labelledby="gl-map-heading">
            <table className="w-full table-fixed text-sm">
              <colgroup>
                <col className="w-[24%]" />
                <col className="w-[23%]" />
                <col className="w-[17%]" />
                <col className="w-[19%]" />
                <col className="w-[17%]" />
              </colgroup>
              <thead className={theadClass}>
                <tr>
                  <th scope="col" className={thCompact}>{t('Posting line')}</th>
                  <th scope="col" className={thCompact}>{t('Account')}</th>
                  <th scope="col" className={thCompact}>{t('Functional class')}</th>
                  <th scope="col" className={thCompact}>{t('Fixed cost centre')}</th>
                  <th scope="col" className={`${thCompact} text-end`}>{t('Actions')}</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => {
                  const d = drafts[r.driverKey] ?? baseDrafts.find((b) => b.driverKey === r.driverKey);
                  if (!d) return null;
                  const ex = exclusiveState(d, inCompanyView);
                  const dup = duplicateClass(d);
                  const isOverridden = inCompanyView && d.accountId !== '';
                  const inheritedAccount = inCompanyView && r.inherited ? accountLabel(r.mappedAccountId) : null;
                  const hintId = `cc-map-excl-${r.driverKey.replace(/[^A-Za-z0-9_-]/g, '_')}`;
                  const hint = (
                    <p id={hintId} data-testid={`cc-map-exclusive-hint-${r.driverKey}`} className="mt-1 text-caption leading-snug text-slate-500 dark:text-slate-400">
                      {t('A posting rule uses either a functional class or a fixed cost centre, not both.')}
                    </p>
                  );
                  const unused = unusedClasses(d);
                  return (
                    <Fragment key={r.driverKey}>
                      <tr data-testid={`cc-map-row-${r.driverKey}`} className="border-t border-slate-100 dark:border-white/5">
                        <td className="px-2 py-2">
                          <div className="flex flex-wrap items-center gap-1.5">
                            <span className="break-words text-slate-800 dark:text-slate-100">{r.label}</span>
                            {isOverridden && <Badge tone="violet" wrap>{t('Company override')}</Badge>}
                            {inCompanyView && !isOverridden && r.mappedAccountId && <InheritedBadge short />}
                          </div>
                          <p className="text-caption text-slate-500">{driverCategoryLabel(t, r.category)}</p>
                          <p className="break-words text-caption text-slate-500">
                            {inheritedAccount && !isOverridden
                              ? <BdiMessage testId={`cc-map-fallback-${r.driverKey}`} message={msg('Fallback {account} · inherits {inherited}')} values={{ account: r.defaultAccount, inherited: inheritedAccount }} />
                              : <BdiMessage testId={`cc-map-fallback-${r.driverKey}`} message={msg('Fallback {account}')} values={{ account: r.defaultAccount }} />}
                          </p>
                        </td>
                        <td className="px-2 py-2">
                          <select
                            aria-label={t('Account for {line}', { line: r.label })}
                            dir={textDir(selectedAccountText(d.accountId, inCompanyView ? t('Inherit group mapping') : t('Use the fallback account')))}
                            value={d.accountId}
                            disabled={!mapEditable}
                            onChange={(ev) => updateDraft(r.driverKey, (x) => setBaseAccount(x, ev.target.value))}
                            className="select w-full min-w-0 text-xs disabled:opacity-60"
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
                        <td className="px-2 py-2">
                          <select
                            aria-label={t('Functional class for {line}', { line: r.label })}
                            value={d.cls}
                            disabled={!mapEditable || ex.classDisabled}
                            aria-describedby={ex.why === 'fixed' ? hintId : ex.why === 'account' ? 'cc-map-account-first-hint' : undefined}
                            data-testid={`cc-map-class-${r.driverKey}`}
                            onChange={(ev) => updateDraft(r.driverKey, (x) => setBaseClass(x, ev.target.value))}
                            className="select w-full min-w-0 text-xs disabled:opacity-60"
                          >
                            <option value="">{t('Any class')}</option>
                            {FUNCTIONAL_CLASSES.map((c) => (
                              <option key={c} value={c} disabled={c === RESERVED_CLASS}>
                                {classLabel(c)}
                              </option>
                            ))}
                          </select>
                          {ex.why === 'fixed' && hint}
                          {dup && (
                            <p role="alert" data-testid={`cc-map-dup-error-${r.driverKey}`} className="mt-1 text-caption font-medium text-rose-700 dark:text-rose-300">
                              {t('This line already has a rule for this class.')}
                            </p>
                          )}
                        </td>
                        <td className="px-2 py-2">
                          <select
                            aria-label={t('Fixed cost centre for {line}', { line: r.label })}
                            dir={textDir(selectedCostCentreText(d.fixedCostCentreId))}
                            value={d.fixedCostCentreId}
                            disabled={!mapEditable || ex.fixedDisabled}
                            aria-describedby={ex.why === 'group' ? 'cc-map-group-hint' : ex.why === 'class' ? hintId : ex.why === 'account' ? 'cc-map-account-first-hint' : undefined}
                            data-testid={`cc-map-fixed-${r.driverKey}`}
                            onChange={(ev) => updateDraft(r.driverKey, (x) => setFixedCostCentre(x, ev.target.value))}
                            className="select w-full min-w-0 text-xs disabled:opacity-50"
                          >
                            <option value="">{t('None')}</option>
                            {fixedOptions.map((c) => (
                              <option key={c.id} value={c.id}>
                                {`${c.code} — ${c.name}`}
                              </option>
                            ))}
                          </select>
                          {ex.why === 'class' && hint}
                        </td>
                        <td className="px-2 py-2 text-end">
                          <div className="flex flex-col items-end gap-1.5">
                            {mapEditable && (
                              <select
                                value=""
                                aria-label={t('Add rule for a class')}
                                data-testid={`cc-map-add-class-${r.driverKey}`}
                                onChange={(ev) => {
                                  const cls = ev.target.value;
                                  if (!cls) return;
                                  updateDraft(r.driverKey, (x) => addClassRule(x, cls));
                                  setAnnounce(t('Added a rule for {class}.', { class: classLabel(cls) }));
                                }}
                                className="select h-8 w-full min-w-0 text-xs"
                              >
                                <option value="">{t('Add rule for a class')}</option>
                                {unused.map((c) => (
                                  <option key={c} value={c} disabled={c === RESERVED_CLASS}>
                                    {classLabel(c)}
                                  </option>
                                ))}
                              </select>
                            )}
                            {mapEditable && isOverridden && (
                              <button
                                type="button"
                                onClick={() => updateDraft(r.driverKey, (x) => setBaseAccount(x, ''))}
                                className="btn-secondary h-7 px-2 text-xs"
                              >
                                <RotateCcw className="h-3 w-3" aria-hidden /> {t('Use group mapping')}
                              </button>
                            )}
                          </div>
                        </td>
                      </tr>
                      {d.classRules.map((rule) => (
                        <tr key={rule.cls} data-testid={`cc-map-class-row-${r.driverKey}-${rule.cls}`} className="border-t border-slate-100 bg-slate-50/50 dark:border-white/5 dark:bg-white/[0.02]">
                          <td className="border-s-2 border-violet-200 py-2 ps-4 pe-2 dark:border-violet-500/30">
                            <div className="flex flex-wrap items-center gap-1.5">
                              <span className="text-xs font-medium text-slate-700 dark:text-slate-200">{t('{class} rule', { class: classLabel(rule.cls) })}</span>
                              {rule.inherited && <InheritedBadge />}
                            </div>
                          </td>
                          <td className="px-2 py-2">
                            <select
                              aria-label={t('Class rule for {line}: {class}', { line: r.label, class: classLabel(rule.cls) })}
                              dir={textDir(selectedAccountText(rule.accountId, rule.inherited ? t('Inherit group mapping') : t('Use the fallback account')))}
                              value={rule.accountId}
                              disabled={!mapEditable}
                              onChange={(ev) => updateDraft(r.driverKey, (x) => setClassRuleAccount(x, rule.cls, ev.target.value))}
                              className="select w-full min-w-0 text-xs disabled:opacity-60"
                            >
                              <option value="">{rule.inherited ? t('Inherit group mapping') : t('Use the fallback account')}</option>
                              {selectableAccounts.map((a) => (
                                <option key={a.id} value={a.id}>
                                  {inCompanyView && a.companyId === null
                                    ? t('{code} — {name} (group)', { code: a.code, name: a.name })
                                    : `${a.code} — ${a.name}`}
                                </option>
                              ))}
                            </select>
                          </td>
                          <td className="px-2 py-2 text-xs text-slate-600 dark:text-slate-300">{classLabel(rule.cls)}</td>
                          <td className="px-2 py-2 text-caption text-slate-400" aria-hidden>—</td>
                          <td className="px-2 py-2 text-end">
                            {mapEditable && !rule.inherited ? (
                              <button
                                type="button"
                                data-testid={`cc-map-remove-${r.driverKey}-${rule.cls}`}
                                aria-label={t('Remove the rule for {line}: {class}', { line: r.label, class: classLabel(rule.cls) })}
                                onClick={() => {
                                  updateDraft(r.driverKey, (x) => removeClassRule(x, rule.cls));
                                  setAnnounce(t('Removed the rule for {class}.', { class: classLabel(rule.cls) }));
                                  requestAnimationFrame(() => document.querySelector<HTMLElement>(`[data-testid="cc-map-class-${r.driverKey}"]`)?.focus());
                                }}
                                className="btn-secondary h-7 px-2 text-xs"
                              >
                                {t('Remove rule')}
                              </button>
                            ) : (
                              <span className="text-caption text-slate-400" aria-hidden>—</span>
                            )}
                          </td>
                        </tr>
                      ))}
                    </Fragment>
                  );
                })}
              </tbody>
            </table>
          </div>
        </PanelState>
        <p role="status" className="sr-only">{announce}</p>
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
          <Field label={t('Type')} hint={t('What kind of account this is. Cost centres are set separately, in Cost centre rule.')}>
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
          {editing?.costCenterRule !== undefined && (
            <div className="sm:col-span-2">
              <Field
                label={t('Cost centre rule')}
                hint={
                  ruleBlockedForType('Required', editForm.accountType)
                    ? `${ruleHint(editForm.costCenterRule)} ${t('Only expense and income accounts can require a cost centre.')}`
                    : ruleHint(editForm.costCenterRule)
                }
              >
                <select
                  value={editForm.costCenterRule}
                  onChange={(e) => setEditForm((x) => ({ ...x, costCenterRule: e.target.value as CostCentreRule }))}
                  disabled={!!locked}
                  data-testid="cc-acct-edit-rule"
                  className="select w-full"
                >
                  {COST_CENTRE_RULES.map((rule) => (
                    <option key={rule} value={rule} disabled={ruleBlockedForType(rule, editForm.accountType)}>
                      {ruleLabel(rule)}
                    </option>
                  ))}
                </select>
              </Field>
              {editRuleError && (
                <p role="alert" data-testid="cc-acct-edit-rule-error" className="mt-1.5 rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-600 dark:bg-rose-500/10 dark:text-rose-400">
                  {editRuleError}
                </p>
              )}
            </div>
          )}
          <div className="sm:col-span-2">
            <Field label={t('ERP account code')} hint={t('The account code in your ERP if it differs from this one. Journal exports use it.')}>
              <input
                value={editForm.externalCode}
                onChange={(e) => setEditForm((x) => ({ ...x, externalCode: e.target.value }))}
                maxLength={60}
                className="input w-full font-mono"
                placeholder={t('Optional')}
              />
            </Field>
          </div>
        </div>
      </Modal>

      {/* Apply the recommended cost centre rules */}
      <Modal
        isOpen={presetOpen}
        title={t('Apply recommended cost centre rules?')}
        onClose={() => setPresetOpen(false)}
        footer={
          <>
            <button type="button" onClick={() => setPresetOpen(false)} className="btn-secondary">
              {t('Cancel')}
            </button>
            <button
              type="button"
              onClick={() => void applyPreset()}
              disabled={presetBusy || presetLoading || !preset || preset.changed === 0 || !presetReason.trim() || !!locked}
              data-testid="cc-acct-preset-apply"
              className="btn-primary disabled:opacity-60"
            >
              {presetBusy ? t('Saving…') : t('Apply rules')}
            </button>
          </>
        }
      >
        <div data-testid="cc-acct-preset-dialog" className="space-y-3">
          <FieldError error={presetError} />
          {presetLoading && <Spinner />}
          {presetLoadError && !presetLoading && (
            <div role="alert" className="flex flex-wrap items-center justify-between gap-2 rounded-lg bg-rose-50 px-3 py-2.5 text-sm text-rose-600 dark:bg-rose-500/10 dark:text-rose-400">
              <p>{t('The preview could not be loaded.')} {presetLoadError}</p>
              <button type="button" onClick={() => void loadPreset()} className="btn-secondary h-8 px-3 text-xs">{t('Retry')}</button>
            </div>
          )}
          {preset && !presetLoading && (
            <>
              <p className="text-sm text-slate-600 dark:text-slate-300">
                {t('Expense and income accounts become Required. Asset, liability and equity accounts become Not allowed.')}
              </p>
              {preset.changed === 0 ? (
                <p data-testid="cc-acct-preset-nochange" role="status" className="rounded-lg bg-emerald-50 px-3 py-2 text-sm text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-300">
                  {t('All accounts already follow the recommended rules.')}
                </p>
              ) : (
                <>
                  <p data-testid="cc-acct-preset-count" role="status" className="text-sm font-semibold text-slate-900 dark:text-white">
                    {t('{count, plural, one {# account will change.} other {# accounts will change.}}', { count: preset.changed })}
                  </p>
                  <table className="w-full text-sm">
                    <caption className="sr-only">{t('Apply recommended cost centre rules?')}</caption>
                    <thead className={theadClass}>
                      <tr>
                        <th scope="col" className={thClass}>{t('Account type')}</th>
                        <th scope="col" className={thClass}>{t('Becomes')}</th>
                        <th scope="col" className={`${thClass} text-end`}>{t('Accounts')}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {preset.byType.map((row) => (
                        <tr key={`${row.accountType}-${row.to}`} className="border-t border-slate-100 dark:border-white/5">
                          <td className="px-3 py-1.5">{accountTypeLabel(t, row.accountType)}</td>
                          <td className="px-3 py-1.5">{ruleLabel(cleanRule(row.to))}</td>
                          <td className="px-3 py-1.5 text-end tabular-nums">{row.count}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </>
              )}
              <p className="text-xs text-slate-500 dark:text-slate-400">{t('Nothing changes in payroll until Cost accounting is turned on.')}</p>
              <Field label={t('Reason for this change')} required hint={t('Recorded in the audit log.')}>
                <input value={presetReason} onChange={(e) => setPresetReason(e.target.value)} data-testid="cc-acct-preset-reason" className="input w-full" maxLength={500} />
              </Field>
            </>
          )}
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
