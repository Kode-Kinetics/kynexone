'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { AlertTriangle, Pencil, Plus, Trash2 } from 'lucide-react';
import { notifyApiError } from '../../api/client';
import {
  financeGlApi,
  GL_DRIVER_CATEGORIES,
  GL_DRIVER_MATCH_MODES,
  GL_ACCOUNT_TYPES,
  type GlDriver,
  type GlDriverRequest,
} from '../../api/financeGl';
import { Modal } from '../Modal';
import { Badge, Field, FieldError, InheritedBadge, PanelState, SystemBadge } from './glUi';

interface Props {
  scope: string | null;
  scopeLabel: string;
  canManage: boolean; // finance.gl.drivers.manage
  canAuthorPredicates: boolean; // finance.gl.drivers.author_predicates
}

const MATCH_SOURCES = ['Bonus', 'Statutory', 'Tax', 'Loan', 'Attendance', 'Leave'] as const;

const emptyDraft = (): GlDriverRequest => ({
  key: '',
  label: '',
  category: 'Earning',
  postingSide: 'DR',
  accountType: 'Expense',
  defaultCode: '',
  defaultName: '',
  matchSource: '',
  matchMode: 'Exact',
  matchComponentCode: '',
  emitsEmployerExpensePair: false,
  pairedExpenseDriverKey: '',
  sortOrder: 500,
  isActive: true,
});

