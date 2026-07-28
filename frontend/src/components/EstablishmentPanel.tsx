'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, CheckCircle2, ChevronDown, ChevronRight, Plus, ShieldCheck, Trash2, Building2 } from 'lucide-react';
import { planningApi } from '../api/planning';
import { establishmentApi, type MatrixLevelCell, type MatrixRow, type StaffingLevelDto } from '../api/establishment';
import { tenantHrConfigApi, type EstablishmentEnforcementMode, type TenantHrConfigDto } from '../api/tenantHrConfig';
import { costCentersApi, type CostCenterDto } from '../api/organization';
import { notifyApiError } from '../api/client';
import { useTenantSettings } from '../contexts/TenantSettingsContext';
import { useAuth } from '../contexts/AuthContext';
import { useLocale } from '../contexts/LocaleContext';
import { Modal } from './Modal';

const NONE = '__none__';
const ESTABLISHMENT_WRITE = 'organization.establishment.write';
const ENFORCEMENT_MODES: EstablishmentEnforcementMode[] = ['Off', 'Advisory', 'Enforced'];

/** Replaces {token} placeholders in a translated template. */
function fmt(template: string, values: Record<string, string | number>): string {
  return template.replace(/\{(\w+)\}/g, (m, key) => (key in values ? String(values[key]) : m));
}

interface RowEdit { approved: number; budget: number; costCenterId: string | null; }

/** One staged level-budget change for the reason/confirm modal. */
interface LevelChange {
  staffingLevelId: string;
  levelName: string;
  before: number | null;
  after: number | null;
  current: number;
}

interface PendingSave {
  row: MatrixRow;
  edit: RowEdit;
  envelopeDirty: boolean;
  costCenterDirty: boolean;
  levelChanges: LevelChange[];
}

