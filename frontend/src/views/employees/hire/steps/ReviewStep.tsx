'use client';

import { useEffect, useState, type ReactNode } from 'react';
import { useLocale } from '@/src/contexts/LocaleContext';
import { formatAmount } from '@/src/lib/hire/money';
import { composeWorkEmail, parseAmount } from '@/src/lib/hire/payload';
import { buildCountryOptions } from '@/src/lib/hire/nationality';
import { bankNameFor, normalizeIban } from '@/src/lib/hire/iban';
import { activationNeeds } from '@/src/lib/hire/hireValidation';
import { formatIsoDate, hijriLine } from '@/src/lib/hire/dates';
import { reviewWarnings } from '@/src/lib/hire/reviewWarnings';
import { SERVER_CODE_KEYS } from '@/src/lib/hire/problem';
import { referenceOptions } from '@/src/lib/referenceLists';
import type { ReferenceValue } from '@/src/lib/referenceLists';
import type { StepProps } from '../types';
import { CONTRACT_FALLBACK, EMPLOYMENT_FALLBACK } from './JobPayDocsStep';
import { GENDER_FALLBACK, MARITAL_FALLBACK } from './PersonContactStep';
import { onboardingTemplatesApi, type HireTemplate, type PreviewTask } from '@/src/api/onboardingTemplates';

function Row({ label, value, ltr, children }: { label: string; value?: string; ltr?: boolean; children?: ReactNode }) {
  const { t } = useLocale();
  return (
    <div className="flex flex-col gap-0.5 py-1.5 sm:flex-row sm:gap-4">
      <dt className="text-sm text-slate-500 sm:w-56 dark:text-slate-400">{label}</dt>
      <dd className="min-w-0 break-words text-sm font-medium text-slate-900 dark:text-white" dir={ltr && value ? 'ltr' : undefined}>
        {value || <span className="font-normal text-slate-500">{t('Not provided')}</span>}
        {value && children}
      </dd>
    </div>
  );
}

function Block({ name, onEdit, children }: { name: string; onEdit: () => void; children: ReactNode }) {
  const { t } = useLocale();
  return (
    <section aria-label={name} className="rounded-xl border border-slate-200 p-4 dark:border-white/10">
      <div className="flex items-center justify-between gap-2">
        <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{name}</h3>
        <button type="button" className="btn-secondary" onClick={onEdit} aria-label={t('Edit {section}', { section: name })}>{t('Edit')}</button>
      </div>
      <dl className="mt-2 divide-y divide-slate-100 dark:divide-white/5">{children}</dl>
    </section>
  );
}

