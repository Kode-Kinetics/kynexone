'use client';

import { useId, useState } from 'react';
import { employeeDraftsApi } from '../api/employeeDrafts';
import { useLocale } from '../contexts/LocaleContext';
import {
  draftIdentityError,
  draftIdentityPayload,
  draftIdentityServerError,
  draftRequestFailureReason,
  type DraftIdentityKind,
} from '../lib/newHireReview';

interface DraftIdentityFixProps {
  draftId: string;
  /** Saudi hire: the National ID. Anyone else: the Iqama number. */
  kind: DraftIdentityKind;
  /** The value already on the draft (empty when the caller may not see sensitive values, or none was entered). */
  current: string;
  /** The caller made or already edited this draft. */
  isMaker: boolean;
  /** The caller holds employees.write, the permission the draft update endpoint requires. */
  canEdit: boolean;
  onSaved: () => void;
}

/**
 * The fix for "Government ID number is missing": a Saudi hire needs the National ID, a non-Saudi hire the Iqama
 * number, and the review had no field for either. The number is checked with the same rules the server applies, saved
 * to the draft, and the activation check runs again. Editing a draft makes the editor one of its makers, and a maker
 * never activates the hire, so the dialog says a different approver will.
 */
export function DraftIdentityFix({ draftId, kind, current, isMaker, canEdit, onSaved }: DraftIdentityFixProps) {
  const { t } = useLocale();
  const inputId = useId();
  const [value, setValue] = useState(current);
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);

  const label = t(kind === 'nationalId' ? 'National ID' : 'Iqama');

  const save = async () => {
    const invalid = draftIdentityError(kind, value);
    if (invalid) { setError(t(invalid)); return; }
    setSaving(true);
    setError('');
    try {
      await employeeDraftsApi.updateIdentity(draftId, draftIdentityPayload(kind, value));
      onSaved();
    } catch (err) {
      const verdict = draftIdentityServerError(kind, err);
      setError(verdict ? t(verdict) : draftRequestFailureReason(err));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div data-testid="draft-identity-fix" className="rounded-lg border border-slate-200 bg-white p-3 dark:border-white/10 dark:bg-white/[0.03]">
      <p className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Government ID needed to activate')}</p>
      <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
        {t('Activation needs this person\'s {document}. Enter it here and the check runs again.', { document: label })}
      </p>
      {!canEdit ? (
        <p className="mt-2 text-sm text-slate-600 dark:text-slate-300">{t('Only someone who can edit new hires can add this number.')}</p>
      ) : (
        <>
          <div className="mt-3 flex flex-wrap items-end gap-3">
            <div className="min-w-[14rem] flex-1">
              <label htmlFor={inputId} className="block text-xs font-medium text-slate-600 dark:text-slate-300">{label}</label>
              <input
                id={inputId}
                dir="ltr"
                inputMode="numeric"
                autoComplete="off"
                maxLength={20}
                className="input mt-1 w-full text-start tabular-nums"
                value={value}
                aria-invalid={error ? true : undefined}
                aria-describedby={error ? `${inputId}-error` : undefined}
                onChange={(e) => { setValue(e.target.value); if (error) setError(''); }}
                onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); void save(); } }}
              />
            </div>
            <button type="button" onClick={() => void save()} disabled={saving || value.trim() === ''} className="btn-secondary h-9 px-3 text-xs disabled:opacity-50">
              {saving ? t('Saving…') : t('Save and re-check')}
            </button>
          </div>
          {error && <p id={`${inputId}-error`} role="alert" className="mt-2 text-sm text-rose-700 dark:text-rose-300">{error}</p>}
          <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">
            {t(isMaker
              ? 'You already worked on this hire, so a different HR approver has to activate it.'
              : 'Saving this makes you an editor of this hire, so a different HR approver has to activate it.')}
          </p>
        </>
      )}
    </div>
  );
}