export function EstablishmentPanel({ readOnly = false, focusDepartmentId, focusLevelId }: {
  readOnly?: boolean;
  /** Deep-link target (from ?department= on the Setup page): expanded + scrolled into view on mount. */
  focusDepartmentId?: string;
  /** Deep-link target (from ?level=): that level's budget input receives focus. */
  focusLevelId?: string;
} = {}) {
  const { currencyCode } = useTenantSettings();
  const { hasPermission } = useAuth();
  const { t, locale } = useLocale();
  const [rows, setRows] = useState<MatrixRow[]>([]);
  const [levels, setLevels] = useState<StaffingLevelDto[]>([]);
  const [costCenters, setCostCenters] = useState<CostCenterDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [edits, setEdits] = useState<Record<string, RowEdit>>({});
  // Raw level-budget input per department per level: '' = uncontrolled (no row), '0' = frozen.
  const [levelEdits, setLevelEdits] = useState<Record<string, Record<string, string>>>({});
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [savedId, setSavedId] = useState('');
  const [adding, setAdding] = useState(false);
  const [newCc, setNewCc] = useState({ code: '', name: '' });
  const [error, setError] = useState('');
  const [pendingSave, setPendingSave] = useState<PendingSave | null>(null);
  const [saveReason, setSaveReason] = useState('');
  const [savingBudget, setSavingBudget] = useState(false);
  const [hrConfig, setHrConfig] = useState<TenantHrConfigDto | null>(null);
  const [pendingMode, setPendingMode] = useState<EstablishmentEnforcementMode | null>(null);
  const [modeReason, setModeReason] = useState('');
  const [savingMode, setSavingMode] = useState(false);

  const canEdit = !readOnly && hasPermission(ESTABLISHMENT_WRITE);
  // False when GET /api/establishment/matrix is unavailable and the panel fell back
  // to the legacy planning endpoint — level features are hidden so no fabricated
  // zeros are ever displayed (popup numbers must always equal panel numbers).
  const [matrixAvailable, setMatrixAvailable] = useState(true);

  const rowRefs = useRef<Record<string, HTMLTableRowElement | null>>({});
  const levelInputRefs = useRef<Record<string, HTMLInputElement | null>>({});
  const focusApplied = useRef(false);

  const load = () => {
    setLoading(true);
    const matrixOrFallback: Promise<MatrixRow[]> = establishmentApi.matrix()
      .then(m => { setMatrixAvailable(true); return m; })
      .catch(() =>
        planningApi.establishment().then(rs => {
          setMatrixAvailable(false);
          return rs.map(r => ({
            ...r, allocated: 0, unallocated: r.approvedHeadcount, managerEmployeeId: null,
            levels: [], unclassifiedCount: 0, unresolvedDepartmentCount: 0,
          } as MatrixRow));
        }).catch(() => [] as MatrixRow[]),
      );
    Promise.all([
      matrixOrFallback,
      // Read-only consumers (e.g. Recruitment) don't fetch the catalog — levels
      // are derived from the matrix cells instead (see activeLevels).
      readOnly ? Promise.resolve([] as StaffingLevelDto[]) : establishmentApi.levels().catch(() => [] as StaffingLevelDto[]),
      costCentersApi.list().then(r => r.items).catch(() => []),
    ])
      .then(([matrix, lvls, cc]) => { setRows(matrix); setLevels(lvls); setCostCenters(cc); })
      .catch(() => {}).finally(() => setLoading(false));
  };
  useEffect(() => { load(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  // Enforcement mode: fetched only for users who can change it (the config endpoint
  // is write-gated server-side; fetching for viewers would surface access errors).
  useEffect(() => {
    if (!canEdit) return;
    tenantHrConfigApi.get().then(setHrConfig).catch(() => setHrConfig(null));
  }, [canEdit]);

  // Deep-link focus: expand the offending department, scroll to it, focus the level input.
  useEffect(() => {
    if (loading || focusApplied.current || !focusDepartmentId || !matrixAvailable) return;
    if (!rows.some(r => r.departmentId === focusDepartmentId)) return;
    focusApplied.current = true;
    setExpanded(e => ({ ...e, [focusDepartmentId]: true }));
    setTimeout(() => {
      rowRefs.current[focusDepartmentId]?.scrollIntoView({ behavior: 'smooth', block: 'center' });
      if (focusLevelId) levelInputRefs.current[`${focusDepartmentId}:${focusLevelId}`]?.focus();
    }, 200);
  }, [loading, rows, focusDepartmentId, focusLevelId, matrixAvailable]);

  const activeLevels = useMemo(() => {
    if (levels.length > 0) return levels.filter(l => l.isActive).sort((a, b) => a.rank - b.rank);
    // Derived from matrix cells for viewers who don't fetch the catalog (cells
    // arrive rank-ordered from the server, so insertion order is preserved).
    const byId = new Map<string, StaffingLevelDto>();
    for (const r of rows) {
      for (const c of r.levels ?? []) {
        if (!byId.has(c.staffingLevelId)) {
          byId.set(c.staffingLevelId, {
            id: c.staffingLevelId, code: c.levelCode, nameEn: c.levelNameEn, nameAr: c.levelNameAr,
            rank: byId.size, isActive: true,
          });
        }
      }
    }
    return Array.from(byId.values());
  }, [levels, rows]);
  const levelName = (l: { nameEn: string; nameAr: string }) => (locale === 'ar' && l.nameAr) ? l.nameAr : l.nameEn;

  const cellFor = (r: MatrixRow, levelId: string): MatrixLevelCell => {
    const cell = (r.levels ?? []).find(c => c.staffingLevelId === levelId);
    if (cell) return cell;
    const lvl = activeLevels.find(l => l.id === levelId);
    return {
      staffingLevelId: levelId,
      levelCode: lvl?.code ?? '', levelNameEn: lvl?.nameEn ?? '', levelNameAr: lvl?.nameAr ?? '',
      budgeted: null, current: 0, gap: null, exitingIncumbents: 0, positionsAtLevel: 0, positionsExceedBudget: false,
    };
  };

  const editFor = (r: MatrixRow): RowEdit =>
    edits[r.departmentId] ?? { approved: r.approvedHeadcount, budget: r.monthlyBudgetAmount, costCenterId: r.costCenterId };
  const setEdit = (r: MatrixRow, patch: Partial<RowEdit>) =>
    setEdits(e => ({ ...e, [r.departmentId]: { ...editFor(r), ...patch } }));

  /** Raw input value for a level budget: undefined edit falls back to the server value. */
  const levelEditValue = (r: MatrixRow, levelId: string): string => {
    const perDept = levelEdits[r.departmentId];
    if (perDept && levelId in perDept) return perDept[levelId];
    const b = cellFor(r, levelId).budgeted;
    return b === null ? '' : String(b);
  };
  const setLevelEdit = (r: MatrixRow, levelId: string, value: string) =>
    setLevelEdits(e => ({ ...e, [r.departmentId]: { ...(e[r.departmentId] ?? {}), [levelId]: value } }));

  const parseBudget = (raw: string): number | null => {
    const trimmed = raw.trim();
    if (trimmed === '') return null;
    const n = parseInt(trimmed, 10);
    return Number.isFinite(n) ? Math.max(0, n) : null;
  };

  const levelChangesFor = (r: MatrixRow): LevelChange[] =>
    activeLevels.flatMap(lvl => {
      const cell = cellFor(r, lvl.id);
      const after = parseBudget(levelEditValue(r, lvl.id));
      if (after === cell.budgeted) return [];
      return [{ staffingLevelId: lvl.id, levelName: levelName(lvl), before: cell.budgeted, after, current: cell.current }];
    });

  /** Sum of in-edit level budgets (uncontrolled levels excluded — partial allocation is allowed). */
  const allocatedInEdit = (r: MatrixRow): number =>
    activeLevels.reduce((sum, lvl) => sum + (parseBudget(levelEditValue(r, lvl.id)) ?? 0), 0);

  const isDirty = (r: MatrixRow): boolean => {
    const e = editFor(r);
    return e.approved !== r.approvedHeadcount || e.budget !== r.monthlyBudgetAmount
      || (e.costCenterId ?? null) !== r.costCenterId || levelChangesFor(r).length > 0;
  };

  const beginSave = (r: MatrixRow) => {
    const e = editFor(r);
    const envelopeDirty = e.approved !== r.approvedHeadcount || e.budget !== r.monthlyBudgetAmount;
    const costCenterDirty = (e.costCenterId ?? null) !== r.costCenterId;
    const levelChanges = levelChangesFor(r);
    if (!envelopeDirty && levelChanges.length === 0) {
      // Cost-centre-only change: no establishment mutation, no reason required.
      if (costCenterDirty) void commitSave({ row: r, edit: e, envelopeDirty, costCenterDirty, levelChanges }, '');
      return;
    }
    setSaveReason('');
    setPendingSave({ row: r, edit: e, envelopeDirty, costCenterDirty, levelChanges });
  };

  const commitSave = async (p: PendingSave, reason: string) => {
    setSavingBudget(true);
    setError('');
    try {
      if (p.levelChanges.length > 0) {
        await establishmentApi.saveBudgets(
          p.row.departmentId,
          p.levelChanges.map(c => ({ staffingLevelId: c.staffingLevelId, budgetedHeadcount: c.after })),
          reason,
        );
      }
      if (p.envelopeDirty || p.costCenterDirty) {
        await planningApi.setEstablishment(p.row.departmentId, {
          approvedHeadcount: p.edit.approved,
          monthlyBudgetAmount: p.edit.budget,
          costCenterId: p.edit.costCenterId ?? null,
          reason: p.envelopeDirty ? reason : undefined,
        });
      }
      setPendingSave(null);
      setSavedId(p.row.departmentId); setTimeout(() => setSavedId(''), 1500);
      setEdits(prev => { const n = { ...prev }; delete n[p.row.departmentId]; return n; });
      setLevelEdits(prev => { const n = { ...prev }; delete n[p.row.departmentId]; return n; });
      load();
    } catch (err) {
      notifyApiError(err, t('Could not save the establishment change.'));
    } finally {
      setSavingBudget(false);
    }
  };

  const commitModeChange = async () => {
    if (!hrConfig || !pendingMode) return;
    setSavingMode(true);
    try {
      const updated = await tenantHrConfigApi.update(
        { ...hrConfig, establishmentEnforcementMode: pendingMode },
        modeReason.trim(),
      );
      setHrConfig(updated);
      setPendingMode(null);
      setModeReason('');
    } catch (err) {
      notifyApiError(err, t('Could not change the enforcement mode.'));
    } finally {
      setSavingMode(false);
    }
  };

  const addCostCenter = async () => {
    if (!newCc.code.trim() || !newCc.name.trim()) { setError('Cost centre needs a code and name.'); return; }
    setError('');
    await costCentersApi.create({ code: newCc.code.trim().toUpperCase(), name: newCc.name.trim(), isActive: true }).catch(() => setError('Could not create cost centre.'));
    setNewCc({ code: '', name: '' }); setAdding(false); load();
  };

  const deleteCostCenter = async (cc: CostCenterDto, deptCount: number) => {
    if (deptCount > 0) { setError(`Reassign the ${deptCount} department(s) under "${cc.name}" before deleting it.`); return; }
    setError('');
    await costCentersApi.remove(cc.id).catch(() => setError('Could not delete cost centre.'));
    load();
  };

  const money = (n: number) => `${currencyCode} ${Math.round(n).toLocaleString()}`;
  const utilization = (spend: number, budget: number) => budget > 0 ? Math.round((spend / budget) * 100) : null;
  const utilTone = (u: number | null) =>
    u === null ? 'text-slate-400' : u > 100 ? 'text-rose-600 dark:text-rose-400' : u > 85 ? 'text-amber-600 dark:text-amber-400' : 'text-emerald-600 dark:text-emerald-400';
  const gapTone = (approved: number, gap: number) =>
    approved <= 0 ? 'text-slate-400' : gap > 0 ? 'text-amber-600 dark:text-amber-400' : gap < 0 ? 'text-rose-600 dark:text-rose-400' : 'text-emerald-600 dark:text-emerald-400';

  // Group departments by cost center (plus an "Unassigned" bucket).
  const groups = useMemo(() => {
    const byId = new Map<string, { id: string | null; name: string; rows: MatrixRow[] }>();
    for (const cc of costCenters) byId.set(cc.id, { id: cc.id, name: cc.name, rows: [] });
    byId.set(NONE, { id: null, name: 'Unassigned', rows: [] });
    for (const r of rows) {
      const key = r.costCenterId && byId.has(r.costCenterId) ? r.costCenterId : NONE;
      byId.get(key)!.rows.push(r);
    }
    return Array.from(byId.values()).filter(g => g.rows.length > 0 || g.id !== null);
  }, [rows, costCenters]);

  const rollup = (gr: MatrixRow[]) => ({
    depts: gr.length,
    approved: gr.reduce((s, r) => s + r.approvedHeadcount, 0),
    current: gr.reduce((s, r) => s + r.currentHeadcount, 0),
    budget: gr.reduce((s, r) => s + r.monthlyBudgetAmount, 0),
    spend: gr.reduce((s, r) => s + r.currentMonthlySpend, 0),
    pipeline: gr.reduce((s, r) => s + r.openRequisitionHeadcount, 0),
  });

  const colSpan = readOnly ? 9 : 11;

  if (loading) return <div className="flex justify-center py-12"><div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>;

  return (
    <div className="space-y-5">
      <p className="rounded-lg border border-slate-200 bg-slate-50 p-3 text-xs text-slate-500 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-400">
        {readOnly
          ? <>Headcount &amp; budget per cost centre (configured in <span className="font-medium">Company Setup → Cost Centres &amp; Budget</span>). <span className="font-medium">Current</span> &amp; <span className="font-medium">Spend</span> are live from active employees.</>
          : <>Plan each cost centre&apos;s departments: set <span className="font-medium">approved headcount</span> and <span className="font-medium">monthly budget</span>, and assign departments to cost centres. Expand a department to set <span className="font-medium">per-level staffing budgets</span> — {t('levels without a budget are uncontrolled ("—"), an explicit 0 freezes the level, and the total approved headcount never hard-blocks — only level budgets do.')}</>}
      </p>

      {/* ── Enforcement mode (tenant-wide; changes are audited with a reason) ── */}
      {!readOnly && hrConfig && (
        <div className="flex flex-wrap items-center gap-3 rounded-xl border border-slate-200 p-3 dark:border-white/10">
          <ShieldCheck className="h-4 w-4 shrink-0 text-sapphire dark:text-cyanAccent" />
          <span className="text-sm font-semibold text-slate-800 dark:text-slate-200">{t('Establishment enforcement')}</span>
          <select
            className="select w-36 text-sm"
            aria-label={t('Establishment enforcement')}
            value={hrConfig.establishmentEnforcementMode || 'Enforced'}
            disabled={!canEdit}
            onChange={ev => { setModeReason(''); setPendingMode(ev.target.value as EstablishmentEnforcementMode); }}
          >
            {ENFORCEMENT_MODES.map(m => <option key={m} value={m}>{t(m)}</option>)}
          </select>
          <span className="text-xs text-slate-500 dark:text-slate-400">
            {hrConfig.establishmentEnforcementMode === 'Off' && t('Level budgets are ignored everywhere.')}
            {hrConfig.establishmentEnforcementMode === 'Advisory' && t('Over-budget assignments are allowed but flagged with a warning.')}
            {(hrConfig.establishmentEnforcementMode === 'Enforced' || !hrConfig.establishmentEnforcementMode) && t('Assignments beyond a level budget are blocked. Levels without a budget row stay uncontrolled.')}
          </span>
        </div>
      )}

      {/* ── Cost-centre rollup cards ─────────────────────────────────────── */}
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {groups.map(g => {
          const tot = rollup(g.rows);
          const u = utilization(tot.spend, tot.budget);
          const deptCount = g.rows.length;
          return (
            <div key={g.id ?? NONE} className="surface p-4">
              <div className="mb-2 flex items-center justify-between">
                <div className="flex items-center gap-2 min-w-0">
                  <Building2 className="h-4 w-4 shrink-0 text-sapphire dark:text-cyanAccent" />
                  <span className="truncate font-semibold text-slate-800 dark:text-white">{g.name}</span>
                </div>
                {!readOnly && g.id && (
                  <button type="button" aria-label={`Delete cost centre ${g.name}`} onClick={() => deleteCostCenter(costCenters.find(c => c.id === g.id)!, deptCount)}
                    className="grid h-6 w-6 place-items-center rounded text-slate-300 hover:bg-rose-50 hover:text-rose-500 dark:hover:bg-rose-500/10"><Trash2 className="h-3.5 w-3.5" /></button>
                )}
              </div>
              <div className="grid grid-cols-2 gap-y-1.5 text-xs">
                <span className="text-slate-400">Headcount</span>
                <span className="text-right font-medium text-slate-700 dark:text-slate-200">{tot.current}{tot.approved > 0 && <span className="text-slate-400"> / {tot.approved}</span>}</span>
                <span className="text-slate-400">Gap</span>
                <span className={`text-right font-medium ${gapTone(tot.approved, tot.approved - tot.current)}`}>{tot.approved > 0 ? (tot.approved - tot.current > 0 ? `+${tot.approved - tot.current}` : tot.approved - tot.current) : '—'}</span>
                <span className="text-slate-400">Budget / Spend</span>
                <span className="text-right font-medium text-slate-700 dark:text-slate-200">{tot.budget > 0 ? `${money(tot.spend)} / ${money(tot.budget)}` : money(tot.spend)}</span>
                <span className="text-slate-400">Utilisation</span>
                <span className={`text-right font-semibold ${utilTone(u)}`}>{u === null ? '— no budget' : `${u}%`}</span>
              </div>
            </div>
          );
        })}
      </div>

      {/* ── Add cost centre ──────────────────────────────────────────────── */}
      {!readOnly && (
        adding ? (
          <div className="flex flex-wrap items-end gap-2 rounded-xl border border-slate-200 p-3 dark:border-white/10">
            <div><label className="mb-1 block text-xs text-slate-500">Code</label><input className="input w-28 text-sm" value={newCc.code} onChange={e => setNewCc(v => ({ ...v, code: e.target.value }))} placeholder="CC-OPS" /></div>
            <div className="flex-1 min-w-[160px]"><label className="mb-1 block text-xs text-slate-500">Name</label><input className="input w-full text-sm" value={newCc.name} onChange={e => setNewCc(v => ({ ...v, name: e.target.value }))} placeholder="Operations Cost Centre" /></div>
            <button type="button" className="btn-primary text-sm" onClick={addCostCenter}>Add</button>
            <button type="button" className="btn-secondary text-sm" onClick={() => { setAdding(false); setNewCc({ code: '', name: '' }); }}>Cancel</button>
          </div>
        ) : (
          <button type="button" className="btn-secondary flex items-center gap-1.5 text-sm" onClick={() => setAdding(true)}><Plus className="h-3.5 w-3.5" />Add cost centre</button>
        )
      )}

      {error && <p className="text-xs text-rose-500">{error}</p>}

      {/* ── Department establishment table, grouped by cost centre ────────── */}
      <div className="overflow-x-auto rounded-xl border border-slate-200 dark:border-white/10">
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-left text-xs text-slate-500 dark:bg-white/[0.03] dark:text-slate-400">
            <tr>
              <th className="w-8 px-2 py-2.5"><span className="sr-only">{t('Staffing levels')}</span></th>
              <th className="px-4 py-2.5 font-medium">Department</th>
              {!readOnly && <th className="px-4 py-2.5 font-medium">Cost Centre</th>}
              <th className="px-4 py-2.5 font-medium text-center">Approved</th>
              <th className="px-4 py-2.5 font-medium text-center">Current</th>
              <th className="px-4 py-2.5 font-medium text-center">Pipeline</th>
              <th className="px-4 py-2.5 font-medium text-center">Gap</th>
              <th className="px-4 py-2.5 font-medium">Budget</th>
              <th className="px-4 py-2.5 font-medium">Spend</th>
              <th className="px-4 py-2.5 font-medium text-center">Util.</th>
              {!readOnly && <th className="px-4 py-2.5"><span className="sr-only">Actions</span></th>}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
            {groups.map(g => g.rows.length === 0 ? null : (
              <GroupRows key={g.id ?? NONE} groupName={g.name} colSpan={colSpan}>
                {g.rows.map(r => {
                  const e = editFor(r);
                  const dirty = isDirty(r);
                  const u = utilization(r.currentMonthlySpend, r.monthlyBudgetAmount);
                  const isExpanded = !!expanded[r.departmentId];
                  const allocated = allocatedInEdit(r);
                  const unallocated = e.approved - allocated;
                  return (
                    <FragmentRows key={r.departmentId}>
                      <tr
                        ref={el => { rowRefs.current[r.departmentId] = el; }}
                        className="hover:bg-slate-50/50 dark:hover:bg-white/[0.02]"
                      >
                        <td className="px-2 py-2">
                          {matrixAvailable && (
                            <button
                              type="button"
                              aria-expanded={isExpanded}
                              aria-label={`${t('Staffing levels')} — ${r.departmentName}`}
                              onClick={() => setExpanded(x => ({ ...x, [r.departmentId]: !isExpanded }))}
                              className="grid h-6 w-6 place-items-center rounded text-slate-400 hover:bg-slate-100 hover:text-slate-600 dark:hover:bg-white/[0.07]"
                            >
                              {isExpanded ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
                            </button>
                          )}
                        </td>
                        <td className="px-4 py-2 font-medium text-slate-800 dark:text-slate-200">{r.departmentName}</td>
                        {!readOnly && (
                          <td className="px-4 py-2">
                            <select className="select w-40 text-sm" aria-label={`Cost centre for ${r.departmentName}`}
                              disabled={!canEdit}
                              value={e.costCenterId ?? ''} onChange={ev => setEdit(r, { costCenterId: ev.target.value || null })}>
                              <option value="">— Unassigned —</option>
                              {costCenters.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
                            </select>
                          </td>
                        )}
                        <td className="px-4 py-2 text-center">
                          {!canEdit
                            ? <span className="font-semibold text-slate-700 dark:text-slate-200">{r.approvedHeadcount || '—'}</span>
                            : <input type="number" min={0} aria-label={`Approved headcount for ${r.departmentName}`} className="input w-16 text-center text-sm" value={e.approved}
                                onChange={ev => setEdit(r, { approved: Math.max(0, parseInt(ev.target.value || '0', 10)) })} />}
                        </td>
                        <td className="px-4 py-2 text-center font-semibold text-slate-700 dark:text-slate-200">{r.currentHeadcount}</td>
                        <td className="px-4 py-2 text-center text-slate-500 dark:text-slate-400">{r.openRequisitionHeadcount || '—'}</td>
                        <td className={`px-4 py-2 text-center font-semibold ${gapTone(r.approvedHeadcount, r.gap)}`}>
                          {r.approvedHeadcount <= 0 ? '—' : r.gap > 0 ? `+${r.gap}` : r.gap}
                        </td>
                        <td className="px-4 py-2">
                          {!canEdit
                            ? <span className="text-slate-600 dark:text-slate-300">{r.monthlyBudgetAmount > 0 ? money(r.monthlyBudgetAmount) : '—'}</span>
                            : <div className="flex items-center gap-1"><span className="text-xs text-slate-400">{currencyCode}</span>
                                <input type="number" min={0} aria-label={`Monthly budget for ${r.departmentName}`} className="input w-24 text-sm" value={e.budget}
                                  onChange={ev => setEdit(r, { budget: Math.max(0, parseFloat(ev.target.value || '0')) })} /></div>}
                        </td>
                        <td className="px-4 py-2 text-slate-600 dark:text-slate-300">{r.currentMonthlySpend > 0 ? money(r.currentMonthlySpend) : '—'}</td>
                        <td className={`px-4 py-2 text-center font-semibold ${utilTone(u)}`}>{u === null ? '—' : `${u}%`}</td>
                        {!readOnly && (
                          <td className="px-4 py-2 text-right">
                            {savedId === r.departmentId
                              ? <span className="inline-flex items-center gap-1 text-xs text-emerald-500"><CheckCircle2 className="h-3.5 w-3.5" /> Saved</span>
                              : canEdit
                                ? <button type="button" className="btn-secondary text-xs disabled:opacity-40" disabled={!dirty} onClick={() => beginSave(r)}>Save</button>
                                : <span className="text-[11px] text-slate-400">{t('Read-only')}</span>}
                          </td>
                        )}
                      </tr>

                      {/* ── Per-level staffing budget breakdown ── */}
                      {isExpanded && matrixAvailable && (
                        <tr className="bg-slate-50/40 dark:bg-white/[0.015]">
                          <td colSpan={colSpan} className="px-4 py-3 sm:px-10">
                            <div className="space-y-2.5">
                              <div className="flex flex-wrap items-center gap-2 text-xs">
                                <span className={`font-semibold ${allocated > e.approved && e.approved > 0 ? 'text-amber-600 dark:text-amber-400' : 'text-slate-600 dark:text-slate-300'}`}>
                                  {fmt(t('Allocated {allocated} of {approved} (unallocated: {unallocated})'), {
                                    allocated,
                                    approved: e.approved > 0 ? e.approved : `— (${t('not set')})`,
                                    unallocated: e.approved > 0 ? unallocated : '—',
                                  })}
                                </span>
                                {allocated > e.approved && e.approved > 0 && (
                                  <span className="inline-flex items-center gap-1 rounded-md bg-amber-50 px-2 py-0.5 font-semibold text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">
                                    <AlertTriangle className="h-3 w-3" />{t('Level budgets exceed the approved headcount.')}
                                  </span>
                                )}
                                {r.unclassifiedCount > 0 && (
                                  <span className="rounded-md bg-slate-100 px-2 py-0.5 font-semibold text-slate-500 dark:bg-white/10 dark:text-slate-300" title={t('Employees with no or unmapped designation — never counted, never blocked.')}>
                                    {fmt(t('Unclassified: {n}'), { n: r.unclassifiedCount })}
                                  </span>
                                )}
                                {r.unresolvedDepartmentCount > 0 && (
                                  <span className="rounded-md bg-slate-100 px-2 py-0.5 font-semibold text-slate-500 dark:bg-white/10 dark:text-slate-300" title={t('Employees whose department is stored as free text and could not be resolved.')}>
                                    {fmt(t('Unresolved department: {n}'), { n: r.unresolvedDepartmentCount })}
                                  </span>
                                )}
                                {r.managerEmployeeId != null && (
                                  <span className="rounded-md bg-slate-100 px-2 py-0.5 text-slate-400 dark:bg-white/10" title={t('The department manager assignment is independent of level budgets and is never used in enforcement.')}>
                                    {t('Manager assigned')}
                                  </span>
                                )}
                              </div>

                              <div className="overflow-x-auto rounded-lg border border-slate-200 dark:border-white/10">
                                <table className="w-full text-xs">
                                  <thead className="bg-slate-50 text-left text-slate-500 dark:bg-white/[0.03] dark:text-slate-400">
                                    <tr>
                                      <th className="px-3 py-2 font-medium">{t('Level')}</th>
                                      <th className="px-3 py-2 font-medium text-center">{t('Budgeted')}</th>
                                      <th className="px-3 py-2 font-medium text-center">{t('Current')}</th>
                                      <th className="px-3 py-2 font-medium text-center">{t('Gap')}</th>
                                      <th className="px-3 py-2 font-medium">{t('Notes')}</th>
                                    </tr>
                                  </thead>
                                  <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                                    {activeLevels.map(lvl => {
                                      const cell = cellFor(r, lvl.id);
                                      const raw = levelEditValue(r, lvl.id);
                                      const inEdit = parseBudget(raw);
                                      // Live gap while typing — client-side from current vs the in-edit value.
                                      const liveGap = inEdit === null ? null : inEdit - cell.current;
                                      return (
                                        <tr key={lvl.id}>
                                          <td className="px-3 py-2 font-medium text-slate-700 dark:text-slate-200">
                                            {levelName(lvl)}
                                            {inEdit === 0 && <span className="ml-1.5 rounded bg-sky-100 px-1.5 py-0.5 text-[10px] font-semibold uppercase text-sky-700 dark:bg-sky-500/15 dark:text-sky-300">{t('frozen')}</span>}
                                          </td>
                                          <td className="px-3 py-2 text-center">
                                            {canEdit ? (
                                              <input
                                                ref={el => { levelInputRefs.current[`${r.departmentId}:${lvl.id}`] = el; }}
                                                type="number" min={0}
                                                aria-label={`${t('Budgeted')} ${levelName(lvl)} — ${r.departmentName}`}
                                                className="input w-16 text-center text-xs"
                                                value={raw}
                                                placeholder="—"
                                                title={t('Empty = uncontrolled (unlimited). 0 = frozen (no additions).')}
                                                onChange={ev => setLevelEdit(r, lvl.id, ev.target.value)}
                                              />
                                            ) : (
                                              <span className="font-semibold text-slate-700 dark:text-slate-200">{cell.budgeted === null ? '—' : cell.budgeted}</span>
                                            )}
                                          </td>
                                          <td className="px-3 py-2 text-center font-semibold text-slate-700 dark:text-slate-200">{cell.current}</td>
                                          <td className="px-3 py-2 text-center">
                                            {liveGap === null
                                              ? <span className="text-slate-400" title={t('Level is uncontrolled — no budget row.')}>—</span>
                                              : liveGap < 0
                                                ? <span className="font-semibold text-rose-600 dark:text-rose-400">{fmt(t('over-establishment: {n}'), { n: Math.abs(liveGap) })}</span>
                                                : <span className={`font-semibold ${liveGap === 0 ? 'text-emerald-600 dark:text-emerald-400' : 'text-amber-600 dark:text-amber-400'}`}>{liveGap > 0 ? `+${liveGap}` : liveGap}</span>}
                                          </td>
                                          <td className="px-3 py-2">
                                            <div className="flex flex-wrap gap-1.5">
                                              {cell.exitingIncumbents > 0 && (
                                                <span className="rounded-md bg-amber-50 px-1.5 py-0.5 font-semibold text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">
                                                  {fmt(t('{n} serving notice'), { n: cell.exitingIncumbents })}
                                                </span>
                                              )}
                                              {cell.positionsExceedBudget && (
                                                <span className="rounded-md bg-amber-50 px-1.5 py-0.5 font-semibold text-amber-700 dark:bg-amber-500/10 dark:text-amber-400" title={t('Level-mapped positions exceed this level budget (warning only).')}>
                                                  {t('Positions exceed budget')}
                                                </span>
                                              )}
                                            </div>
                                          </td>
                                        </tr>
                                      );
                                    })}
                                    {activeLevels.length === 0 && (
                                      <tr><td colSpan={5} className="px-3 py-4 text-center text-slate-400">{t('No staffing levels configured yet.')}</td></tr>
                                    )}
                                  </tbody>
                                </table>
                              </div>
                            </div>
                          </td>
                        </tr>
                      )}
                    </FragmentRows>
                  );
                })}
              </GroupRows>
            ))}
            {rows.length === 0 && <tr><td colSpan={colSpan} className="px-4 py-8 text-center text-slate-400">No departments yet. Create departments first (or use AI Setup).</td></tr>}
          </tbody>
        </table>
      </div>

      {/* ── Reason + confirm modal for establishment changes (mandatory, audited) ── */}
      <Modal
        isOpen={!!pendingSave}
        title={t('Confirm establishment change')}
        onClose={() => setPendingSave(null)}
        footer={
          <>
            <button type="button" className="btn-secondary" onClick={() => setPendingSave(null)}>{t('Cancel')}</button>
            <button
              type="button"
              className="btn-primary disabled:opacity-50"
              disabled={savingBudget || !saveReason.trim()}
              onClick={() => pendingSave && commitSave(pendingSave, saveReason.trim())}
            >
              {savingBudget ? t('Saving…') : t('Confirm save')}
            </button>
          </>
        }
      >
        {pendingSave && (
          <div className="space-y-3 text-sm">
            <p className="text-slate-600 dark:text-slate-300">
              <span className="font-semibold">{pendingSave.row.departmentName}</span>
            </p>

            {pendingSave.levelChanges.length > 0 && (
              <ul className="space-y-1.5">
                {pendingSave.levelChanges.map(c => (
                  <li key={c.staffingLevelId} className="flex flex-wrap items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-xs dark:border-white/10">
                    <span className="font-semibold text-slate-700 dark:text-slate-200">{c.levelName}</span>
                    <span className="text-slate-500 dark:text-slate-400">
                      {c.before === null ? `— (${t('uncontrolled')})` : c.before} → {c.after === null ? `— (${t('uncontrolled')})` : c.after}
                    </span>
                    {c.after === null && c.before !== null && (
                      <span className="rounded bg-slate-100 px-1.5 py-0.5 font-semibold text-slate-500 dark:bg-white/10 dark:text-slate-300">{t('Clearing a budget returns that level to unlimited.')}</span>
                    )}
                    {c.after === 0 && (
                      <span className="rounded bg-sky-100 px-1.5 py-0.5 font-semibold text-sky-700 dark:bg-sky-500/15 dark:text-sky-300">{t('0 freezes the level — no additions will be sanctioned.')}</span>
                    )}
                    {c.after !== null && c.after < c.current && (
                      <span className="inline-flex items-center gap-1 rounded bg-amber-50 px-1.5 py-0.5 font-semibold text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">
                        <AlertTriangle className="h-3 w-3" />
                        {fmt(t('Below current occupancy ({current}) — existing staff are never affected; the level will show as over-establishment.'), { current: c.current })}
                      </span>
                    )}
                  </li>
                ))}
              </ul>
            )}

            {pendingSave.envelopeDirty && (
              <p className="rounded-lg border border-slate-200 px-3 py-2 text-xs text-slate-500 dark:border-white/10 dark:text-slate-400">
                {t('Approved headcount / monthly budget')}: {pendingSave.row.approvedHeadcount} → {pendingSave.edit.approved} · {money(pendingSave.row.monthlyBudgetAmount)} → {money(pendingSave.edit.budget)}
              </p>
            )}

            {(() => {
              const approvedTarget = pendingSave.edit.approved;
              const alloc = allocatedInEdit(pendingSave.row);
              return approvedTarget > 0 && alloc > approvedTarget ? (
                <p className="flex items-center gap-1.5 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs font-medium text-amber-700 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
                  <AlertTriangle className="h-3.5 w-3.5 shrink-0" />
                  {fmt(t('Level budgets total {sum}, above the approved headcount of {approved}. Saving is allowed — this is a warning.'), { sum: alloc, approved: approvedTarget })}
                </p>
              ) : null;
            })()}

            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">
              {t('Reason for change')} <span className="text-red-500">*</span>
              <textarea
                value={saveReason}
                onChange={ev => setSaveReason(ev.target.value)}
                rows={2}
                className="input mt-1.5 w-full resize-none"
                placeholder={t('e.g. Approved by management for Q3 expansion')}
              />
            </label>
            <p className="text-xs text-slate-400">{t('A reason is required for every establishment change (audited).')}</p>
          </div>
        )}
      </Modal>

      {/* ── Enforcement-mode change modal (reason mandatory, audited) ── */}
      <Modal
        isOpen={!!pendingMode}
        title={t('Change enforcement mode')}
        onClose={() => setPendingMode(null)}
        size="sm"
        footer={
          <>
            <button type="button" className="btn-secondary" onClick={() => setPendingMode(null)}>{t('Cancel')}</button>
            <button
              type="button"
              className="btn-primary disabled:opacity-50"
              disabled={savingMode || !modeReason.trim()}
              onClick={commitModeChange}
            >
              {savingMode ? t('Saving…') : t('Confirm')}
            </button>
          </>
        }
      >
        {pendingMode && hrConfig && (
          <div className="space-y-3 text-sm">
            <p className="text-slate-600 dark:text-slate-300">
              {t(hrConfig.establishmentEnforcementMode || 'Enforced')} → <span className="font-semibold">{t(pendingMode)}</span>
            </p>
            {pendingMode === 'Off' && (
              <p className="flex items-center gap-1.5 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs font-medium text-amber-700 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
                <AlertTriangle className="h-3.5 w-3.5 shrink-0" />
                {t('Turning enforcement off disables every level budget in this tenant.')}
              </p>
            )}
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">
              {t('Reason for change')} <span className="text-red-500">*</span>
              <textarea value={modeReason} onChange={ev => setModeReason(ev.target.value)} rows={2} className="input mt-1.5 w-full resize-none" />
            </label>
          </div>
        )}
      </Modal>
    </div>
  );
}

/** Cost-centre group header + its department rows (kept as table-row siblings). */
function GroupRows({ groupName, colSpan, children }: { groupName: string; colSpan: number; children: React.ReactNode }) {
  return (
    <>
      <tr className="bg-slate-50/60 dark:bg-white/[0.02]">
        <td colSpan={colSpan} className="px-4 py-1.5 text-[11px] font-semibold uppercase tracking-wide text-slate-400">{groupName}</td>
      </tr>
      {children}
    </>
  );
}

/** Fragment alias for a department row + its expansion row. */
function FragmentRows({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
