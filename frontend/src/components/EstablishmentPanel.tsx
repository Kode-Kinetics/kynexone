'use client';

import { useEffect, useMemo, useState } from 'react';
import { CheckCircle2, Plus, Trash2, Building2 } from 'lucide-react';
import { planningApi, type EstablishmentRow } from '../api/planning';
import { costCentersApi, type CostCenterDto } from '../api/organization';
import { useTenantSettings } from '../contexts/TenantSettingsContext';

const NONE = '__none__';

interface RowEdit { approved: number; budget: number; costCenterId: string | null; }

export function EstablishmentPanel({ readOnly = false }: { readOnly?: boolean } = {}) {
  const { currencyCode } = useTenantSettings();
  const [rows, setRows] = useState<EstablishmentRow[]>([]);
  const [costCenters, setCostCenters] = useState<CostCenterDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [edits, setEdits] = useState<Record<string, RowEdit>>({});
  const [savedId, setSavedId] = useState('');
  const [adding, setAdding] = useState(false);
  const [newCc, setNewCc] = useState({ code: '', name: '' });
  const [error, setError] = useState('');

  const load = () => {
    setLoading(true);
    Promise.all([planningApi.establishment(), costCentersApi.list().then(r => r.items).catch(() => [])])
      .then(([est, cc]) => { setRows(est); setCostCenters(cc); })
      .catch(() => {}).finally(() => setLoading(false));
  };
  useEffect(() => { load(); }, []);

  const editFor = (r: EstablishmentRow): RowEdit =>
    edits[r.departmentId] ?? { approved: r.approvedHeadcount, budget: r.monthlyBudgetAmount, costCenterId: r.costCenterId };
  const setEdit = (r: EstablishmentRow, patch: Partial<RowEdit>) =>
    setEdits(e => ({ ...e, [r.departmentId]: { ...editFor(r), ...patch } }));

  const save = async (r: EstablishmentRow) => {
    const e = editFor(r);
    await planningApi.setEstablishment(r.departmentId, {
      approvedHeadcount: e.approved, monthlyBudgetAmount: e.budget,
      costCenterId: e.costCenterId ?? null,
    }).catch(() => {});
    setSavedId(r.departmentId); setTimeout(() => setSavedId(''), 1500);
    setEdits(prev => { const n = { ...prev }; delete n[r.departmentId]; return n; });
    load();
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
    const byId = new Map<string, { id: string | null; name: string; rows: EstablishmentRow[] }>();
    for (const cc of costCenters) byId.set(cc.id, { id: cc.id, name: cc.name, rows: [] });
    byId.set(NONE, { id: null, name: 'Unassigned', rows: [] });
    for (const r of rows) {
      const key = r.costCenterId && byId.has(r.costCenterId) ? r.costCenterId : NONE;
      byId.get(key)!.rows.push(r);
    }
    return Array.from(byId.values()).filter(g => g.rows.length > 0 || g.id !== null);
  }, [rows, costCenters]);

  const rollup = (gr: EstablishmentRow[]) => ({
    depts: gr.length,
    approved: gr.reduce((s, r) => s + r.approvedHeadcount, 0),
    current: gr.reduce((s, r) => s + r.currentHeadcount, 0),
    budget: gr.reduce((s, r) => s + r.monthlyBudgetAmount, 0),
    spend: gr.reduce((s, r) => s + r.currentMonthlySpend, 0),
    pipeline: gr.reduce((s, r) => s + r.openRequisitionHeadcount, 0),
  });

  if (loading) return <div className="flex justify-center py-12"><div className="h-5 w-5 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>;

  return (
    <div className="space-y-5">
      <p className="rounded-lg border border-slate-200 bg-slate-50 p-3 text-xs text-slate-500 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-400">
        {readOnly
          ? <>Headcount &amp; budget per cost centre (configured in <span className="font-medium">Company Setup → Cost Centres &amp; Budget</span>). <span className="font-medium">Current</span> &amp; <span className="font-medium">Spend</span> are live from active employees.</>
          : <>Plan each cost centre&apos;s departments: set <span className="font-medium">approved headcount</span> and <span className="font-medium">monthly budget</span>, and assign departments to cost centres. <span className="font-medium">Current</span> headcount and actual <span className="font-medium">salary spend</span> are counted live, so vacancies, resignations and over-budget spend surface automatically.</>}
      </p>

      {/* ── Cost-centre rollup cards ─────────────────────────────────────── */}
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {groups.map(g => {
          const t = rollup(g.rows);
          const u = utilization(t.spend, t.budget);
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
                <span className="text-right font-medium text-slate-700 dark:text-slate-200">{t.current}{t.approved > 0 && <span className="text-slate-400"> / {t.approved}</span>}</span>
                <span className="text-slate-400">Gap</span>
                <span className={`text-right font-medium ${gapTone(t.approved, t.approved - t.current)}`}>{t.approved > 0 ? (t.approved - t.current > 0 ? `+${t.approved - t.current}` : t.approved - t.current) : '—'}</span>
                <span className="text-slate-400">Budget / Spend</span>
                <span className="text-right font-medium text-slate-700 dark:text-slate-200">{t.budget > 0 ? `${money(t.spend)} / ${money(t.budget)}` : money(t.spend)}</span>
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
            {groups.map(g => (
              <GroupBody
                key={g.id ?? NONE} groupName={g.name} rows={g.rows} readOnly={readOnly}
                editFor={editFor} setEdit={setEdit} save={save} savedId={savedId}
                costCenters={costCenters} currencyCode={currencyCode}
                money={money} utilization={utilization} utilTone={utilTone} gapTone={gapTone}
              />
            ))}
            {rows.length === 0 && <tr><td colSpan={readOnly ? 8 : 10} className="px-4 py-8 text-center text-slate-400">No departments yet. Create departments first (or use AI Setup).</td></tr>}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function GroupBody({ groupName, rows, readOnly, editFor, setEdit, save, savedId, costCenters, currencyCode, money, utilization, utilTone, gapTone }: {
  groupName: string; rows: EstablishmentRow[]; readOnly: boolean;
  editFor: (r: EstablishmentRow) => RowEdit; setEdit: (r: EstablishmentRow, p: Partial<RowEdit>) => void;
  save: (r: EstablishmentRow) => void; savedId: string; costCenters: CostCenterDto[]; currencyCode: string;
  money: (n: number) => string; utilization: (s: number, b: number) => number | null;
  utilTone: (u: number | null) => string; gapTone: (a: number, g: number) => string;
}) {
  if (rows.length === 0) return null;
  const colSpan = readOnly ? 8 : 10;
  return (
    <>
      <tr className="bg-slate-50/60 dark:bg-white/[0.02]">
        <td colSpan={colSpan} className="px-4 py-1.5 text-[11px] font-semibold uppercase tracking-wide text-slate-400">{groupName}</td>
      </tr>
      {rows.map(r => {
        const e = editFor(r);
        const dirty = e.approved !== r.approvedHeadcount || e.budget !== r.monthlyBudgetAmount || (e.costCenterId ?? null) !== r.costCenterId;
        const u = utilization(r.currentMonthlySpend, r.monthlyBudgetAmount);
        return (
          <tr key={r.departmentId} className="hover:bg-slate-50/50 dark:hover:bg-white/[0.02]">
            <td className="px-4 py-2 font-medium text-slate-800 dark:text-slate-200">{r.departmentName}</td>
            {!readOnly && (
              <td className="px-4 py-2">
                <select className="select w-40 text-sm" aria-label={`Cost centre for ${r.departmentName}`}
                  value={e.costCenterId ?? ''} onChange={ev => setEdit(r, { costCenterId: ev.target.value || null })}>
                  <option value="">— Unassigned —</option>
                  {costCenters.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select>
              </td>
            )}
            <td className="px-4 py-2 text-center">
              {readOnly
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
              {readOnly
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
                  : <button type="button" className="btn-secondary text-xs disabled:opacity-40" disabled={!dirty} onClick={() => save(r)}>Save</button>}
              </td>
            )}
          </tr>
        );
      })}
    </>
  );
}
