'use client';

import { useLocale } from '../../contexts/LocaleContext';
import { msg } from '../../i18n/translations';
import type {
  CodeName, CompanySetupCostCentre, CompanySetupDepartment, CompanySetupDesignation, CompanySetupDraftBody, CompanySetupGrade,
} from '../../api/companySetup';
import { MANAGE_TAB, inCatalog, stepWarnings } from '../../lib/companySetupWizard';
import { RowsEditor, type RowColumn } from './RowsEditor';
import { Notice, SectionHeader, useWizardForm } from './wizardForm';
import { UseDefaultsButton } from './UseDefaultsButton';
import { SectionSaveBar, SectionSavedStatus, sectionHeadingId } from './sectionSave';

type ListKey = 'costCentres' | 'departments' | 'designations' | 'grades';

/**
 * Step 4. Cost centres belong to this company. Departments, designations and grades are the group's
 * shared catalog: a code the group already has is reused as it is (never renamed or re-linked), a new
 * code is created for the whole group.
 */
export function OrganisationStep({ defaults }: { defaults: CompanySetupDraftBody | null }) {
  const { t, locale } = useLocale();
  const { body, update, context, readOnly, baseline } = useWizardForm();
  const catalog = context?.sharedCatalog;
  // New shared codes need group scope (A5): a company super admin picks from the catalog only.
  const canAddShared = !!context?.isGroupScope;
  const existing = !!body.existingCompanyId;
  // Existing mode: a shared row the company already uses always plans as reuse (the server only warns
  // shared_row_not_edited on a rename), so it is shown read-only even if the catalog list missed it.
  const sharedExisting = (row: { existingId?: string | null }, nameEn: string, nameAr?: string) =>
    existing && row.existingId ? { nameEn, nameAr: nameAr ?? null } : undefined;
  const manage = (key: keyof typeof MANAGE_TAB) => existing ? { href: `/setup?tab=${MANAGE_TAB[key].tab}&companyId=${encodeURIComponent(body.existingCompanyId!)}`, label: MANAGE_TAB[key].label } : undefined;
  const set = <K extends ListKey>(key: K, rows: CompanySetupDraftBody[K]) => update(b => ({ ...b, [key]: rows }));
  const name = (c: CodeName) => (locale === 'ar' ? c.nameAr || c.nameEn : c.nameEn);
  const SECTION: Record<ListKey, string> = { costCentres: t('Cost centres'), departments: t('Departments'), designations: t('Designations'), grades: t('Grades') };
  const defaultsButton = (key: ListKey) => defaults && defaults[key].length > 0 && !readOnly && !existing && (key === 'costCentres' || canAddShared)
    ? <UseDefaultsButton current={body[key] as unknown[]} defaults={defaults[key] as unknown[]} section={SECTION[key]} onApply={() => set(key, defaults[key] as never)} />
    : undefined;

  const codeOptions = (rows: { code: string; nameEn?: string; titleEn?: string }[], shared: readonly CodeName[] = []) => {
    const seen = new Set<string>();
    const out: { value: string; label: string }[] = [];
    for (const r of [...rows.map(r => ({ code: r.code, nameEn: r.nameEn ?? r.titleEn ?? '' })), ...shared]) {
      const code = r.code.trim();
      if (!code || seen.has(code.toUpperCase())) continue;
      seen.add(code.toUpperCase());
      out.push({ value: code, label: r.nameEn ? `${code} · ${r.nameEn}` : code });
    }
    return out;
  };

  const costCentreCols: RowColumn<CompanySetupCostCentre>[] = [
    { key: 'code', label: msg('Code'), required: true, width: 'w-full sm:w-28' },
    { key: 'nameEn', label: msg('Name (English)'), required: true, width: 'w-full sm:w-56' },
    { key: 'nameAr', label: msg('Name (Arabic)'), arabic: true, width: 'w-full sm:w-56' },
  ];
  const departmentCols: RowColumn<CompanySetupDepartment>[] = [
    { key: 'code', label: msg('Code'), required: true, width: 'w-full sm:w-28' },
    { key: 'nameEn', label: msg('Name (English)'), required: true, width: 'w-full sm:w-48' },
    { key: 'nameAr', label: msg('Name (Arabic)'), arabic: true, width: 'w-full sm:w-48' },
    { key: 'parentCode', label: msg('Parent department'), kind: 'select', options: codeOptions(body.departments, catalog?.departments), width: 'w-full sm:w-44' },
    { key: 'branchCode', label: msg('Branch'), kind: 'select', options: codeOptions(body.branches), width: 'w-full sm:w-40' },
    { key: 'costCentreCode', label: msg('Cost centre'), kind: 'select', options: codeOptions(body.costCentres), width: 'w-full sm:w-40' },
  ];
  const designationCols: RowColumn<CompanySetupDesignation>[] = [
    { key: 'code', label: msg('Code'), required: true, width: 'w-full sm:w-28' },
    { key: 'titleEn', label: msg('Title (English)'), required: true, width: 'w-full sm:w-48' },
    { key: 'titleAr', label: msg('Title (Arabic)'), arabic: true, width: 'w-full sm:w-48' },
    { key: 'departmentCode', label: msg('Department'), kind: 'select', options: codeOptions(body.departments, catalog?.departments), width: 'w-full sm:w-44' },
    { key: 'gradeCode', label: msg('Grade'), kind: 'select', options: codeOptions(body.grades, catalog?.grades), width: 'w-full sm:w-36' },
  ];
  const gradeCols: RowColumn<CompanySetupGrade>[] = [
    { key: 'code', label: msg('Code'), required: true, width: 'w-full sm:w-24' },
    { key: 'nameEn', label: msg('Name (English)'), required: true, width: 'w-full sm:w-44' },
    { key: 'nameAr', label: msg('Name (Arabic)'), arabic: true, width: 'w-full sm:w-44' },
    { key: 'level', label: msg('Level'), kind: 'number', inputMode: 'numeric', width: 'w-[calc(50%-0.375rem)] sm:w-20' },
    { key: 'minSalary', label: msg('Minimum salary'), kind: 'number', inputMode: 'decimal', width: 'w-[calc(50%-0.375rem)] sm:w-32' },
    { key: 'midSalary', label: msg('Midpoint salary'), kind: 'number', inputMode: 'decimal', width: 'w-[calc(50%-0.375rem)] sm:w-32' },
    { key: 'maxSalary', label: msg('Maximum salary'), kind: 'number', inputMode: 'decimal', width: 'w-[calc(50%-0.375rem)] sm:w-32' },
  ];

  /** Shared codes not yet in this draft, offered as one-click reuse. */
  const reuseChips = (key: 'departments' | 'designations' | 'grades', shared: readonly CodeName[] | undefined, make: (c: CodeName) => never) => {
    const rows = body[key] as { code: string }[];
    const available = (shared ?? []).filter(c => !rows.some(r => r.code.trim().toUpperCase() === c.code.toUpperCase()));
    if (available.length === 0) return null;
    return (
      <div className="mb-3 rounded-lg bg-slate-50 p-3 dark:bg-white/[0.03]">
        <p className="text-xs font-semibold text-slate-700 dark:text-slate-300">{t('Already in the shared group catalog ({count}). Choose any to use them as they are.', { count: available.length })}</p>
        <div className="mt-2 flex flex-wrap gap-1.5">
          {available.map(c => (
            <button key={c.code} type="button" disabled={readOnly} className="rounded-full border border-slate-300 bg-white px-2.5 py-1 text-xs font-medium text-slate-700 hover:border-sapphire hover:text-sapphire dark:border-white/15 dark:bg-transparent dark:text-slate-200"
              onClick={() => set(key, [...(body[key] as never[]), make(c)] as never)}>
              {t('Use {code} · {name}', { code: c.code, name: name(c) })}
            </button>
          ))}
        </div>
      </div>
    );
  };

  const warnings = stepWarnings(4, { body, context });
  const sharedNotice = !canAddShared && <Notice tone="info">{t('Only a group administrator can add new shared codes. You can use the ones the group already has.')}</Notice>;
  return (
    <div className="space-y-8">
      <section>
        <SectionHeader id={sectionHeadingId('costCentres')} status={<SectionSavedStatus section="costCentres" />} title={t('Cost centres')} description={t('Belong to this company only. Departments below can be charged to one of them.')} actions={defaultsButton('costCentres')} />
        <RowsEditor<CompanySetupCostCentre> path="costCentres" rows={body.costCentres} columns={costCentreCols} onChange={rows => set('costCentres', rows)}
          newRow={() => ({ code: '', nameEn: '', nameAr: '' })} addLabel={t('Add cost centre')} emptyText={t('No cost centres yet. Optional; add them if finance reports by cost centre.')}
          rowLabel={i => t('Cost centre {number}', { number: i + 1 })} manage={manage('costCentres')} baselineRows={baseline?.costCentres} />
        <SectionSaveBar section="costCentres" />
      </section>

      <div className="rounded-xl border border-dashed border-slate-300 p-4 dark:border-white/15">
        <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Shared group catalog')}</p>
        <p className="mt-0.5 text-sm text-slate-600 dark:text-slate-400">{t('Departments, designations and grades are shared by every company in the group. Codes the group already has are reused as they are; new codes are added for everyone.')}</p>
      </div>
      {sharedNotice}

      <section>
        <SectionHeader id={sectionHeadingId('departments')} status={<SectionSavedStatus section="departments" />} title={t('Departments')} actions={defaultsButton('departments')} />
        {reuseChips('departments', catalog?.departments, c => ({ code: c.code, nameEn: c.nameEn, nameAr: c.nameAr ?? '' }) as never)}
        <RowsEditor<CompanySetupDepartment> path="departments" rows={body.departments} columns={departmentCols} onChange={rows => set('departments', rows)}
          newRow={() => ({ code: '', nameEn: '', nameAr: '' })} addLabel={t('Add department')} emptyText={t('No departments yet.')}
          rowLabel={i => t('Department {number}', { number: i + 1 })} reused={row => inCatalog(catalog?.departments, row.code) ?? sharedExisting(row, row.nameEn, row.nameAr)}
          manage={manage('departments')} baselineRows={baseline?.departments} canAdd={canAddShared}
          footer={!canAddShared ? <span className="text-xs text-slate-600 dark:text-slate-400">{t('New codes need a group administrator.')}</span> : undefined} />
        <SectionSaveBar section="departments" />
      </section>

      <section>
        <SectionHeader id={sectionHeadingId('designations')} status={<SectionSavedStatus section="designations" />} title={t('Designations')} actions={defaultsButton('designations')} />
        {reuseChips('designations', catalog?.designations, c => ({ code: c.code, titleEn: c.nameEn, titleAr: c.nameAr ?? '' }) as never)}
        <RowsEditor<CompanySetupDesignation> path="designations" rows={body.designations} columns={designationCols} onChange={rows => set('designations', rows)}
          newRow={() => ({ code: '', titleEn: '', titleAr: '' })} addLabel={t('Add designation')} emptyText={t('No designations yet.')}
          rowLabel={i => t('Designation {number}', { number: i + 1 })} reused={row => inCatalog(catalog?.designations, row.code) ?? sharedExisting(row, row.titleEn, row.titleAr)}
          manage={manage('designations')} baselineRows={baseline?.designations} canAdd={canAddShared} />
        <SectionSaveBar section="designations" />
      </section>

      <section>
        <SectionHeader id={sectionHeadingId('grades')} status={<SectionSavedStatus section="grades" />} title={t('Grades')} description={context?.gradesCurrency ? t('Salary bands are in {currency}, the group’s grade currency.', { currency: context.gradesCurrency }) : t('Salary bands are in {currency}.', { currency: body.company.currency || '—' })} actions={defaultsButton('grades')} />
        {reuseChips('grades', catalog?.grades, c => ({ code: c.code, nameEn: c.nameEn, nameAr: c.nameAr ?? '', level: 1, minSalary: 0, midSalary: 0, maxSalary: 0 }) as never)}
        <RowsEditor<CompanySetupGrade> path="grades" rows={body.grades} columns={gradeCols} onChange={rows => set('grades', rows)}
          newRow={() => ({ code: '', nameEn: '', nameAr: '', level: body.grades.length + 1, minSalary: 0, midSalary: 0, maxSalary: 0 })} addLabel={t('Add grade')} emptyText={t('No grades yet.')}
          rowLabel={i => t('Grade {number}', { number: i + 1 })} reused={row => inCatalog(catalog?.grades, row.code) ?? sharedExisting(row, row.nameEn, row.nameAr)}
          manage={manage('grades')} baselineRows={baseline?.grades} canAdd={canAddShared} />
        {warnings.map(w => <div key={w.field} className="mt-3"><Notice tone="warning" role="status">{t(w.message, w.params)}</Notice></div>)}
        <SectionSaveBar section="grades" />
      </section>
    </div>
  );
}
