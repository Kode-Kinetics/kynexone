'use client';

import { useCallback, useEffect, useState } from 'react';
import { Pencil, Plus, Trash2 } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { useAuth } from '../../contexts/AuthContext';
import { packageApi, type Dependant, type DependantInput, type DependantRelationship } from '../../api/package';
import { date } from './packageFormat';

const RELATIONSHIPS: DependantRelationship[] = ['Spouse', 'Child', 'Parent', 'Other'];
const EMPTY: DependantInput = { fullName: '', relationship: 'Child', dateOfBirth: '', nationalId: '' };

/**
 * The dependants a medical, ticket or education entitlement covers (Release A R2). HR adds, edits and removes them; every
 * change is audited on the server. Read-only without employees.write. "No dependants on file" when there are none.
 */
export function DependantsPanel({ employeeId, onChanged }: { employeeId: number; onChanged?: () => void }) {
  const { t, locale } = useLocale();
  const { hasPermission } = useAuth();
  const canEdit = hasPermission('employees.write');
  const [rows, setRows] = useState<Dependant[] | null>(null);
  const [editing, setEditing] = useState<{ id: string | null; input: DependantInput } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try { setRows(await packageApi.dependants(employeeId)); setError(null); }
    catch { setRows([]); setError(t('The dependants could not be loaded.')); }
  }, [employeeId, t]);
  useEffect(() => { void load(); }, [load]);

  const save = async () => {
    if (!editing) return;
    setBusy(true);
    setError(null);
    const input = { ...editing.input, dateOfBirth: editing.input.dateOfBirth || null, nationalId: editing.input.nationalId?.trim() || null };
    try {
      if (editing.id) await packageApi.updateDependant(employeeId, editing.id, input);
      else await packageApi.addDependant(employeeId, input);
      setEditing(null);
      await load();
      onChanged?.();
    } catch (e) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? t('The dependant could not be saved.'));
    } finally { setBusy(false); }
  };

  const remove = async (row: Dependant) => {
    if (!window.confirm(t('Remove this dependant from the employee’s file?'))) return;
    setBusy(true);
    try { await packageApi.removeDependant(employeeId, row.id); await load(); onChanged?.(); }
    catch { setError(t('The dependant could not be removed.')); }
    finally { setBusy(false); }
  };

  const field = 'w-full rounded-lg border border-slate-300 bg-white px-2 py-1.5 text-sm dark:border-white/15 dark:bg-white/[0.04]';
  return (
    <section aria-label={t('Dependants')} className="rounded-2xl border border-slate-200 bg-white dark:border-white/10 dark:bg-white/[0.03]">
      <header className="flex items-center justify-between gap-2 border-b border-slate-100 px-4 py-2 dark:border-white/5">
        <h4 className="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{t('Dependants')}</h4>
        {canEdit && !editing && (
          <button type="button" onClick={() => setEditing({ id: null, input: { ...EMPTY } })}
            className="inline-flex items-center gap-1 rounded-lg px-2 py-1 text-xs font-semibold text-sapphire hover:bg-sapphire/10 dark:text-cyanAccent">
            <Plus className="h-3.5 w-3.5" aria-hidden="true" />{t('Add a dependant')}
          </button>
        )}
      </header>
      {error && <p role="alert" className="px-4 pt-2 text-xs text-amber-700 dark:text-amber-300">{error}</p>}
      {rows === null ? <div className="m-4 h-10 animate-pulse rounded bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
        : rows.length === 0 && !editing ? <p className="px-4 py-3 text-sm text-slate-500 dark:text-slate-400">{t('No dependants on file.')}</p>
        : (
          <ul className="divide-y divide-slate-100 dark:divide-white/5">
            {rows.map((row) => (
              <li key={row.id} className="flex flex-wrap items-center justify-between gap-2 px-4 py-2 text-sm">
                <span className="min-w-0">
                  <span className="font-medium text-slate-800 dark:text-slate-100">{row.fullName}</span>
                  <span className="text-slate-500 dark:text-slate-400"> · {t(row.relationship)}{row.dateOfBirth ? ` · ${date(row.dateOfBirth, locale)}` : ''}</span>
                </span>
                {canEdit && (
                  <span className="flex gap-1">
                    <button type="button" disabled={busy} onClick={() => setEditing({ id: row.id, input: { fullName: row.fullName, relationship: row.relationship, dateOfBirth: row.dateOfBirth, nationalId: row.nationalId } })}
                      className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-xs text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/5">
                      <Pencil className="h-3 w-3" aria-hidden="true" />{t('Edit')}
                    </button>
                    <button type="button" disabled={busy} onClick={() => void remove(row)}
                      className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-xs text-rose-600 hover:bg-rose-50 dark:text-rose-300 dark:hover:bg-rose-500/10">
                      <Trash2 className="h-3 w-3" aria-hidden="true" />{t('Remove')}
                    </button>
                  </span>
                )}
              </li>
            ))}
          </ul>
        )}
      {editing && (
        <form className="grid gap-2 px-4 py-3 sm:grid-cols-2" onSubmit={(e) => { e.preventDefault(); void save(); }}>
          <label className="text-xs text-slate-600 dark:text-slate-300">{t('Full name')}
            <input required className={field} value={editing.input.fullName} onChange={(e) => setEditing({ ...editing, input: { ...editing.input, fullName: e.target.value } })} />
          </label>
          <label className="text-xs text-slate-600 dark:text-slate-300">{t('Relationship')}
            <select className={field} value={editing.input.relationship} onChange={(e) => setEditing({ ...editing, input: { ...editing.input, relationship: e.target.value as DependantRelationship } })}>
              {RELATIONSHIPS.map((r) => <option key={r} value={r}>{t(r)}</option>)}
            </select>
          </label>
          <label className="text-xs text-slate-600 dark:text-slate-300">{t('Date of birth')}
            <input required type="date" className={field} value={editing.input.dateOfBirth ?? ''} onChange={(e) => setEditing({ ...editing, input: { ...editing.input, dateOfBirth: e.target.value } })} />
          </label>
          <label className="text-xs text-slate-600 dark:text-slate-300">{t('Iqama or ID number (optional)')}
            <input className={field} inputMode="numeric" value={editing.input.nationalId ?? ''} onChange={(e) => setEditing({ ...editing, input: { ...editing.input, nationalId: e.target.value } })} />
          </label>
          <div className="flex gap-2 sm:col-span-2">
            <button type="submit" disabled={busy} className="rounded-lg bg-sapphire px-3 py-1.5 text-xs font-semibold text-white disabled:opacity-60">{t('Save dependant')}</button>
            <button type="button" onClick={() => setEditing(null)} className="rounded-lg px-3 py-1.5 text-xs text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-white/5">{t('Cancel')}</button>
          </div>
        </form>
      )}
    </section>
  );
}
