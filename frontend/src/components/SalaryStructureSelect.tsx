'use client';

import { useEffect, useId, useMemo, useState, type ReactNode } from 'react';
import { payrollApi, type SalaryStructure } from '../api/payroll';
import { useLocale } from '../contexts/LocaleContext';
import { requireList } from '../lib/listResponse';
import { salaryStructureChoices } from '../lib/salaryStructureChoices';

/**
 * The salary structure an employee's salary is put on: a choice of the active structures for the employee's
 * company (or group-wide), never free text. Blank means "use the default" — the grade's structure, or a direct
 * salary for an employee without a grade. Used by both Add Employee and Edit Employee.
 */
export function SalaryStructureSelect({
  companyId,
  value,
  onChange,
  currentCode,
  retainCurrent = true,
  id,
  disabled,
  labelSuffix,
  onInvalidSelectionChange,
}: {
  companyId?: string | null;
  value: string;
  onChange(code: string): void;
  /** The code the employee is on today (edit only): kept as a choice even if it is no longer active. */
  currentCode?: string | null;
  /** Create passes false: a choice that the (newly selected) company does not offer is cleared, not kept. */
  retainCurrent?: boolean;
  id?: string;
  disabled?: boolean;
  labelSuffix?: ReactNode;
  /**
   * Edit only: true while the selected code is the employee's current one AND the company's structures loaded
   * without it, i.e. it is retired, superseded or another company's. The server refuses a salary change on it
   * (422 inactive/unknown_salary_structure), so the caller blocks saving a salary change until another is chosen.
   * When the list could not be loaded nothing is known, so nothing is flagged and the server decides.
   */
  onInvalidSelectionChange?(invalid: boolean): void;
}) {
  const { t } = useLocale();
  const generatedId = useId();
  const controlId = id ?? generatedId;
  const [structures, setStructures] = useState<SalaryStructure[]>([]);
  const [failed, setFailed] = useState(false);
  const [loadedFor, setLoadedFor] = useState<string | null | undefined>(undefined);

  useEffect(() => {
    let cancelled = false;
    setFailed(false);
    setLoadedFor(undefined);
    payrollApi.listSalaryStructures(companyId || undefined)
      .then((rows) => { if (!cancelled) { setStructures(requireList<SalaryStructure>(rows, 'salary structures')); setLoadedFor(companyId ?? null); } })
      .catch(() => { if (!cancelled) { setStructures([]); setFailed(true); } });
    return () => { cancelled = true; };
  }, [companyId]);

  const choices = useMemo(
    () => salaryStructureChoices(structures, companyId, retainCurrent ? currentCode ?? (value || null) : null),
    [structures, companyId, currentCode, value, retainCurrent],
  );

  useEffect(() => {
    if (retainCurrent || loadedFor === undefined || !value) return;
    if (!choices.some((choice) => choice.value === value)) onChange('');
  }, [retainCurrent, loadedFor, value, choices, onChange]);

  const loaded = loadedFor !== undefined;
  const selectionInvalid = loaded && !!value && !!choices.find((choice) => choice.value === value)?.retainedCurrent;
  useEffect(() => { onInvalidSelectionChange?.(selectionInvalid); }, [selectionInvalid, onInvalidSelectionChange]);

  return (
    <label htmlFor={controlId} className="block min-w-0 text-sm font-medium text-slate-700 dark:text-slate-300">
      <span className="flex min-w-0 items-center gap-1.5 leading-snug">{t('Salary structure')}{labelSuffix}</span>
      <select id={controlId} value={value} disabled={disabled} onChange={(event) => onChange(event.target.value)} className="select mt-1.5 w-full">
        <option value="">{t('Default for the grade')}</option>
        {choices.map((choice) => (
          <option key={choice.value} value={choice.value}>
            {choice.retainedCurrent && loaded
              ? t('{name} ({code}) — not valid any more, choose another', { name: choice.name, code: choice.value })
              : choice.groupWide
                ? t('{name} ({code}) — all companies', { name: choice.name, code: choice.value })
                : `${choice.name} (${choice.value})`}
          </option>
        ))}
      </select>
      {selectionInvalid && <span role="alert" className="mt-1 block text-xs font-normal text-rose-700 dark:text-rose-300">{t('This salary structure is no longer valid for this employee. Choose another structure, or the default, before saving salary changes.')}</span>}
      {failed && <span role="status" className="mt-1 block text-xs font-normal text-amber-700 dark:text-amber-300">{t('Salary structures could not be loaded. Leave this on the default, or try again later.')}</span>}
    </label>
  );
}
