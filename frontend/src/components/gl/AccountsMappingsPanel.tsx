'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { Pencil, Plus, RotateCcw, Trash2 } from 'lucide-react';
import { notifyApiError } from '../../api/client';
import {
  financeGlApi,
  GL_ACCOUNT_TYPES,
  type GlAccount,
  type GlMappingRow,
} from '../../api/financeGl';
import type { CostCenterDto } from '../../api/organization';
import { Modal } from '../Modal';
import { Badge, Field, FieldError, InheritedBadge, PanelState } from './glUi';

interface Props {
  /** null = group / tenant-default scope; a guid = a company override scope. */
  scope: string | null;
  scopeLabel: string;
  isGroupScope: boolean;
  canManage: boolean;
  costCenters: CostCenterDto[];
}

interface MapEdit {
  accountId: string;
  segmentCostCenterId: string;
}

const isForce = (err: unknown): boolean =>
  Boolean((err as { response?: { data?: { requiresForce?: boolean } } })?.response?.data?.requiresForce);

export function AccountsMappingsPanel({ scope, scopeLabel, isGroupScope, canManage, costCenters }: Props) {
  const inCompanyView = scope !== null;

  const [ownAccounts, setOwnAccounts] = useState<GlAccount[]>([]);
  const [groupDefaults, setGroupDefaults] = useState<GlAccount[]>([]);
  const [rows, setRows] = useState<GlMappingRow[]>([]);
  const [edits, setEdits] = useState<Record<string, MapEdit>>({});
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [seeding, setSeeding] = useState(false);

  // Cost centers a mapping in this scope may tag: tenant-global + this company's own.
  const visibleCostCenters = useMemo(
    () => costCenters.filter((c) => c.isActive && (!c.companyId || c.companyId === scope)),
    [costCenters, scope],
  );

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const [own, maps] = await Promise.all([financeGlApi.listAccounts(scope), financeGlApi.listMappings(scope)]);
      // Group defaults, referenceable inside a company view, are only fetchable by a group-scoped caller.
      let groupDefs: GlAccount[] = [];
      if (inCompanyView && isGroupScope) {
        try {
          groupDefs = (await financeGlApi.listAccounts(null)).filter((a) => a.companyId === null);
        } catch {
          groupDefs = [];
        }
      }
      setOwnAccounts(inCompanyView ? own.filter((a) => a.companyId === scope) : own.filter((a) => a.companyId === null));
      setGroupDefaults(groupDefs);
      setRows(maps);
      setEdits(
        Object.fromEntries(
          maps.map((r) => [
            r.driverKey,
            // A company view starts inherited rows unset (no override); an existing company
            // override (or the group mapping in group view) pre-fills the select.
            {
              accountId: r.inherited ? '' : r.mappedAccountId ?? '',
              segmentCostCenterId: r.inherited ? '' : r.segmentCostCenterId ?? '',
            },
          ]),
        ),
      );
    } catch (e) {
      notifyApiError(e);
    } finally {
      setLoading(false);
    }
  }, [scope, inCompanyView, isGroupScope]);

  useEffect(() => {
    load();
  }, [load]);

  // Accounts a mapping in this scope may point at: own + inherited group defaults (active only).
  const selectableAccounts = useMemo(() => {
    const own = ownAccounts.filter((a) => a.isActive);
    const inherited = inCompanyView ? groupDefaults.filter((a) => a.isActive) : [];
    return [...own, ...inherited];
  }, [ownAccounts, groupDefaults, inCompanyView]);

  const accountLabel = useCallback(
    (id: string | null) => {
      if (!id) return null;
      const a = selectableAccounts.find((x) => x.id === id) ?? groupDefaults.find((x) => x.id === id);
      return a ? `${a.code} — ${a.name}` : null;
    },
    [selectableAccounts, groupDefaults],
  );

  const dirty = useMemo(() => {
    return rows.some((r) => {
      const e = edits[r.driverKey];
      if (!e) return false;
      const base = r.inherited ? '' : r.mappedAccountId ?? '';
      const baseSeg = r.inherited ? '' : r.segmentCostCenterId ?? '';
      return e.accountId !== base || e.segmentCostCenterId !== baseSeg;
    });
  }, [rows, edits]);

  const setEdit = (driverKey: string, patch: Partial<MapEdit>) =>
    setEdits((prev) => ({ ...prev, [driverKey]: { ...prev[driverKey], ...patch } }));

  const saveMappings = async () => {
    setSaving(true);
    try {
      const payload = Object.entries(edits)
        .filter(([, e]) => e.accountId)
        .map(([driverKey, e]) => ({
          driverKey,
          accountId: e.accountId,
          segmentCostCenterId: e.segmentCostCenterId || null,
        }));
      await financeGlApi.setMappings(payload, scope);
      await load();
    } catch (e) {
      notifyApiError(e);
    } finally {
      setSaving(false);
    }
  };

  const seed = async () => {
    setSeeding(true);
    try {
      await financeGlApi.seedDefaults();
      await load();
    } catch (e) {
      notifyApiError(e);
    } finally {
      setSeeding(false);
    }
  };

  // ── Account create / edit / delete ─────────────────────────────────────────
  const [newAcct, setNewAcct] = useState({ code: '', name: '', accountType: 'Expense' });
  const [editing, setEditing] = useState<GlAccount | null>(null);
  const [editForm, setEditForm] = useState({ name: '', accountType: 'Expense', isActive: true });
  const [editError, setEditError] = useState('');
  const [savingEdit, setSavingEdit] = useState(false);
  const [deletingId, setDeletingId] = useState<string | null>(null);

  const addAccount = async () => {
    if (!newAcct.code.trim() || !newAcct.name.trim()) return;
    try {
      await financeGlApi.createAccount({ ...newAcct, isActive: true }, scope);
      setNewAcct({ code: '', name: '', accountType: 'Expense' });
      await load();
    } catch (e) {
      notifyApiError(e);
    }
  };

  const openEdit = (a: GlAccount) => {
    setEditing(a);
    setEditForm({ name: a.name, accountType: a.accountType, isActive: a.isActive });
    setEditError('');
  };

  const saveEdit = async (force = false) => {
    if (!editing) return;
    if (!editForm.name.trim()) {
      setEditError('Name is required.');
      return;
    }
    setSavingEdit(true);
    setEditError('');
    try {
      await financeGlApi.updateAccount(
        editing.id,
        { code: editing.code, name: editForm.name.trim(), accountType: editForm.accountType, isActive: editForm.isActive },
        { force },
      );
      setEditing(null);
      await load();
    } catch (err) {
      if (isForce(err)) {
        if (window.confirm('This account is referenced by an active mapping in this scope. Deactivate anyway? The driver will fall back to its default in the GL journal.')) {
          await saveEdit(true);
          return;
        }
        setEditError('Deactivation cancelled — the account is still mapped.');
      } else {
        setEditError((err as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Failed to save.');
      }
    } finally {
      setSavingEdit(false);
    }
  };

  const deleteAccount = async (a: GlAccount) => {
    if (!window.confirm(`Delete account ${a.code} — ${a.name}?`)) return;
    setDeletingId(a.id);
    try {
      await financeGlApi.deleteAccount(a.id);
      await load();
    } catch (e) {
      notifyApiError(e); // surfaces "Account is in use by a payroll mapping; remap it first."
    } finally {
      setDeletingId(null);
    }
  };

  const overrideCount = rows.filter((r) => !r.inherited && r.mappedAccountId).length;

  return (
    <div className="space-y-6">
      {/* ── Chart of accounts ── */}
      <div className="surface p-4">
        <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
          <div>
            <h3 className="text-sm font-semibold text-slate-900 dark:text-white">Chart of Accounts</h3>
            <p className="text-xs text-slate-500 dark:text-slate-400">
              GL accounts payroll can post to for <span className="font-medium">{scopeLabel}</span>. Unmapped drivers use their built-in default.
            </p>
          </div>
          {!inCompanyView && canManage && (
            <button type="button" onClick={seed} disabled={seeding} className="btn-secondary text-xs disabled:opacity-60">
              {seeding ? 'Seeding…' : 'Seed defaults'}
            </button>
          )}
        </div>

        <div className="overflow-hidden rounded-lg border border-slate-200 dark:border-white/10">
          <table className="w-full text-sm">
            <thead className="bg-slate-50 text-left text-xs text-slate-500 dark:bg-white/[0.03] dark:text-slate-400">
              <tr>
                <th className="px-3 py-2">Code</th>
                <th className="px-3 py-2">Name</th>
                <th className="px-3 py-2">Type</th>
                <th className="px-3 py-2 text-right">Scope</th>
              </tr>
            </thead>
            <tbody>
              {ownAccounts.map((a) => (
                <tr key={a.id} className="border-t border-slate-100 dark:border-white/5">
                  <td className="px-3 py-2 font-mono text-xs">{a.code}</td>
                  <td className="px-3 py-2">{a.name}</td>
                  <td className="px-3 py-2 text-slate-500">{a.accountType}</td>
                  <td className="px-3 py-2">
                    <div className="flex items-center justify-end gap-1.5">
                      {inCompanyView ? <Badge tone="violet">Company</Badge> : <Badge tone="sapphire">Group</Badge>}
                      <span className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11px] font-semibold ${a.isActive ? 'bg-emeraldZ/10 text-emeraldZ dark:bg-emeraldZ/20' : 'bg-slate-100 text-slate-400 dark:bg-white/10 dark:text-slate-500'}`}>
                        <span className={`h-1.5 w-1.5 rounded-full ${a.isActive ? 'bg-emeraldZ' : 'bg-slate-400'}`} />
                        {a.isActive ? 'Active' : 'Inactive'}
                      </span>
                      {canManage && (
                        <>
                          <button type="button" onClick={() => openEdit(a)} className="btn-secondary h-7 px-2 text-xs">
                            <Pencil className="h-3 w-3" /> Edit
                          </button>
                          <button
                            type="button"
                            onClick={() => deleteAccount(a)}
                            disabled={deletingId === a.id}
                            aria-label="Delete account"
                            className="grid h-7 w-7 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-rose-300 hover:bg-rose-50 hover:text-rose-500 disabled:opacity-40 dark:border-white/10 dark:hover:border-rose-500/30 dark:hover:bg-rose-500/10 dark:hover:text-rose-400"
                          >
                            <Trash2 className="h-3.5 w-3.5" />
                          </button>
                        </>
                      )}
                    </div>
                  </td>
                </tr>
              ))}

              {/* Inherited group accounts (company view): referenceable, read-only here. */}
              {inCompanyView &&
                groupDefaults.map((a) => (
                  <tr key={a.id} className="border-t border-slate-100 opacity-60 dark:border-white/5">
                    <td className="px-3 py-2 font-mono text-xs">{a.code}</td>
                    <td className="px-3 py-2">{a.name}</td>
                    <td className="px-3 py-2 text-slate-500">{a.accountType}</td>
                    <td className="px-3 py-2">
                      <div className="flex items-center justify-end">
                        <InheritedBadge />
                      </div>
                    </td>
                  </tr>
                ))}

              {canManage && (
                <tr className="border-t border-slate-100 dark:border-white/5">
                  <td className="px-3 py-2">
                    <input
                      aria-label="New account code"
                      value={newAcct.code}
                      onChange={(e) => setNewAcct((x) => ({ ...x, code: e.target.value }))}
                      className="input h-8 w-24 text-xs"
                      placeholder="5006"
                    />
                  </td>
                  <td className="px-3 py-2">
                    <input
                      aria-label="New account name"
                      value={newAcct.name}
                      onChange={(e) => setNewAcct((x) => ({ ...x, name: e.target.value }))}
                      className="input h-8 w-full text-xs"
                      placeholder="Account name"
                    />
                  </td>
                  <td className="px-3 py-2">
                    <select
                      aria-label="New account type"
                      value={newAcct.accountType}
                      onChange={(e) => setNewAcct((x) => ({ ...x, accountType: e.target.value }))}
                      className="select h-8 text-xs"
                    >
                      {GL_ACCOUNT_TYPES.map((t) => (
                        <option key={t}>{t}</option>
                      ))}
                    </select>
                  </td>
                  <td className="px-3 py-2 text-right">
                    <button type="button" onClick={addAccount} className="btn-secondary h-8 px-2 text-xs">
                      <Plus className="h-3 w-3" /> Add {inCompanyView ? 'company' : 'group'} account
                    </button>
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        {loading && ownAccounts.length === 0 && <p className="mt-2 text-xs text-slate-400">Loading accounts…</p>}
      </div>

      {/* ── Payroll GL mapping ── */}
      <div className="surface p-4">
        <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
          <div>
            <h3 className="text-sm font-semibold text-slate-900 dark:text-white">Payroll GL Mapping</h3>
            <p className="text-xs text-slate-500 dark:text-slate-400">
              {inCompanyView
                ? 'Point each posting line at a company account to override the group default. Leave blank to inherit.'
                : 'Point each posting line at a group account. Blank = use the built-in default.'}
              {inCompanyView && overrideCount > 0 && (
                <span className="ml-1 font-medium text-violet-600 dark:text-violet-300">{overrideCount} company override{overrideCount === 1 ? '' : 's'}.</span>
              )}
            </p>
          </div>
          {canManage && (
            <button type="button" onClick={saveMappings} disabled={saving || !dirty} className="btn-primary text-xs disabled:opacity-60">
              {saving ? 'Saving…' : 'Save mappings'}
            </button>
          )}
        </div>

        <PanelState loading={loading} empty={rows.length === 0} emptyLabel="No posting drivers found. Seed defaults to get started.">
          <div className="space-y-1.5">
            {rows.map((r) => {
              const e = edits[r.driverKey] ?? { accountId: '', segmentCostCenterId: '' };
              const isOverridden = inCompanyView && e.accountId !== '';
              const inheritedAccount = inCompanyView && r.inherited ? accountLabel(r.mappedAccountId) : null;
              return (
                <div
                  key={r.driverKey}
                  className="grid grid-cols-12 items-center gap-2 rounded-md px-2 py-1.5 hover:bg-slate-50 dark:hover:bg-white/[0.03]"
                >
                  <div className="col-span-12 sm:col-span-4">
                    <div className="flex items-center gap-1.5">
                      <p className="text-sm text-slate-800 dark:text-slate-100">{r.label}</p>
                      {isOverridden && <Badge tone="violet">Override</Badge>}
                      {inCompanyView && !isOverridden && r.mappedAccountId && <InheritedBadge />}
                    </div>
                    <p className="text-[11px] text-slate-400">
                      default: {r.defaultAccount}
                      {inheritedAccount && !isOverridden && <span className="ml-1 text-slate-400">· inherits {inheritedAccount}</span>}
                    </p>
                  </div>
                  <div className="col-span-4 hidden text-xs text-slate-500 sm:col-span-2 sm:block">{r.category}</div>
                  <div className="col-span-8 sm:col-span-4">
                    <select
                      aria-label={`Account for ${r.label}`}
                      value={e.accountId}
                      disabled={!canManage}
                      onChange={(ev) => setEdit(r.driverKey, { accountId: ev.target.value, ...(ev.target.value ? {} : { segmentCostCenterId: '' }) })}
                      className="select w-full text-xs disabled:opacity-60"
                    >
                      <option value="">{inCompanyView ? '— inherit group default —' : '— use default —'}</option>
                      {selectableAccounts.map((a) => (
                        <option key={a.id} value={a.id}>
                          {a.code} — {a.name}
                          {inCompanyView && a.companyId === null ? ' (group)' : ''}
                        </option>
                      ))}
                    </select>
                  </div>
                  <div className="col-span-4 flex items-center gap-1 sm:col-span-2">
                    <select
                      aria-label={`Cost-center tag for ${r.label}`}
                      title="Optional cost-center tag stamped on this posting line"
                      value={e.segmentCostCenterId}
                      disabled={!canManage || !e.accountId}
                      onChange={(ev) => setEdit(r.driverKey, { segmentCostCenterId: ev.target.value })}
                      className="select w-full text-xs disabled:opacity-50"
                    >
                      <option value="">— no CC tag —</option>
                      {visibleCostCenters.map((c) => (
                        <option key={c.id} value={c.id}>
                          {c.code}
                        </option>
                      ))}
                    </select>
                    {canManage && isOverridden && (
                      <button
                        type="button"
                        onClick={() => setEdit(r.driverKey, { accountId: '', segmentCostCenterId: '' })}
                        aria-label={`Revert ${r.label} to group default`}
                        title="Revert to group default"
                        className="grid h-7 w-7 shrink-0 place-items-center rounded-md border border-slate-200 text-slate-400 hover:border-sapphire/40 hover:text-sapphire dark:border-white/10"
                      >
                        <RotateCcw className="h-3.5 w-3.5" />
                      </button>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        </PanelState>
      </div>

      {/* Edit account modal */}
      <Modal
        isOpen={!!editing}
        title={`Edit Account ${editing?.code ?? ''}`}
        onClose={() => setEditing(null)}
        footer={
          <>
            <button type="button" onClick={() => setEditing(null)} className="btn-secondary">
              Cancel
            </button>
            <button type="button" onClick={() => saveEdit(false)} disabled={savingEdit} className="btn-primary disabled:opacity-60">
              {savingEdit ? 'Saving…' : 'Save'}
            </button>
          </>
        }
      >
        <FieldError error={editError} />
        <div className="grid grid-cols-2 gap-3">
          <Field label="Code">
            <input value={editing?.code ?? ''} disabled className="input w-full opacity-60" title="Account code is immutable" />
          </Field>
          <Field label="Type">
            <select
              value={editForm.accountType}
              onChange={(e) => setEditForm((x) => ({ ...x, accountType: e.target.value }))}
              className="select w-full"
            >
              {GL_ACCOUNT_TYPES.map((t) => (
                <option key={t}>{t}</option>
              ))}
            </select>
          </Field>
          <Field label="Name" required>
            <input value={editForm.name} onChange={(e) => setEditForm((x) => ({ ...x, name: e.target.value }))} className="input w-full" />
          </Field>
          <Field label="Status">
            <select
              value={editForm.isActive ? 'true' : 'false'}
              onChange={(e) => setEditForm((x) => ({ ...x, isActive: e.target.value === 'true' }))}
              className="select w-full"
            >
              <option value="true">Active</option>
              <option value="false">Inactive</option>
            </select>
          </Field>
        </div>
      </Modal>
    </div>
  );
}