export function ReviewStep({ form, lookups, currency, companyCountry, company, today, banks, onEdit, hireTemplate }: StepProps & { onEdit: (step: 1 | 2 | 3) => void; hireTemplate?: HireTemplate | null }) {
  const { t, locale } = useLocale();
  const ar = locale === 'ar';
  const nat = buildCountryOptions().find(o => o.code === form.nationalityCode);
  const name = <T extends { id: string }>(rows: T[], id: string, pick: (r: T) => string) => { const r = rows.find(x => x.id === id); return r ? pick(r) : ''; };
  const money = (v: string) => { const n = parseAmount(v); return n && n > 0 ? formatAmount(n, currency, locale) : ''; };
  // CD34: dates for people (Gregorian words, Latin digits), with the Umm al-Qura date under the ID and passport expiries.
  const date = (iso: string) => formatIsoDate(iso, locale);
  const dateRow = (key: string, label: string, iso: string, withHijri = false) => (
    <Row label={label} value={date(iso)}>
      {withHijri && hijriLine(iso, locale) && <span data-testid={`review-hijri-${key}`} dir="auto" className="block text-xs font-normal text-slate-500 dark:text-slate-400">{t('Hijri: {date}', { date: hijriLine(iso, locale) })}</span>}
    </Row>
  );
  const choice = (list: readonly ReferenceValue[], fallback: readonly string[], value: string) => {
    const v = value.trim();
    if (!v) return '';
    const o = referenceOptions(list, locale, fallback, v).find(x => x.value.toLowerCase() === v.toLowerCase());
    return t(o?.label ?? v);
  };
  const ID_TYPES: Record<string, string> = { nationalId: 'National ID', iqama: 'Iqama', border: 'Border Number', gccId: 'GCC national ID', passport: 'Passport' };
  const grade = lookups.grades.find(g => g.id === form.gradeId);
  const basic = parseAmount(form.basic);
  const [checklist, setChecklist] = useState<{ templateId: string; tasks: PreviewTask[] } | null>(null);
  const templateId = hireTemplate?.id;
  useEffect(() => {
    if (!templateId) return;
    let live = true;
    onboardingTemplatesApi.hire.checklistPreview(templateId, form.nationalityCode, form.startDate || undefined)
      .then(r => { if (live) setChecklist({ templateId, tasks: r.tasks }); }).catch(() => { if (live) setChecklist(null); });
    return () => { live = false; };
  }, [templateId, form.nationalityCode, form.startDate]);
  const raised = checklist && checklist.templateId === templateId ? checklist.tasks : null;
  const warnings = reviewWarnings(form, { today });
  if (grade && basic !== undefined && basic > 0 && grade.maxSalary > 0 && (basic < grade.minSalary || basic > grade.maxSalary)) warnings.push('grade_band_out_of_range');
  const bank = companyCountry === 'SA' ? bankNameFor(form.iban, banks, locale) : '';
  const workEmail = composeWorkEmail(form.workEmail, company?.emailDomain || undefined) ?? '';
  const missing = [
    ...activationNeeds(form).map(n => t(n.key === 'startDate' ? 'Start date' : 'Designation or grade')),
    !form.nationalityCode && t('Nationality'), !form.idNumber.trim() && t('ID number'), !form.iban.trim() && t('IBAN'), !form.arabicName.trim() && t('Arabic full name'), !form.dob && t('Date of birth'),
  ].filter(Boolean) as string[];
  return (
    <div className="space-y-4">
      {warnings.length > 0 && (
        <section aria-label={t('Warnings you will be asked to confirm')} className="rounded-xl border border-amber-300 bg-amber-50 p-4 text-sm text-amber-950 dark:border-amber-400/30 dark:bg-amber-500/10 dark:text-amber-100">
          <h3 className="font-semibold">{t('Warnings you will be asked to confirm')}</h3>
          <ul className="mt-1 list-disc ps-5">{warnings.map(code => <li key={code}>{t(SERVER_CODE_KEYS[code] ?? 'Some details need your confirmation before saving.')}</li>)}</ul>
        </section>
      )}
      <Block name={t('Hire basics')} onEdit={() => onEdit(1)}>
        <Row label={t('English full name')} value={form.englishName.trim()} />
        <Row label={t('Company')} value={name(lookups.companies, form.companyId, c => (ar && c.legalNameAr ? c.legalNameAr : c.legalNameEn))} />
        {dateRow('startDate', t('Start date'), form.startDate)}
        <Row label={t('Designation')} value={name(lookups.designations, form.designationId, d => (ar && d.titleAr ? d.titleAr : d.titleEn))} />
        <Row label={t('Grade')} value={name(lookups.grades, form.gradeId, g => `${g.code} - ${g.name}`)} />
        <Row label={t('Branch')} value={name(lookups.branches, form.branchId, b => (ar && b.nameAr ? b.nameAr : b.nameEn))} />
        <Row label={t('Department')} value={name(lookups.departments, form.departmentId, d => (ar && d.nameAr ? d.nameAr : d.nameEn))} />
        <Row label={t('Work email')} value={workEmail} ltr />
        {hireTemplate && <Row label={t('Hire template')} value={ar && hireTemplate.nameAr ? hireTemplate.nameAr : hireTemplate.name} />}
        {raised && (
          <Row label={t('Onboarding checklist')} value={t('{tasks, plural, one {# task} other {# tasks}}, {blocking, plural, one {# blocks activation} other {# block activation}}', { tasks: Number(raised.length), blocking: Number(raised.filter(x => x.blocksActivation).length) })}>
            <ul data-testid="review-checklist" className="mt-1 list-disc ps-5 text-xs font-normal text-slate-600 dark:text-slate-300">
              {raised.map(x => <li key={x.templateTaskId}>{ar && x.titleAr ? x.titleAr : x.title}</li>)}
            </ul>
          </Row>
        )}
      </Block>
      <Block name={t('Person and contact')} onEdit={() => onEdit(2)}>
        <Row label={t('Arabic full name')} value={form.arabicName.trim()} />
        <Row label={t('Gender')} value={choice(lookups.refs.gender, GENDER_FALLBACK, form.gender)} />
        <Row label={t('Marital status')} value={choice(lookups.refs.maritalStatus, MARITAL_FALLBACK, form.maritalStatus)} />
        <Row label={t('Nationality')} value={nat ? (ar ? nat.nameAr : nat.nameEn) : ''} />
        {dateRow('dob', t('Date of birth'), form.dob)}
        <Row label={t('Mobile number')} value={form.mobileNational.trim()} ltr />
        <Row label={t('Personal email')} value={form.personalEmail.trim()} ltr />
      </Block>
      <Block name={t('Job and pay')} onEdit={() => onEdit(3)}>
        <Row label={t('Employment type')} value={choice(lookups.refs.employmentType, EMPLOYMENT_FALLBACK, form.employmentType)} />
        <Row label={t('Contract type')} value={choice(lookups.refs.contractType, CONTRACT_FALLBACK, form.contractType)} />
        {form.contractEndDate && dateRow('contractEndDate', t('Contract end date'), form.contractEndDate)}
        {form.probationEndDate && dateRow('probationEndDate', t('Probation end date'), form.probationEndDate)}
        <Row label={t('IBAN')} value={form.iban.trim() ? normalizeIban(form.iban) : ''} ltr />
        {bank && <Row label={t('Bank')} value={bank} />}
        <Row label={t('Basic salary')} value={money(form.basic)} ltr />
      </Block>
      <Block name={t('Documents')} onEdit={() => onEdit(3)}>
        {form.idType && <Row label={t('ID type')} value={t(ID_TYPES[form.idType] ?? form.idType)} />}
        <Row label={t('ID number')} value={form.idNumber.trim()} ltr />
        {dateRow('idExpiry', t('ID expiry date'), form.idExpiry, true)}
        {form.passportNumber.trim() && <Row label={t('Passport number')} value={form.passportNumber.trim().toUpperCase()} ltr />}
        {form.passportNumber.trim() && dateRow('passportExpiry', t('Passport expiry date'), form.passportExpiry, true)}
      </Block>
      {missing.length > 0 && (
        <div className="rounded-xl border border-slate-200 p-4 text-sm dark:border-white/10">
          <p className="font-semibold">{t('Before activation')}</p>
          <p className="mt-1 text-slate-600 dark:text-slate-300">{new Intl.ListFormat(locale, { type: 'unit', style: 'narrow' }).format(missing)}</p>
          <p className="mt-2 text-slate-500">{t('You can leave this for now and add it later from the employee record.')}</p>
        </div>
      )}
    </div>
  );
}
