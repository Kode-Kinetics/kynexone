'use client';

import type { ReactNode } from 'react';
import { useLocale } from '@/src/contexts/LocaleContext';
import { formatAmount } from '@/src/lib/hire/money';
import { parseAmount } from '@/src/lib/hire/payload';
import { buildCountryOptions } from '@/src/lib/hire/nationality';
import { normalizeIban } from '@/src/lib/hire/iban';
import type { StepProps } from '../types';

function Row({ label, value, ltr }: { label: string; value?: string; ltr?: boolean }) {
  const { t } = useLocale();
  return (
    <div className="flex flex-col gap-0.5 py-1.5 sm:flex-row sm:gap-4">
      <dt className="text-sm text-slate-500 sm:w-56 dark:text-slate-400">{label}</dt>
      <dd className="min-w-0 break-words text-sm font-medium text-slate-900 dark:text-white" dir={ltr && value ? 'ltr' : undefined}>{value || <span className="font-normal text-slate-500">{t('Not provided')}</span>}</dd>
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

export function ReviewStep({ form, lookups, currency, onEdit }: StepProps & { onEdit: (step: 1 | 2 | 3) => void }) {
  const { t, locale } = useLocale();
  const ar = locale === 'ar';
  const nat = buildCountryOptions().find(o => o.code === form.nationalityCode);
  const name = <T extends { id: string }>(rows: T[], id: string, pick: (r: T) => string) => { const r = rows.find(x => x.id === id); return r ? pick(r) : ''; };
  const money = (v: string) => { const n = parseAmount(v); return n && n > 0 ? formatAmount(n, currency, locale) : ''; };
  const missing = [
    !form.nationalityCode && t('Nationality'), !form.idNumber.trim() && t('ID number'), !form.iban.trim() && t('IBAN'), !form.arabicName.trim() && t('Arabic full name'), !form.dob && t('Date of birth'),
  ].filter(Boolean) as string[];
  return (
    <div className="space-y-4">
      <Block name={t('Hire basics')} onEdit={() => onEdit(1)}>
        <Row label={t('English full name')} value={form.englishName.trim()} />
        <Row label={t('Company')} value={name(lookups.companies, form.companyId, c => (ar && c.legalNameAr ? c.legalNameAr : c.legalNameEn))} />
        <Row label={t('Start date')} value={form.startDate} ltr />
        <Row label={t('Designation')} value={name(lookups.designations, form.designationId, d => (ar && d.titleAr ? d.titleAr : d.titleEn))} />
        <Row label={t('Grade')} value={name(lookups.grades, form.gradeId, g => `${g.code} - ${g.name}`)} />
        <Row label={t('Branch')} value={name(lookups.branches, form.branchId, b => (ar && b.nameAr ? b.nameAr : b.nameEn))} />
        <Row label={t('Department')} value={name(lookups.departments, form.departmentId, d => (ar && d.nameAr ? d.nameAr : d.nameEn))} />
      </Block>
      <Block name={t('Person and contact')} onEdit={() => onEdit(2)}>
        <Row label={t('Arabic full name')} value={form.arabicName.trim()} />
        <Row label={t('Nationality')} value={nat ? (ar ? nat.nameAr : nat.nameEn) : ''} />
        <Row label={t('Date of birth')} value={form.dob} ltr />
        <Row label={t('Mobile number')} value={form.mobileNational.trim()} ltr />
        <Row label={t('Personal email')} value={form.personalEmail.trim()} ltr />
      </Block>
      <Block name={t('Job and pay')} onEdit={() => onEdit(3)}>
        <Row label={t('Employment type')} value={form.employmentType} />
        <Row label={t('Contract type')} value={form.contractType} />
        <Row label={t('IBAN')} value={form.iban.trim() ? normalizeIban(form.iban) : ''} ltr />
        <Row label={t('Basic salary')} value={money(form.basic)} ltr />
      </Block>
      <Block name={t('Documents')} onEdit={() => onEdit(3)}>
        <Row label={t('ID number')} value={form.idNumber.trim()} ltr />
        <Row label={t('ID expiry date')} value={form.idExpiry} ltr />
      </Block>
      {missing.length > 0 && (
        <div className="rounded-xl border border-slate-200 p-4 text-sm dark:border-white/10">
          <p className="font-semibold">{t('Missing for activation')}</p>
          <p className="mt-1 text-slate-600 dark:text-slate-300">{missing.join(t(', '))}</p>
          <p className="mt-2 text-slate-500">{t('You can leave these for now. The employee can complete them in Self-Service after the first day.')}</p>
        </div>
      )}
    </div>
  );
}
