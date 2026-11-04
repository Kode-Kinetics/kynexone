'use client';

import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react';
import Link from 'next/link';
import { createPortal } from 'react-dom';
import { ChevronRight, MoreHorizontal, Pencil, Plus, Lock } from 'lucide-react';
import { costCentersApi, type CompanyDto, type CostCenterDto } from '../../api/organization';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { describeApiError } from '../../lib/apiError';
import {
  buildTree, codeProblem, filterItems, inUseCounts, inUseFromError, inUseTotalOf, isCodeLocked, isFiltering, NO_COMPANY_FILTER, parentOptions, saveBody,
  type InUseKey, type MasterForm,
} from '../../lib/costCentreMaster';
import { FUNCTIONAL_CLASSES, pickServerMessage, COST_ACCOUNTING_SETUP_HREF } from '../../lib/glCostAccounting';
import { usageSentence } from '../../lib/orgUsage';
import { Modal } from '../Modal';
import { ConfirmDialog } from '../ui/ConfirmDialog';
import { useSetupEditorState } from './SetupEditorState';
import { downloadCsv, ImportExportToolbar } from '../ImportExportToolbar';
import { Badge, FieldError, LoadError, Spinner, WarningNote } from '../gl/glUi';
import { useOrgCompanies } from './OrgCompanyControls';
import { useOrgWriteAccess } from './OrgRowActions';

/**
 * Setup → Company structure → Cost centres (screen 1). A parent tree with the functional class, the ERP code and the
 * company; a reason for the changes finance audits; the M-8 "in use" and M-9 "code locked" refusals shown as the server
 * words them. Without organization.costcenter.manage everything is read-only, with the reason in text.
 */

const emptyForm = (companyId: string): MasterForm => ({ companyId, code: '', name: '', nameAr: '', isActive: true, parentId: '', functionalClass: '', glSegment: '', reason: '' });
const formOf = (c: CostCenterDto): MasterForm => ({
  companyId: c.companyId ?? '', code: c.code, name: c.name, nameAr: c.nameAr ?? '', isActive: c.isActive,
  parentId: c.parentId ?? '', functionalClass: c.functionalClass ?? '', glSegment: c.glSegment ?? '', reason: '',
});

const IN_USE_LINKS: Partial<Record<InUseKey, string>> = {
  departments: '/setup?tab=departments',
  employees: '/people',
  rules: '/setup?tab=glMapping',
  default: COST_ACCOUNTING_SETUP_HREF,
};

