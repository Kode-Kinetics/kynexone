'use client';

import { useEffect, useState } from 'react';
import { Loader2 } from 'lucide-react';
import { employeeDraftsApi } from '../api/employeeDrafts';
import { branchesApi, departmentsApi, designationsApi } from '../api/organization';
import type { PagedResult } from '../api/organization';
import { draftRequestFailureReason } from '../lib/newHireReview';

type Option = { value: string; label: string };
type Field = 'department' | 'designation' | 'branch';

const LABELS: Record<Field, string> = { department: 'Department', designation: 'Designation', branch: 'Branch' };

/** Reads every page of an organisation list (bounded), keeping only active records. */
async function loadAll<T extends { isActive?: boolean }>(
  fetchPage: (page: number) => Promise<PagedResult<T>>,
  toOption: (item: T) => Option,
): Promise<Option[]> {
  const options: Option[] = [];
  for (let page = 1; page <= 20; page++) {
    const res = await fetchPage(page);
    for (const item of res.items) if (item.isActive !== false) options.push(toOption(item));
    if (page * res.pageSize >= res.total || res.items.length === 0) break;
  }
  return options.sort((a, b) => a.label.localeCompare(b.label));
}

interface DraftPlacementFixProps {
  draftId: string;
  current: Record<Field, string>;
  /** The placement fields the activation check could not match. */
  problemFields: Field[];
  /** The caller made this draft. A checker who edits it becomes one of its makers. */
  isMaker: boolean;
  onSaved: () => void;
}

/**
 * The fix for "does not match any active department/designation/branch": pick the value from the
 * organisation records approval resolves against. Names are saved exactly as the records spell them,
 * so the activation check and approval match them.
 */
export function DraftPlacementFix({ draftId, current, problemFields, isMaker, onSaved }: DraftPlacementFixProps) {
  const [options, setOptions] = useState<Record<Field, Option[]> | null>(null);
  const [loadError, setLoadError] = useState('');
  const [values, setValues] = useState<Record<Field, string>>(current);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState('');

  useEffect(() => {
    let cancelled = false;
    setLoadError('');
    Promise.all([
      loadAll((p) => departmentsApi.list(undefined, p, 100), (d) => ({ value: d.nameEn, label: d.nameEn })),
      loadAll((p) => designationsApi.list(undefined, p, 100), (d) => ({ value: d.titleEn, label: d.titleEn })),
      loadAll((p) => branchesApi.list(undefined, p, 100), (b) => ({ value: b.nameEn, label: b.nameEn })),
    ])
      .then(([department, designation, branch]) => { if (!cancelled) setOptions({ department, designation, branch }); })
      .catch((err) => { if (!cancelled) setLoadError(draftRequestFailureReason(err)); });
    return () => { cancelled = true; };
  }, []);

  const changed = (Object.keys(values) as Field[]).filter((f) => values[f] !== current[f]);

  const save = async () => {
    if (changed.length === 0) return;
    setSaving(true);
    setSaveError('');
    try {
      await employeeDraftsApi.updatePlacement(draftId, Object.fromEntries(changed.map((f) => [f, values[f]])));
      onSaved();
    } catch (err) {
      setSaveError(draftRequestFailureReason(err));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="rounded-lg border border-slate-200 bg-white p-3 dark:border-white/10 dark:bg-white/[0.03]">
      <p className="text-sm font-semibold text-slate-800 dark:text-slate-100">Fix the placement</p>
      <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
        Pick from your organisation records so activation can match them.
        {!isMaker && ' Saving a change makes you an editor of this hire, so a different HR approver will have to approve it.'}
      </p>
      {loadError && (
        <p role="alert" className="mt-2 text-sm text-rose-700 dark:text-rose-300">The organisation lists could not be loaded: {loadError}</p>
      )}
      {!options && !loadError && (
        <p className="mt-2 flex items-center gap-2 text-sm text-slate-500"><Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> Loading organisation records…</p>
      )}
      {options && (
        <div className="mt-3 grid gap-3 sm:grid-cols-3">
          {(['designation', 'department', 'branch'] as Field[]).map((field) => {
            const known = options[field].some((o) => o.value === values[field]);
            return (
              <label key={field} className="block text-xs font-medium text-slate-600 dark:text-slate-300">
                {LABELS[field]}{problemFields.includes(field) ? ' (does not match)' : ''}
                <select
                  className="input mt-1 w-full"
                  value={known ? values[field] : ''}
                  onChange={(e) => setValues((v) => ({ ...v, [field]: e.target.value }))}
                >
                  <option value="">{values[field] && !known ? `Not in records: ${values[field]}` : 'None'}</option>
                  {options[field].map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                </select>
              </label>
            );
          })}
        </div>
      )}
      {saveError && <p role="alert" className="mt-2 text-sm text-rose-700 dark:text-rose-300">{saveError}</p>}
      {options && (
        <button type="button" onClick={() => void save()} disabled={saving || changed.length === 0} className="btn-secondary mt-3 h-8 px-3 text-xs disabled:opacity-50">
          {saving ? 'Saving…' : 'Save placement'}
        </button>
      )}
    </div>
  );
}
