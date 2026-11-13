'use client';

import { useEffect, useState } from 'react';
import { offersApi } from '../../api/recruitment';
import { useT } from '../../hooks/useT';
import type { CatalogueOptions } from '../../lib/recruitmentCatalogue';

export interface CatalogueChoice { departmentId: string; designationId: string; gradeId: string }

interface Props {
  value: CatalogueChoice;
  onChange: (value: CatalogueChoice, options: CatalogueOptions) => void;
  /** Called once the lists are loaded, so a form can derive from them (a grade's band, a name for a title). */
  onLoaded?: (options: CatalogueOptions) => void;
  idPrefix: string;
  /** Show the grade picker (requisition and opening: yes; the offer takes the grade through the opening). */
  withGrade?: boolean;
}

/**
 * Department, designation and grade for a requisition or an opening, chosen from the organisation's own records.
 * Free text let a name no record matched through, and it surfaced only when HR tried to activate the hire.
 */
export function CatalogueFields({ value, onChange, onLoaded, idPrefix, withGrade = true }: Props) {
  const t = useT();
  const [options, setOptions] = useState<CatalogueOptions | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    offersApi.placementOptions()
      .then((o) => {
        if (cancelled) return;
        const next: CatalogueOptions = { departments: o.departments, designations: o.designations, grades: o.grades ?? [] };
        setOptions(next);
        onLoaded?.(next);
      })
      .catch(() => { if (!cancelled) setFailed(true); });
    return () => { cancelled = true; };
    // The lists are fetched once per form.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (failed) {
    return <p role="alert" className="text-xs text-rose-600 dark:text-rose-300">{t('The department, designation and grade lists could not be loaded.')}</p>;
  }

  const change = (next: CatalogueChoice) => options && onChange(next, options);
  const empty = options && (options.designations.length === 0 || options.departments.length === 0);
  const labelClass = 'mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400';

  return (
    <div className="space-y-3">
      <div className={`grid gap-3 ${withGrade ? 'grid-cols-3' : 'grid-cols-2'}`}>
        <div>
          <label htmlFor={`${idPrefix}-department`} className={labelClass}>{t('Department *')}</label>
          <select id={`${idPrefix}-department`} className="select w-full" disabled={!options} value={value.departmentId}
            onChange={(e) => change({ ...value, departmentId: e.target.value })}>
            <option value="">{options ? t('Choose a department') : t('Loading…')}</option>
            {options?.departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </select>
        </div>
        <div>
          <label htmlFor={`${idPrefix}-designation`} className={labelClass}>{t('Designation *')}</label>
          <select id={`${idPrefix}-designation`} className="select w-full" disabled={!options} value={value.designationId}
            onChange={(e) => change({ ...value, designationId: e.target.value })}>
            <option value="">{options ? t('Choose a designation') : t('Loading…')}</option>
            {options?.designations.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </select>
        </div>
        {withGrade && (
          <div>
            <label htmlFor={`${idPrefix}-grade`} className={labelClass}>{t('Grade')}</label>
            <select id={`${idPrefix}-grade`} className="select w-full" disabled={!options} value={value.gradeId}
              onChange={(e) => change({ ...value, gradeId: e.target.value })}>
              <option value="">{options ? t('No grade') : t('Loading…')}</option>
              {options?.grades.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
            </select>
          </div>
        )}
      </div>
      {empty && (
        <p className="text-xs text-amber-700 dark:text-amber-300">
          {t("Add departments, designations and grades under Setup first. They are picked from your organisation's own records.")}
        </p>
      )}
    </div>
  );
}
