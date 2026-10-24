'use client';

import { useCallback, useMemo } from 'react';
import { useCompany } from '../../contexts/CompanyContext';
import { useT } from '../../hooks/useT';

/**
 * Addendum B (2026-10-23): departments, designations, grades and cost centres belong to ONE company.
 * These controls are shared by the four Setup organisation tabs: the company list, the default for a new
 * row (the company switcher's selection, else the only company), the Company filter with its
 * "Unassigned — choose the company" option, and the notice that legacy rows still need a company.
 */

/** Filter value that shows only rows without a company. */
export const UNASSIGNED_FILTER = '__unassigned__';

export interface OrgCompanyOption { id: string; name: string }

export function useOrgCompanies(extra?: { id: string; legalNameEn: string }[]) {
  const { companies: access, selectedCompanyId } = useCompany();
  const options = useMemo<OrgCompanyOption[]>(() => {
    const byId = new Map<string, OrgCompanyOption>();
    for (const c of access) byId.set(c.id, { id: c.id, name: c.name });
    for (const c of extra ?? []) if (!byId.has(c.id)) byId.set(c.id, { id: c.id, name: c.legalNameEn });
    return [...byId.values()];
  }, [access, extra]);
  const defaultCompanyId = selectedCompanyId ?? (options.length === 1 ? options[0].id : '');
  const nameOf = useCallback((id?: string | null) => (id ? options.find((c) => c.id === id)?.name ?? null : null), [options]);
  return { options, defaultCompanyId, nameOf, selectedCompanyId };
}

/** Rows matching the Company filter: '' = all, UNASSIGNED_FILTER = no company, else that company. */
export function filterByCompany<T extends { companyId?: string | null }>(rows: T[], filter: string): T[] {
  if (!filter) return rows;
  if (filter === UNASSIGNED_FILTER) return rows.filter((r) => !r.companyId);
  return rows.filter((r) => r.companyId === filter);
}

export function CompanyFilterSelect({ value, onChange, options, hasUnassigned }: {
  value: string; onChange: (v: string) => void; options: OrgCompanyOption[]; hasUnassigned: boolean;
}) {
  const t = useT();
  if (options.length <= 1 && !hasUnassigned) return null;
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} className="select" title={t('Filter by company')} aria-label={t('Filter by company')}>
      <option value="">{t('All companies')}</option>
      {options.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
      {hasUnassigned && <option value={UNASSIGNED_FILTER}>{t('Unassigned — choose the company')}</option>}
    </select>
  );
}

/** Shown above a tab's table while legacy rows have no company. */
export function UnassignedCompanyNotice({ count, onShow }: { count: number; onShow: () => void }) {
  const t = useT();
  if (count === 0) return null;
  return (
    <div role="status" className="mb-3 flex flex-wrap items-center justify-between gap-2 rounded-lg border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-900 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">
      <span>{t('{count} rows have no company yet. Open each one and choose the company it belongs to.', { count })}</span>
      <button type="button" onClick={onShow} className="btn-secondary h-7 px-2 text-xs">{t('Show unassigned rows')}</button>
    </div>
  );
}

/** The table cell: the company's name, or a clear "Unassigned" marker. */
export function CompanyCell({ name }: { name: string | null }) {
  const t = useT();
  return name
    ? <span className="text-slate-600 dark:text-slate-300">{name}</span>
    : <span className="rounded-full bg-amber-500/10 px-2 py-0.5 text-xs font-semibold text-amber-700 dark:text-amber-300">{t('Unassigned')}</span>;
}

/**
 * The form's Company field. A new row (or an unassigned legacy row) picks a company; a row that already has one
 * shows it locked, because a company's departments, designations and grades are its own.
 */
export function CompanySelectField({ value, onChange, options, locked, lockedName }: {
  value: string; onChange: (v: string) => void; options: OrgCompanyOption[]; locked: boolean; lockedName?: string | null;
}) {
  const t = useT();
  if (locked) {
    return (
      <div>
        <select value={value} disabled className="select w-full" aria-label={t('Company')}>
          <option value={value}>{lockedName ?? value}</option>
        </select>
        <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{t("The company can't be changed after it is set.")}</p>
      </div>
    );
  }
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} className="select w-full" aria-label={t('Company')} required>
      <option value="">{t('Choose the company')}</option>
      {options.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
    </select>
  );
}