export function CostCentreMasterPanel({ companies }: { companies: CompanyDto[] }) {
  const { t, locale } = useLocale();
  const { hasPermission, hasRole } = useAuth();
  // The API's rules, so a control the server would refuse is shown disabled with the reason, never hidden:
  // create, edit, import and delete need the Admin or HR Manager role AND organization.costcenter.manage; delete and
  // restore also need organization.delete (restore carries no role rule); the company must be within the person's grant.
  const hasManage = hasPermission('organization.costcenter.manage');
  const hasWriteRole = hasRole('Admin') || hasRole('HR Manager');
  const canManage = hasManage && hasWriteRole;
  const hasDelete = hasPermission('organization.delete');
  const writeBlockedReason = !hasManage
    ? t('You can view cost centres but not change them. Changing them needs the Manage cost centres permission.')
    : !hasWriteRole ? t('You can view cost centres but not change them. Changing them needs the Admin or HR Manager role.') : undefined;
  const deleteBlockedReason = hasDelete ? undefined : t('Deleting or restoring a cost centre needs the organization.delete permission.');
  const writeAccess = useOrgWriteAccess(null);
  const blockedNoteId = useId();
  const canSetClass = hasPermission('finance.gl.manage');
  const org = useOrgCompanies(companies);
  const headingId = useId();

  const [items, setItems] = useState<CostCenterDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState('');
  const [view, setView] = useState<'tree' | 'flat'>('tree');
  const [filters, setFilters] = useState({ company: '', cls: '', status: '', search: '' });
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [savedNote, setSavedNote] = useState('');

  const [formOpen, setFormOpen] = useState(false);
  const [editing, setEditing] = useState<CostCenterDto | null>(null);
  const [form, setForm] = useState<MasterForm>(emptyForm(''));
  const [errors, setErrors] = useState<{ code?: string; name?: string; company?: string; reason?: string }>({});
  const [formError, setFormError] = useState('');
  const [lockedNote, setLockedNote] = useState('');
  const [saving, setSaving] = useState(false);

  const [action, setAction] = useState<{ kind: 'deactivate' | 'delete'; item: CostCenterDto } | null>(null);
  const [actionReason, setActionReason] = useState('');
  const [actionError, setActionError] = useState('');
  const [actionReasonError, setActionReasonError] = useState('');
  const [inUse, setInUse] = useState<{ item: CostCenterDto; counts: Record<string, number> } | null>(null);
  const [rowError, setRowError] = useState('');
  const [discarding, setDiscarding] = useState<null | 'form' | 'action'>(null);
  const initialForm = useRef<MasterForm>(emptyForm(''));
  const [menu, setMenu] = useState<{ id: string; top: number; above: number; start: number; viaKeyboard: boolean } | null>(null);
  const triggerRefs = useRef(new Map<string, HTMLButtonElement>());

  // Setup asks before leaving the page while a form or a reason dialog is open (like the other Setup tabs).
  useSetupEditorState(formOpen || !!action);

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError('');
    try { setItems(await costCentersApi.listAll()); }
    catch (err) { setLoadError(describeApiError(err, t)); }
    finally { setLoading(false); }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(() => { void load(); }, [load]);

  const classLabel = useCallback((cls?: string | null) => {
    switch (cls) {
      case 'COGS': return t('Cost of goods sold');
      case 'SELLING': return t('Selling');
      case 'G_AND_A': return t('General & administrative');
      case 'R_AND_D': return t('Research & development');
      case 'OPERATIONS': return t('Operations');
      case 'CAPITALISABLE': return t('Capitalisable (reserved)');
      default: return cls ?? '';
    }
  }, [t]);
  const display = useCallback((c: CostCenterDto) => (locale === 'ar' && c.nameAr ? c.nameAr : c.name), [locale]);

  const filtering = isFiltering(filters);
  const flatList = filtering || view === 'flat';
  const hasHierarchy = useMemo(() => items.some((i) => !!i.parentId), [items]);
  const codeById = useMemo(() => new Map(items.map((i) => [i.id, i.code])), [items]);
  const rows = useMemo(() => {
    if (flatList) {
      return filterItems(items, filters).slice().sort((a, b) => a.code.localeCompare(b.code, 'en', { numeric: true })).map((item) => ({ item, depth: 0, hasChildren: false }));
    }
    return buildTree(items, expanded);
  }, [expanded, flatList, filters, items]);
  const unassignedCount = items.filter((i) => !i.companyId).length;

  // ── Form ──
  const openNew = () => {
    setEditing(null);
    initialForm.current = emptyForm(org.defaultCompanyId || '');
    setForm(initialForm.current);
    setErrors({}); setFormError(''); setLockedNote('');
    setFormOpen(true);
  };
  const openEdit = (c: CostCenterDto) => {
    setEditing(c);
    initialForm.current = formOf(c);
    setForm(initialForm.current);
    setErrors({}); setFormError(''); setLockedNote('');
    setFormOpen(true);
  };
  const formDirty = formOpen && (Object.keys(form) as Array<keyof MasterForm>).some((k) => form[k] !== initialForm.current[k]);
  // Typed input is not thrown away by Esc, a click outside or Cancel without asking; a pristine form closes at once.
  const closeForm = () => { if (saving) return; if (formDirty) setDiscarding('form'); else setFormOpen(false); };
  const closeAction = () => { if (saving) return; if (actionReason.trim()) setDiscarding('action'); else setAction(null); };
  const set = <K extends keyof MasterForm>(key: K, value: MasterForm[K]) => setForm((f) => ({ ...f, [key]: value }));

  // M-9: the server says when a posted line carries the code; the 409 handler below stays for an older server.
  const codeLockedByServer = !!editing && isCodeLocked(editing);
  const classChanged = editing ? form.functionalClass !== (editing.functionalClass ?? '') : !!form.functionalClass;
  // The ERP code decides where journal exports land: finance's to change on an existing cost centre, and recorded with a reason.
  const segmentChanged = !!editing && form.glSegment.trim().toLowerCase() !== (editing.glSegment ?? '').toLowerCase();
  const deactivating = !!editing && editing.isActive && !form.isActive;
  const movingCompany = !!editing && !!editing.companyId && form.companyId !== editing.companyId;
  const needsReason = classChanged || segmentChanged || deactivating || movingCompany;
  // A company the person's grant does not cover cannot be written, so it cannot be picked (its option is disabled) and a
  // cost centre already in one cannot be moved out of it from here: the select is disabled and says why.
  const companyBlockedReason = formOpen && form.companyId && editing && !writeAccess(editing.companyId).allowed ? writeAccess(editing.companyId).reason : undefined;
  const parents = parentOptions(items, { id: editing?.id, companyId: form.companyId, parentId: editing?.parentId ?? null });

  const showServerError = (err: unknown) => pickServerMessage((err as { response?: { data?: unknown } })?.response?.data, locale) ?? describeApiError(err, t);

  const save = async () => {
    const next: typeof errors = {};
    const problem = codeProblem(form.code);
    if (problem === 'required') next.code = t('Enter a code.');
    else if (problem === 'format') next.code = t('Start with a letter or number and use only letters, numbers, . - _ (max 30).');
    if (!form.name.trim()) next.name = t('Enter a name.');
    if (!form.companyId && org.options.length > 1) next.company = t('Choose the company this belongs to.');
    if (needsReason && !form.reason.trim()) next.reason = t('Enter a reason for this change.');
    setErrors(next);
    setFormError('');
    if (Object.keys(next).length) return;
    setSaving(true);
    try {
      const body = saveBody(editing, form);
      if (editing) await costCentersApi.update(editing.id, body);
      else await costCentersApi.create(body);
      setFormOpen(false);
      setSavedNote(t('Cost centre saved.'));
      await load();
    } catch (err) {
      const inUseRefusal = inUseFromError(err);
      const code = (err as { response?: { data?: { error?: string } } })?.response?.data?.error;
      if (inUseRefusal && editing) setInUse({ item: editing, counts: inUseRefusal.counts });
      else if (code === 'cost_centre_code_locked' && editing) {
        // M-9: the code and company are frozen. Put them back and say why, next to the code.
        setLockedNote(showServerError(err));
        setForm((f) => ({ ...f, code: editing.code, companyId: editing.companyId ?? '' }));
      } else setFormError(showServerError(err));
    } finally { setSaving(false); }
  };

  // ── Deactivate and delete ──
  const openAction = (kind: 'deactivate' | 'delete', item: CostCenterDto) => { setMenu(null); setRowError(''); setSavedNote(''); setActionReason(''); setActionError(''); setActionReasonError(''); setAction({ kind, item }); };
  const runAction = async () => {
    if (!action) return;
    const { kind, item } = action;
    if (!actionReason.trim()) { setActionReasonError(t('Enter a reason for this change.')); return; }
    setSaving(true); setActionError(''); setActionReasonError('');
    try {
      let note = '';
      if (kind === 'delete') {
        // The server decides: nothing uses it = deleted; something does = turned off (it can be restored). Say which.
        const result = await costCentersApi.remove(item.id, actionReason);
        note = result?.outcome === 'turnedOff'
          ? t('Turned off {name}. {reason}', { name: item.code, reason: result.usage?.summary ? `${usageSentence(result.usage.items, t, result.usage.summary)}. ${t('Restore anytime.')}` : t('Restore anytime.') })
          : t('Deleted {name}.', { name: item.code });
      } else await costCentersApi.update(item.id, saveBody(item, { ...formOf(item), isActive: false, reason: actionReason }));
      setAction(null);
      if (note) setSavedNote(note);
      await load();
    } catch (err) {
      const refusal = inUseFromError(err);
      if (refusal) { setAction(null); setInUse({ item, counts: refusal.counts }); }
      else setActionError(showServerError(err));
    } finally { setSaving(false); }
  };

  // ── Restore: no reason is asked; the server's guard may refuse (a payroll run is awaiting review or lock). ──
  const restoreRow = async (item: CostCenterDto) => {
    setMenu(null); setRowError(''); setSavedNote('');
    try {
      await costCentersApi.restore(item.id);
      setSavedNote(t('Turned {name} back on.', { name: item.code }));
      await load();
    } catch (err) { setRowError(showServerError(err)); }
  };

  // ── Row menu ──
  const openMenu = (item: CostCenterDto, viaKeyboard: boolean) => {
    const el = triggerRefs.current.get(item.id);
    const rect = el?.getBoundingClientRect();
    const rtl = typeof document !== 'undefined' && document.documentElement.dir === 'rtl';
    setMenu({ id: item.id, top: (rect?.bottom ?? 0) + 4, above: (rect?.top ?? 0) - 4, start: rtl ? (rect?.left ?? 0) : (rect?.right ?? 0), viaKeyboard });
  };
  const closeMenu = (restoreFocus = true) => {
    const id = menu?.id;
    setMenu(null);
    if (restoreFocus && id) requestAnimationFrame(() => triggerRefs.current.get(id)?.focus());
  };

  const toggle = (id: string) => setExpanded((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  const hasNoCompanyOption = unassignedCount > 0;

  return (
    <section aria-label={t('Cost centres')} className="space-y-4">
      {/* Setup's own header shows the title and description; the region is named for assistive technology below. */}
      <div className="flex flex-wrap items-center justify-end gap-3">
        <div className="flex flex-wrap items-center gap-2">
          {canManage ? (
            <div data-testid="cc-master-import">
              <ImportExportToolbar
                entityName="Cost Centers"
                onExport={async () => { downloadCsv(await costCentersApi.export(), 'cost-centers.csv'); }}
                onDownloadTemplate={async () => { downloadCsv(await costCentersApi.importTemplate(), 'cost-centers-template.csv'); }}
                onImport={async (csv) => { const r = await costCentersApi.import(csv, org.defaultCompanyId || undefined); void load(); return r; }}
              />
            </div>
          ) : (
            <ImportExportToolbar
              entityName="Cost Centers"
              canImport={false}
              onExport={async () => { downloadCsv(await costCentersApi.export(), 'cost-centers.csv'); }}
              onDownloadTemplate={async () => undefined}
              onImport={async () => ({ received: 0, created: 0, skipped: 0, errors: [] })}
            />
          )}
          <button type="button" data-testid="cc-master-new" onClick={openNew} disabled={!canManage} title={writeBlockedReason} aria-describedby={writeBlockedReason ? blockedNoteId : undefined} className="btn-primary !bg-blue-700 hover:!bg-blue-800 disabled:cursor-not-allowed disabled:opacity-50"><Plus className="h-4 w-4" aria-hidden />{t('New cost centre')}</button>
        </div>
      </div>

      {writeBlockedReason && (
        <div id={blockedNoteId} data-testid="cc-master-readonly-note"><WarningNote>{writeBlockedReason}</WarningNote></div>
      )}
      {savedNote && <p role="status" className="text-sm font-semibold text-emerald-700 dark:text-emerald-300">{savedNote}</p>}
      {rowError && <FieldError error={rowError} testId="cc-master-action-error" />}

      <div className="flex flex-wrap items-center gap-2">
        {(org.options.length > 1 || hasNoCompanyOption) && (
          <select data-testid="cc-master-filter-company" aria-label={t('Filter by company')} value={filters.company} onChange={(e) => setFilters((f) => ({ ...f, company: e.target.value }))} className="select">
            <option value="">{t('All companies')}</option>
            {org.options.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            {hasNoCompanyOption && <option value={NO_COMPANY_FILTER}>{t('No company')}</option>}
          </select>
        )}
        <select data-testid="cc-master-filter-class" aria-label={t('Functional class')} value={filters.cls} onChange={(e) => setFilters((f) => ({ ...f, cls: e.target.value }))} className="select">
          <option value="">{t('All classes')}</option>
          {FUNCTIONAL_CLASSES.map((c) => <option key={c} value={c}>{classLabel(c)}</option>)}
        </select>
        <select data-testid="cc-master-filter-status" aria-label={t('Status')} value={filters.status} onChange={(e) => setFilters((f) => ({ ...f, status: e.target.value }))} className="select">
          <option value="">{t('All statuses')}</option>
          <option value="active">{t('Active')}</option>
          <option value="inactive">{t('Inactive')}</option>
        </select>
        <input data-testid="cc-master-search" type="search" aria-label={t('Search cost centres')} placeholder={t('Search code or name…')} value={filters.search} onChange={(e) => setFilters((f) => ({ ...f, search: e.target.value }))} className="input w-56" />
        <div role="group" aria-label={t('View')} className="ms-auto inline-flex overflow-hidden rounded-lg border border-slate-200 dark:border-white/10">
          <button type="button" data-testid="cc-master-view-tree" aria-pressed={view === 'tree'} onClick={() => setView('tree')} className={`min-h-8 px-3 text-sm font-medium ${view === 'tree' ? 'bg-blue-700 text-white' : 'bg-white text-slate-700 dark:bg-white/5 dark:text-slate-200'}`}>{t('Tree')}</button>
          <button type="button" data-testid="cc-master-view-flat" aria-pressed={view === 'flat'} onClick={() => setView('flat')} className={`min-h-8 px-3 text-sm font-medium ${view === 'flat' ? 'bg-blue-700 text-white' : 'bg-white text-slate-700 dark:bg-white/5 dark:text-slate-200'}`}>{t('Flat')}</button>
        </div>
      </div>
      <p role="status" aria-live="polite" className={filtering && view === 'tree' ? 'text-xs text-slate-600 dark:text-slate-300' : 'sr-only'}>{filtering && view === 'tree' ? t('Showing flat results') : ''}</p>
      {!loading && !loadError && items.length > 0 && !hasHierarchy && (
        <p data-testid="cc-master-no-hierarchy" className="text-xs text-slate-600 dark:text-slate-300">{t('No cost centre has a parent yet, so Tree and Flat look the same. Choose a Parent cost centre when editing one to build a hierarchy.')}</p>
      )}

      {loadError ? <LoadError message={loadError} onRetry={() => void load()} />
        : loading ? <Spinner />
        : items.length === 0 ? <p className="py-10 text-center text-sm text-slate-500 dark:text-slate-400">{t('No cost centres yet. Add one, or import a CSV.')}</p>
        : rows.length === 0 ? <p className="py-10 text-center text-sm text-slate-500 dark:text-slate-400">{t('No cost centres match your filters.')}</p>
        : (
          <div className="surface overflow-x-auto" tabIndex={0} role="group" aria-label={t('Cost centres')}>
            {/* Fixed layout: the columns share the width the page gives them (about 640px beside Setup's menu); long text wraps and nothing is cut. */}
            <table data-testid="cc-master-table" className="w-full table-fixed text-sm">
              <colgroup>
                {flatList
                  ? <><col className="w-[12%]" /><col className="w-[14%]" /><col className="w-[10%]" /><col className="w-[9%]" /><col className="w-[11%]" /><col className="w-[12%]" /><col className="w-[7%]" /><col className="w-[8%]" /><col className="w-[17%]" /></>
                  : <><col className="w-[15%]" /><col className="w-[15%]" /><col className="w-[10%]" /><col className="w-[12%]" /><col className="w-[14%]" /><col className="w-[8%]" /><col className="w-[9%]" /><col className="w-[17%]" /></>}
              </colgroup>
              <thead className="bg-slate-50 text-xs text-slate-500 dark:bg-white/[0.03] dark:text-slate-400">
                <tr>
                  {(flatList
                    ? ['Code', 'Name', 'Parent', 'ERP code', 'Functional class', 'Company', 'In use', 'Status']
                    : ['Code', 'Name', 'ERP code', 'Functional class', 'Company', 'In use', 'Status']
                  ).map((h) => <th key={h} scope="col" className="px-3 py-2 text-start font-medium">{t(h)}</th>)}
                  <th scope="col" className="px-3 py-2 text-start font-medium"><span className="sr-only">{t('Actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {rows.map(({ item, depth, hasChildren }) => (
                  <tr key={item.id} data-testid={`cc-master-row-${item.code}`} data-depth={depth} className="border-t border-slate-100 dark:border-white/5">
                    <td className="px-3 py-2 font-mono text-xs [overflow-wrap:anywhere]" style={{ paddingInlineStart: `${12 + depth * 16}px` }}>
                      <span className="inline-flex items-center gap-1">
                        {hasChildren ? (
                          <button type="button" data-testid={`cc-master-expand-${item.code}`} aria-expanded={expanded.has(item.id)} aria-label={expanded.has(item.id) ? t('Collapse {code}', { code: item.code }) : t('Expand {code}', { code: item.code })} onClick={() => toggle(item.id)} className="grid h-6 w-6 place-items-center rounded text-slate-600 hover:bg-slate-100 focus-visible:ring-2 focus-visible:ring-sapphire dark:text-slate-300 dark:hover:bg-white/10">
                            <ChevronRight className={`h-4 w-4 transition-transform rtl:-scale-x-100 ${expanded.has(item.id) ? 'rotate-90 rtl:-rotate-90' : ''}`} aria-hidden />
                          </button>
                        ) : <span className="inline-block w-6" aria-hidden />}
                        <bdi dir="ltr">{item.code}</bdi>
                        {depth > 0 && <span className="sr-only">{t('Level {level}', { level: depth + 1 })}</span>}
                      </span>
                    </td>
                    <td className="px-3 py-2 [overflow-wrap:anywhere]"><bdi dir="auto">{display(item)}</bdi></td>
                    {flatList && (
                      <td data-testid={`cc-master-parent-${item.code}`} className="px-3 py-2 font-mono text-xs [overflow-wrap:anywhere]">
                        {item.parentId && codeById.get(item.parentId) ? <bdi dir="ltr">{codeById.get(item.parentId)}</bdi> : <><span aria-hidden>—</span><span className="sr-only">{t('No parent')}</span></>}
                      </td>
                    )}
                    <td className="px-3 py-2 font-mono text-xs [overflow-wrap:anywhere]">{item.glSegment ? <bdi dir="ltr">{item.glSegment}</bdi> : <><span aria-hidden>—</span><span className="sr-only">{t('Not set')}</span></>}</td>
                    <td className="px-3 py-2 text-xs">{item.functionalClass ? classLabel(item.functionalClass) : <><span aria-hidden>—</span><span className="sr-only">{t('Not set')}</span></>}</td>
                    <td className="px-3 py-2 text-xs">
                      {item.companyId ? org.nameOf(item.companyId) : (
                        <span data-testid={`cc-master-nocompany-chip-${item.code}`} className="inline-block max-w-full rounded-lg bg-rose-50 px-2 py-1 text-start text-xs font-semibold leading-snug text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">{t('No company: cannot be used for posting. Assign a company.')}</span>
                      )}
                    </td>
                    <td data-testid={`cc-master-inuse-${item.code}`} className="px-3 py-2 text-xs">
                      {inUseTotalOf(item) === null ? <><span aria-hidden>—</span><span className="sr-only">{t('Not set')}</span></>
                        : inUseTotalOf(item) === 0 ? <span className="text-slate-500 dark:text-slate-400">{t('Not in use')}</span>
                        : <Badge tone="amber"><span title={t('{count} in use', { count: inUseTotalOf(item) ?? 0 })}><span aria-hidden>{inUseTotalOf(item)}</span><span className="sr-only">{t('{count} in use', { count: inUseTotalOf(item) ?? 0 })}</span></span></Badge>}
                    </td>
                    <td className="px-3 py-2"><Badge tone={item.isActive ? 'emerald' : 'slate'}>{item.isActive ? t('Active') : t('Inactive')}</Badge></td>
                    <td className="px-3 py-2">
                      {(() => {
                        const scope = writeAccess(item.companyId);
                        const editBlocked = writeBlockedReason ?? (scope.allowed ? undefined : scope.reason);
                        return (
                          <div className="space-y-1">
                            <div className="flex flex-wrap items-center gap-1">
                              <button type="button" onClick={() => openEdit(item)} disabled={!!editBlocked} title={editBlocked} aria-describedby={writeBlockedReason ? blockedNoteId : undefined} className="btn-secondary h-8 px-2 text-xs disabled:cursor-not-allowed disabled:opacity-50"><Pencil className="h-3 w-3" aria-hidden /> {t('Edit')}</button>
                              <button
                                type="button"
                                ref={(el) => { if (el) triggerRefs.current.set(item.id, el); else triggerRefs.current.delete(item.id); }}
                                aria-haspopup="menu" aria-expanded={menu?.id === item.id}
                                aria-label={t('More actions for {code}', { code: item.code })}
                                disabled={!hasManage || !scope.allowed}
                                title={!hasManage ? writeBlockedReason : scope.reason}
                                onClick={() => (menu?.id === item.id ? closeMenu() : openMenu(item, false))}
                                onKeyDown={(e) => { if (e.key === 'ArrowDown' || e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openMenu(item, true); } }}
                                className="btn-secondary h-8 w-8 justify-center px-0 disabled:cursor-not-allowed disabled:opacity-50"
                              ><MoreHorizontal className="h-4 w-4" aria-hidden /></button>
                            </div>
                            {!writeBlockedReason && scope.reason && <p className="text-caption leading-snug text-slate-600 dark:text-slate-300">{scope.reason}</p>}
                          </div>
                        );
                      })()}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

      {menu && (() => {
        const it = items.find((i) => i.id === menu.id);
        if (!it) return null;
        return <RowMenu menu={menu} onClose={closeMenu} items={[
          it.isActive
            ? { key: 'deactivate', label: t('Deactivate'), disabledReason: writeBlockedReason, onSelect: () => openAction('deactivate', it) }
            : { key: 'restore', label: t('Restore'), disabledReason: deleteBlockedReason, onSelect: () => void restoreRow(it) },
          { key: 'delete', label: t('Delete'), tone: 'danger', disabledReason: writeBlockedReason ?? deleteBlockedReason, onSelect: () => openAction('delete', it) },
        ]} />;
      })()}

      {/* Create and edit */}
      <Modal
        isOpen={formOpen}
        title={editing ? t('Edit cost centre {code}', { code: editing.code }) : t('New cost centre')}
        onClose={closeForm}
        size="lg"
        footer={<>
          <button type="button" onClick={closeForm} disabled={saving} className="btn-secondary">{t('Cancel')}</button>
          <button type="button" data-testid="cc-form-save" onClick={() => void save()} disabled={saving} className="btn-primary !bg-blue-700 hover:!bg-blue-800">{saving ? t('Saving…') : t('Save')}</button>
        </>}
      >
        <FieldError error={formError} testId="cc-form-error" />
        <div className="grid gap-3 sm:grid-cols-2">
          <Labelled label={t('Company')} required error={errors.company}>
            {(id, describedBy) => (
              <>
                <select id={id} aria-describedby={describedBy} data-testid="cc-form-company" value={form.companyId} disabled={!!lockedNote || codeLockedByServer || companyBlockedReason !== undefined} onChange={(e) => setForm((f) => ({ ...f, companyId: e.target.value, parentId: '' }))} className="select w-full">
                  <option value="">{t('Choose the company')}</option>
                  {org.options.map((c) => <option key={c.id} value={c.id} disabled={!writeAccess(c.id).allowed && c.id !== form.companyId}>{c.name}</option>)}
                </select>
                {(lockedNote || codeLockedByServer) && <p className="mt-1 text-xs text-slate-600 dark:text-slate-300">{t('The company cannot change once the cost centre is in use.')}</p>}
                {companyBlockedReason && <p data-testid="cc-form-company-blocked" className="mt-1 text-xs text-slate-600 dark:text-slate-300">{companyBlockedReason}</p>}
              </>
            )}
          </Labelled>
          <Labelled label={t('Code')} required error={errors.code} hint={t('Letters, numbers, dot, dash or underscore. Up to 30 characters. It cannot change once journal lines use it.')}>
            {(id, describedBy) => (
              <>
                <div className="relative">
                  <input id={id} aria-describedby={describedBy} aria-invalid={!!errors.code} data-testid="cc-form-code" dir="ltr" value={form.code} readOnly={!!lockedNote || codeLockedByServer} maxLength={40} onChange={(e) => set('code', e.target.value)} className={`input w-full ${lockedNote || codeLockedByServer ? 'bg-slate-50 pe-9 dark:bg-white/5' : ''}`} />
                  {lockedNote && <Lock className="pointer-events-none absolute end-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-500" aria-hidden />}
                </div>
                {(lockedNote || codeLockedByServer) && <p data-testid="cc-form-code-locked-note" role="note" className="mt-1 text-xs font-medium text-amber-800 dark:text-amber-300">{lockedNote || t('The code cannot change because journal lines already carry it. You can rename it.')}</p>}
              </>
            )}
          </Labelled>
          <Labelled label={t('Name')} required error={errors.name}>
            {(id, describedBy) => <input id={id} aria-describedby={describedBy} aria-invalid={!!errors.name} data-testid="cc-form-name" dir="auto" value={form.name} onChange={(e) => set('name', e.target.value)} className="input w-full" />}
          </Labelled>
          <Labelled label={t('Name (Arabic)')}>
            {(id) => <input id={id} data-testid="cc-form-name-ar" dir="rtl" lang="ar" value={form.nameAr} onChange={(e) => set('nameAr', e.target.value)} className="input w-full" />}
          </Labelled>
          <Labelled label={t('Parent cost centre')}>
            {(id) => (
              <select id={id} data-testid="cc-form-parent" value={form.parentId} onChange={(e) => set('parentId', e.target.value)} className="select w-full" dir="auto">
                <option value="">{t('None (top level)')}</option>
                {parents.map((p) => <option key={p.id} value={p.id}>{p.code} — {display(p)}</option>)}
              </select>
            )}
          </Labelled>
          <Labelled label={t('Functional class')} hint={canSetClass ? undefined : t('Only finance can change the functional class.')}>
            {(id, describedBy) => (
              <select id={id} aria-describedby={describedBy} data-testid="cc-form-class" value={form.functionalClass} disabled={!canSetClass} onChange={(e) => set('functionalClass', e.target.value)} className="select w-full">
                <option value="">{t('Not set')}</option>
                {FUNCTIONAL_CLASSES.map((c) => <option key={c} value={c} disabled={c === 'CAPITALISABLE'}>{classLabel(c)}</option>)}
              </select>
            )}
          </Labelled>
          <Labelled label={t('ERP code')} hint={editing && !canSetClass ? t('Only finance can change the ERP code.') : t('The cost-centre value in your ERP, if it differs from the code. Journal exports use it.')}>
            {(id, describedBy) => <input id={id} aria-describedby={describedBy} data-testid="cc-form-erp-code" dir="ltr" value={form.glSegment} maxLength={30} disabled={!!editing && !canSetClass} onChange={(e) => set('glSegment', e.target.value)} className="input w-full" />}
          </Labelled>
          <Labelled label={t('Status')}>
            {(id) => (
              <select id={id} data-testid="cc-form-status" value={form.isActive ? 'true' : 'false'} onChange={(e) => set('isActive', e.target.value === 'true')} className="select w-full">
                <option value="true">{t('Active')}</option>
                <option value="false">{t('Inactive')}</option>
              </select>
            )}
          </Labelled>
          {needsReason && (
            <div className="sm:col-span-2">
              <Labelled label={t('Reason')} required error={errors.reason} hint={t('Required to change the functional class or ERP code, deactivate, or move to another company. Recorded in the audit log.')}>
                {(id, describedBy) => <input id={id} aria-describedby={describedBy} aria-invalid={!!errors.reason} data-testid="cc-form-reason" value={form.reason} maxLength={500} onChange={(e) => set('reason', e.target.value)} className="input w-full" />}
              </Labelled>
            </div>
          )}
        </div>
      </Modal>

      {/* Deactivate or delete */}
      <Modal
        isOpen={!!action}
        title={action?.kind === 'delete' ? t('Delete {code}?', { code: action.item.code }) : t('Deactivate {code}?', { code: action?.item.code ?? '' })}
        onClose={closeAction}
        size="md"
        footer={<>
          <button type="button" onClick={closeAction} disabled={saving} className="btn-secondary">{t('Cancel')}</button>
          <button type="button" onClick={() => void runAction()} disabled={saving} className={action?.kind === 'delete' ? 'btn-primary !bg-rose-700 hover:!bg-rose-800' : 'btn-primary !bg-blue-700 hover:!bg-blue-800'}>{saving ? t('Saving…') : action?.kind === 'delete' ? t('Delete') : t('Deactivate')}</button>
        </>}
      >
        <div className="space-y-3">
          <p className="text-sm text-slate-700 dark:text-slate-200">{action?.kind === 'delete' ? t('The cost centre leaves the list. Posted journal lines keep it.') : t('Posted journal lines keep it. It can no longer be assigned.')}</p>
          {action && (
            <div>
              <label htmlFor={`${headingId}-action-reason`} className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">{t('Reason')} <span aria-hidden className="text-rose-500">*</span></label>
              <input
                id={`${headingId}-action-reason`} data-testid="cc-form-reason" value={actionReason} maxLength={500}
                aria-invalid={!!actionReasonError} aria-describedby={actionReasonError ? `${headingId}-action-reason-error` : undefined}
                onChange={(e) => { setActionReason(e.target.value); if (actionReasonError) setActionReasonError(''); }} className="input w-full"
              />
              {actionReasonError && <p id={`${headingId}-action-reason-error`} role="alert" className="mt-1 text-xs font-medium text-rose-700 dark:text-rose-300">{actionReasonError}</p>}
            </div>
          )}
          <FieldError error={actionError} />
        </div>
      </Modal>

      <ConfirmDialog
        state={discarding ? {
          title: t('Discard changes?'),
          message: t('What you typed has not been saved.'),
          tone: 'primary',
          confirmLabel: t('Discard changes'),
          cancelLabel: t('Keep editing'),
          onConfirm: () => { if (discarding === 'form') setFormOpen(false); else setAction(null); setDiscarding(null); },
        } : null}
        onCancel={() => setDiscarding(null)}
      />

      {/* M-8: what uses it */}
      <Modal isOpen={!!inUse} title={t('Cost centre {code} is in use', { code: inUse?.item.code ?? '' })} onClose={() => setInUse(null)} size="md"
        footer={<button type="button" onClick={() => setInUse(null)} className="btn-secondary">{t('Close')}</button>}>
        <div data-testid="cc-inuse-dialog" className="space-y-3">
          <ul className="space-y-1.5 text-sm text-slate-800 dark:text-slate-100">
            {inUse && inUseCounts(inUse.counts).map(({ key, count }) => {
              const text = inUseText(t, key, count);
              const href = IN_USE_LINKS[key];
              return (
                <li key={key} data-testid={`cc-inuse-count-${key}`}>
                  {href ? <Link href={href} className="font-semibold text-blue-700 underline underline-offset-2 dark:text-blue-300">{text}</Link> : text}
                </li>
              );
            })}
          </ul>
          <p className="text-sm text-slate-600 dark:text-slate-300">{t('Move them to another cost centre first.')}</p>
        </div>
      </Modal>
    </section>
  );
}

function inUseText(t: ReturnType<typeof useLocale>['t'], key: InUseKey, count: number): string {
  switch (key) {
    case 'departments': return t('{count, plural, one {# department} other {# departments}}', { count });
    case 'employees': return t('{count, plural, one {# employee} other {# employees}}', { count });
    case 'positions': return t('{count, plural, one {# position} other {# positions}}', { count });
    case 'rules': return t('{count, plural, one {# posting rule} other {# posting rules}}', { count });
    case 'children': return t('{count, plural, one {# child cost centre} other {# child cost centres}}', { count });
    default: return t('Company default');
  }
}

/** A labelled control: the label, the control (given its id and described-by), a hint and an error that name it. */
function Labelled({ label, required, hint, error, children }: {
  label: string; required?: boolean; hint?: string; error?: string; children: (id: string, describedBy: string | undefined) => React.ReactNode;
}) {
  const id = useId();
  const hintId = hint ? `${id}-hint` : undefined;
  const errorId = error ? `${id}-error` : undefined;
  const describedBy = [errorId, hintId].filter(Boolean).join(' ') || undefined;
  return (
    <div>
      <label htmlFor={id} className="mb-1.5 block text-sm font-medium text-slate-700 dark:text-slate-300">{label}{required && <span aria-hidden className="text-rose-500"> *</span>}</label>
      {children(id, describedBy)}
      {hint && <p id={hintId} className="mt-1 text-caption leading-snug text-slate-500 dark:text-slate-400">{hint}</p>}
      {error && <p id={errorId} role="alert" className="mt-1 text-xs font-medium text-rose-700 dark:text-rose-300">{error}</p>}
    </div>
  );
}

/** The ⋯ menu: arrow keys move, Escape closes and returns focus, a click elsewhere closes. */
interface MenuEntry { key: string; label: string; tone?: 'danger'; onSelect: () => void; /** Shown under the label and the entry does nothing: a blocked action is disabled with its reason, never hidden. */ disabledReason?: string }

function RowMenu({ menu, onClose, items }: {
  menu: { top: number; above: number; start: number; viaKeyboard: boolean }; onClose: (restoreFocus?: boolean) => void;
  items: MenuEntry[];
}) {
  const refs = useRef<Array<HTMLButtonElement | null>>([]);
  const box = useRef<HTMLDivElement>(null);
  // Below the button when it fits, otherwise flipped above it; clamped so it is always inside the viewport.
  const [top, setTop] = useState<number | null>(null);
  useLayoutEffect(() => {
    const h = box.current?.offsetHeight ?? 0;
    const room = window.innerHeight - menu.top - 8;
    const y = h <= room ? menu.top : menu.above - h >= 8 ? menu.above - h : Math.max(8, window.innerHeight - h - 8);
    setTop(y);
  }, [menu.top, menu.above]);
  useEffect(() => { if (top !== null) refs.current[0]?.focus(); }, [top]);
  // A scroll or resize moves the button away from the menu, so the menu closes rather than float loose.
  useEffect(() => {
    // Closes only when the button really moved out from under the menu (a scroll event can trail the click that opened it).
    const away = () => {
      const t = document.querySelector('[aria-haspopup="menu"][aria-expanded="true"]');
      if (!t || Math.abs(t.getBoundingClientRect().top - (menu.above + 4)) > 1) onClose(false);
    };
    window.addEventListener('scroll', away, true);
    window.addEventListener('resize', away);
    return () => { window.removeEventListener('scroll', away, true); window.removeEventListener('resize', away); };
  }, [onClose, menu.above]);
  useEffect(() => {
    const onDown = (e: MouseEvent) => { if (!(e.target as HTMLElement).closest('[role="menu"]') && !(e.target as HTMLElement).closest('[aria-haspopup="menu"]')) onClose(false); };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [onClose]);
  const rtl = typeof document !== 'undefined' && document.documentElement.dir === 'rtl';
  const move = (delta: number) => {
    const list = refs.current.filter(Boolean) as HTMLButtonElement[];
    const i = list.indexOf(document.activeElement as HTMLButtonElement);
    list[(i + delta + list.length) % list.length]?.focus();
  };
  return createPortal(
    <div
      role="menu"
      ref={box}
      style={{ position: 'fixed', top: top ?? menu.top, visibility: top === null ? 'hidden' : 'visible', ...(rtl ? { left: menu.start } : { right: `calc(100vw - ${menu.start}px)` }) }}
      className="z-50 min-w-40 rounded-lg border border-slate-200 bg-white p-1 shadow-xl dark:border-white/10 dark:bg-slate-900"
      onKeyDown={(e) => {
        if (e.key === 'ArrowDown') { e.preventDefault(); move(1); }
        else if (e.key === 'ArrowUp') { e.preventDefault(); move(-1); }
        else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); onClose(true); }
        else if (e.key === 'Tab') onClose(false);
      }}
    >
      {items.map((entry, i) => (
        <button
          key={entry.key} ref={(el) => { refs.current[i] = el; }} role="menuitem" type="button"
          aria-disabled={entry.disabledReason ? true : undefined}
          onClick={() => { if (!entry.disabledReason) entry.onSelect(); }}
          className={`block w-full rounded-lg px-3 py-2 text-start text-sm focus-visible:bg-slate-100 ${entry.disabledReason ? 'cursor-not-allowed text-slate-500 dark:text-slate-400' : entry.tone === 'danger' ? 'text-rose-700 hover:bg-rose-50 focus-visible:bg-rose-50 dark:text-rose-300 dark:hover:bg-rose-500/10' : 'text-slate-800 hover:bg-slate-50 dark:text-slate-100 dark:hover:bg-white/5'}`}
        >
          {entry.label}
          {entry.disabledReason && <span className="mt-0.5 block max-w-56 whitespace-normal text-caption font-normal leading-snug">{entry.disabledReason}</span>}
        </button>
      ))}
    </div>,
    document.body,
  );
}