export function DriverManagerPanel({ scope, scopeLabel, canManage, canAuthorPredicates }: Props) {
  const inCompanyView = scope !== null;
  const [drivers, setDrivers] = useState<GlDriver[]>([]);
  const [loading, setLoading] = useState(true);

  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<GlDriver | null>(null);
  const [draft, setDraft] = useState<GlDriverRequest>(emptyDraft());
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  const [deletingId, setDeletingId] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      setDrivers(await financeGlApi.listDrivers(scope));
    } catch (e) {
      notifyApiError(e);
    } finally {
      setLoading(false);
    }
  }, [scope]);

  useEffect(() => {
    load();
  }, [load]);

  // A driver is editable in this scope only if owned by it (system + group-custom in group view;
  // company-custom in a company view). Inherited rows are read-only.
  const ownedHere = (d: GlDriver) => d.companyId === scope;

  const systemDrDrivers = useMemo(
    () => drivers.filter((d) => d.postingSide === 'DR' && d.isSystem),
    [drivers],
  );

  const setField = <K extends keyof GlDriverRequest>(k: K, v: GlDriverRequest[K]) =>
    setDraft((d) => ({ ...d, [k]: v }));

  const openCreate = () => {
    setEditing(null);
    setDraft(emptyDraft());
    setError('');
    setModalOpen(true);
  };

  const openEdit = (d: GlDriver) => {
    setEditing(d);
    setDraft({
      key: d.key,
      label: d.label,
      category: d.category,
      postingSide: d.postingSide,
      accountType: d.accountType,
      defaultCode: d.defaultCode,
      defaultName: d.defaultName,
      matchSource: d.matchSource ?? '',
      matchMode: d.matchMode,
      matchComponentCode: d.matchComponentCode ?? '',
      emitsEmployerExpensePair: d.emitsEmployerExpensePair,
      pairedExpenseDriverKey: d.pairedExpenseDriverKey ?? '',
      sortOrder: d.sortOrder,
      isActive: d.isActive,
    });
    setError('');
    setModalOpen(true);
  };

  const validateDraft = (): string | null => {
    if (editing?.isSystem) return null; // system: only editable fields sent; server enforces the rest
    if (!draft.key.trim()) return 'Key is required.';
    if (!draft.defaultCode.trim() || !draft.defaultName.trim()) return 'Default account code and name are required.';
    const mode = draft.matchMode ?? 'Exact';
    const hasCode = !!draft.matchComponentCode?.trim();
    if (mode === 'Any' && hasCode) return 'Match mode "Any" must not carry a component code.';
    if (mode !== 'Any' && !hasCode) return `Match mode "${mode}" requires a component code.`;
    if (draft.emitsEmployerExpensePair && !draft.pairedExpenseDriverKey?.trim())
      return 'A paired employer-expense driver is required when the employer-expense pair is enabled.';
    return null;
  };

  const save = async () => {
    const v = validateDraft();
    if (v) {
      setError(v);
      return;
    }
    setSaving(true);
    setError('');
    try {
      if (editing) {
        await financeGlApi.updateDriver(editing.id, draft);
        setModalOpen(false);
        await load();
      } else {
        const res = await financeGlApi.createDriver(draft, scope);
        setModalOpen(false);
        await load();
        if (res.warning && typeof window !== 'undefined') {
          window.dispatchEvent(new CustomEvent('zayra:error', { detail: res.warning }));
        }
      }
    } catch (err) {
      setError((err as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Failed to save driver.');
    } finally {
      setSaving(false);
    }
  };

  const remove = async (d: GlDriver) => {
    if (!window.confirm(`Delete custom driver "${d.key}"?`)) return;
    setDeletingId(d.id);
    try {
      await financeGlApi.deleteDriver(d.id);
      await load();
    } catch (e) {
      notifyApiError(e);
    } finally {
      setDeletingId(null);
    }
  };

  const mode = draft.matchMode ?? 'Exact';
  const isSystemEdit = !!editing?.isSystem;
  // Non-Admin authors are restricted to Exact-code drivers with no employer-expense pairing.
  const predicateLocked = !canAuthorPredicates && !isSystemEdit;

  return (
    <div className="surface p-4">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <div>
          <h3 className="text-sm font-semibold text-slate-900 dark:text-white">Posting Drivers</h3>
          <p className="text-xs text-slate-500 dark:text-slate-400">
            How payroll lines route to GL accounts for <span className="font-medium">{scopeLabel}</span>. System drivers are read-only; add custom drivers for bespoke components.
          </p>
        </div>
        {canManage && (
          <button type="button" onClick={openCreate} className="btn-primary text-xs">
            <Plus className="h-3.5 w-3.5" /> Add driver
          </button>
        )}
      </div>

      <PanelState loading={loading} empty={drivers.length === 0} emptyLabel="No drivers found. Seed defaults from the Accounts tab.">
        <div className="overflow-x-auto rounded-lg border border-slate-200 dark:border-white/10">
          <table className="w-full min-w-[720px] text-sm">
            <thead className="bg-slate-50 text-left text-xs text-slate-500 dark:bg-white/[0.03] dark:text-slate-400">
              <tr>
                <th className="px-3 py-2">Key</th>
                <th className="px-3 py-2">Category</th>
                <th className="px-3 py-2">Side</th>
                <th className="px-3 py-2">Match</th>
                <th className="px-3 py-2">Default account</th>
                <th className="px-3 py-2 text-right">Manage</th>
              </tr>
            </thead>
            <tbody>
              {drivers.map((d) => {
                // A non-predicate-author may only edit system drivers (limited fields) and their own
                // Exact, non-paired custom drivers — never a predicate-authored one they can't re-save.
                const editable =
                  canManage &&
                  ownedHere(d) &&
                  (d.isSystem || canAuthorPredicates || (d.matchMode === 'Exact' && !d.emitsEmployerExpensePair));
                const matchDesc =
                  d.matchMode === 'Any'
                    ? `any${d.matchSource ? ` · ${d.matchSource}` : ''}`
                    : `${d.matchMode} "${d.matchComponentCode ?? ''}"${d.matchSource ? ` · ${d.matchSource}` : ''}`;
                return (
                  <tr key={d.id} className={`border-t border-slate-100 dark:border-white/5 ${d.isActive ? '' : 'opacity-50'}`}>
                    <td className="px-3 py-2">
                      <div className="flex items-center gap-1.5">
                        <span className="font-mono text-xs">{d.key}</span>
                        {d.isSystem && <SystemBadge />}
                        {inCompanyView && d.companyId === null && <InheritedBadge />}
                        {inCompanyView && d.companyId === scope && <Badge tone="violet">Company</Badge>}
                        {d.emitsEmployerExpensePair && <Badge tone="amber" title={`Emits a paired DR line to ${d.pairedExpenseDriverKey}`}>ER pair</Badge>}
                      </div>
                      <p className="text-[11px] text-slate-400">{d.label}</p>
                    </td>
                    <td className="px-3 py-2 text-slate-500">{d.category}</td>
                    <td className="px-3 py-2 font-mono text-xs text-slate-500">{d.postingSide}</td>
                    <td className="px-3 py-2 text-xs text-slate-500">{matchDesc}</td>
                    <td className="px-3 py-2 font-mono text-xs text-slate-500">
                      {d.defaultCode} — {d.defaultName}
                    </td>
                    <td className="px-3 py-2">
                      <div className="flex items-center justify-end gap-1">
                        {editable ? (
                          <>
                            <button type="button" onClick={() => openEdit(d)} className="btn-secondary h-7 px-2 text-xs">
                              <Pencil className="h-3 w-3" /> Edit
                            </button>
                            {!d.isSystem && (
                              <button
                                type="button"
                                onClick={() => remove(d)}
                                disabled={deletingId === d.id}
                                aria-label="Delete driver"
                                className="grid h-7 w-7 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-500 disabled:opacity-40 dark:border-white/10 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400"
                              >
                                <Trash2 className="h-3.5 w-3.5" />
                              </button>
                            )}
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

      {/* Create / edit driver modal */}
      <Modal
        isOpen={modalOpen}
        title={editing ? (isSystemEdit ? `System Driver ${editing.key}` : `Edit Driver ${editing.key}`) : 'Add Custom Driver'}
        onClose={() => setModalOpen(false)}
        size="lg"
        footer={
          <>
            <button type="button" onClick={() => setModalOpen(false)} className="btn-secondary">
              Cancel
            </button>
            <button type="button" onClick={save} disabled={saving} className="btn-primary disabled:opacity-60">
              {saving ? 'Saving…' : 'Save'}
            </button>
          </>
        }
      >
        <FieldError error={error} />
        {isSystemEdit && (
          <p className="mb-3 rounded-lg bg-sapphire/10 px-3 py-2 text-xs text-sapphire dark:bg-sapphire/15 dark:text-cyanAccent">
            This is a system driver. Only its label, default account, sort order and active state can change — routing rules are locked.
          </p>
        )}
        {predicateLocked && !editing && (
          <p className="mb-3 flex items-start gap-1.5 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">
            <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
            You can add Exact-code drivers and remap accounts. Suffix/Prefix/Any predicates and employer-expense pairs require the higher-trust
            &nbsp;<span className="font-mono">drivers.author_predicates</span>&nbsp;permission.
          </p>
        )}

        <div className="grid grid-cols-2 gap-3">
          <Field label="Key" required hint='Unique, e.g. "EARN:WELLNESS". Cannot reuse a system key.'>
            <input
              value={draft.key}
              disabled={isSystemEdit}
              onChange={(e) => setField('key', e.target.value)}
              className="input w-full font-mono disabled:opacity-60"
              placeholder="EARN:WELLNESS"
            />
          </Field>
          <Field label="Label" required>
            <input value={draft.label} onChange={(e) => setField('label', e.target.value)} className="input w-full" placeholder="Earning — Wellness" />
          </Field>

          <Field label="Category" required>
            <select value={draft.category} disabled={isSystemEdit} onChange={(e) => setField('category', e.target.value)} className="select w-full disabled:opacity-60">
              {GL_DRIVER_CATEGORIES.map((c) => (
                <option key={c}>{c}</option>
              ))}
            </select>
          </Field>
          <Field label="Posting side" required>
            <select value={draft.postingSide} disabled={isSystemEdit} onChange={(e) => setField('postingSide', e.target.value)} className="select w-full disabled:opacity-60">
              <option value="DR">DR — Debit</option>
              <option value="CR">CR — Credit</option>
            </select>
          </Field>

          <Field label="Default account code" required>
            <input value={draft.defaultCode} onChange={(e) => setField('defaultCode', e.target.value)} className="input w-full font-mono" placeholder="5006" />
          </Field>
          <Field label="Default account name" required>
            <input value={draft.defaultName} onChange={(e) => setField('defaultName', e.target.value)} className="input w-full" placeholder="Wellness Allowance Expense" />
          </Field>

          <Field label="Account type">
            <select value={draft.accountType} disabled={isSystemEdit} onChange={(e) => setField('accountType', e.target.value)} className="select w-full disabled:opacity-60">
              {GL_ACCOUNT_TYPES.map((t) => (
                <option key={t}>{t}</option>
              ))}
            </select>
          </Field>
          <Field label="Sort order" hint="Lower sorts earlier; used to break specificity ties.">
            <input
              type="number"
              value={draft.sortOrder}
              onChange={(e) => setField('sortOrder', Number(e.target.value))}
              className="input w-full"
            />
          </Field>

          {/* Routing predicate — locked for system drivers and non-predicate-authors */}
          <Field label="Match mode" hint="Exact matches a component code; Any is a category catch-all.">
            <select
              value={mode}
              disabled={isSystemEdit || predicateLocked}
              onChange={(e) => setField('matchMode', e.target.value)}
              className="select w-full disabled:opacity-60"
            >
              {GL_DRIVER_MATCH_MODES.map((m) => (
                <option key={m} value={m} disabled={predicateLocked && m !== 'Exact'}>
                  {m}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Match component code" hint={mode === 'Any' ? 'Not used for "Any".' : 'The component code to match.'}>
            <input
              value={draft.matchComponentCode ?? ''}
              disabled={isSystemEdit || mode === 'Any'}
              onChange={(e) => setField('matchComponentCode', e.target.value)}
              className="input w-full font-mono disabled:opacity-60"
              placeholder="WELLNESS"
            />
          </Field>

          <Field label="Match source" hint="Optional: restrict to a component source.">
            <select
              value={draft.matchSource ?? ''}
              disabled={isSystemEdit || predicateLocked}
              onChange={(e) => setField('matchSource', e.target.value)}
              className="select w-full disabled:opacity-60"
            >
              <option value="">Any source</option>
              {MATCH_SOURCES.map((s) => (
                <option key={s}>{s}</option>
              ))}
            </select>
          </Field>
          <Field label="Active">
            <select value={draft.isActive ? 'true' : 'false'} onChange={(e) => setField('isActive', e.target.value === 'true')} className="select w-full">
              <option value="true">Active</option>
              <option value="false">Inactive</option>
            </select>
          </Field>

          {!predicateLocked && (
            <>
              <Field label="Employer-expense pair" hint="Emits a paired employer DR line (statutory employer cost).">
                <select
                  value={draft.emitsEmployerExpensePair ? 'true' : 'false'}
                  disabled={isSystemEdit}
                  onChange={(e) => setField('emitsEmployerExpensePair', e.target.value === 'true')}
                  className="select w-full disabled:opacity-60"
                >
                  <option value="false">No</option>
                  <option value="true">Yes</option>
                </select>
              </Field>
              {draft.emitsEmployerExpensePair && (
                <Field label="Paired expense driver" required hint="Must be a system DR balancing driver.">
                  <select
                    value={draft.pairedExpenseDriverKey ?? ''}
                    disabled={isSystemEdit}
                    onChange={(e) => setField('pairedExpenseDriverKey', e.target.value)}
                    className="select w-full disabled:opacity-60"
                  >
                    <option value="">Select…</option>
                    {systemDrDrivers.map((d) => (
                      <option key={d.key} value={d.key}>
                        {d.key}
                      </option>
                    ))}
                  </select>
                </Field>
              )}
            </>
          )}
        </div>
      </Modal>
    </div>
  );
}
