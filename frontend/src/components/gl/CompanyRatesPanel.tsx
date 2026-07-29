'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { Pencil, Plus, Trash2 } from 'lucide-react';
import { notifyApiError } from '../../api/client';
import {
  financeRatesApi,
  type CompanyRate,
  type CompanyRateRequest,
  type RateRegistryEntry,
} from '../../api/financeRates';
import { Modal } from '../Modal';
import { Field, FieldError, InheritedBadge, PanelState, StatusBadge, fmtDate, todayIso } from './glUi';

interface Props {
  scope: string | null;
  scopeLabel: string;
  canManage: boolean; // payroll.rates.manage
}

const unitSuffix = (unit: string) =>
  unit === 'percent' ? '%' : unit === 'days' ? ' days' : unit === 'multiplier' ? '×' : '';

const formatValue = (value: string, unit: string) =>
  unit === 'multiplier' ? `${value}×` : `${value}${unitSuffix(unit)}`;

interface RateDraft {
  rateKey: string;
  rateValue: string;
  effectiveFrom: string;
  effectiveTo: string;
  notes: string;
}

const emptyDraft = (): RateDraft => ({ rateKey: '', rateValue: '', effectiveFrom: todayIso(), effectiveTo: '', notes: '' });

export function CompanyRatesPanel({ scope, scopeLabel, canManage }: Props) {
  const inCompanyView = scope !== null;
  const [rates, setRates] = useState<CompanyRate[]>([]);
  const [registry, setRegistry] = useState<RateRegistryEntry[]>([]);
  const [loading, setLoading] = useState(true);

  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<CompanyRate | null>(null);
  const [draft, setDraft] = useState<RateDraft>(emptyDraft());
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  const [deletingId, setDeletingId] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const [r, reg] = await Promise.all([financeRatesApi.listCompanyRates(scope), financeRatesApi.registry()]);
      setRates(r);
      setRegistry(reg);
    } catch (e) {
      notifyApiError(e);
    } finally {
      setLoading(false);
    }
  }, [scope]);

  useEffect(() => {
    load();
  }, [load]);

  const registryFor = useCallback((key: string) => registry.find((d) => d.rateKey === key), [registry]);

  const selectedDef = registryFor(draft.rateKey);

  const openCreate = () => {
    setEditing(null);
    setDraft({ ...emptyDraft(), rateKey: registry[0]?.rateKey ?? '' });
    setError('');
    setModalOpen(true);
  };

  const openEdit = (r: CompanyRate) => {
    setEditing(r);
    setDraft({
      rateKey: r.rateKey,
      rateValue: r.rateValue,
      effectiveFrom: r.effectiveFrom.slice(0, 10),
      effectiveTo: r.effectiveTo ? r.effectiveTo.slice(0, 10) : '',
      notes: r.notes ?? '',
    });
    setError('');
    setModalOpen(true);
  };

  const validate = (): string | null => {
    if (!draft.rateKey) return 'Select a rate.';
    if (!draft.rateValue.trim()) return 'A value is required.';
    if (!draft.effectiveFrom) return 'An effective-from date is required.';
    const def = registryFor(draft.rateKey);
    if (def && def.dataType === 'decimal') {
      const v = Number(draft.rateValue);
      if (Number.isNaN(v)) return 'Value must be a number.';
      if (def.minValue != null && v < def.minValue) return `Value must be at least ${def.minValue}.`;
      if (def.maxValue != null && v > def.maxValue) return `Value must be at most ${def.maxValue}.`;
    }
    if (draft.effectiveTo && draft.effectiveTo < draft.effectiveFrom) return 'Effective-to cannot be before effective-from.';
    return null;
  };

  const save = async () => {
    const v = validate();
    if (v) {
      setError(v);
      return;
    }
    setSaving(true);
    setError('');
    const body: CompanyRateRequest = {
      rateKey: draft.rateKey,
      rateValue: draft.rateValue.trim(),
      effectiveFrom: draft.effectiveFrom,
      effectiveTo: draft.effectiveTo || null,
      notes: draft.notes || null,
    };
    try {
      if (editing) await financeRatesApi.updateCompanyRate(editing.id, body);
      else await financeRatesApi.createCompanyRate(body, scope);
      setModalOpen(false);
      await load();
    } catch (err) {
      setError((err as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Failed to save rate.');
    } finally {
      setSaving(false);
    }
  };

  const remove = async (r: CompanyRate) => {
    if (!window.confirm(`Delete rate "${r.rateKey}"? This archives it and removes it from resolution.`)) return;
    setDeletingId(r.id);
    try {
      await financeRatesApi.deleteCompanyRate(r.id);
      await load();
    } catch (e) {
      notifyApiError(e);
    } finally {
      setDeletingId(null);
    }
  };

  const ownedHere = (r: CompanyRate) => (inCompanyView ? r.companyId === scope : r.companyId === null);

  const sorted = useMemo(
    () => [...rates].sort((a, b) => a.rateKey.localeCompare(b.rateKey) || (a.effectiveFrom < b.effectiveFrom ? 1 : -1)),
    [rates],
  );

  return (
    <div className="surface p-4">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <div>
          <h3 className="text-sm font-semibold text-slate-900 dark:text-white">Company Rates</h3>
          <p className="text-xs text-slate-500 dark:text-slate-400">
            Client-configurable, non-statutory rates for <span className="font-medium">{scopeLabel}</span> — custom allowances, deductions, pay parameters and above-floor EOSB. Effective-dated; a change supersedes rather than overwrites.
          </p>
        </div>
        {canManage && (
          <button type="button" onClick={openCreate} disabled={registry.length === 0} className="btn-primary text-xs disabled:opacity-60">
            <Plus className="h-3.5 w-3.5" /> Add rate
          </button>
        )}
      </div>

      <PanelState loading={loading} empty={sorted.length === 0} emptyLabel="No company rates configured yet.">
        <div className="overflow-x-auto rounded-lg border border-slate-200 dark:border-white/10">
          <table className="w-full min-w-[680px] text-sm">
            <thead className="bg-slate-50 text-left text-xs text-slate-500 dark:bg-white/[0.03] dark:text-slate-400">
              <tr>
                <th className="px-3 py-2">Rate</th>
                <th className="px-3 py-2">Category</th>
                <th className="px-3 py-2 text-right">Value</th>
                <th className="px-3 py-2">Effective</th>
                <th className="px-3 py-2">Status</th>
                <th className="px-3 py-2 text-right">Manage</th>
              </tr>
            </thead>
            <tbody>
              {sorted.map((r) => {
                const editable = canManage && ownedHere(r) && r.status === 'Active';
                const inherited = inCompanyView && r.companyId === null;
                return (
                  <tr key={r.id} className={`border-t border-slate-100 dark:border-white/5 ${r.status === 'Archived' ? 'opacity-50' : ''}`}>
                    <td className="px-3 py-2">
                      <div className="flex items-center gap-1.5">
                        <span className="font-mono text-xs">{r.rateKey}</span>
                        {inherited && <InheritedBadge />}
                      </div>
                      {r.notes && <p className="text-[11px] text-slate-400">{r.notes}</p>}
                    </td>
                    <td className="px-3 py-2 text-slate-500">{r.rateCategory}</td>
                    <td className="px-3 py-2 text-right font-medium tabular-nums">{formatValue(r.rateValue, r.unit)}</td>
                    <td className="px-3 py-2 text-xs text-slate-500">
                      {fmtDate(r.effectiveFrom)} → {r.effectiveTo ? fmtDate(r.effectiveTo) : 'open'}
                    </td>
                    <td className="px-3 py-2">
                      <StatusBadge status={r.status} />
                    </td>
                    <td className="px-3 py-2">
                      <div className="flex items-center justify-end gap-1">
                        {editable ? (
                          <>
                            <button type="button" onClick={() => openEdit(r)} className="btn-secondary h-7 px-2 text-xs" title="Change value (supersedes this row)">
                              <Pencil className="h-3 w-3" /> Change
                            </button>
                            <button
                              type="button"
                              onClick={() => remove(r)}
                              disabled={deletingId === r.id}
                              aria-label="Delete rate"
                              className="grid h-7 w-7 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-500 disabled:opacity-40 dark:border-white/10 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400"
                            >
                              <Trash2 className="h-3.5 w-3.5" />
                            </button>
                          </>
                        ) : (
                          <span className="text-[11px] text-slate-300 dark:text-slate-600">—</span>
                        )}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </PanelState>

      <Modal
        isOpen={modalOpen}
        title={editing ? `Change ${editing.rateKey}` : 'Add Company Rate'}
        onClose={() => setModalOpen(false)}
        footer={
          <>
            <button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">
              Cancel
            </button>
            <button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">
              {saving ? 'Saving…' : editing ? 'Supersede' : 'Save'}
            </button>
          </>
        }
      >
        <FieldError error={error} />
        {editing && (
          <p className="mb-3 rounded-lg bg-sapphire/10 px-3 py-2 text-xs text-sapphire dark:bg-sapphire/15 dark:text-cyanAccent">
            Saving supersedes the current row: it is archived (closed the day before the new date) and a new effective-dated rate is created — historical payroll keeps its original value.
          </p>
        )}
        <div className="space-y-3">
          <Field label="Rate" required>
            <select
              value={draft.rateKey}
              disabled={!!editing}
              onChange={(e) => setDraft((d) => ({ ...d, rateKey: e.target.value }))}
              className="select w-full disabled:opacity-60"
            >
              <option value="">Select a rate…</option>
              {registry.map((d) => (
                <option key={d.rateKey} value={d.rateKey}>
                  {d.rateKey} · {d.rateCategory}
                </option>
              ))}
            </select>
            {selectedDef?.description && <p className="mt-1 text-[11px] text-slate-400">{selectedDef.description}</p>}
          </Field>

          <div className="grid grid-cols-2 gap-3">
            <Field
              label="Value"
              required
              hint={
                selectedDef
                  ? `Unit: ${selectedDef.unit}${selectedDef.minValue != null ? ` · min ${selectedDef.minValue}` : ''}${selectedDef.maxValue != null ? ` · max ${selectedDef.maxValue}` : ''}`
                  : undefined
              }
            >
              <div className="flex items-center gap-2">
                <input
                  type={selectedDef?.dataType === 'decimal' ? 'number' : 'text'}
                  step="any"
                  min={selectedDef?.minValue ?? undefined}
                  max={selectedDef?.maxValue ?? undefined}
                  value={draft.rateValue}
                  onChange={(e) => setDraft((d) => ({ ...d, rateValue: e.target.value }))}
                  className="input w-full"
                  placeholder="0"
                />
                {selectedDef && unitSuffix(selectedDef.unit) && (
                  <span className="text-sm text-slate-400">{selectedDef.unit === 'multiplier' ? '×' : selectedDef.unit}</span>
                )}
              </div>
            </Field>
            <Field label="Category">
              <input value={selectedDef?.rateCategory ?? ''} disabled className="input w-full opacity-60" title="Set by the rate registry" />
            </Field>
          </div>

          <div className="grid grid-cols-2 gap-3">
            <Field label="Effective from" required>
              <input type="date" value={draft.effectiveFrom} onChange={(e) => setDraft((d) => ({ ...d, effectiveFrom: e.target.value }))} className="input w-full" />
            </Field>
            <Field label="Effective to" hint="Leave blank for open-ended.">
              <input type="date" value={draft.effectiveTo} onChange={(e) => setDraft((d) => ({ ...d, effectiveTo: e.target.value }))} className="input w-full" />
            </Field>
          </div>

          <Field label="Notes">
            <textarea value={draft.notes} onChange={(e) => setDraft((d) => ({ ...d, notes: e.target.value }))} className="input w-full" rows={2} placeholder="Optional context for the record" />
          </Field>
        </div>
      </Modal>

      {!canManage && (
        <p className="mt-3 text-[11px] text-slate-400">Read-only — you do not have permission to edit company rates.</p>
      )}
      {canManage && registry.length === 0 && (
        <p className="mt-3 text-[11px] text-slate-400">No client-configurable rate keys are registered. Seed defaults from the Accounts tab.</p>
      )}
    </div>
  );
}
